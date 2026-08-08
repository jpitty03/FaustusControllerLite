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
        var root = JObject.Load(
            new JsonTextReader(new StringReader(json)),
            new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
        var schemaVersion = root.Value<int?>(nameof(BankrollState.SchemaVersion))
            ?? throw new InvalidDataException("Bankroll schema version was missing.");
        var state = JsonConvert.DeserializeObject<BankrollState>(json)
            ?? throw new InvalidDataException("Bankroll state was empty.");
        var migrated = schemaVersion is 1 or 2 or 3 or 4 or 5;
        var trackedMigrated = state.TrackedOrder?.SchemaVersion is 1 or 2 or 3 or 4 or 5;
        var workflowMigrated = state.Workflow?.SchemaVersion == 1;
        if (migrated)
        {
            var targetMetadata = root.Value<string>("TargetMetadata");
            var availableTarget = root.Value<long?>("AvailableTarget") ?? 0;
            var reservedTarget = root.Value<long?>("ReservedTarget") ?? 0;
            var completedTarget = root.Value<long?>("CompletedUncollectedTarget") ?? 0;
            if (string.IsNullOrWhiteSpace(targetMetadata) &&
                (availableTarget != 0 || reservedTarget != 0 || completedTarget != 0))
            {
                throw new InvalidDataException("Legacy target balances had no exact metadata identity.");
            }
            if (!string.IsNullOrWhiteSpace(targetMetadata))
            {
                if (!state.NonCoreBalances.TryAdd(targetMetadata, new NonCoreBalanceState
                {
                    Available = availableTarget,
                    Reserved = reservedTarget,
                    CompletedUncollected = completedTarget,
                }))
                {
                    throw new InvalidDataException("Legacy target metadata duplicated a non-core balance entry.");
                }
            }
            state.SchemaVersion = BankrollState.CurrentSchemaVersion;
        }
        if (trackedMigrated && state.TrackedOrder is { } migratedTracked)
        {
            if (migratedTracked.StashTransferIntent is { } legacyStashIntent)
                legacyStashIntent.StashCustodyMode = StashCustodyMode.VisibleCurrencyStashExact;
            TrackedOrderLifecycle.MigrateLegacyAssetProgress(migratedTracked);
            TrackedOrderLifecycle.MigrateLegacyAssetAmounts(migratedTracked);
            migratedTracked.SchemaVersion = TrackedOrderState.CurrentSchemaVersion;
            state.HasUnresolvedOrder = migratedTracked.IsUnresolved;
        }
        if (workflowMigrated && state.Workflow is { } migratedWorkflow)
        {
            var legacyFingerprint = migratedWorkflow.PlanFingerprint;
            if (!WorkflowCoordinator.LegacyVersionOneFingerprintMatches(migratedWorkflow))
            {
                throw new InvalidDataException("Schema-one workflow failed legacy fingerprint authentication.");
            }
            if (migratedWorkflow.Phase == WorkflowExecutionPhase.LegActive)
            {
                if (state.TrackedOrder is not { } activeTracked ||
                    !WorkflowCoordinator.LegacyActiveTrackedMatches(
                        migratedWorkflow, activeTracked, legacyFingerprint))
                {
                    throw new InvalidDataException("Schema-one active workflow did not match its tracked order.");
                }
                migratedWorkflow.CurrentProbeSessionId = activeTracked.ProbeSessionId;
            }
            else if (migratedWorkflow.CurrentProbeSessionId == Guid.Empty)
            {
                migratedWorkflow.CurrentProbeSessionId = migratedWorkflow.OriginProbeSessionId;
            }
            migratedWorkflow.SchemaVersion = WorkflowExecutionState.CurrentSchemaVersion;
            if (string.IsNullOrWhiteSpace(migratedWorkflow.TerminalChaosMetadata) && migratedWorkflow.Legs.Count > 0)
                migratedWorkflow.TerminalChaosMetadata = migratedWorkflow.Legs[^1].ToMetadata;
            migratedWorkflow.PlanFingerprint = WorkflowCoordinator.ComputeFingerprint(migratedWorkflow);
            if (migratedWorkflow.Phase == WorkflowExecutionPhase.LegActive && state.TrackedOrder is { } migratedActive)
                migratedActive.CandidateSignature = migratedWorkflow.PlanFingerprint;
        }

        if (state.SchemaVersion != BankrollState.CurrentSchemaVersion ||
            !state.IsInitialized ||
            !string.Equals(state.League, league, StringComparison.Ordinal) ||
            state.SeededChaos < 0 || state.SeededDivine < 0 ||
            state.AvailableChaos < 0 || state.AvailableDivine < 0 ||
            state.ReservedChaos < 0 || state.ReservedDivine < 0 ||
            state.CompletedUncollectedChaos < 0 || state.CompletedUncollectedDivine < 0 ||
            !NonCoreBalancesAreValid(state.NonCoreBalances) ||
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
        if (state.TrackedOrder is { StashTransferIntent: not null } armed &&
            (armed.Status is not TrackedOrderStatus.StashTransferArmed and not TrackedOrderStatus.Ambiguous ||
             !IsValidStashTransferIntent(armed.StashTransferIntent) ||
             !IntentMatchesSettlementAsset(armed, armed.StashTransferIntent!)))
        {
            throw new InvalidDataException("Stash-transfer-armed state lacked durable recovery evidence.");
        }
        if (state.TrackedOrder is { Status: TrackedOrderStatus.StashTransferArmed, StashTransferIntent: null })
            throw new InvalidDataException("Stash-transfer-armed state lacked durable recovery evidence.");
        if (state.TrackedOrder is { StashTransferIntent: not null, CollectionAssetIntent: not null })
            throw new InvalidDataException("Collection and stash-transfer intents cannot coexist.");
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
        ValidateReservationConsistency(state);
        if (state.Workflow is { } workflow)
        {
            if (workflow.League != league)
            {
                throw new InvalidDataException("Workflow league did not match canonical bankroll league.");
            }
            WorkflowCoordinator.Validate(workflow, state.TrackedOrder);
        }

        if (migrated || trackedMigrated || workflowMigrated)
        {
            Save(state);
        }

        return state;
    }

    private static bool IsValidStashTransferIntent(StashTransferIntentState? intent)
    {
        if (intent is null || !StashCustodyPolicy.TryResolve(intent.Metadata, out var expectedMode) ||
            intent.StashCustodyMode != expectedMode || intent.Amount <= 0 ||
            intent.InventoryAmountBefore != intent.Amount || intent.VisibleStashAmountBefore < 0 ||
            !string.IsNullOrWhiteSpace(intent.Metadata) == false ||
            string.IsNullOrWhiteSpace(intent.NonTargetInventoryFingerprint) ||
            intent.AreaInstanceId == 0 || intent.ArmedAtUtc == default)
        {
            return false;
        }
        try
        {
            return intent.AggregateOwnedBefore >= checked(intent.InventoryAmountBefore + intent.VisibleStashAmountBefore);
        }
        catch (OverflowException)
        {
            return false;
        }
    }

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
        intent.InventoryAmountBefore == intent.Amount &&
        (intent.Metadata == tracked.WantedMetadata && intent.Amount == tracked.PendingWantedBatchAmount ||
         intent.Metadata == tracked.OfferedMetadata && intent.Amount == tracked.PendingReturnBatchAmount);

    private static bool IsValidCollectionAssetIntent(CollectionAssetIntentState? intent, TrackedOrderState tracked) =>
        intent is not null && intent.IntentId != Guid.Empty &&
        intent.TerminalStatus is TrackedOrderStatus.CompletedUncollected or TrackedOrderStatus.CanceledUncollected &&
        tracked.TerminalRemainingOfferedAmount is not null && tracked.TerminalReceivedWantedAmount is not null &&
        intent.Amount > 0 &&
        (intent.WantedSlot
            ? intent.Metadata == tracked.WantedMetadata &&
              intent.Amount <= TrackedOrderLifecycle.RemainingWantedToCollect(tracked)
            : intent.Metadata == tracked.OfferedMetadata &&
              intent.Amount <= TrackedOrderLifecycle.RemainingReturnToCollect(tracked)) &&
        intent.InventoryAmountBefore >= 0 && intent.VisibleStashAmountBefore >= 0 &&
        intent.AggregateOwnedBefore >= 0 &&
        !string.IsNullOrWhiteSpace(intent.NonTargetInventoryFingerprint) &&
        !string.IsNullOrWhiteSpace(intent.UnrelatedOrdersFingerprint) && intent.AreaInstanceId != 0 &&
        intent.ArmedAtUtc != default;

    public void Save(BankrollState state)
    {
        if (state.SchemaVersion != BankrollState.CurrentSchemaVersion || !state.IsInitialized ||
            string.IsNullOrWhiteSpace(state.League) || state.SeededChaos < 0 || state.SeededDivine < 0 ||
            state.AvailableChaos < 0 || state.AvailableDivine < 0 || state.ReservedChaos < 0 ||
            state.ReservedDivine < 0 || state.CompletedUncollectedChaos < 0 ||
            state.CompletedUncollectedDivine < 0 || !NonCoreBalancesAreValid(state.NonCoreBalances) ||
            state.HasUnresolvedOrder != (state.TrackedOrder?.IsUnresolved == true))
        {
            throw new InvalidDataException("Bankroll state failed save-time schema or value validation.");
        }
        ValidateReservationConsistency(state);
        if (state.Workflow is { } workflow)
        {
            if (workflow.League != state.League)
            {
                throw new InvalidDataException("Workflow league did not match canonical bankroll league.");
            }
            WorkflowCoordinator.Validate(workflow, state.TrackedOrder);
        }
        ValidateTrackedForSave(state.TrackedOrder, state.League);
        Directory.CreateDirectory(_directory);
        var path = GetStatePath(state.League);
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, JsonConvert.SerializeObject(state, Formatting.Indented));
        File.Move(temporaryPath, path, true);
    }

    private static void ValidateReservationConsistency(BankrollState state)
    {
        var tracked = state.TrackedOrder;
        var reservationRequired = tracked?.IsUnresolved == true && tracked.LedgerCommittedAtUtc is null &&
            tracked.Status is TrackedOrderStatus.Armed or TrackedOrderStatus.Pending or TrackedOrderStatus.TimedOut or
                TrackedOrderStatus.CancelArmed or TrackedOrderStatus.CancelClicked or TrackedOrderStatus.Ambiguous;
        var expectedChaos = reservationRequired && tracked!.OfferedMetadata == BankrollState.ChaosMetadata
            ? tracked.OfferedAmount : 0;
        var expectedDivine = reservationRequired && tracked!.OfferedMetadata == BankrollState.DivineMetadata
            ? tracked.OfferedAmount : 0;
        if (state.ReservedChaos != expectedChaos || state.ReservedDivine != expectedDivine)
        {
            throw new InvalidDataException("Canonical reservations did not exactly match the unresolved tracked order.");
        }
        foreach (var pair in state.NonCoreBalances)
        {
            var expected = reservationRequired && tracked!.OfferedMetadata == pair.Key ? tracked.OfferedAmount : 0;
            if (pair.Value.Reserved != expected)
                throw new InvalidDataException("Canonical non-core reservation did not match the exact unresolved order.");
        }
        if (reservationRequired && expectedChaos == 0 && expectedDivine == 0 &&
            !state.NonCoreBalances.ContainsKey(tracked!.OfferedMetadata))
            throw new InvalidDataException("Unresolved order had no exact offered-metadata reservation bucket.");
    }

    private static void ValidateTrackedForSave(TrackedOrderState? tracked, string league)
    {
        if (tracked is null) return;
        if (tracked.SchemaVersion != TrackedOrderState.CurrentSchemaVersion || tracked.League != league ||
            tracked.Status == TrackedOrderStatus.None || tracked.AttemptId == Guid.Empty ||
            tracked.ProbeSessionId == Guid.Empty || string.IsNullOrWhiteSpace(tracked.CandidateSignature) ||
            string.IsNullOrWhiteSpace(tracked.OfferedMetadata) || string.IsNullOrWhiteSpace(tracked.WantedMetadata) ||
            tracked.OfferedMetadata == tracked.WantedMetadata || tracked.OfferedAmount <= 0 || tracked.WantedAmount <= 0 ||
            tracked.ClickedAtUtc is null || tracked.BaselineOrderIds.Any(id => id <= 0) ||
            tracked.BaselineOrderIds.Distinct().Count() != tracked.BaselineOrderIds.Count ||
            (tracked.Status is TrackedOrderStatus.Pending or TrackedOrderStatus.CompletedUncollected or
                TrackedOrderStatus.CollectionArmed or TrackedOrderStatus.Collected or
                TrackedOrderStatus.StashTransferArmed or TrackedOrderStatus.Stashed or
                TrackedOrderStatus.TimedOut or TrackedOrderStatus.CancelArmed or
                TrackedOrderStatus.CancelClicked or TrackedOrderStatus.CanceledUncollected) &&
                tracked.PlayerOrderId is not > 0)
        {
            throw new InvalidDataException("Tracked order failed save-time transition validation.");
        }
        if (RequiresLifecycleIdentity(tracked.Status) && !TrackedOrderLifecycle.HasDurableIdentity(tracked))
            throw new InvalidDataException("Tracked order lacked durable lifecycle identity.");
        if (!TrackedOrderLifecycle.AssetProgressIsValid(tracked))
            throw new InvalidDataException("Tracked order settlement progress was inconsistent.");
        if (tracked.Status == TrackedOrderStatus.Stashed &&
            (!HasCompleteTerminalEvidence(tracked) ||
             TrackedOrderLifecycle.CreateSettlementAssets(tracked,
                 tracked.TerminalRemainingOfferedAmount!.Value,
                 tracked.TerminalReceivedWantedAmount!.Value).Count == 0))
            throw new InvalidDataException("Stashed tracked order lacked terminal settlement evidence.");
        if (tracked.StashTransferIntent is { } stashIntent &&
            (tracked.Status is not TrackedOrderStatus.StashTransferArmed and not TrackedOrderStatus.Ambiguous ||
             !IsValidStashTransferIntent(stashIntent) || !IntentMatchesSettlementAsset(tracked, stashIntent)))
            throw new InvalidDataException("Stash-transfer intent failed save-time validation.");
        if (tracked.Status == TrackedOrderStatus.StashTransferArmed && tracked.StashTransferIntent is null)
            throw new InvalidDataException("Stash-transfer intent failed save-time validation.");
        if (tracked.StashTransferIntent is not null && tracked.CollectionAssetIntent is not null)
            throw new InvalidDataException("Collection and stash-transfer intents cannot coexist.");
        if ((tracked.Status is TrackedOrderStatus.CollectionArmed or TrackedOrderStatus.Ambiguous) &&
            tracked.CollectionAssetIntent is not null &&
            !IsValidCollectionAssetIntent(tracked.CollectionAssetIntent, tracked))
            throw new InvalidDataException("Collection-asset intent failed save-time validation.");
        if ((tracked.Status is TrackedOrderStatus.CancelArmed or TrackedOrderStatus.CancelClicked) &&
            !IsValidCancelIntent(tracked.CancelIntent, tracked))
            throw new InvalidDataException("Cancellation intent failed save-time validation.");
        if ((tracked.Status is TrackedOrderStatus.CompletedUncollected or TrackedOrderStatus.CanceledUncollected) &&
            !HasCompleteTerminalEvidence(tracked))
            throw new InvalidDataException("Terminal tracked order lacked complete save-time evidence.");
    }

    private static bool NonCoreBalancesAreValid(Dictionary<string, NonCoreBalanceState>? balances)
    {
        if (balances is null) return false;
        foreach (var pair in balances)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || pair.Key == BankrollState.ChaosMetadata ||
                pair.Key == BankrollState.DivineMetadata || pair.Value is null ||
                pair.Value.Available < 0 || pair.Value.Reserved < 0 || pair.Value.CompletedUncollected < 0)
            {
                return false;
            }
        }
        return true;
    }

    public void AppendAudit(BankrollAuditEvent auditEvent)
    {
        Directory.CreateDirectory(_directory);
        File.AppendAllText(
            GetAuditPath(auditEvent.League),
            JsonConvert.SerializeObject(auditEvent, Formatting.None) + Environment.NewLine);
    }

    public void AppendWorkflowAudit(WorkflowAuditEvent auditEvent)
    {
        Directory.CreateDirectory(_directory);
        File.AppendAllText(
            GetAuditPath(auditEvent.League),
            JsonConvert.SerializeObject(auditEvent, Formatting.None) + Environment.NewLine);
    }

    public void AppendForcedResetAudit(ForcedResetAuditEvent auditEvent)
    {
        Directory.CreateDirectory(_directory);
        File.AppendAllText(
            GetAuditPath(auditEvent.League),
            JsonConvert.SerializeObject(auditEvent, Formatting.None) + Environment.NewLine);
    }

    /// <summary>
    /// Moves the canonical state file (and any interrupted temporary write) aside under timestamped
    /// names so a forced reset never destroys evidence, even when that evidence is unparseable.
    /// Returns the quarantine paths that were created.
    /// </summary>
    public IReadOnlyList<string> QuarantineState(string league, DateTimeOffset at)
    {
        var path = GetStatePath(league);
        var quarantined = new List<string>();
        foreach (var candidate in new[] { path, path + ".tmp" })
        {
            if (FileQuarantine.TryMoveAside(candidate, at, out var target)) quarantined.Add(target);
        }
        return quarantined;
    }

    private string GetStatePath(string league) => Path.Combine(_directory, $"bankroll-{Sanitize(league)}.json");
    private string GetAuditPath(string league) => Path.Combine(_directory, $"execution-audit-{Sanitize(league)}.jsonl");

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
    }
}

/// <summary>Renames state files aside instead of deleting them.</summary>
public static class FileQuarantine
{
    public static bool TryMoveAside(string path, DateTimeOffset at, out string quarantinePath)
    {
        quarantinePath = string.Empty;
        if (!File.Exists(path)) return false;
        var target = ForcedFreshStateReset.QuarantinePath(path, at);
        for (var attempt = 1; File.Exists(target); attempt++)
        {
            target = ForcedFreshStateReset.QuarantinePath(path, at, attempt);
        }
        File.Move(path, target);
        quarantinePath = target;
        return true;
    }
}

public sealed record ForcedResetAuditEvent(
    int SchemaVersion,
    string EventType,
    string League,
    DateTimeOffset OccurredAtUtc,
    string DiscardedCustody,
    string QuarantinedPaths)
{
    public static ForcedResetAuditEvent Create(
        string league, DateTimeOffset occurredAtUtc, string discardedCustody, IEnumerable<string> quarantinedPaths) =>
        new(1,
            "ForcedFreshStateReset",
            league,
            occurredAtUtc,
            discardedCustody,
            string.Join(" | ", quarantinedPaths));
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

public sealed record WorkflowAuditEvent(
    int SchemaVersion,
    string EventType,
    string League,
    DateTimeOffset OccurredAtUtc,
    Guid WorkflowId,
    int CompletedLegs,
    long PlannedRealizedChaos,
    long PlannedProfitChaos,
    long? ActualTerminalChaos,
    long? ActualProfitChaos,
    string Detail)
{
    public static WorkflowAuditEvent From(WorkflowExecutionState workflow, TrackedOrderState tracked)
    {
        var actual = workflow.Phase == WorkflowExecutionPhase.Completed
            ? tracked.TerminalReceivedWantedAmount
            : null;
        return new WorkflowAuditEvent(
            1,
            workflow.Phase == WorkflowExecutionPhase.Completed ? "WorkflowCompleted" : "WorkflowStopped",
            workflow.League,
            workflow.UpdatedAtUtc,
            workflow.WorkflowId,
            workflow.CurrentLegIndex,
            workflow.PlannedRealizedChaos,
            workflow.PlannedProfitChaos,
            actual,
            actual is null ? null : actual.Value - workflow.BenchmarkChaos,
            workflow.Detail);
    }
}
