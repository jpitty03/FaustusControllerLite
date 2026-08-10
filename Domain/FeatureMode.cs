using System;
using System.Collections.Generic;
using System.Linq;

namespace FaustusControllerLite.Domain;

/// <summary>
/// Which of the two mutually exclusive top-level features owns the automation hotkeys.
/// Exactly one is active at a time; there is deliberately no "both" and no "neither".
/// </summary>
public enum FeatureMode
{
    Arbitrage,
    SellSweep,
}

/// <summary>
/// Which feature an action belongs to. <see cref="Shared"/> actions are infrastructure that both
/// features depend on identically (calibration, diagnostics, rate probing, recovery) and are never
/// gated by the active mode.
/// </summary>
public enum FeatureActionScope
{
    Shared,
    Arbitrage,
    SellSweep,
}

/// <summary>
/// The unresolved-state facts that decide whether the active feature may be changed. Every field is
/// a reason to refuse; an all-false instance is a clean switch. Captured as a record so the decision
/// is a pure function of observed state and can be tested without the SDK.
/// </summary>
public sealed record FeatureModeBlockers(
    bool BankrollHasUnresolvedOrder,
    bool TrackedOrderUnresolved,
    bool WorkflowActive,
    bool FullWorkflowAuthorized,
    bool StartingNewWorkflow,
    bool PersistenceLoadBlocked,
    bool InputOperationActive,
    bool SellSweepUnresolved)
{
    public static FeatureModeBlockers None { get; } = new(false, false, false, false, false, false, false, false);
}

public static class FeatureModeGate
{
    public const string ArbitrageLabel = "Arbitrage";
    public const string SellSweepLabel = "Sell Sweep";

    public static IReadOnlyList<string> Labels { get; } = new[] { ArbitrageLabel, SellSweepLabel };

    public static string ToLabel(FeatureMode mode) => mode switch
    {
        FeatureMode.Arbitrage => ArbitrageLabel,
        FeatureMode.SellSweep => SellSweepLabel,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    /// <summary>
    /// Exact-label parse. Deliberately ordinal and deliberately not lenient: a settings file that has
    /// been hand-edited or corrupted must not silently resolve to a feature that sends input. The
    /// caller is expected to fail closed on false rather than substitute a default.
    /// </summary>
    public static bool TryParse(string? label, out FeatureMode mode)
    {
        if (string.Equals(label, ArbitrageLabel, StringComparison.Ordinal))
        {
            mode = FeatureMode.Arbitrage;
            return true;
        }
        if (string.Equals(label, SellSweepLabel, StringComparison.Ordinal))
        {
            mode = FeatureMode.SellSweep;
            return true;
        }

        mode = default;
        return false;
    }

    /// <summary>
    /// Whether an action of the given scope may run under the resolved mode. A null
    /// <paramref name="active"/> models an unresolvable selector: shared infrastructure still works
    /// so the operator can diagnose and re-pick, but nothing feature-scoped runs.
    /// </summary>
    public static bool IsAllowed(FeatureMode? active, FeatureActionScope scope) => scope switch
    {
        FeatureActionScope.Shared => true,
        FeatureActionScope.Arbitrage => active == FeatureMode.Arbitrage,
        FeatureActionScope.SellSweep => active == FeatureMode.SellSweep,
        _ => false,
    };

    public static string DescribeRefusal(FeatureMode? active, FeatureActionScope scope)
    {
        if (active is null)
        {
            return "Active Feature selector is unreadable; pick Arbitrage or Sell Sweep. " +
                   "No feature action ran and no input was sent.";
        }

        return $"{ToLabel(scope == FeatureActionScope.Arbitrage ? FeatureMode.Arbitrage : FeatureMode.SellSweep)} " +
               $"action ignored because the active feature is {ToLabel(active.Value)}. No input was sent.";
    }

    /// <summary>
    /// Decides a requested feature change. Refuses while the feature being left still owns unresolved
    /// state, because switching would orphan a live order or uncollected proceeds and leave the
    /// bankroll crediting a position nothing is driving any more.
    /// </summary>
    public static bool TrySwitch(
        FeatureMode current,
        FeatureMode requested,
        FeatureModeBlockers blockers,
        out string refusal)
    {
        ArgumentNullException.ThrowIfNull(blockers);

        if (current == requested)
        {
            refusal = string.Empty;
            return true;
        }

        var reasons = DescribeBlockers(current, blockers);
        if (reasons.Count > 0)
        {
            refusal =
                $"Active Feature change to {ToLabel(requested)} refused: {string.Join("; ", reasons)}. " +
                $"Resolve this under {ToLabel(current)} first; the selection was restored and no state changed.";
            return false;
        }

        refusal = string.Empty;
        return true;
    }

    /// <summary>
    /// The unresolved-state reasons that block leaving <paramref name="current"/>, in a stable order
    /// so the status line does not churn between frames.
    /// </summary>
    public static IReadOnlyList<string> DescribeBlockers(FeatureMode current, FeatureModeBlockers blockers)
    {
        ArgumentNullException.ThrowIfNull(blockers);

        var reasons = new List<string>();

        // Persistence has to be checked in both directions. If the store could not be read we cannot
        // prove the feature is idle, and an unprovable idle is not an idle.
        if (blockers.PersistenceLoadBlocked)
        {
            reasons.Add("persisted state could not be read, so idleness cannot be proven");
        }

        if (current == FeatureMode.Arbitrage)
        {
            if (blockers.BankrollHasUnresolvedOrder) reasons.Add("bankroll holds an unresolved order");
            if (blockers.TrackedOrderUnresolved) reasons.Add("a tracked order is unresolved");
            if (blockers.WorkflowActive) reasons.Add("a workflow is active");
            if (blockers.FullWorkflowAuthorized) reasons.Add("continuous trading is authorized");
            if (blockers.StartingNewWorkflow) reasons.Add("a new workflow is starting");
        }
        else if (current == FeatureMode.SellSweep && blockers.SellSweepUnresolved)
        {
            reasons.Add("the sell sweep holds unresolved orders or uncollected proceeds");
        }

        // Input operations are mode-independent: a cursor mid-tween or a click mid-flight belongs to
        // whoever armed it, and reassigning ownership mid-gesture is how you get a stray click.
        if (blockers.InputOperationActive)
        {
            reasons.Add("an input operation is running");
        }

        return reasons;
    }
}
