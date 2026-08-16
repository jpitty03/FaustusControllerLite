namespace FaustusControllerLite.Orders;

public enum TrackedOrderStatus
{
    None,
    Armed,
    Pending,
    CompletedUncollected,
    Ambiguous,
    CollectionArmed,
    Collected,
    StashTransferArmed,
    Stashed,
    TimedOut,
    CancelArmed,
    CancelClicked,
    CanceledUncollected,
}

public enum StashCustodyMode
{
    VisibleCurrencyStashExact = 1,
    AffinityAggregate = 2,
}

public static class StashCustodyPolicy
{
    public const string ReflectingMistMetadata = "Metadata/Items/Currency/ReflectiveMist";
    public const string CurrencyPrefix = "Metadata/Items/Currency/";
    public const string ScarabPrefix = "Metadata/Items/Scarabs/";

    public const string CurrencyTabType = "CurrencyStash";
    public const string FragmentTabType = "FragmentStash";

    /// <summary>
    /// The stash tab type that ctrl+shift+click sends this asset family to. Custody is provable
    /// exactly when that home tab is the visible one, and only in aggregate otherwise.
    /// </summary>
    public static bool TryResolveHomeTabType(string metadata, out string homeTabType)
    {
        if (metadata is not null)
        {
            if (metadata.StartsWith(CurrencyPrefix, StringComparison.Ordinal))
            {
                homeTabType = CurrencyTabType;
                return true;
            }
            if (metadata.StartsWith(ScarabPrefix, StringComparison.Ordinal))
            {
                homeTabType = FragmentTabType;
                return true;
            }
        }
        homeTabType = string.Empty;
        return false;
    }

    /// <summary>Tab types whose contents this plugin can read as custody evidence.</summary>
    public static bool IsCustodyTabType(string tabType) =>
        string.Equals(tabType, CurrencyTabType, StringComparison.Ordinal) ||
        string.Equals(tabType, FragmentTabType, StringComparison.Ordinal);

    public static bool IsSupported(string metadata) => TryResolveHomeTabType(metadata, out _);

    /// <summary>
    /// Resolves custody against the tab that is actually visible. The asset's home tab being
    /// visible yields <see cref="StashCustodyMode.VisibleCurrencyStashExact"/> (the visible stash
    /// count must rise by exactly the moved amount); any other custody tab yields
    /// <see cref="StashCustodyMode.AffinityAggregate"/> (the item leaves via affinity and only
    /// unchanged aggregate ownership is provable).
    /// </summary>
    public static bool TryResolve(string metadata, string visibleTabType, out StashCustodyMode mode)
    {
        if (!TryResolveHomeTabType(metadata, out var homeTabType) || !IsCustodyTabType(visibleTabType))
        {
            mode = default;
            return false;
        }
        mode = string.Equals(homeTabType, visibleTabType, StringComparison.Ordinal)
            ? StashCustodyMode.VisibleCurrencyStashExact
            : StashCustodyMode.AffinityAggregate;
        if (string.Equals(metadata, ReflectingMistMetadata, StringComparison.Ordinal))
            mode = StashCustodyMode.AffinityAggregate;
        return true;
    }

    public static bool TryResolve(
        string metadata,
        string visibleTabType,
        long inventoryAmount,
        long visibleStashAmount,
        long aggregateOwned,
        out StashCustodyMode mode)
    {
        if (!TryResolve(metadata, visibleTabType, out mode) || inventoryAmount < 0 ||
            visibleStashAmount < 0 || aggregateOwned < 0)
        {
            mode = default;
            return false;
        }
        try
        {
            var locallyAccounted = checked(inventoryAmount + visibleStashAmount);
            if (locallyAccounted > aggregateOwned)
            {
                mode = default;
                return false;
            }
            if (mode == StashCustodyMode.VisibleCurrencyStashExact && locallyAccounted < aggregateOwned)
                mode = StashCustodyMode.AffinityAggregate;
            return true;
        }
        catch (OverflowException)
        {
            mode = default;
            return false;
        }
    }

    /// <summary>
    /// Legacy resolution that assumes the Currency Stash is visible. Reproduces the original
    /// currency-exact / scarab-affinity table exactly.
    /// </summary>
    public static bool TryResolve(string metadata, out StashCustodyMode mode) =>
        TryResolve(metadata, CurrencyTabType, out mode);

    /// <summary>
    /// Whether a persisted intent's recorded custody mode is legitimate for its metadata. The tab
    /// that was visible at arm time is not persisted, so both modes are reachable for any supported
    /// asset; the load-bearing check stays at recovery time, where the mode is re-derived from the
    /// live visible tab and a mismatch forces an ambiguous (safe) classification.
    /// </summary>
    public static bool IsResolvableCustody(string metadata, StashCustodyMode mode) =>
        IsSupported(metadata) &&
        mode is StashCustodyMode.VisibleCurrencyStashExact or StashCustodyMode.AffinityAggregate;
}

/// <summary>
/// Which orders may sit in the canonical resting set and which must be the one active slot.
///
/// The exchange holds ten orders, and a competing sell is priced to rest, so a sweep that keeps
/// only one order live spends nearly all of its time waiting. Resting several is safe; acting on
/// several is not. The split is drawn at armed input: a status may rest exactly when it is stable,
/// carries durable identity, and holds no armed intent that a controller could still be mid-way
/// through. Everything else is active-only, so at any instant at most one order is one click away
/// from changing, and every proof in this namespace stays a statement about a single order.
///
/// <see cref="TrackedOrderStatus.Ambiguous"/> rests deliberately. Ambiguity is durable evidence that
/// is never retried and never discarded; refusing to let it rest would mean a resting order that
/// turned ambiguous while the active slot was busy had nowhere to be recorded, and unrecordable
/// canonical state is the wedge shape this plugin has already been bitten by. It rests, it keeps
/// <c>HasUnresolvedOrder</c> true, and it blocks new trading until an operator resolves it.
/// </summary>
/// <summary>
/// The status text an order row shows, and the one thing the plugin is allowed to conclude from it.
///
/// A row is evidence of being *terminal* - finished trading, safe to settle - and nothing more. It is
/// deliberately not evidence of *which* terminal it reached. The game does not keep the row text and
/// the SDK's <c>IsCanceled</c> flag in agreement: a partially filled order cancelled by the plugin
/// reports <c>completed=True canceled=False</c> while its row still reads "Order Cancelled", and
/// deriving the expected text from the flag made every second-asset collection on such an order
/// refuse with "lost exact visible status evidence". Which terminal it is, and for how much, is
/// proved by the SDK amounts and the durable intent - never by this string.
/// </summary>
public static class OrderRowStatusText
{
    public const string Listed = "Order Listed";
    public const string Cancelled = "Order Cancelled";
    public const string Completed = "Order Completed";

    /// <summary>
    /// Whether these row texts say the row has stopped trading. A live row reads
    /// <see cref="Listed"/> and is refused, which is the safety property this check exists for.
    /// </summary>
    public static bool IsTerminal(IEnumerable<string> rowTexts) =>
        rowTexts.Any(text =>
            text.Equals(Cancelled, StringComparison.OrdinalIgnoreCase) ||
            text.Equals(Completed, StringComparison.OrdinalIgnoreCase));
}

public static class TrackedOrderRestPolicy
{
    public static bool CanRest(TrackedOrderStatus status) => status is
        TrackedOrderStatus.Pending or TrackedOrderStatus.TimedOut or
        TrackedOrderStatus.CompletedUncollected or TrackedOrderStatus.CanceledUncollected or
        TrackedOrderStatus.Ambiguous;

    /// <summary>
    /// A resting order the sweep may promote into the active slot to settle. Ambiguous is excluded:
    /// it is for an operator, not for a controller.
    /// </summary>
    public static bool NeedsSettlement(TrackedOrderStatus status) => status is
        TrackedOrderStatus.TimedOut or TrackedOrderStatus.CompletedUncollected or
        TrackedOrderStatus.CanceledUncollected;

    /// <summary>
    /// Whether this order alone stops all trading. A resting <see cref="TrackedOrderStatus.Pending"/>
    /// order no longer does - that is the whole point of resting - but ambiguity always does.
    /// </summary>
    public static bool BlocksTrading(TrackedOrderState? order) =>
        order is not null && order.Status == TrackedOrderStatus.Ambiguous;

    /// <summary>
    /// Whether a live observation says this order may be left resting. Note this reads the
    /// observation, not the stored status: promotion deliberately does not change an order's status,
    /// so a rule that asked the status instead would demote a just-promoted order straight back.
    /// </summary>
    public static bool ObservationAllowsRest(LifecycleObservationKind kind) =>
        kind == LifecycleObservationKind.Pending;

    /// <summary>
    /// Whether a live observation says this order has stopped waiting and needs the active slot.
    /// </summary>
    public static bool ObservationRequiresSettlement(LifecycleObservationKind kind) => kind is
        LifecycleObservationKind.TimedOut or LifecycleObservationKind.Completed or
        LifecycleObservationKind.Canceled or LifecycleObservationKind.Ambiguous;
}

public sealed class TrackedOrderState
{
    public const int CurrentSchemaVersion = 7;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string League { get; set; } = string.Empty;
    public TrackedOrderStatus Status { get; set; }
    public int? PlayerOrderId { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public DateTimeOffset? ClickedAtUtc { get; set; }
    public string OfferedMetadata { get; set; } = string.Empty;
    public string WantedMetadata { get; set; } = string.Empty;
    public long OfferedAmount { get; set; }
    public long WantedAmount { get; set; }
    public int? GoldCost { get; set; }
    public Guid AttemptId { get; set; }
    public Guid ProbeSessionId { get; set; }
    public string CandidateSignature { get; set; } = string.Empty;
    public uint OfferedHash { get; set; }
    public uint WantedHash { get; set; }
    public List<int> BaselineOrderIds { get; set; } = [];
    public string Detail { get; set; } = string.Empty;
    public StashTransferIntentState? StashTransferIntent { get; set; }
    public DateTimeOffset? OrderCreationDateUtc { get; set; }
    public int? PlacedOfferedRatioPart { get; set; }
    public int? PlacedWantedRatioPart { get; set; }
    public DateTimeOffset? WaitStartedAtUtc { get; set; }
    public DateTimeOffset? WaitUntilUtc { get; set; }
    public DateTimeOffset? TimeoutObservedAtUtc { get; set; }
    public DateTimeOffset? LastObservedAtUtc { get; set; }
    public long? LastRemainingOfferedAmount { get; set; }
    public long? LastReceivedWantedAmount { get; set; }
    public DateTimeOffset? TerminalObservedAtUtc { get; set; }
    public long? TerminalRemainingOfferedAmount { get; set; }
    public long? TerminalReceivedWantedAmount { get; set; }
    public DateTimeOffset? LedgerCommittedAtUtc { get; set; }
    public CancelIntentState? CancelIntent { get; set; }
    public CollectionAssetIntentState? CollectionAssetIntent { get; set; }
    public bool WantedAssetCollected { get; set; }
    public bool OfferedReturnCollected { get; set; }
    public bool WantedAssetStashed { get; set; }
    public bool OfferedReturnStashed { get; set; }
    public long SettledWantedAmount { get; set; }
    public long PendingWantedBatchAmount { get; set; }
    public long SettledReturnAmount { get; set; }
    public long PendingReturnBatchAmount { get; set; }
    public long? BulkCollectionOwnedBaseline { get; set; }
    public int OfferedMaxStackSize { get; set; }
    public int WantedMaxStackSize { get; set; }

    public bool IsUnresolved => Status is TrackedOrderStatus.Armed or
        TrackedOrderStatus.Pending or TrackedOrderStatus.CompletedUncollected or TrackedOrderStatus.Ambiguous or
        TrackedOrderStatus.CollectionArmed or TrackedOrderStatus.Collected or TrackedOrderStatus.StashTransferArmed or
        TrackedOrderStatus.TimedOut or TrackedOrderStatus.CancelArmed or TrackedOrderStatus.CancelClicked or
        TrackedOrderStatus.CanceledUncollected;
}

public sealed class CollectionAssetIntentState
{
    public Guid IntentId { get; set; }
    public string Metadata { get; set; } = string.Empty;
    public long Amount { get; set; }
    public bool WantedSlot { get; set; }
    public TrackedOrderStatus TerminalStatus { get; set; }
    public long InventoryAmountBefore { get; set; }
    public long VisibleStashAmountBefore { get; set; }
    public long AggregateOwnedBefore { get; set; }
    public string NonTargetInventoryFingerprint { get; set; } = string.Empty;

    /// <summary>
    /// The volatile half, computed over every unrelated order the sweep does not own. Unchanged in
    /// meaning and in value from before the split whenever <see cref="SiblingOrderIds"/> is empty.
    /// </summary>
    public string UnrelatedOrdersFingerprint { get; set; } = string.Empty;

    /// <summary>
    /// The immutable half, computed over every unrelated order including the siblings. This is what
    /// still refuses an order that appears, vanishes, or turns into a different order mid-settlement.
    /// </summary>
    public string UnrelatedIdentityFingerprint { get; set; } = string.Empty;

    /// <summary>
    /// The resting orders the sweep owns at arm time. Only these are permitted to take a fill while
    /// this settlement runs; every other order is held to its exact amounts as before.
    /// </summary>
    public List<int> SiblingOrderIds { get; set; } = [];
    public int AreaInstanceId { get; set; }
    public DateTimeOffset ArmedAtUtc { get; set; }
}

public sealed class CancelIntentState
{
    public Guid IntentId { get; set; }
    public DateTimeOffset ArmedAtUtc { get; set; }
    public int AreaInstanceId { get; set; }
    public int PlayerOrderIdAtArm { get; set; }
    public long RemainingOfferedAtArm { get; set; }
    public long ReceivedWantedAtArm { get; set; }
    public string UnrelatedOrdersFingerprint { get; set; } = string.Empty;
    public string UnrelatedIdentityFingerprint { get; set; } = string.Empty;
    public List<int> SiblingOrderIds { get; set; } = [];
    public DateTimeOffset? ConfirmationOpenedAtUtc { get; set; }
    public DateTimeOffset? ConfirmClickAttemptedAtUtc { get; set; }
}

public sealed class StashTransferIntentState
{
    public StashCustodyMode StashCustodyMode { get; set; }
    public string Metadata { get; set; } = string.Empty;
    public long Amount { get; set; }
    public long InventoryAmountBefore { get; set; }
    public long VisibleStashAmountBefore { get; set; }
    public long AggregateOwnedBefore { get; set; }
    public string NonTargetInventoryFingerprint { get; set; } = string.Empty;
    public int AreaInstanceId { get; set; }
    public DateTimeOffset ArmedAtUtc { get; set; }
}

public static class CollectionAbortEvidence
{
    public static bool HasCollectionInputBoundary(TrackedOrderState? tracked) =>
        tracked is not null &&
        (tracked.Status == TrackedOrderStatus.CollectionArmed ||
         tracked.Status == TrackedOrderStatus.Ambiguous && tracked.CollectionAssetIntent is not null);

    public static bool HasStashInputBoundary(TrackedOrderState? tracked) =>
        tracked is not null &&
        (tracked.Status == TrackedOrderStatus.StashTransferArmed ||
         tracked.Status == TrackedOrderStatus.Ambiguous && tracked.StashTransferIntent is not null);

    public static bool CanRecoverUntouchedCanceledTerminal(TrackedOrderState? tracked) =>
        tracked is
        {
            Status: TrackedOrderStatus.Ambiguous,
            PlayerOrderId: > 0,
            TerminalObservedAtUtc: not null,
            TerminalRemainingOfferedAmount: not null,
            TerminalReceivedWantedAmount: not null,
            LedgerCommittedAtUtc: not null,
            CancelIntent: null,
            CollectionAssetIntent: null,
            StashTransferIntent: null,
            WantedAssetCollected: false,
            OfferedReturnCollected: false,
            WantedAssetStashed: false,
            OfferedReturnStashed: false,
            SettledWantedAmount: 0,
            PendingWantedBatchAmount: 0,
            SettledReturnAmount: 0,
            PendingReturnBatchAmount: 0,
        } &&
        TrackedOrderLifecycle.HasDurableIdentity(tracked) &&
        tracked.TerminalRemainingOfferedAmount.GetValueOrDefault() +
            tracked.TerminalReceivedWantedAmount.GetValueOrDefault() > 0;
}
