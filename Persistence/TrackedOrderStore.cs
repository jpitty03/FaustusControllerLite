using FaustusControllerLite.Orders;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FaustusControllerLite.Persistence;

public sealed class TrackedOrderStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
    private static readonly JsonSerializerOptions AuditJsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _directory;

    public TrackedOrderStore(string directory)
    {
        _directory = directory;
    }

    public TrackedOrderState? Load(string league)
    {
        var path = StatePath(league);
        if (!File.Exists(path))
        {
            return null;
        }

        var state = JsonSerializer.Deserialize<TrackedOrderState>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException("Tracked order state was empty.");
        var migrated = state.SchemaVersion is 1 or 2 or 3 or 4;
        if (migrated)
        {
            TrackedOrderLifecycle.MigrateLegacyAssetProgress(state);
            TrackedOrderLifecycle.MigrateLegacyAssetAmounts(state);
            state.SchemaVersion = TrackedOrderState.CurrentSchemaVersion;
        }
        Validate(state, league);
        if (migrated) Save(state);
        return state;
    }

    public void Save(TrackedOrderState state)
    {
        Validate(state, state.League);
        Directory.CreateDirectory(_directory);
        var path = StatePath(state.League);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state, JsonOptions));
        File.Move(temporary, path, overwrite: true);
    }

    public void AppendAudit(TrackedOrderState state, string eventType)
    {
        Directory.CreateDirectory(_directory);
        var audit = new
        {
            SchemaVersion = 1,
            EventType = eventType,
            state.League,
            state.Status,
            state.PlayerOrderId,
            state.UpdatedAtUtc,
            state.ClickedAtUtc,
            state.OfferedMetadata,
            state.WantedMetadata,
            state.OfferedAmount,
            state.WantedAmount,
            state.GoldCost,
            state.Detail,
            state.StashTransferIntent,
            state.CancelIntent,
            state.CollectionAssetIntent,
            state.WantedAssetCollected,
            state.OfferedReturnCollected,
            state.WantedAssetStashed,
            state.OfferedReturnStashed,
            state.SettledWantedAmount,
            state.PendingWantedBatchAmount,
            state.SettledReturnAmount,
            state.PendingReturnBatchAmount,
            state.OfferedMaxStackSize,
            state.WantedMaxStackSize
        };
        File.AppendAllText(
            Path.Combine(_directory, $"execution-audit-{Sanitize(state.League)}.jsonl"),
            JsonSerializer.Serialize(audit, AuditJsonOptions) + Environment.NewLine);
    }

    private string StatePath(string league) =>
        Path.Combine(_directory, $"tracked-order-{Sanitize(league)}.json");

    private static void Validate(TrackedOrderState state, string league)
    {
        if (state.SchemaVersion != TrackedOrderState.CurrentSchemaVersion ||
            string.IsNullOrWhiteSpace(state.League) ||
            !string.Equals(state.League, league, StringComparison.Ordinal) ||
            state.Status == TrackedOrderStatus.None ||
            state.UpdatedAtUtc == default ||
            state.AttemptId == Guid.Empty || state.ProbeSessionId == Guid.Empty ||
            string.IsNullOrWhiteSpace(state.CandidateSignature) ||
            string.IsNullOrWhiteSpace(state.OfferedMetadata) ||
            string.IsNullOrWhiteSpace(state.WantedMetadata) ||
            state.OfferedMetadata == state.WantedMetadata ||
            state.OfferedAmount <= 0 || state.WantedAmount <= 0 ||
            state.PlayerOrderId is <= 0 ||
            state.BaselineOrderIds.Any(id => id <= 0) ||
            state.BaselineOrderIds.Distinct().Count() != state.BaselineOrderIds.Count ||
            state.Status is TrackedOrderStatus.Pending or TrackedOrderStatus.CompletedUncollected or
                TrackedOrderStatus.CollectionArmed or TrackedOrderStatus.Collected or
                TrackedOrderStatus.StashTransferArmed or TrackedOrderStatus.Stashed or
                TrackedOrderStatus.TimedOut or TrackedOrderStatus.CancelArmed or
                TrackedOrderStatus.CancelClicked or TrackedOrderStatus.CanceledUncollected &&
            state.PlayerOrderId is null)
        {
            throw new InvalidDataException("Tracked order state failed schema or economics validation.");
        }
        if (!TrackedOrderLifecycle.AssetProgressIsValid(state))
        {
            throw new InvalidDataException("Tracked settlement-asset progress was internally inconsistent.");
        }
        if (state.Status == TrackedOrderStatus.Stashed &&
            (state.TerminalObservedAtUtc is null || state.LedgerCommittedAtUtc is null ||
             state.TerminalRemainingOfferedAmount is not { } stashedRemaining ||
             state.TerminalReceivedWantedAmount is not { } stashedReceived ||
             TrackedOrderLifecycle.CreateSettlementAssets(state, stashedRemaining, stashedReceived).Count == 0))
        {
            throw new InvalidDataException("Resolved stashed state lacked terminal settlement evidence.");
        }
        if (state.Status is TrackedOrderStatus.Pending or TrackedOrderStatus.TimedOut or
                TrackedOrderStatus.CancelArmed or TrackedOrderStatus.CancelClicked or
                TrackedOrderStatus.CanceledUncollected &&
            !TrackedOrderLifecycle.HasDurableIdentity(state))
        {
            throw new InvalidDataException("Tracked lifecycle state lacked durable identity.");
        }
        if (state.Status == TrackedOrderStatus.StashTransferArmed &&
            (state.StashTransferIntent is null || state.StashTransferIntent.Amount <= 0 ||
             string.IsNullOrWhiteSpace(state.StashTransferIntent.NonTargetInventoryFingerprint) ||
             state.TerminalRemainingOfferedAmount is not { } stashRemaining ||
             state.TerminalReceivedWantedAmount is not { } stashReceived ||
             !TrackedOrderLifecycle.CreateSettlementAssets(state, stashRemaining, stashReceived)
                 .Any(asset => asset.Metadata == state.StashTransferIntent.Metadata &&
                     asset.Amount == state.StashTransferIntent.Amount &&
                     state.StashTransferIntent.InventoryAmountBefore == asset.Amount)))
        {
            throw new InvalidDataException("Tracked stash-transfer intent was invalid.");
        }
        if (state.Status is TrackedOrderStatus.CollectionArmed or TrackedOrderStatus.Ambiguous &&
            state.CollectionAssetIntent is not null &&
            (state.CollectionAssetIntent is null || state.CollectionAssetIntent.IntentId == Guid.Empty ||
             state.CollectionAssetIntent.TerminalStatus is not TrackedOrderStatus.CompletedUncollected and
                 not TrackedOrderStatus.CanceledUncollected ||
             state.TerminalRemainingOfferedAmount is not { } collectionRemaining ||
             state.TerminalReceivedWantedAmount is not { } collectionReceived ||
             !TrackedOrderLifecycle.CreateSettlementAssets(state, collectionRemaining, collectionReceived).Any(asset =>
                 asset.Metadata == state.CollectionAssetIntent.Metadata &&
                 asset.Amount == state.CollectionAssetIntent.Amount &&
                 asset.WantedSlot == state.CollectionAssetIntent.WantedSlot)))
        {
            throw new InvalidDataException("Canceled return collection intent was invalid.");
        }
        if (state.Status is TrackedOrderStatus.CancelArmed or TrackedOrderStatus.CancelClicked &&
            (state.CancelIntent is null || state.CancelIntent.IntentId == Guid.Empty ||
             state.CancelIntent.AreaInstanceId == 0 || state.CancelIntent.PlayerOrderIdAtArm <= 0 ||
             state.CancelIntent.RemainingOfferedAtArm <= 0 ||
             string.IsNullOrWhiteSpace(state.CancelIntent.UnrelatedOrdersFingerprint) ||
             state.Status == TrackedOrderStatus.CancelClicked &&
                 (state.CancelIntent.ConfirmationOpenedAtUtc is null || state.CancelIntent.ConfirmClickAttemptedAtUtc is null)))
        {
            throw new InvalidDataException("Tracked cancellation intent was invalid.");
        }
        if (state.Status is TrackedOrderStatus.CompletedUncollected or TrackedOrderStatus.CanceledUncollected &&
            (state.TerminalObservedAtUtc is null || state.TerminalRemainingOfferedAmount is < 0 ||
             state.TerminalRemainingOfferedAmount > state.OfferedAmount ||
             state.TerminalReceivedWantedAmount is < 0 ||
             state.TerminalReceivedWantedAmount > state.WantedAmount || state.LedgerCommittedAtUtc is null))
        {
            throw new InvalidDataException("Tracked terminal evidence was incomplete.");
        }
    }

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
    }
}
