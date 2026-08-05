namespace FaustusControllerLite.Orders;

public enum ArmedReconciliationKind
{
    Waiting,
    Bound,
    Ambiguous,
}

public sealed record ArmedReconciliation(
    ArmedReconciliationKind Kind,
    PlacedOrderSnapshot? Order,
    TrackedOrderStatus Status,
    string Detail);

/// <summary>
/// Reconciles a placement whose click already happened but whose outcome was never bound to a real
/// order. The placement controller only observes briefly; a reload, interruption, or rejected match
/// can leave either an armed state or a placement-only ambiguous state without the durable identity
/// required by the normal lifecycle. This is observation only and never authorizes a click.
/// </summary>
public static class ArmedPlacementReconciliation
{
    public const int ObservationGraceSeconds = 15;

    public static bool CanReconcile(TrackedOrderState tracked) =>
        tracked.Status == TrackedOrderStatus.Armed ||
        tracked.Status == TrackedOrderStatus.Ambiguous &&
        tracked.ClickedAtUtc is not null &&
        tracked.OrderCreationDateUtc is null &&
        tracked.PlacedOfferedRatioPart is null &&
        tracked.PlacedWantedRatioPart is null &&
        tracked.LedgerCommittedAtUtc is null &&
        tracked.CancelIntent is null &&
        tracked.CollectionAssetIntent is null &&
        tracked.StashTransferIntent is null &&
        tracked.SettledWantedAmount == 0 && tracked.PendingWantedBatchAmount == 0 &&
        tracked.SettledReturnAmount == 0 && tracked.PendingReturnBatchAmount == 0;

    public static ArmedReconciliation Evaluate(
        TrackedOrderState armed,
        IReadOnlyCollection<PlacedOrderSnapshot> orders,
        DateTimeOffset observedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(armed);
        ArgumentNullException.ThrowIfNull(orders);
        if (!CanReconcile(armed) || armed.ClickedAtUtc is not { } clickedAtUtc ||
            armed.OfferedHash == 0 || armed.WantedHash == 0 ||
            armed.OfferedAmount <= 0 || armed.WantedAmount <= 0)
        {
            return Ambiguous("Armed placement lacked the click time or exact identity needed to reconcile it.");
        }
        if (orders.Any(order => order.PlayerOrderId <= 0) ||
            orders.Select(order => order.PlayerOrderId).Distinct().Count() != orders.Count)
        {
            return Ambiguous("Order list contained a nonpositive or duplicate ID.");
        }

        var baseline = armed.BaselineOrderIds.ToHashSet();
        if (!baseline.IsSubsetOf(orders.Select(order => order.PlayerOrderId)))
        {
            return Ambiguous("A baseline order disappeared while an armed placement was outstanding.");
        }

        var candidates = orders.Where(order => !baseline.Contains(order.PlayerOrderId)).ToArray();
        if (candidates.Length == 0)
        {
            return observedAtUtc - clickedAtUtc < TimeSpan.FromSeconds(ObservationGraceSeconds)
                ? new ArmedReconciliation(ArmedReconciliationKind.Waiting, null, TrackedOrderStatus.Armed,
                    "Armed placement has produced no new order yet.")
                : Ambiguous("Armed placement produced no observable new order.");
        }
        if (candidates.Length != 1)
        {
            return Ambiguous($"Observed {candidates.Length} new orders for one armed placement.");
        }

        var order = candidates[0];
        if (armed.PlayerOrderId is > 0 && order.PlayerOrderId != armed.PlayerOrderId ||
            order.OfferedHash != armed.OfferedHash || order.WantedHash != armed.WantedHash ||
            !string.Equals(order.OfferedMetadata, armed.OfferedMetadata, StringComparison.Ordinal) ||
            !string.Equals(order.WantedMetadata, armed.WantedMetadata, StringComparison.Ordinal) ||
            order.OriginalOfferedAmount != armed.OfferedAmount ||
            !PlacementOrderMatcher.RatiosEquivalent(
                order.OfferedRatioPart, order.WantedRatioPart, armed.OfferedAmount, armed.WantedAmount) ||
            !PlacementOrderMatcher.CreationDateIsPlausible(order.CreationDate, clickedAtUtc, observedAtUtc))
        {
            return Ambiguous("The single new order did not match the armed pair, amount, ratio, or click time.");
        }

        if (!order.IsCompleted)
        {
            if (order.IsCanceled || order.RemainingOfferedAmount <= 0 ||
                order.RemainingOfferedAmount > order.OriginalOfferedAmount ||
                order.ReceivedWantedAmount < 0 || order.ReceivedWantedAmount > armed.WantedAmount)
            {
                return Ambiguous("The new order is neither cleanly pending nor terminal.");
            }
            return new ArmedReconciliation(ArmedReconciliationKind.Bound, order, TrackedOrderStatus.Pending,
                $"Bound armed placement to pending order {order.PlayerOrderId}.");
        }

        // Cancellation may legitimately have filled nothing; completion may not.
        var provable = order.IsCanceled
            ? PlacementOrderMatcher.TerminalAmountsProvable(order, armed.WantedAmount)
            : PlacementOrderMatcher.CompletedFillProvable(order, armed.WantedAmount);
        if (!provable)
        {
            return Ambiguous("Terminal order reported amounts its placed ratio cannot prove.");
        }

        var fullyFilled = order.RemainingOfferedAmount == 0 && order.ReceivedWantedAmount == armed.WantedAmount;
        var status = order.IsCanceled && !fullyFilled
            ? TrackedOrderStatus.CanceledUncollected
            : TrackedOrderStatus.CompletedUncollected;
        return new ArmedReconciliation(ArmedReconciliationKind.Bound, order, status,
            $"Bound armed placement to terminal order {order.PlayerOrderId}: " +
            $"remaining={order.RemainingOfferedAmount}, received={order.ReceivedWantedAmount}.");
    }

    private static ArmedReconciliation Ambiguous(string detail) =>
        new(ArmedReconciliationKind.Ambiguous, null, TrackedOrderStatus.Ambiguous, detail);
}
