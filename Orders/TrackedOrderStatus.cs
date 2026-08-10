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
        return true;
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

public sealed class TrackedOrderState
{
    public const int CurrentSchemaVersion = 6;

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
    public string UnrelatedOrdersFingerprint { get; set; } = string.Empty;
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
