using FaustusControllerLite.Orders;
using System.Globalization;

namespace FaustusControllerLite.Domain;

/// <summary>
/// Pure support for the forced fresh-state reset: the manual override that abandons canonical
/// accounting when the safe reset refuses, because the state is unreadable or still holds custody
/// the plugin can no longer resolve on its own. Forcing never deletes evidence — the canonical file
/// is quarantined under a timestamped name — and it never moves an item, so whatever custody this
/// describes is exactly what the operator must reconcile by hand.
/// </summary>
public static class ForcedFreshStateReset
{
    public const string NothingDiscarded = "Nothing unresolved: balances reseed and no custody is discarded.";

    /// <summary>
    /// Names the quarantine copy of <paramref name="path"/>. The original extension is kept inside
    /// the name so a quarantined file can never be loaded as canonical state by any store.
    /// </summary>
    public static string QuarantinePath(string path, DateTimeOffset at, int attempt = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegative(attempt);
        var stamp = at.ToUniversalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var suffix = attempt == 0
            ? string.Empty
            : "-" + attempt.ToString(CultureInfo.InvariantCulture);
        return $"{path}.quarantined-{stamp}Z{suffix}.bak";
    }

    /// <summary>
    /// Describes, in exact metadata and amounts, everything a forced reset stops accounting for.
    /// This text is shown before arming, recorded in the audit log, and is the operator's only
    /// record of what to move or write off by hand.
    /// </summary>
    public static string DescribeDiscardedCustody(
        BankrollState? bankroll,
        TrackedOrderState? tracked,
        bool canonicalStateUnreadable)
    {
        var items = new List<string>();
        if (canonicalStateUnreadable)
        {
            items.Add("canonical state is unreadable, so its contents cannot be enumerated");
        }
        if (bankroll is { IsInitialized: true })
        {
            DescribeBalances(items, bankroll);
            if (bankroll.Workflow is { } workflow)
            {
                items.Add($"workflow {workflow.WorkflowId} ({workflow.Phase}) at leg " +
                    $"{workflow.CurrentLegIndex + 1} of {workflow.Legs.Count}");
            }
        }
        if (tracked is not null)
        {
            items.Add(DescribeTracked(tracked));
        }

        return items.Count == 0 ? NothingDiscarded : string.Join("; ", items) + ".";
    }

    private static void DescribeBalances(List<string> items, BankrollState bankroll)
    {
        foreach (var (metadata, balance) in EnumerateBalances(bankroll))
        {
            var parts = new List<string>();
            if (balance.Available != 0) parts.Add($"available {balance.Available}");
            if (balance.Reserved != 0) parts.Add($"RESERVED {balance.Reserved}");
            if (balance.CompletedUncollected != 0) parts.Add($"uncollected {balance.CompletedUncollected}");
            if (parts.Count > 0) items.Add($"ledger {metadata}: {string.Join(", ", parts)}");
        }
    }

    private static IEnumerable<(string Metadata, NonCoreBalanceState Balance)> EnumerateBalances(BankrollState bankroll)
    {
        yield return (BankrollState.ChaosMetadata, new NonCoreBalanceState
        {
            Available = bankroll.AvailableChaos,
            Reserved = bankroll.ReservedChaos,
            CompletedUncollected = bankroll.CompletedUncollectedChaos,
        });
        yield return (BankrollState.DivineMetadata, new NonCoreBalanceState
        {
            Available = bankroll.AvailableDivine,
            Reserved = bankroll.ReservedDivine,
            CompletedUncollected = bankroll.CompletedUncollectedDivine,
        });
        foreach (var pair in bankroll.NonCoreBalances.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            yield return (pair.Key, pair.Value);
        }
    }

    private static string DescribeTracked(TrackedOrderState tracked)
    {
        var identity = tracked.PlayerOrderId?.ToString(CultureInfo.InvariantCulture) ?? "unknown";
        var text = $"tracked order {identity} ({tracked.Status}): {tracked.OfferedAmount} " +
            $"{tracked.OfferedMetadata} -> {tracked.WantedAmount} {tracked.WantedMetadata}";
        var custody = new List<string>();
        DescribeSlot(custody, "proceeds", tracked.WantedMetadata,
            TrackedOrderLifecycle.RemainingWantedToCollect(tracked), tracked.PendingWantedBatchAmount);
        DescribeSlot(custody, "offered return", tracked.OfferedMetadata,
            TrackedOrderLifecycle.RemainingReturnToCollect(tracked), tracked.PendingReturnBatchAmount);
        return custody.Count == 0 ? text : $"{text}, {string.Join(", ", custody)}";
    }

    private static void DescribeSlot(
        List<string> custody, string slot, string metadata, long uncollected, long pendingBatch)
    {
        if (pendingBatch > 0)
        {
            custody.Add($"{pendingBatch} {metadata} of {slot} is in inventory unstashed and must be moved by hand");
        }
        if (uncollected > 0)
        {
            custody.Add($"{uncollected} {metadata} of {slot} was never collected from the exchange");
        }
    }
}
