using FaustusControllerLite.Domain;
using FaustusControllerLite.Orders;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FaustusControllerLite.Persistence;

public sealed class BankrollStore
{
    private readonly string _directory;

    public BankrollStore(string directory)
    {
        _directory = directory;
    }

    public BankrollState? Load(string league)
    {
        var path = GetStatePath(league);
        if (!File.Exists(path))
        {
            return null;
        }

        var json = File.ReadAllText(path);
        var root = JObject.Parse(json);
        var schemaVersion = root.Value<int?>(nameof(BankrollState.SchemaVersion))
            ?? throw new InvalidDataException("Bankroll schema version was missing.");
        var state = JsonConvert.DeserializeObject<BankrollState>(json)
            ?? throw new InvalidDataException("Bankroll state was empty.");
        var migrated = schemaVersion is 1 or 2 or 3;
        var trackedMigrated = state.TrackedOrder?.SchemaVersion is 1 or 2 or 3;
        if (migrated)
        {
            state.SchemaVersion = BankrollState.CurrentSchemaVersion;
        }
        if (trackedMigrated && state.TrackedOrder is { } migratedTracked)
        {
            TrackedOrderLifecycle.MigrateLegacyAssetProgress(migratedTracked);
            migratedTracked.SchemaVersion = TrackedOrderState.CurrentSchemaVersion;
            state.HasUnresolvedOrder = migratedTracked.IsUnresolved;
        }

        if (state.SchemaVersion != BankrollState.CurrentSchemaVersion ||
            !state.IsInitialized ||
            !string.Equals(state.League, league, StringComparison.Ordinal) ||
            state.SeededChaos < 0 || state.SeededDivine < 0 ||
            state.AvailableChaos < 0 || state.AvailableDivine < 0 ||
            state.ReservedChaos < 0 || state.ReservedDivine < 0 ||
            state.CompletedUncollectedChaos < 0 || state.CompletedUncollectedDivine < 0 ||
            state.AvailableTarget < 0 || state.ReservedTarget < 0 || state.CompletedUncollectedTarget < 0 ||
            state.HasUnresolvedOrder != (state.TrackedOrder?.IsUnresolved == true))
        {
            throw new InvalidDataException("Bankroll state failed schema or value validation.");
        }
        if (state.TrackedOrder is { } tracked &&
            (tracked.SchemaVersion != TrackedOrderState.CurrentSchemaVersion ||
             tracked.League != league || tracked.Status == TrackedOrderStatus.None ||
             tracked.AttemptId == Guid.Empty || tracked.ProbeSessionId == Guid.Empty ||
             string.IsNullOrWhiteSpace(tracked.CandidateSignature) ||
             string.IsNullOrWhiteSpace(tracked.OfferedMetadata) ||
             string.IsNullOrWhiteSpace(tracked.WantedMetadata) ||
             tracked.OfferedMetadata == tracked.WantedMetadata ||
             tracked.OfferedAmount <= 0 || tracked.WantedAmount <= 0 ||
             tracked.ClickedAtUtc is null ||
             tracked.BaselineOrderIds.Any(id => id <= 0) ||
             tracked.BaselineOrderIds.Distinct().Count() != tracked.BaselineOrderIds.Count ||
              tracked.Status is TrackedOrderStatus.Pending or TrackedOrderStatus.CompletedUncollected or
                  TrackedOrderStatus.CollectionArmed or TrackedOrderStatus.Collected or
                  TrackedOrderStatus.StashTransferArmed or TrackedOrderStatus.Stashed or
                  TrackedOrderStatus.TimedOut or TrackedOrderStatus.CancelArmed or
                  TrackedOrderStatus.CancelClicked or TrackedOrderStatus.CanceledUncollected &&
               tracked.PlayerOrderId is not > 0))
        {
            throw new InvalidDataException("Tracked order inside bankroll failed transition validation.");
        }
        if (state.TrackedOrder is { } lifecycleTracked && RequiresLifecycleIdentity(lifecycleTracked.Status) &&
            !TrackedOrderLifecycle.HasDurableIdentity(lifecycleTracked))
        {
            throw new InvalidDataException("Unresolved lifecycle state lacked durable creation/ratio/deadline identity.");
        }
        if (state.TrackedOrder is { } progressTracked && !TrackedOrderLifecycle.AssetProgressIsValid(progressTracked))
        {
            throw new InvalidDataException("Settlement-asset progress was internally inconsistent.");
        }
        if (state.TrackedOrder is { Status: TrackedOrderStatus.Stashed } stashedState &&
            (!HasCompleteTerminalEvidence(stashedState) ||
             TrackedOrderLifecycle.CreateSettlementAssets(stashedState,
                 stashedState.TerminalRemainingOfferedAmount!.Value,
                 stashedState.TerminalReceivedWantedAmount!.Value).Count == 0))
        {
            throw new InvalidDataException("Resolved stashed state lacked terminal settlement evidence.");
        }
        if (state.TrackedOrder is { Status: TrackedOrderStatus.StashTransferArmed } armed &&
            (!IsValidStashTransferIntent(armed.StashTransferIntent) ||
             !IntentMatchesSettlementAsset(armed, armed.StashTransferIntent!)))
        {
            throw new InvalidDataException("Stash-transfer-armed state lacked durable recovery evidence.");
        }
        if (state.TrackedOrder is { Status: TrackedOrderStatus.CollectionArmed or TrackedOrderStatus.Ambiguous,
                CollectionAssetIntent: not null } assetCollection &&
            !IsValidCollectionAssetIntent(assetCollection.CollectionAssetIntent, assetCollection))
        {
            throw new InvalidDataException("Canceled return collection lacked exact durable asset intent.");
        }
        if (state.TrackedOrder is { Status: TrackedOrderStatus.CancelArmed or TrackedOrderStatus.CancelClicked } cancelState &&
            !IsValidCancelIntent(cancelState.CancelIntent, cancelState))
        {
            throw new InvalidDataException("Cancellation state lacked durable exact intent evidence.");
        }
        if (state.TrackedOrder is { Status: TrackedOrderStatus.CompletedUncollected or TrackedOrderStatus.CanceledUncollected } terminalState &&
            !HasCompleteTerminalEvidence(terminalState))
        {
            throw new InvalidDataException("Terminal tracked state lacked complete amounts and ledger commit evidence.");
        }

        if (migrated || trackedMigrated)
        {
            Save(state);
        }

        return state;
    }

    private static bool IsValidStashTransferIntent(StashTransferIntentState? intent) =>
        intent is not null && !string.IsNullOrWhiteSpace(intent.Metadata) && intent.Amount > 0 &&
        intent.InventoryAmountBefore == intent.Amount && intent.VisibleStashAmountBefore >= 0 &&
        intent.AggregateOwnedBefore >= intent.Amount &&
        !string.IsNullOrWhiteSpace(intent.NonTargetInventoryFingerprint) &&
        intent.AreaInstanceId != 0 && intent.ArmedAtUtc != default;

    private static bool RequiresLifecycleIdentity(TrackedOrderStatus status) =>
        status is TrackedOrderStatus.Pending or TrackedOrderStatus.TimedOut or
            TrackedOrderStatus.CancelArmed or TrackedOrderStatus.CancelClicked or
            TrackedOrderStatus.CanceledUncollected;

    private static bool IsValidCancelIntent(CancelIntentState? intent, TrackedOrderState tracked) =>
        intent is not null && intent.IntentId != Guid.Empty && intent.ArmedAtUtc != default &&
        intent.AreaInstanceId != 0 && intent.PlayerOrderIdAtArm > 0 &&
        intent.RemainingOfferedAtArm is > 0 and <= int.MaxValue &&
        intent.RemainingOfferedAtArm <= tracked.OfferedAmount && intent.ReceivedWantedAtArm >= 0 &&
        intent.ReceivedWantedAtArm <= tracked.WantedAmount &&
        !string.IsNullOrWhiteSpace(intent.UnrelatedOrdersFingerprint) &&
        (tracked.Status != TrackedOrderStatus.CancelClicked ||
         intent.ConfirmationOpenedAtUtc is not null && intent.ConfirmClickAttemptedAtUtc is not null);

    private static bool HasCompleteTerminalEvidence(TrackedOrderState tracked) =>
        tracked.TerminalObservedAtUtc is not null && tracked.TerminalRemainingOfferedAmount is >= 0 &&
        tracked.TerminalRemainingOfferedAmount <= tracked.OfferedAmount &&
        tracked.TerminalReceivedWantedAmount is >= 0 &&
        tracked.TerminalReceivedWantedAmount <= tracked.WantedAmount && tracked.LedgerCommittedAtUtc is not null;

    private static bool IntentMatchesSettlementAsset(TrackedOrderState tracked, StashTransferIntentState intent) =>
        tracked.TerminalRemainingOfferedAmount is { } remaining &&
        tracked.TerminalReceivedWantedAmount is { } received &&
        TrackedOrderLifecycle.CreateSettlementAssets(tracked, remaining, received)
            .Any(asset => asset.Metadata == intent.Metadata && asset.Amount == intent.Amount &&
                intent.InventoryAmountBefore == intent.Amount);

    private static bool IsValidCollectionAssetIntent(CollectionAssetIntentState? intent, TrackedOrderState tracked) =>
        intent is not null && intent.IntentId != Guid.Empty &&
        intent.TerminalStatus is TrackedOrderStatus.CompletedUncollected or TrackedOrderStatus.CanceledUncollected &&
        tracked.TerminalRemainingOfferedAmount is { } remaining && tracked.TerminalReceivedWantedAmount is { } received &&
        TrackedOrderLifecycle.CreateSettlementAssets(tracked, remaining, received).Any(asset =>
            asset.Metadata == intent.Metadata && asset.Amount == intent.Amount && asset.WantedSlot == intent.WantedSlot) &&
        intent.InventoryAmountBefore >= 0 && intent.VisibleStashAmountBefore >= 0 &&
        intent.AggregateOwnedBefore >= 0 &&
        !string.IsNullOrWhiteSpace(intent.NonTargetInventoryFingerprint) &&
        !string.IsNullOrWhiteSpace(intent.UnrelatedOrdersFingerprint) && intent.AreaInstanceId != 0 &&
        intent.ArmedAtUtc != default;

    public void Save(BankrollState state)
    {
        Directory.CreateDirectory(_directory);
        var path = GetStatePath(state.League);
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, JsonConvert.SerializeObject(state, Formatting.Indented));
        File.Move(temporaryPath, path, true);
    }

    public void AppendAudit(BankrollAuditEvent auditEvent)
    {
        Directory.CreateDirectory(_directory);
        File.AppendAllText(
            GetAuditPath(auditEvent.League),
            JsonConvert.SerializeObject(auditEvent, Formatting.None) + Environment.NewLine);
    }

    private string GetStatePath(string league) => Path.Combine(_directory, $"bankroll-{Sanitize(league)}.json");
    private string GetAuditPath(string league) => Path.Combine(_directory, $"execution-audit-{Sanitize(league)}.jsonl");

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
    }
}

public sealed record BankrollAuditEvent(
    int SchemaVersion,
    string EventType,
    string League,
    DateTimeOffset OccurredAtUtc,
    long Chaos,
    long Divine)
{
    public static BankrollAuditEvent Seeded(BankrollState state) => new(
        1,
        "BankrollSeededOrReset",
        state.League,
        state.UpdatedAtUtc,
        state.AvailableChaos,
        state.AvailableDivine);
}
