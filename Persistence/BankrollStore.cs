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
        var migrated = schemaVersion is 1 or 2 or 3 or 4 or 5 or 6;
        var trackedMigrated = state.AllOrders.Any(order => IsLegacyTrackedSchema(order.SchemaVersion));
        var workflowMigrated = state.Workflow?.SchemaVersion is 1 or 2 or 3;
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
        if (trackedMigrated)
        {
            foreach (var migratedTracked in state.AllOrders)
            {
                if (!IsLegacyTrackedSchema(migratedTracked.SchemaVersion)) continue;
                if (migratedTracked.StashTransferIntent is { } legacyStashIntent)
                    legacyStashIntent.StashCustodyMode = StashCustodyMode.VisibleCurrencyStashExact;
                TrackedOrderLifecycle.MigrateLegacyAssetProgress(migratedTracked);
                TrackedOrderLifecycle.MigrateLegacyAssetAmounts(migratedTracked);
                migratedTracked.SchemaVersion = TrackedOrderState.CurrentSchemaVersion;
            }
            state.HasUnresolvedOrder = state.ComputeUnresolved();
        }
        if (workflowMigrated && state.Workflow is { } migratedWorkflow)
        {
            var legacyFingerprint = migratedWorkflow.PlanFingerprint;
            var fingerprintMatches = migratedWorkflow.SchemaVersion switch
            {
                1 => WorkflowCoordinator.LegacyVersionOneFingerprintMatches(migratedWorkflow),
                2 => WorkflowCoordinator.LegacyVersionTwoFingerprintMatches(migratedWorkflow),
                3 => WorkflowCoordinator.LegacyVersionThreeFingerprintMatches(migratedWorkflow),
                _ => false,
            };
            if (!fingerprintMatches)
            {
                throw new InvalidDataException($"Schema-{migratedWorkflow.SchemaVersion} workflow failed legacy fingerprint authentication.");
            }
            if (migratedWorkflow.Phase == WorkflowExecutionPhase.LegActive)
            {
                if (state.TrackedOrder is not { } activeTracked ||
                    !WorkflowCoordinator.LegacyActiveTrackedMatches(
                        migratedWorkflow, activeTracked, legacyFingerprint))
                {
                    throw new InvalidDataException($"Schema-{migratedWorkflow.SchemaVersion} active workflow did not match its tracked order.");
                }
                migratedWorkflow.CurrentProbeSessionId = activeTracked.ProbeSessionId;
            }
            else if (migratedWorkflow.CurrentProbeSessionId == Guid.Empty)
            {
                migratedWorkflow.CurrentProbeSessionId = migratedWorkflow.OriginProbeSessionId;
            }
            if (string.IsNullOrWhiteSpace(migratedWorkflow.TerminalChaosMetadata) && migratedWorkflow.Legs.Count > 0)
                migratedWorkflow.TerminalChaosMetadata = migratedWorkflow.Legs[^1].ToMetadata;
            if (migratedWorkflow.SchemaVersion == 3)
                WorkflowCoordinator.MigrateVersionThreeUnitEconomics(migratedWorkflow);
            else
                WorkflowCoordinator.MigrateLegacySemantics(migratedWorkflow);
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
            state.HasUnresolvedOrder != state.ComputeUnresolved())
        {
            throw new InvalidDataException("Bankroll state failed schema or value validation.");
        }
        ValidateRestingSet(state);
        foreach (var slot in state.AllOrders)
        {
            ValidateTrackedOnLoad(slot, league);
        }
        ValidateReservationConsistency(state);
        if (state.Workflow is { } workflow)
        {
            if (workflow.League != league)
            {
                throw new InvalidDataException("Workflow league did not match canonical bankroll league.");
            }
            if (state.RestingOrders.Count != 0)
            {
                throw new InvalidDataException("A workflow cannot run alongside resting sweep orders.");
            }
            WorkflowCoordinator.Validate(workflow, state.TrackedOrder);
            if (workflow.Phase == WorkflowExecutionPhase.Completed &&
                ContinuousWorkflowLoop.TryDescribeUnsettledCanonicalState(state, state.TrackedOrder, out _))
                throw new InvalidDataException("Completed workflow still had unsettled canonical custody.");
        }

        if (migrated || trackedMigrated || workflowMigrated)
        {
            Save(state);
        }

        return state;
    }

    private static bool IsLegacyTrackedSchema(int schemaVersion) =>
        schemaVersion is 1 or 2 or 3 or 4 or 5 or 6;

    /// <summary>
    /// The resting set's own rules, which exist only because more than one order can now be known at
    /// once. A resting order must be restable (see <see cref="TrackedOrderRestPolicy"/>), must be a
    /// distinct attempt, and must offer a distinct asset - two live sells of the same item share one
    /// queue and compete with each other, so the sweep never creates that shape and canonical state
    /// refuses to represent it.
    /// </summary>
    private static void ValidateRestingSet(BankrollState state)
    {
        foreach (var resting in state.RestingOrders)
        {
            if (resting is null)
            {
                throw new InvalidDataException("A resting order entry was null.");
            }
            if (!TrackedOrderRestPolicy.CanRest(resting.Status))
            {
                throw new InvalidDataException(
                    $"Resting order status {resting.Status} holds armed input and cannot rest.");
            }
        }

        var attempts = state.AllOrders.Select(order => order.AttemptId).ToList();
        if (attempts.Distinct().Count() != attempts.Count)
        {
            throw new InvalidDataException("Canonical orders repeated an attempt identity.");
        }

        var offered = state.AllOrders.Select(order => order.OfferedMetadata).ToList();
        if (offered.Distinct(StringComparer.Ordinal).Count() != offered.Count)
        {
            throw new InvalidDataException("Two canonical orders offered the same asset.");
        }
    }

    private static void ValidateTrackedOnLoad(TrackedOrderState? order, string league)
    {
        if (order is { } tracked &&
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
        if (order is { } lifecycleTracked && RequiresLifecycleIdentity(lifecycleTracked.Status) &&
            !TrackedOrderLifecycle.HasDurableIdentity(lifecycleTracked))
        {
            throw new InvalidDataException("Unresolved lifecycle state lacked durable creation/ratio/deadline identity.");
        }
        if (order is { } progressTracked && !TrackedOrderLifecycle.AssetProgressIsValid(progressTracked))
        {
            throw new InvalidDataException("Settlement-asset progress was internally inconsistent.");
        }
        if (order is { BulkCollectionOwnedBaseline: < 0 } or
            { Status: TrackedOrderStatus.Stashed, BulkCollectionOwnedBaseline: not null })
        {
            throw new InvalidDataException("Bulk collection ownership baseline was invalid.");
        }
        if (order is { Status: TrackedOrderStatus.Stashed } stashedState &&
            (!HasCompleteTerminalEvidence(stashedState) ||
             TrackedOrderLifecycle.CreateSettlementAssets(stashedState,
                 stashedState.TerminalRemainingOfferedAmount!.Value,
                 stashedState.TerminalReceivedWantedAmount!.Value).Count == 0))
        {
            throw new InvalidDataException("Resolved stashed state lacked terminal settlement evidence.");
        }
        if (order is { StashTransferIntent: not null } armed &&
            (armed.Status is not TrackedOrderStatus.StashTransferArmed and not TrackedOrderStatus.Ambiguous ||
             !IsValidStashTransferIntent(armed.StashTransferIntent) ||
             !IntentMatchesSettlementAsset(armed, armed.StashTransferIntent!)))
        {
            throw new InvalidDataException("Stash-transfer-armed state lacked durable recovery evidence.");
        }
        if (order is { Status: TrackedOrderStatus.StashTransferArmed, StashTransferIntent: null })
            throw new InvalidDataException("Stash-transfer-armed state lacked durable recovery evidence.");
        if (order is { StashTransferIntent: not null, CollectionAssetIntent: not null })
            throw new InvalidDataException("Collection and stash-transfer intents cannot coexist.");
        if (order is { Status: TrackedOrderStatus.CollectionArmed or TrackedOrderStatus.Ambiguous,
                CollectionAssetIntent: not null } assetCollection &&
            !IsValidCollectionAssetIntent(assetCollection.CollectionAssetIntent, assetCollection))
        {
            throw new InvalidDataException("Canceled return collection lacked exact durable asset intent.");
        }
        if (order is { Status: TrackedOrderStatus.CancelArmed or TrackedOrderStatus.CancelClicked } cancelState &&
            !IsValidCancelIntent(cancelState.CancelIntent, cancelState))
        {
            throw new InvalidDataException("Cancellation state lacked durable exact intent evidence.");
        }
        if (order is { Status: TrackedOrderStatus.CompletedUncollected or TrackedOrderStatus.CanceledUncollected } terminalState &&
            !HasCompleteTerminalEvidence(terminalState))
        {
            throw new InvalidDataException("Terminal tracked state lacked complete amounts and ledger commit evidence.");
        }
    }

    private static bool IsValidStashTransferIntent(StashTransferIntentState? intent)
    {
        if (intent is null ||
            !StashCustodyPolicy.IsResolvableCustody(intent.Metadata, intent.StashCustodyMode) ||
            intent.Amount <= 0 ||
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
            state.HasUnresolvedOrder != state.ComputeUnresolved())
        {
            throw new InvalidDataException("Bankroll state failed save-time schema or value validation.");
        }
        ValidateRestingSet(state);
        ValidateReservationConsistency(state);
        if (state.Workflow is { } workflow)
        {
            if (workflow.League != state.League)
            {
                throw new InvalidDataException("Workflow league did not match canonical bankroll league.");
            }
            if (state.RestingOrders.Count != 0)
            {
                throw new InvalidDataException("A workflow cannot run alongside resting sweep orders.");
            }
            WorkflowCoordinator.Validate(workflow, state.TrackedOrder);
            if (workflow.Phase == WorkflowExecutionPhase.Completed &&
                ContinuousWorkflowLoop.TryDescribeUnsettledCanonicalState(state, state.TrackedOrder, out _))
                throw new InvalidDataException("Completed workflow still had unsettled canonical custody.");
        }
        foreach (var slot in state.AllOrders)
        {
            ValidateTrackedForSave(slot, state.League);
        }
        Directory.CreateDirectory(_directory);
        var path = GetStatePath(state.League);
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, JsonConvert.SerializeObject(state, Formatting.Indented));
        File.Move(temporaryPath, path, true);
    }

    /// <summary>
    /// Reservations must still match the orders holding principal exactly, order for order. With a
    /// resting set that means summing across every slot instead of reading one - and because two
    /// slots may never offer the same asset, each metadata's expected reservation still comes from
    /// exactly one order, so this stays an exact identity rather than a bound.
    /// </summary>
    private static void ValidateReservationConsistency(BankrollState state)
    {
        var expected = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var order in state.AllOrders)
        {
            if (!ReservationRequired(order))
            {
                continue;
            }
            if (!expected.TryAdd(order.OfferedMetadata, order.OfferedAmount))
            {
                throw new InvalidDataException("Two canonical orders reserved the same offered asset.");
            }
        }

        var expectedChaos = expected.GetValueOrDefault(BankrollState.ChaosMetadata);
        var expectedDivine = expected.GetValueOrDefault(BankrollState.DivineMetadata);
        if (state.ReservedChaos != expectedChaos || state.ReservedDivine != expectedDivine)
        {
            throw new InvalidDataException("Canonical reservations did not exactly match the unresolved tracked order.");
        }
        foreach (var pair in state.NonCoreBalances)
        {
            if (pair.Value.Reserved != expected.GetValueOrDefault(pair.Key))
                throw new InvalidDataException("Canonical non-core reservation did not match the exact unresolved order.");
        }
        foreach (var metadata in expected.Keys)
        {
            if (metadata != BankrollState.ChaosMetadata && metadata != BankrollState.DivineMetadata &&
                !state.NonCoreBalances.ContainsKey(metadata))
                throw new InvalidDataException("Unresolved order had no exact offered-metadata reservation bucket.");
        }
    }

    private static bool ReservationRequired(TrackedOrderState order) =>
        order.IsUnresolved && order.LedgerCommittedAtUtc is null &&
        order.Status is TrackedOrderStatus.Armed or TrackedOrderStatus.Pending or TrackedOrderStatus.TimedOut or
            TrackedOrderStatus.CancelArmed or TrackedOrderStatus.CancelClicked or TrackedOrderStatus.Ambiguous;

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
    WorkflowClosureMode ClosureMode,
    string SettlementMetadata,
    long PlannedSettlementAmount,
    string ProfitMetadata,
    long PlannedProfitAmount,
    long ValuedProfitChaos,
    long? ActualSettlementAmount,
    long? ActualProfitAmount,
    long PlannedRealizedChaos,
    long PlannedRestorationChaosSpent,
    long PlannedProfitChaos,
    long? ActualTerminalChaos,
    long? ActualChaosRealized,
    long? ActualRestorationChaosSpent,
    long? ActualRestoredPrincipal,
    long? ActualProfitChaos,
    string Detail)
{
    public static WorkflowAuditEvent From(WorkflowExecutionState workflow, TrackedOrderState tracked)
    {
        var legacyActual = workflow.ClosureMode == WorkflowClosureMode.LegacyTerminalChaos &&
            workflow.Phase == WorkflowExecutionPhase.Completed
            ? tracked.TerminalReceivedWantedAmount
            : null;
        var closedCompleted = (workflow.ClosureMode is WorkflowClosureMode.ClosedCycle or
                WorkflowClosureMode.SameAssetCycle) &&
            workflow.Phase == WorkflowExecutionPhase.Completed;
        return new WorkflowAuditEvent(
            3,
            workflow.Phase == WorkflowExecutionPhase.Completed ? "WorkflowCompleted" : "WorkflowStopped",
            workflow.League,
            workflow.UpdatedAtUtc,
            workflow.WorkflowId,
            workflow.CurrentLegIndex,
            workflow.ClosureMode,
            workflow.SettlementMetadata,
            workflow.PlannedSettlementAmount,
            workflow.ProfitMetadata,
            workflow.PlannedProfitAmount,
            workflow.ValuedProfitChaos,
            closedCompleted ? workflow.ActualSettlementAmount : null,
            closedCompleted ? workflow.ActualProfitAmount : null,
            workflow.PlannedRealizedChaos,
            workflow.PlannedRestorationSpendChaos,
            workflow.PlannedProfitChaos,
            legacyActual,
            closedCompleted ? workflow.ActualChaosRealized : null,
            closedCompleted ? workflow.CumulativeActualRestorationChaosSpent : null,
            closedCompleted ? workflow.RestoredPrincipal : null,
            legacyActual is not null
                ? legacyActual.Value - workflow.BenchmarkChaos
                : closedCompleted && workflow.ClosureMode == WorkflowClosureMode.ClosedCycle
                    ? workflow.ActualChaosRealized - workflow.CumulativeActualRestorationChaosSpent
                    : null,
            workflow.Detail);
    }
}
