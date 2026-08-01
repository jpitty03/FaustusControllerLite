using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace FaustusControllerLite.Orders;

public enum LifecycleObservationKind
{
    NotVisible,
    Pending,
    TimedOut,
    Completed,
    Canceled,
    Transitioning,
    Ambiguous,
}

public sealed record LifecycleObservation(
    LifecycleObservationKind Kind,
    PlacedOrderSnapshot? Order,
    string Detail);

public sealed record SettlementAsset(
    string Metadata,
    long Amount,
    bool WantedSlot);

public static class TrackedOrderLifecycle
{
    public static void MigrateLegacyAssetProgress(TrackedOrderState tracked)
    {
        if (tracked.SchemaVersion > 3 || tracked.TerminalRemainingOfferedAmount is not { } remaining ||
            tracked.TerminalReceivedWantedAmount is not { } received)
        {
            return;
        }
        var assets = CreateSettlementAssets(tracked, remaining, received);
        if (tracked.Status is TrackedOrderStatus.Collected or TrackedOrderStatus.StashTransferArmed or
                TrackedOrderStatus.Stashed)
        {
            tracked.WantedAssetCollected = assets.Any(asset => asset.WantedSlot);
            tracked.OfferedReturnCollected = assets.Any(asset => !asset.WantedSlot);
        }
        if (tracked.Status == TrackedOrderStatus.Stashed)
        {
            tracked.WantedAssetStashed = assets.Any(asset => asset.WantedSlot);
            tracked.OfferedReturnStashed = assets.Any(asset => !asset.WantedSlot);
        }
    }

    public static bool AssetProgressIsValid(TrackedOrderState tracked)
    {
        if (tracked.WantedAssetStashed && !tracked.WantedAssetCollected ||
            tracked.OfferedReturnStashed && !tracked.OfferedReturnCollected)
        {
            return false;
        }
        if (tracked.TerminalRemainingOfferedAmount is not { } remaining ||
            tracked.TerminalReceivedWantedAmount is not { } received)
        {
            if (tracked.Status == TrackedOrderStatus.Stashed) return false;
            return !tracked.WantedAssetCollected && !tracked.OfferedReturnCollected &&
                !tracked.WantedAssetStashed && !tracked.OfferedReturnStashed;
        }
        var assets = CreateSettlementAssets(tracked, remaining, received);
        if (tracked.Status == TrackedOrderStatus.Stashed && assets.Count == 0) return false;
        var hasWanted = assets.Any(asset => asset.WantedSlot);
        var hasReturn = assets.Any(asset => !asset.WantedSlot);
        if (tracked.WantedAssetCollected && !hasWanted || tracked.OfferedReturnCollected && !hasReturn ||
            tracked.WantedAssetStashed && !hasWanted || tracked.OfferedReturnStashed && !hasReturn)
        {
            return false;
        }
        var allCollected = assets.All(asset => asset.WantedSlot
            ? tracked.WantedAssetCollected
            : tracked.OfferedReturnCollected);
        var allStashed = assets.All(asset => asset.WantedSlot
            ? tracked.WantedAssetStashed
            : tracked.OfferedReturnStashed);
        if ((tracked.Status is TrackedOrderStatus.Collected or TrackedOrderStatus.StashTransferArmed or
                TrackedOrderStatus.Stashed) && !allCollected ||
            tracked.Status == TrackedOrderStatus.Stashed && !allStashed)
        {
            return false;
        }
        if (tracked.Status is TrackedOrderStatus.CollectionArmed or TrackedOrderStatus.Ambiguous &&
            tracked.CollectionAssetIntent is { } intent &&
            (intent.WantedSlot ? tracked.WantedAssetCollected : tracked.OfferedReturnCollected))
        {
            return false;
        }
        return true;
    }

    public static LifecycleObservation Evaluate(
        TrackedOrderState tracked,
        IReadOnlyCollection<PlacedOrderSnapshot> orders,
        DateTimeOffset observedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(tracked);
        ArgumentNullException.ThrowIfNull(orders);
        if (!HasDurableIdentity(tracked))
        {
            return new LifecycleObservation(LifecycleObservationKind.Ambiguous, null,
                "Tracked pending order lacked durable creation/ratio identity.");
        }

        var matches = orders.Where(order => IdentityMatches(tracked, order)).ToArray();
        if (matches.Length == 0)
        {
            return new LifecycleObservation(LifecycleObservationKind.NotVisible, null,
                "Exact tracked order is not currently visible; no transition inferred.");
        }
        if (matches.Length != 1)
        {
            return new LifecycleObservation(LifecycleObservationKind.Ambiguous, null,
                $"Found {matches.Length} orders with the exact durable identity.");
        }

        var order = matches[0];
        if (order.RemainingOfferedAmount < 0 || order.RemainingOfferedAmount > order.OriginalOfferedAmount ||
            order.ReceivedWantedAmount < 0 || order.ReceivedWantedAmount > tracked.WantedAmount)
        {
            return new LifecycleObservation(LifecycleObservationKind.Ambiguous, order,
                "Live order reported impossible remaining or received amounts.");
        }

        if (order.IsCanceled)
        {
            if (!order.IsCompleted)
            {
                return new LifecycleObservation(LifecycleObservationKind.Transitioning, order,
                    "Canceled flag appeared before the completed terminal flag; waiting for stable terminal evidence.");
            }
            if (!CanceledAmountsMatchPlacedRatio(order))
            {
                return new LifecycleObservation(LifecycleObservationKind.Ambiguous, order,
                    "Canceled order lacked completed flag or exact placed-ratio terminal arithmetic.");
            }
            var kind = order.RemainingOfferedAmount == 0 && order.ReceivedWantedAmount == tracked.WantedAmount
                ? LifecycleObservationKind.Completed
                : LifecycleObservationKind.Canceled;
            return new LifecycleObservation(kind, order,
                kind == LifecycleObservationKind.Completed
                    ? "Cancel/completion race ended with full planned economics."
                    : "Exact order is canceled with authoritative terminal amounts.");
        }
        if (order.IsCompleted)
        {
            return order.ReceivedWantedAmount == tracked.WantedAmount
                ? new LifecycleObservation(LifecycleObservationKind.Completed, order,
                    "Exact order completed; any remaining offered amount is a collectible return.")
                : new LifecycleObservation(LifecycleObservationKind.Ambiguous, order,
                    "Completed order did not report the planned wanted amount.");
        }
        if (order.RemainingOfferedAmount == 0)
        {
            return new LifecycleObservation(LifecycleObservationKind.Ambiguous, order,
                "Nonterminal order reported no offered amount remaining.");
        }

        return observedAtUtc >= tracked.WaitUntilUtc
            ? new LifecycleObservation(LifecycleObservationKind.TimedOut, order,
                "Exact pending order remained outstanding at its captured deadline.")
            : new LifecycleObservation(LifecycleObservationKind.Pending, order,
                "Exact order remains pending.");
    }

    public static IReadOnlyList<SettlementAsset> CreateSettlementAssets(
        TrackedOrderState tracked,
        long remainingOffered,
        long receivedWanted)
    {
        if (remainingOffered < 0 || remainingOffered > tracked.OfferedAmount ||
            receivedWanted < 0 || receivedWanted > tracked.WantedAmount)
        {
            throw new ArgumentOutOfRangeException(nameof(remainingOffered));
        }

        var assets = new List<SettlementAsset>(2);
        if (receivedWanted > 0) assets.Add(new SettlementAsset(tracked.WantedMetadata, receivedWanted, true));
        if (remainingOffered > 0) assets.Add(new SettlementAsset(tracked.OfferedMetadata, remainingOffered, false));
        return assets;
    }

    public static bool HasDurableIdentity(TrackedOrderState tracked) =>
        tracked.OrderCreationDateUtc is not null && tracked.PlacedOfferedRatioPart is > 0 &&
        tracked.PlacedWantedRatioPart is > 0 && tracked.WaitUntilUtc is not null &&
        tracked.OfferedHash != 0 && tracked.WantedHash != 0;

    public static bool IdentityMatches(TrackedOrderState tracked, PlacedOrderSnapshot order) =>
        HasDurableIdentity(tracked) && order.CreationDate == tracked.OrderCreationDateUtc &&
        order.OfferedMetadata == tracked.OfferedMetadata && order.OfferedHash == tracked.OfferedHash &&
        order.WantedMetadata == tracked.WantedMetadata && order.WantedHash == tracked.WantedHash &&
        order.OriginalOfferedAmount == tracked.OfferedAmount &&
        order.OfferedRatioPart == tracked.PlacedOfferedRatioPart &&
        order.WantedRatioPart == tracked.PlacedWantedRatioPart &&
        (tracked.GoldCost is null || order.GoldCost == tracked.GoldCost);

    public static bool TerminalIdentityMatches(TrackedOrderState tracked, PlacedOrderSnapshot order) =>
        HasDurableIdentity(tracked) && order.IsCompleted && order.CreationDate == tracked.OrderCreationDateUtc &&
        order.OfferedMetadata == tracked.OfferedMetadata && order.OfferedHash == tracked.OfferedHash &&
        order.WantedMetadata == tracked.WantedMetadata && order.WantedHash == tracked.WantedHash &&
        order.OriginalOfferedAmount == tracked.OfferedAmount &&
        order.OfferedRatioPart == tracked.PlacedOfferedRatioPart &&
        order.WantedRatioPart == tracked.PlacedWantedRatioPart &&
        (tracked.GoldCost is null || order.GoldCost == tracked.GoldCost || order.GoldCost == 0);

    private static bool CanceledAmountsMatchPlacedRatio(PlacedOrderSnapshot order)
    {
        var spent = order.OriginalOfferedAmount - order.RemainingOfferedAmount;
        return spent >= 0 && order.OfferedRatioPart > 0 && order.WantedRatioPart > 0 &&
            spent % order.OfferedRatioPart == 0 &&
            (System.Numerics.BigInteger)(spent / order.OfferedRatioPart) * order.WantedRatioPart ==
                order.ReceivedWantedAmount;
    }

    public static string OrderSetFingerprint(IEnumerable<PlacedOrderSnapshot> orders)
    {
        var canonical = string.Join("\n", orders.Select(order => string.Join("|",
                order.CreationDate.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                order.OfferedMetadata, order.OfferedHash.ToString(CultureInfo.InvariantCulture),
                order.WantedMetadata, order.WantedHash.ToString(CultureInfo.InvariantCulture),
                order.OriginalOfferedAmount.ToString(CultureInfo.InvariantCulture),
                order.RemainingOfferedAmount.ToString(CultureInfo.InvariantCulture),
                order.ReceivedWantedAmount.ToString(CultureInfo.InvariantCulture),
                order.OfferedRatioPart.ToString(CultureInfo.InvariantCulture),
                order.WantedRatioPart.ToString(CultureInfo.InvariantCulture),
                order.GoldCost.ToString(CultureInfo.InvariantCulture),
                order.IsCompleted ? "1" : "0", order.IsCanceled ? "1" : "0"))
            .OrderBy(value => value, StringComparer.Ordinal));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
