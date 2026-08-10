using FaustusControllerLite.Orders;

namespace FaustusControllerLite.Domain;

public enum CandidateOutcome
{
    Accepted,
    NoneAccepted,
    Blocked,
}

public enum WorkflowPreparationResult
{
    Accepted,
    NoCandidate,
    RetryableUnavailable,
    Failed,
}

public enum ContinuousLoopAction
{
    Wait,
    StartNewWorkflowScan,
    DriveActiveWorkflow,
    StopWithoutDurableRoute,
    StopUnsafeTerminalState,
}

public sealed record ContinuousLoopSnapshot(
    bool StartingNewWorkflow,
    bool InputOperationActive,
    WorkflowExecutionPhase? Phase,
    bool CanonicalStateSettled,
    DateTimeOffset? NextScanAtUtc,
    DateTimeOffset NowUtc);

public static class ContinuousWorkflowLoop
{
    public const int MinimumRetrySeconds = 2;
    public const int MaximumRetrySeconds = 90;
    public const int MinimumJitterSeconds = -2;
    public const int MaximumJitterSeconds = 2;
    public const int IdleHeartbeatSeconds = 30;
    public const int StalePlacementLatchSeconds = 5;

    /// <summary>
    /// True when the placement latch is set while no controller owns it. Such a latch can hold no
    /// armed input, but it does block every scan, so it may be released instead of waiting forever.
    /// </summary>
    public static bool IsStalePlacementLatch(bool placementLatched, bool owningControllerRunning) =>
        placementLatched && !owningControllerRunning;

    /// <summary>True once an observed condition has held without interruption for the given seconds.</summary>
    public static bool HasPersistedFor(DateTimeOffset? sinceUtc, DateTimeOffset nowUtc, int seconds) =>
        sinceUtc is { } since && nowUtc - since >= TimeSpan.FromSeconds(seconds);

    /// <summary>Whole-second retry delay for a valid probe that accepted no route.</summary>
    public static int ResolveRetrySeconds(int configuredSeconds, int jitterSeconds) =>
        Math.Clamp(
            Math.Clamp(configuredSeconds, MinimumRetrySeconds, MaximumRetrySeconds) +
                Math.Clamp(jitterSeconds, MinimumJitterSeconds, MaximumJitterSeconds),
            MinimumRetrySeconds,
            MaximumRetrySeconds);

    public static WorkflowPreparationResult ClassifyNewWorkflowProbe(CandidateOutcome outcome, bool hasCandidate) =>
        outcome switch
        {
            CandidateOutcome.Accepted when hasCandidate => WorkflowPreparationResult.Accepted,
            CandidateOutcome.NoneAccepted when !hasCandidate => WorkflowPreparationResult.NoCandidate,
            _ => WorkflowPreparationResult.Failed,
        };

    /// <summary>Only a planner-complete no-route result may be retried after a cooldown.</summary>
    public static bool IsRetryable(WorkflowPreparationResult result) =>
        result is WorkflowPreparationResult.NoCandidate or WorkflowPreparationResult.RetryableUnavailable;

    /// <summary>
    /// True when no order, reservation, or settlement amount is outstanding, so a fresh
    /// workflow may take custody of the bankroll.
    /// </summary>
    public static bool TryDescribeUnsettledCanonicalState(
        BankrollState bankroll,
        TrackedOrderState? tracked,
        out string reason)
    {
        ArgumentNullException.ThrowIfNull(bankroll);
        if (bankroll.HasUnresolvedOrder || tracked?.IsUnresolved == true)
        {
            reason = "a tracked order is unresolved";
            return true;
        }
        if (bankroll.ReservedChaos != 0 || bankroll.ReservedDivine != 0 ||
            bankroll.NonCoreBalances.Values.Any(balance => balance.Reserved != 0))
        {
            reason = "reserved principal has not been released";
            return true;
        }
        if (bankroll.CompletedUncollectedChaos != 0 || bankroll.CompletedUncollectedDivine != 0 ||
            bankroll.NonCoreBalances.Values.Any(balance => balance.CompletedUncollected != 0))
        {
            reason = "settled proceeds remain uncollected";
            return true;
        }
        if (tracked is not null)
        {
            if (tracked.Status is not TrackedOrderStatus.None and not TrackedOrderStatus.Stashed)
            {
                reason = $"tracked order state {tracked.Status} is not durably stashed";
                return true;
            }
            if (tracked.PendingWantedBatchAmount != 0 || tracked.PendingReturnBatchAmount != 0)
            {
                reason = "a collected batch is still pending stash custody";
                return true;
            }
        }

        reason = string.Empty;
        return false;
    }

    public static ContinuousLoopAction Decide(ContinuousLoopSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Phase == WorkflowExecutionPhase.LegActive)
        {
            return snapshot.InputOperationActive
                ? ContinuousLoopAction.Wait
                : ContinuousLoopAction.DriveActiveWorkflow;
        }
        if (snapshot.Phase == WorkflowExecutionPhase.Ambiguous)
        {
            return snapshot.InputOperationActive
                ? ContinuousLoopAction.Wait
                : ContinuousLoopAction.StopUnsafeTerminalState;
        }
        if (snapshot.InputOperationActive)
        {
            return ContinuousLoopAction.Wait;
        }
        if (snapshot.StartingNewWorkflow)
        {
            return ScanOrWait(snapshot);
        }
        if (snapshot.Phase is null)
        {
            return ContinuousLoopAction.StopWithoutDurableRoute;
        }
        return snapshot.Phase == WorkflowExecutionPhase.ReadyForLeg
            ? snapshot.NextScanAtUtc is { } deadline && snapshot.NowUtc < deadline
                ? ContinuousLoopAction.Wait
                : ContinuousLoopAction.DriveActiveWorkflow
            : ScanOrWait(snapshot);
    }

    private static ContinuousLoopAction ScanOrWait(ContinuousLoopSnapshot snapshot)
    {
        if (!snapshot.CanonicalStateSettled) return ContinuousLoopAction.StopUnsafeTerminalState;
        return snapshot.NextScanAtUtc is { } deadline && snapshot.NowUtc < deadline
            ? ContinuousLoopAction.Wait
            : ContinuousLoopAction.StartNewWorkflowScan;
    }
}
