using FaustusControllerLite.Orders;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace FaustusControllerLite.Domain;

public enum WorkflowExecutionPhase
{
    ReadyForLeg,
    LegActive,
    Completed,
    Stopped,
    Ambiguous,
}

public enum WorkflowDirectiveKind
{
    None,
    ReprobeAndPrepareCurrentLeg,
    ObserveCurrentOrder,
    AuthorizeCancellation,
    RecoverCancellationWithoutRetry,
    AuthorizeSettlementCollection,
    RecoverSettlementCollectionWithoutRetry,
    AuthorizeStashTransfer,
    RecoverStashTransferWithoutRetry,
    ManualReconciliationRequired,
}

public enum WorkflowClosureMode
{
    LegacyTerminalChaos = 1,
    ClosedCycle = 2,
    SameAssetCycle = 3,
}

public enum WorkflowLegRole
{
    Conversion = 1,
    ChaosRealization = 2,
    PrincipalRestoration = 3,
    CycleClosure = 4,
}

public enum WorkflowRefreshResult
{
    Refreshed,
    RetryableUnavailable,
    Failed,
}

public sealed class WorkflowExecutionState
{
    public const int CurrentSchemaVersion = 4;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public Guid WorkflowId { get; set; }
    public string League { get; set; } = string.Empty;
    public Guid OriginProbeSessionId { get; set; }
    public Guid CurrentProbeSessionId { get; set; }
    public string PlanFingerprint { get; set; } = string.Empty;
    public WorkflowExecutionPhase Phase { get; set; }
    public int CurrentLegIndex { get; set; }
    public Guid? CurrentAttemptId { get; set; }
    public long CurrentInputAmount { get; set; }
    public string TerminalChaosMetadata { get; set; } = string.Empty;
    public long StartingPrincipal { get; set; }
    public long BenchmarkChaos { get; set; }
    public long PlannedRealizedChaos { get; set; }
    public long PlannedProfitChaos { get; set; }
    public WorkflowClosureMode ClosureMode { get; set; }
    public string StartingMetadata { get; set; } = string.Empty;
    public string ChaosMetadata { get; set; } = string.Empty;
    public int ChaosRealizationLegIndex { get; set; }
    public long RestorationPrincipal { get; set; }
    public long RestoredPrincipal { get; set; }
    public long OutstandingPrincipal { get; set; }
    public long PlannedRestorationSpendChaos { get; set; }
    public long CumulativeActualRestorationChaosSpent { get; set; }
    public long ActualChaosRealized { get; set; }
    public string SettlementMetadata { get; set; } = string.Empty;
    public long PlannedSettlementAmount { get; set; }
    public long ActualSettlementAmount { get; set; }
    public string ProfitMetadata { get; set; } = string.Empty;
    public long PlannedProfitAmount { get; set; }
    public long ActualProfitAmount { get; set; }
    public long ValuedProfitChaos { get; set; }
    public long ProfitValuationNumerator { get; set; }
    public long ProfitValuationDenominator { get; set; }
    public long CumulativeCycleSettlementAmount { get; set; }
    public List<WorkflowLegPlan> Legs { get; set; } = [];
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public string Detail { get; set; } = string.Empty;

    public bool IsActive => Phase is WorkflowExecutionPhase.ReadyForLeg or WorkflowExecutionPhase.LegActive;
}

public sealed class WorkflowLegPlan
{
    public int Index { get; set; }
    public WorkflowLegRole Role { get; set; }
    public string FromMetadata { get; set; } = string.Empty;
    public string FromName { get; set; } = string.Empty;
    public uint FromHash { get; set; }
    public string ToMetadata { get; set; } = string.Empty;
    public string ToName { get; set; } = string.Empty;
    public uint ToHash { get; set; }
    public long RateNumerator { get; set; }
    public long RateDenominator { get; set; }
    public QuoteExecutionIntent ExecutionIntent { get; set; }
    public QuoteBookSource SourceBook { get; set; }
    public long InputAvailable { get; set; }
    public long InputSpent { get; set; }
    public long Output { get; set; }
    public long InputRemainder { get; set; }
    public long? ExpectedGold { get; set; }
}

public static class WorkflowCoordinator
{
    public static bool CanReplaceBeforeFirstPlacement(
        WorkflowExecutionState workflow,
        TrackedOrderState? tracked) =>
        workflow.Phase == WorkflowExecutionPhase.ReadyForLeg &&
        workflow.CurrentLegIndex == 0 && workflow.CurrentAttemptId is null &&
        tracked?.IsUnresolved != true;

    public static WorkflowExecutionState Create(
        string league,
        RouteCandidate candidate,
        Guid probeSessionId,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(league);
        ArgumentNullException.ThrowIfNull(candidate);
        if (probeSessionId == Guid.Empty || candidate.Legs.Count is < 2 or > 3)
        {
            throw new ArgumentException("A workflow requires a probe session and two or three exact legs.");
        }

        var directCycle = candidate.CycleKind == RouteCycleKind.SameAssetDivineCycle;
        var chaosRealizationLegIndex = candidate.ChaosRealizationLegIndex >= 0
            ? candidate.ChaosRealizationLegIndex
            : candidate.Legs.Count - 1;
        var settlementMetadata = directCycle ? candidate.Path[^1].Metadata : candidate.Path[^1].Metadata;
        var profitMetadata = string.IsNullOrWhiteSpace(candidate.ProfitMetadata)
            ? candidate.Path[chaosRealizationLegIndex + 1].Metadata
            : candidate.ProfitMetadata;
        var valuation = candidate.ProfitValuationEdge;

        var state = new WorkflowExecutionState
        {
            WorkflowId = Guid.NewGuid(),
            League = league,
            OriginProbeSessionId = probeSessionId,
            CurrentProbeSessionId = probeSessionId,
            Phase = WorkflowExecutionPhase.ReadyForLeg,
            CurrentLegIndex = 0,
            CurrentInputAmount = candidate.StartingPrincipal,
            TerminalChaosMetadata = directCycle
                ? valuation?.To.Metadata ?? candidate.Path[^1].Metadata
                : candidate.Path[chaosRealizationLegIndex + 1].Metadata,
            StartingPrincipal = candidate.StartingPrincipal,
            BenchmarkChaos = directCycle ? candidate.ValuedProfitChaos : candidate.BenchmarkChaos,
            PlannedRealizedChaos = candidate.RealizedChaos,
            PlannedProfitChaos = candidate.ProfitChaos,
            ClosureMode = directCycle ? WorkflowClosureMode.SameAssetCycle : WorkflowClosureMode.ClosedCycle,
            StartingMetadata = candidate.Path[0].Metadata,
            ChaosMetadata = directCycle
                ? valuation?.To.Metadata ?? candidate.Path[^1].Metadata
                : candidate.Path[chaosRealizationLegIndex + 1].Metadata,
            ChaosRealizationLegIndex = directCycle ? -1 : chaosRealizationLegIndex,
            RestorationPrincipal = candidate.RestorationPrincipal,
            RestoredPrincipal = 0,
            OutstandingPrincipal = candidate.RestorationPrincipal,
            PlannedRestorationSpendChaos = candidate.PlannedRestorationSpendChaos,
            CumulativeActualRestorationChaosSpent = 0,
            ActualChaosRealized = 0,
            SettlementMetadata = settlementMetadata,
            PlannedSettlementAmount = candidate.PlannedSettlementAmount > 0
                ? candidate.PlannedSettlementAmount
                : candidate.Legs[^1].Output,
            ActualSettlementAmount = 0,
            ProfitMetadata = profitMetadata,
            PlannedProfitAmount = candidate.ProfitAmount > 0 ? candidate.ProfitAmount : candidate.ProfitChaos,
            ActualProfitAmount = 0,
            ValuedProfitChaos = candidate.ValuedProfitChaos > 0 ? candidate.ValuedProfitChaos : candidate.ProfitChaos,
            ProfitValuationNumerator = valuation?.Rate.Numerator ?? 0,
            ProfitValuationDenominator = valuation?.Rate.Denominator ?? 0,
            CumulativeCycleSettlementAmount = 0,
            StartedAtUtc = now,
            UpdatedAtUtc = now,
            Detail = "Exact route snapshot persisted; current leg requires fresh probing before placement.",
            Legs = candidate.Legs.Select((leg, index) => FromRouteLeg(
                index,
                leg,
                directCycle && index == candidate.Legs.Count - 1
                    ? WorkflowLegRole.CycleClosure
                    : index == chaosRealizationLegIndex
                    ? WorkflowLegRole.ChaosRealization
                    : candidate.RestorationPrincipal > 0 && index == candidate.Legs.Count - 1
                        ? WorkflowLegRole.PrincipalRestoration
                        : WorkflowLegRole.Conversion)).ToList(),
        };
        state.PlanFingerprint = ComputeFingerprint(state);
        Validate(state, null);
        return state;
    }

    public static WorkflowDirectiveKind Decide(WorkflowExecutionState workflow, TrackedOrderState? tracked)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        if (!workflow.IsActive) return WorkflowDirectiveKind.None;
        if (workflow.Phase == WorkflowExecutionPhase.ReadyForLeg)
        {
            return WorkflowDirectiveKind.ReprobeAndPrepareCurrentLeg;
        }
        if (tracked is null || workflow.CurrentAttemptId != tracked.AttemptId)
        {
            return WorkflowDirectiveKind.ManualReconciliationRequired;
        }

        return tracked.Status switch
        {
            TrackedOrderStatus.Pending => WorkflowDirectiveKind.ObserveCurrentOrder,
            TrackedOrderStatus.TimedOut => WorkflowDirectiveKind.AuthorizeCancellation,
            TrackedOrderStatus.CancelArmed or TrackedOrderStatus.CancelClicked =>
                WorkflowDirectiveKind.RecoverCancellationWithoutRetry,
            TrackedOrderStatus.CompletedUncollected or TrackedOrderStatus.CanceledUncollected =>
                WorkflowDirectiveKind.AuthorizeSettlementCollection,
            TrackedOrderStatus.CollectionArmed when tracked.CollectionAssetIntent is not null =>
                WorkflowDirectiveKind.RecoverSettlementCollectionWithoutRetry,
            TrackedOrderStatus.CollectionArmed => WorkflowDirectiveKind.ManualReconciliationRequired,
            TrackedOrderStatus.Collected => WorkflowDirectiveKind.AuthorizeStashTransfer,
            TrackedOrderStatus.StashTransferArmed => WorkflowDirectiveKind.RecoverStashTransferWithoutRetry,
            TrackedOrderStatus.Ambiguous when tracked.StashTransferIntent is not null =>
                WorkflowDirectiveKind.RecoverStashTransferWithoutRetry,
            TrackedOrderStatus.Ambiguous when tracked.CollectionAssetIntent is not null =>
                WorkflowDirectiveKind.RecoverSettlementCollectionWithoutRetry,
            TrackedOrderStatus.Armed or TrackedOrderStatus.Ambiguous => WorkflowDirectiveKind.ManualReconciliationRequired,
            _ => WorkflowDirectiveKind.None,
        };
    }

    public static bool TryApplyTrackedState(
        WorkflowExecutionState workflow,
        TrackedOrderState tracked,
        DateTimeOffset now,
        out WorkflowExecutionState next,
        out string failure)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(tracked);
        next = Clone(workflow);
        if (!workflow.IsActive || workflow.CurrentLegIndex < 0 || workflow.CurrentLegIndex >= workflow.Legs.Count)
        {
            failure = "Workflow is not positioned on an active leg.";
            return false;
        }
        var leg = workflow.Legs[workflow.CurrentLegIndex];
        if (!TrackedMatchesLeg(workflow, tracked, leg))
        {
            failure = "Tracked order does not match the exact current workflow leg.";
            return false;
        }

        if (workflow.Phase == WorkflowExecutionPhase.ReadyForLeg)
        {
            if (tracked.Status != TrackedOrderStatus.Armed || workflow.CurrentAttemptId is not null)
            {
                failure = "Only a newly persisted armed placement can bind a ready workflow leg.";
                return false;
            }
            next.Phase = WorkflowExecutionPhase.LegActive;
            next.CurrentAttemptId = tracked.AttemptId;
            next.UpdatedAtUtc = now;
            next.Detail = $"Leg {leg.Index + 1} placement intent bound to attempt {tracked.AttemptId:D}.";
            failure = string.Empty;
            return true;
        }

        if (workflow.CurrentAttemptId != tracked.AttemptId)
        {
            failure = "Tracked attempt does not match the workflow's current placement attempt.";
            return false;
        }
        if (tracked.Status != TrackedOrderStatus.Stashed)
        {
            next.UpdatedAtUtc = now;
            failure = string.Empty;
            return true;
        }

        if (leg.Role == WorkflowLegRole.CycleClosure)
        {
            if (tracked.TerminalRemainingOfferedAmount is not { } remaining ||
                tracked.TerminalReceivedWantedAmount is not { } received ||
                remaining < 0 || remaining > tracked.OfferedAmount || received < 0 ||
                received > 0 && (!tracked.WantedAssetCollected || !tracked.WantedAssetStashed) ||
                remaining > 0 && (!tracked.OfferedReturnCollected || !tracked.OfferedReturnStashed) ||
                tracked.PendingWantedBatchAmount != 0 || tracked.PendingReturnBatchAmount != 0)
            {
                failure = "Cycle-closing settlement lacked exact terminal amounts or verified stash custody.";
                return false;
            }

            try
            {
                next.CumulativeCycleSettlementAmount = checked(
                    workflow.CumulativeCycleSettlementAmount + received);
                next.CurrentAttemptId = null;
                next.UpdatedAtUtc = now;
                if (remaining == 0)
                {
                    next.ActualSettlementAmount = next.CumulativeCycleSettlementAmount;
                    next.ActualProfitAmount = checked(next.ActualSettlementAmount - next.StartingPrincipal);
                    next.CurrentLegIndex++;
                    next.CurrentInputAmount = received;
                    next.Phase = next.ActualProfitAmount > 0
                        ? WorkflowExecutionPhase.Completed
                        : WorkflowExecutionPhase.Stopped;
                    next.Detail = next.ActualProfitAmount > 0
                        ? $"Same-asset cycle closed with actual profit {next.ActualProfitAmount} {next.ProfitMetadata}."
                        : "Same-asset cycle closed without a positive authenticated profit.";
                }
                else
                {
                    var rate = new Rational(leg.RateNumerator, leg.RateDenominator);
                    var conversion = rate.ConvertWholeLots(remaining);
                    if (conversion.InputSpent != remaining || conversion.Output <= 0)
                    {
                        failure = "Returned cycle-closing asset is incompatible with the authenticated quote lot.";
                        return false;
                    }
                    var nextLeg = next.Legs[workflow.CurrentLegIndex];
                    nextLeg.InputAvailable = remaining;
                    nextLeg.InputSpent = remaining;
                    nextLeg.Output = conversion.Output;
                    nextLeg.InputRemainder = 0;
                    next.CurrentInputAmount = remaining;
                    next.PlannedSettlementAmount = checked(
                        next.CumulativeCycleSettlementAmount + conversion.Output);
                    next.PlannedProfitAmount = checked(next.PlannedSettlementAmount - next.StartingPrincipal);
                    next.Phase = WorkflowExecutionPhase.ReadyForLeg;
                    next.Detail = $"Cycle-closing attempt stashed; {remaining} {leg.FromMetadata} remains for the same competing limit.";
                }
                next.PlanFingerprint = ComputeFingerprint(next);
                failure = string.Empty;
                return true;
            }
            catch (OverflowException exception)
            {
                failure = $"Cycle-closing settlement arithmetic overflowed: {exception.Message}";
                return false;
            }
        }

        if (leg.Role == WorkflowLegRole.PrincipalRestoration)
        {
            if (tracked.TerminalRemainingOfferedAmount is not { } remaining ||
                tracked.TerminalReceivedWantedAmount is not { } received ||
                remaining < 0 || remaining > tracked.OfferedAmount ||
                received < 0 || received > workflow.OutstandingPrincipal ||
                received > 0 && (!tracked.WantedAssetCollected || !tracked.WantedAssetStashed) ||
                remaining > 0 && (!tracked.OfferedReturnCollected || !tracked.OfferedReturnStashed) ||
                tracked.PendingWantedBatchAmount != 0 || tracked.PendingReturnBatchAmount != 0)
            {
                failure = "Restoration settlement lacked exact terminal amounts or verified stash custody.";
                return false;
            }

            try
            {
                var spent = checked(tracked.OfferedAmount - remaining);
                next.RestoredPrincipal = checked(workflow.RestoredPrincipal + received);
                next.CumulativeActualRestorationChaosSpent = checked(
                    workflow.CumulativeActualRestorationChaosSpent + spent);
                if (next.RestoredPrincipal > workflow.RestorationPrincipal)
                {
                    failure = "Restoration settlement would exceed the authenticated principal.";
                    return false;
                }

                next.OutstandingPrincipal = checked(workflow.RestorationPrincipal - next.RestoredPrincipal);
                next.CurrentAttemptId = null;
                next.UpdatedAtUtc = now;
                if (next.OutstandingPrincipal == 0)
                {
                    next.CurrentLegIndex++;
                    next.CurrentInputAmount = received;
                    next.Phase = WorkflowExecutionPhase.Completed;
                    next.PlannedRestorationSpendChaos = next.CumulativeActualRestorationChaosSpent;
                    next.PlannedProfitChaos = checked(
                        next.PlannedRealizedChaos - next.PlannedRestorationSpendChaos);
                    next.ActualSettlementAmount = next.RestoredPrincipal;
                    next.ActualProfitAmount = checked(
                        next.ActualChaosRealized - next.CumulativeActualRestorationChaosSpent);
                    next.Detail = $"Divine principal restored across verified attempts; actual Chaos profit " +
                        $"{next.ActualChaosRealized - next.CumulativeActualRestorationChaosSpent}.";
                }
                else
                {
                    var rate = new Rational(leg.RateNumerator, leg.RateDenominator);
                    if (!rate.TryGetInputForExactOutput(next.OutstandingPrincipal, out var requiredInput) ||
                        requiredInput <= 0)
                    {
                        failure = "Outstanding restoration principal is incompatible with the authenticated quote lot.";
                        return false;
                    }

                    var nextLeg = next.Legs[workflow.CurrentLegIndex];
                    nextLeg.InputAvailable = requiredInput;
                    nextLeg.InputSpent = requiredInput;
                    nextLeg.Output = next.OutstandingPrincipal;
                    nextLeg.InputRemainder = 0;
                    next.CurrentInputAmount = requiredInput;
                    next.PlannedRestorationSpendChaos = checked(
                        next.CumulativeActualRestorationChaosSpent + requiredInput);
                    next.PlannedProfitChaos = checked(
                        next.PlannedRealizedChaos - next.PlannedRestorationSpendChaos);
                    next.Phase = WorkflowExecutionPhase.ReadyForLeg;
                    next.Detail = $"Restoration attempt stashed; {next.OutstandingPrincipal} Divine remains for a fresh Immediate quote.";
                }

                next.PlanFingerprint = ComputeFingerprint(next);
                failure = string.Empty;
                return true;
            }
            catch (OverflowException exception)
            {
                failure = $"Restoration settlement arithmetic overflowed: {exception.Message}";
                return false;
            }
        }

        var exactFill = tracked.TerminalRemainingOfferedAmount == 0 &&
            tracked.TerminalReceivedWantedAmount == leg.Output &&
            tracked.WantedAssetCollected && tracked.WantedAssetStashed &&
            (tracked.TerminalRemainingOfferedAmount == 0 ||
             tracked.OfferedReturnCollected && tracked.OfferedReturnStashed);
        next.CurrentAttemptId = null;
        next.UpdatedAtUtc = now;
        if (leg.Role == WorkflowLegRole.ChaosRealization)
        {
            next.ActualChaosRealized = tracked.TerminalReceivedWantedAmount.GetValueOrDefault();
        }
        if (!exactFill)
        {
            next.Phase = WorkflowExecutionPhase.Stopped;
            next.Detail = $"Leg {leg.Index + 1} settled safely but did not exactly fill: " +
                $"remaining={tracked.TerminalRemainingOfferedAmount}, received={tracked.TerminalReceivedWantedAmount}.";
            next.PlanFingerprint = ComputeFingerprint(next);
            failure = string.Empty;
            return true;
        }

        next.CurrentLegIndex++;
        next.CurrentInputAmount = tracked.TerminalReceivedWantedAmount.GetValueOrDefault();
        if (next.CurrentLegIndex == next.Legs.Count)
        {
            next.Phase = WorkflowExecutionPhase.Completed;
            next.ActualSettlementAmount = tracked.TerminalReceivedWantedAmount.GetValueOrDefault();
            next.ActualProfitAmount = workflow.RestorationPrincipal == 0
                ? checked(next.ActualSettlementAmount - next.StartingPrincipal)
                : next.ActualProfitAmount;
            next.Detail = $"All {next.Legs.Count} workflow legs completed with exact verified stash custody.";
        }
        else
        {
            next.Phase = WorkflowExecutionPhase.ReadyForLeg;
            next.Detail = $"Leg {leg.Index + 1} completed exactly; leg {next.CurrentLegIndex + 1} requires fresh probing.";
        }
        next.PlanFingerprint = ComputeFingerprint(next);
        failure = string.Empty;
        return true;
    }

    public static WorkflowRefreshResult TryRefreshRemainingPlan(
        WorkflowExecutionState workflow,
        IReadOnlyCollection<DirectedExchangeEdge> edges,
        Guid probeSessionId,
        long spendCap,
        long minimumProfitChaos,
        DateTimeOffset now,
        out WorkflowExecutionState next,
        out RouteLegResult? currentLeg,
        out string failure,
        bool enableCompetingPriceImprovement = false)
    {
        // Improvement is attempted as a whole alternative plan. If any part of it fails - spread,
        // depth, lot alignment, or profit - the original-rate refresh runs instead, so enabling the
        // feature can never cost a workflow a leg it would otherwise have been able to place.
        if (enableCompetingPriceImprovement &&
            workflow.ClosureMode is WorkflowClosureMode.SameAssetCycle or WorkflowClosureMode.ClosedCycle &&
            TryRefreshImprovedPlan(workflow, edges, probeSessionId, spendCap, minimumProfitChaos, now,
                out var improvedNext, out var improvedLeg))
        {
            next = improvedNext;
            currentLeg = improvedLeg;
            failure = string.Empty;
            return WorkflowRefreshResult.Refreshed;
        }

        return workflow.ClosureMode switch
        {
            WorkflowClosureMode.LegacyTerminalChaos => TryRefreshLegacyRemainingPlan(
                workflow, edges, probeSessionId, spendCap, minimumProfitChaos,
                now, out next, out currentLeg, out failure),
            WorkflowClosureMode.SameAssetCycle => TryRefreshSameAssetCycle(
                workflow, edges, probeSessionId, spendCap, minimumProfitChaos,
                now, out next, out currentLeg, out failure, improve: false),
            _ => TryRefreshClosedRemainingPlan(
                workflow, edges, probeSessionId, spendCap, minimumProfitChaos,
                now, out next, out currentLeg, out failure, improve: false),
        };
    }

    private static bool TryRefreshImprovedPlan(
        WorkflowExecutionState workflow,
        IReadOnlyCollection<DirectedExchangeEdge> edges,
        Guid probeSessionId,
        long spendCap,
        long minimumProfitChaos,
        DateTimeOffset now,
        out WorkflowExecutionState next,
        out RouteLegResult? currentLeg)
    {
        var result = workflow.ClosureMode == WorkflowClosureMode.SameAssetCycle
            ? TryRefreshSameAssetCycle(workflow, edges, probeSessionId, spendCap, minimumProfitChaos,
                now, out next, out currentLeg, out _, improve: true)
            : TryRefreshClosedRemainingPlan(workflow, edges, probeSessionId, spendCap, minimumProfitChaos,
                now, out next, out currentLeg, out _, improve: true);
        return result == WorkflowRefreshResult.Refreshed && currentLeg is not null;
    }

    /// <summary>
    /// Replaces one competing leg with its minimum one-unit improvement when the fresh book still
    /// leaves room for it, falling back to the caller's amounts otherwise. A leg that is already
    /// marked improved is left exactly as planned: re-improving it on every reprobe would walk the
    /// resting price away from the quote one unit at a time.
    /// </summary>
    private static bool TryImproveRefreshedLeg(
        bool improve,
        DirectedExchangeEdge competing,
        IEnumerable<DirectedExchangeEdge> edges,
        long inputAvailable,
        long inputSpent,
        long output,
        out DirectedExchangeEdge legEdge,
        out long legInputSpent,
        out long legOutput)
    {
        legEdge = competing;
        legInputSpent = inputSpent;
        legOutput = output;
        if (!improve || competing.ExecutionIntent != QuoteExecutionIntent.Competing ||
            competing.SourceBook == QuoteBookSource.ImprovedCompeting)
        {
            return false;
        }

        var immediate = edges
            .Where(candidate => candidate.From.Metadata == competing.From.Metadata &&
                candidate.To.Metadata == competing.To.Metadata &&
                candidate.From.Hash == competing.From.Hash && candidate.To.Hash == competing.To.Hash &&
                candidate.ExecutionIntent == QuoteExecutionIntent.Immediate)
            .OrderByDescending(candidate => candidate.Rate)
            .ThenByDescending(candidate => candidate.CapturedAt)
            .FirstOrDefault();
        if (!FaustusRoutePlanner.TryImproveCompetingLeg(competing, immediate, inputAvailable, inputSpent, output,
                out var improvedEdge, out var improvedInputSpent, out var improvedOutput))
        {
            return false;
        }

        legEdge = improvedEdge!;
        legInputSpent = improvedInputSpent;
        legOutput = improvedOutput;
        return true;
    }

    private static WorkflowRefreshResult TryRefreshSameAssetCycle(
        WorkflowExecutionState workflow,
        IReadOnlyCollection<DirectedExchangeEdge> edges,
        Guid probeSessionId,
        long spendCap,
        long minimumProfitChaos,
        DateTimeOffset now,
        out WorkflowExecutionState next,
        out RouteLegResult? currentLeg,
        out string failure,
        bool improve)
    {
        next = Clone(workflow);
        currentLeg = null;
        if (workflow.Phase != WorkflowExecutionPhase.ReadyForLeg ||
            workflow.CurrentLegIndex < 0 || workflow.CurrentLegIndex >= workflow.Legs.Count ||
            probeSessionId == Guid.Empty || spendCap <= 0)
        {
            failure = "Same-asset workflow is not ready for a positively funded refresh.";
            return WorkflowRefreshResult.Failed;
        }

        try
        {
            var amount = workflow.CurrentLegIndex == 0 ? spendCap : workflow.CurrentInputAmount;
            if (spendCap < amount)
            {
                failure = "Same-asset workflow exceeded verified canonical/live funds.";
                return workflow.CurrentLegIndex > 0
                    ? WorkflowRefreshResult.RetryableUnavailable
                    : WorkflowRefreshResult.Failed;
            }
            var refreshed = new List<(WorkflowLegPlan Plan, DirectedExchangeEdge Edge)>();
            for (var index = workflow.CurrentLegIndex; index < workflow.Legs.Count; index++)
            {
                var planned = workflow.Legs[index];
                var edge = SelectFreshEdge(planned, edges);
                if (edge is null)
                {
                    failure = $"Fresh market lacked same-asset leg {planned.FromMetadata}->{planned.ToMetadata}.";
                    return workflow.CurrentLegIndex > 0 || planned.Role == WorkflowLegRole.CycleClosure
                        ? WorkflowRefreshResult.RetryableUnavailable
                        : WorkflowRefreshResult.Failed;
                }

                if (index == workflow.CurrentLegIndex && planned.ExecutionIntent == QuoteExecutionIntent.Competing)
                {
                    if (planned.InputSpent <= 0 || planned.InputSpent > amount || planned.Output <= 0)
                    {
                        failure = $"Preserved same-asset competing leg {index + 1} exceeded verified funds.";
                        return workflow.CurrentLegIndex > 0
                            ? WorkflowRefreshResult.RetryableUnavailable
                            : WorkflowRefreshResult.Failed;
                    }
                    var preservedEdge = edge with
                    {
                        Rate = new Rational(planned.RateNumerator, planned.RateDenominator),
                        SourceBook = planned.SourceBook,
                    };
                    TryImproveRefreshedLeg(improve, preservedEdge, edges, amount,
                        planned.InputSpent, planned.Output,
                        out var currentEdge, out var currentInput, out var currentOutput);
                    var plan = FromEdge(index, planned.Role, planned.ExpectedGold, currentEdge,
                        amount, currentInput, currentOutput, checked(amount - currentInput));
                    refreshed.Add((plan, currentEdge));
                    amount = currentOutput;
                    continue;
                }

                var conversion = edge.Rate.ConvertWholeLots(amount, edge.InputLimit);
                if (conversion.InputSpent <= 0 || conversion.Output <= 0)
                {
                    failure = $"Same-asset leg {index + 1} could not form an executable lot.";
                    return workflow.CurrentLegIndex > 0 || planned.Role == WorkflowLegRole.CycleClosure
                        ? WorkflowRefreshResult.RetryableUnavailable
                        : WorkflowRefreshResult.Failed;
                }
                TryImproveRefreshedLeg(improve, edge, edges, amount,
                    conversion.InputSpent, conversion.Output,
                    out var legEdge, out var legInput, out var legOutput);
                var refreshedPlan = FromEdge(index, planned.Role, planned.ExpectedGold, legEdge,
                    amount, legInput, legOutput, checked(amount - legInput));
                refreshed.Add((refreshedPlan, legEdge));
                amount = legOutput;
            }

            var startingPrincipal = workflow.CurrentLegIndex == 0
                ? refreshed[0].Plan.InputSpent
                : workflow.StartingPrincipal;
            var settlement = checked(workflow.CumulativeCycleSettlementAmount + amount);
            var profitAmount = checked(settlement - startingPrincipal);
            if (profitAmount <= 0)
            {
                failure = "Same-asset cycle no longer has a positive settlement profit.";
                return workflow.CurrentLegIndex > 0
                    ? WorkflowRefreshResult.RetryableUnavailable
                    : WorkflowRefreshResult.Failed;
            }

            var valuedProfit = workflow.ValuedProfitChaos;
            var valuationNumerator = workflow.ProfitValuationNumerator;
            var valuationDenominator = workflow.ProfitValuationDenominator;
            if (workflow.CurrentLegIndex == 0)
            {
                var valuation = edges.Where(edge =>
                        edge.From.Metadata == workflow.ProfitMetadata &&
                        edge.To.Metadata == workflow.ChaosMetadata &&
                        edge.ExecutionIntent == QuoteExecutionIntent.Immediate)
                    .OrderByDescending(edge => edge.Rate)
                    .ThenByDescending(edge => edge.CapturedAt)
                    .FirstOrDefault();
                if (valuation is null)
                {
                    failure = "Fresh matrix lacked Immediate Divine/Chaos profit valuation.";
                    return WorkflowRefreshResult.Failed;
                }
                var valued = valuation.Rate.ConvertWholeLots(profitAmount, valuation.InputLimit);
                if (valued.InputSpent != profitAmount || valued.Output < minimumProfitChaos)
                {
                    failure = $"Same-asset profit is valued below {minimumProfitChaos} Chaos or lacks full valuation depth.";
                    return WorkflowRefreshResult.Failed;
                }
                valuedProfit = valued.Output;
                valuationNumerator = valuation.Rate.Numerator;
                valuationDenominator = valuation.Rate.Denominator;
            }

            foreach (var item in refreshed) next.Legs[item.Plan.Index] = item.Plan;
            next.StartingPrincipal = startingPrincipal;
            next.CurrentInputAmount = workflow.CurrentLegIndex == 0
                ? startingPrincipal
                : refreshed[0].Plan.InputAvailable;
            next.PlannedSettlementAmount = settlement;
            next.PlannedProfitAmount = profitAmount;
            next.PlannedProfitChaos = valuedProfit;
            next.ValuedProfitChaos = valuedProfit;
            next.ProfitValuationNumerator = valuationNumerator;
            next.ProfitValuationDenominator = valuationDenominator;
            next.CurrentProbeSessionId = probeSessionId;
            next.UpdatedAtUtc = now;
            next.Detail = $"Fresh same-asset cycle verified at {profitAmount} {workflow.ProfitMetadata} profit, valued {valuedProfit} Chaos.";
            next.PlanFingerprint = ComputeFingerprint(next);
            var first = refreshed[0];
            currentLeg = new RouteLegResult(first.Edge, first.Plan.InputAvailable, first.Plan.InputSpent,
                first.Plan.Output, first.Plan.InputRemainder, first.Plan.ExpectedGold);
            failure = string.Empty;
            return WorkflowRefreshResult.Refreshed;
        }
        catch (OverflowException exception)
        {
            failure = $"Same-asset refresh arithmetic overflowed: {exception.Message}";
            return WorkflowRefreshResult.Failed;
        }
    }

    private static WorkflowRefreshResult TryRefreshLegacyRemainingPlan(
        WorkflowExecutionState workflow,
        IReadOnlyCollection<DirectedExchangeEdge> edges,
        Guid probeSessionId,
        long spendCap,
        long minimumProfitChaos,
        DateTimeOffset now,
        out WorkflowExecutionState next,
        out RouteLegResult? currentLeg,
        out string failure)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(edges);
        next = Clone(workflow);
        currentLeg = null;
        if (workflow.Phase != WorkflowExecutionPhase.ReadyForLeg || workflow.CurrentLegIndex >= workflow.Legs.Count ||
            probeSessionId == Guid.Empty || spendCap < workflow.CurrentInputAmount)
        {
            failure = "Workflow is not ready for a positively funded leg refresh.";
            return WorkflowRefreshResult.Failed;
        }

        try
        {
            var amount = workflow.CurrentInputAmount;
            var refreshed = new List<(WorkflowLegPlan Plan, DirectedExchangeEdge Edge)>(workflow.Legs.Count - workflow.CurrentLegIndex);
            for (var index = workflow.CurrentLegIndex; index < workflow.Legs.Count; index++)
            {
                var planned = workflow.Legs[index];
                var edge = edges
                    .Where(candidate => candidate.From.Metadata == planned.FromMetadata &&
                        candidate.To.Metadata == planned.ToMetadata &&
                        candidate.ExecutionIntent == planned.ExecutionIntent)
                    .OrderByDescending(candidate => candidate.Rate)
                    .ThenByDescending(candidate => candidate.CapturedAt)
                    .FirstOrDefault();
                if (edge is null)
                {
                    failure = $"Fresh matrix lacked authorized leg {planned.FromMetadata}->{planned.ToMetadata} ({planned.ExecutionIntent}).";
                    return WorkflowRefreshResult.Failed;
                }
                if (index == workflow.CurrentLegIndex && planned.ExecutionIntent == QuoteExecutionIntent.Competing)
                {
                    if (planned.InputSpent > amount || planned.InputSpent <= 0 || planned.Output <= 0)
                    {
                        failure = $"Preserved competing workflow leg {index + 1} exceeded current verified funds.";
                        return WorkflowRefreshResult.Failed;
                    }
                    var preserved = new WorkflowLegPlan
                    {
                        Index = index,
                        Role = planned.Role,
                        FromMetadata = planned.FromMetadata,
                        FromName = planned.FromName,
                        FromHash = planned.FromHash,
                        ToMetadata = planned.ToMetadata,
                        ToName = planned.ToName,
                        ToHash = planned.ToHash,
                        RateNumerator = planned.RateNumerator,
                        RateDenominator = planned.RateDenominator,
                        ExecutionIntent = planned.ExecutionIntent,
                        SourceBook = planned.SourceBook,
                        InputAvailable = amount,
                        InputSpent = planned.InputSpent,
                        Output = planned.Output,
                        InputRemainder = checked(amount - planned.InputSpent),
                        ExpectedGold = planned.ExpectedGold,
                    };
                    var preservedEdge = edge with
                    {
                        Rate = new Rational(planned.RateNumerator, planned.RateDenominator),
                        SourceBook = planned.SourceBook,
                    };
                    refreshed.Add((preserved, preservedEdge));
                    amount = preserved.Output;
                    continue;
                }
                if (amount < edge.Rate.Denominator ||
                    edge.ExecutionIntent == QuoteExecutionIntent.Immediate && edge.ImmediateInputDepth < edge.Rate.Denominator)
                {
                    failure = $"Fresh depth or funds could not form one whole lot for workflow leg {index + 1}.";
                    return WorkflowRefreshResult.Failed;
                }
                var conversion = edge.Rate.ConvertWholeLots(amount, edge.InputLimit);
                if (conversion.InputSpent <= 0 || conversion.Output <= 0)
                {
                    failure = $"Fresh workflow leg {index + 1} produced no executable whole lot.";
                    return WorkflowRefreshResult.Failed;
                }
                var plan = new WorkflowLegPlan
                {
                    Index = index,
                    Role = planned.Role,
                    FromMetadata = edge.From.Metadata,
                    FromName = edge.From.Name,
                    FromHash = edge.From.Hash,
                    ToMetadata = edge.To.Metadata,
                    ToName = edge.To.Name,
                    ToHash = edge.To.Hash,
                    RateNumerator = edge.Rate.Numerator,
                    RateDenominator = edge.Rate.Denominator,
                    ExecutionIntent = edge.ExecutionIntent,
                    SourceBook = edge.SourceBook,
                    InputAvailable = amount,
                    InputSpent = conversion.InputSpent,
                    Output = conversion.Output,
                    InputRemainder = conversion.InputRemainder,
                    ExpectedGold = planned.ExpectedGold,
                };
                refreshed.Add((plan, edge));
                amount = conversion.Output;
            }

            var benchmark = workflow.BenchmarkChaos;
            if (workflow.CurrentLegIndex == 0)
            {
                var effectiveStartingPrincipal = refreshed[0].Plan.InputSpent;
                if (workflow.Legs[0].FromMetadata == workflow.TerminalChaosMetadata)
                {
                    benchmark = effectiveStartingPrincipal;
                }
                else
                {
                    var benchmarkEdge = edges.FirstOrDefault(candidate =>
                        candidate.From.Metadata == workflow.Legs[0].FromMetadata &&
                        candidate.To.Metadata == workflow.TerminalChaosMetadata &&
                        candidate.ExecutionIntent == QuoteExecutionIntent.Immediate);
                    if (benchmarkEdge is null)
                    {
                        failure = "Fresh matrix lacked the immediate starting-principal Chaos benchmark.";
                        return WorkflowRefreshResult.Failed;
                    }
                    var conversion = benchmarkEdge.Rate.ConvertWholeLots(
                        effectiveStartingPrincipal, benchmarkEdge.ImmediateInputDepth);
                    if (conversion.InputSpent != effectiveStartingPrincipal)
                    {
                        failure = "Fresh immediate Chaos benchmark could not value the full starting principal.";
                        return WorkflowRefreshResult.Failed;
                    }
                    benchmark = conversion.Output;
                }
            }
            var profit = checked(amount - benchmark);
            if (profit < minimumProfitChaos)
            {
                failure = $"Refreshed remaining path profit {profit} is below {minimumProfitChaos} Chaos.";
                return WorkflowRefreshResult.Failed;
            }
            foreach (var item in refreshed) next.Legs[item.Plan.Index] = item.Plan;
            if (workflow.CurrentLegIndex == 0)
            {
                next.StartingPrincipal = refreshed[0].Plan.InputSpent;
                next.CurrentInputAmount = refreshed[0].Plan.InputSpent;
            }
            else
            {
                next.CurrentInputAmount = refreshed[0].Plan.InputAvailable;
            }
            next.BenchmarkChaos = benchmark;
            next.PlannedRealizedChaos = amount;
            next.PlannedProfitChaos = profit;
            next.PlannedSettlementAmount = amount;
            next.PlannedProfitAmount = profit;
            next.ValuedProfitChaos = profit;
            next.CurrentProbeSessionId = probeSessionId;
            next.PlanFingerprint = ComputeFingerprint(next);
            next.UpdatedAtUtc = now;
            next.Detail = $"Fresh remaining path verified at {profit} planned Chaos profit.";
            var first = refreshed[0];
            currentLeg = new RouteLegResult(
                first.Edge,
                first.Plan.InputAvailable,
                first.Plan.InputSpent,
                first.Plan.Output,
                first.Plan.InputRemainder,
                first.Plan.ExpectedGold);
            failure = string.Empty;
            return WorkflowRefreshResult.Refreshed;
        }
        catch (OverflowException exception)
        {
            failure = $"Workflow refresh arithmetic overflowed: {exception.Message}";
            return WorkflowRefreshResult.Failed;
        }
    }

    private static WorkflowRefreshResult TryRefreshClosedRemainingPlan(
        WorkflowExecutionState workflow,
        IReadOnlyCollection<DirectedExchangeEdge> edges,
        Guid probeSessionId,
        long spendCap,
        long minimumProfitChaos,
        DateTimeOffset now,
        out WorkflowExecutionState next,
        out RouteLegResult? currentLeg,
        out string failure,
        bool improve)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(edges);
        next = Clone(workflow);
        currentLeg = null;
        if (workflow.Phase != WorkflowExecutionPhase.ReadyForLeg ||
            workflow.CurrentLegIndex < 0 || workflow.CurrentLegIndex >= workflow.Legs.Count ||
            probeSessionId == Guid.Empty || spendCap <= 0)
        {
            failure = "Workflow is not ready for a positively funded closed-cycle refresh.";
            return WorkflowRefreshResult.Failed;
        }

        var restorationIndex = workflow.Legs.FindIndex(leg => leg.Role == WorkflowLegRole.PrincipalRestoration);
        var retryable = workflow.CurrentLegIndex > 0;
        try
        {
            var amount = workflow.CurrentInputAmount;
            long startingPrincipal = workflow.StartingPrincipal;
            if (workflow.CurrentLegIndex == 0)
            {
                var first = SelectFreshEdge(workflow.Legs[0], edges);
                if (first is null)
                {
                    failure = "Fresh matrix lacked the authenticated first closed-cycle edge.";
                    return WorkflowRefreshResult.Failed;
                }

                var preserveCompetingPrincipal = workflow.Legs[0].ExecutionIntent == QuoteExecutionIntent.Competing;
                var principalCap = preserveCompetingPrincipal
                    ? workflow.Legs[0].InputSpent
                    : spendCap;
                if (principalCap > spendCap)
                {
                    failure = "Preserved competing first leg exceeded verified canonical/live funds.";
                    return WorkflowRefreshResult.Failed;
                }
                if (first.ExecutionIntent == QuoteExecutionIntent.Immediate)
                    principalCap = Math.Min(principalCap, first.ImmediateInputDepth);
                if (restorationIndex >= 0)
                {
                    var closing = SelectFreshEdge(workflow.Legs[restorationIndex], edges);
                    if (closing is null || closing.ExecutionIntent != QuoteExecutionIntent.Immediate)
                    {
                        failure = "Fresh matrix lacked the authenticated Immediate restoration edge.";
                        return WorkflowRefreshResult.Failed;
                    }
                    var closingLots = closing.ImmediateInputDepth / closing.Rate.Denominator;
                    var restorablePrincipal = checked(closingLots * closing.Rate.Numerator);
                    principalCap = Math.Min(principalCap, restorablePrincipal);
                    var commonLot = LeastCommonMultiple(first.Rate.Denominator, closing.Rate.Numerator);
                    startingPrincipal = preserveCompetingPrincipal
                        ? principalCap
                        : checked(principalCap / commonLot * commonLot);
                    if (startingPrincipal % commonLot != 0)
                    {
                        failure = "Preserved competing principal is incompatible with the closing output lot.";
                        return WorkflowRefreshResult.Failed;
                    }
                }
                else
                {
                    startingPrincipal = preserveCompetingPrincipal
                        ? principalCap
                        : checked(principalCap / first.Rate.Denominator * first.Rate.Denominator);
                }

                if (startingPrincipal <= 0)
                {
                    failure = "Fresh closed-cycle depth could not fund one complete principal lot.";
                    return WorkflowRefreshResult.Failed;
                }
                amount = startingPrincipal;
            }
            else if (workflow.Legs[workflow.CurrentLegIndex].Role != WorkflowLegRole.PrincipalRestoration &&
                     spendCap < amount)
            {
                failure = "Current closed-cycle leg exceeded verified canonical/live funds.";
                return WorkflowRefreshResult.Failed;
            }
            else if (workflow.Legs[workflow.CurrentLegIndex].Role == WorkflowLegRole.PrincipalRestoration)
            {
                amount = spendCap;
            }

            var refreshed = new List<(WorkflowLegPlan Plan, DirectedExchangeEdge Edge)>();
            var plannedChaosRealized = workflow.CurrentLegIndex > workflow.ChaosRealizationLegIndex &&
                workflow.ActualChaosRealized > 0
                    ? workflow.ActualChaosRealized
                    : workflow.PlannedRealizedChaos;
            var plannedRestorationSpend = workflow.CumulativeActualRestorationChaosSpent;
            for (var index = workflow.CurrentLegIndex; index < workflow.Legs.Count; index++)
            {
                var planned = workflow.Legs[index];
                var edge = SelectFreshEdge(planned, edges);
                if (edge is null)
                {
                    failure = $"Fresh matrix lacked authenticated leg {planned.FromMetadata}->{planned.ToMetadata} ({planned.ExecutionIntent}).";
                    return planned.Role == WorkflowLegRole.PrincipalRestoration && retryable
                        ? WorkflowRefreshResult.RetryableUnavailable
                        : WorkflowRefreshResult.Failed;
                }

                if (planned.Role == WorkflowLegRole.PrincipalRestoration)
                {
                    var target = workflow.CurrentLegIndex == 0
                        ? startingPrincipal
                        : workflow.OutstandingPrincipal;
                    if (edge.ExecutionIntent != QuoteExecutionIntent.Immediate || target <= 0 ||
                        !edge.Rate.TryGetInputForExactOutput(target, out var requiredInput) ||
                        requiredInput <= 0 || edge.ImmediateInputDepth < requiredInput ||
                        index == workflow.CurrentLegIndex && spendCap < requiredInput ||
                        workflow.CurrentLegIndex == 0 && amount < requiredInput)
                    {
                        failure = $"Exact Immediate restoration of {target} principal is not currently executable.";
                        return retryable
                            ? WorkflowRefreshResult.RetryableUnavailable
                            : WorkflowRefreshResult.Failed;
                    }

                    var available = index == workflow.CurrentLegIndex
                        ? spendCap
                        : Math.Max(amount, requiredInput);
                    var plan = FromEdge(index, planned.Role, planned.ExpectedGold, edge,
                        available, requiredInput, target, checked(available - requiredInput));
                    refreshed.Add((plan, edge));
                    plannedRestorationSpend = checked(
                        workflow.CumulativeActualRestorationChaosSpent + requiredInput);
                    amount = target;
                    continue;
                }

                if (index == workflow.CurrentLegIndex && planned.ExecutionIntent == QuoteExecutionIntent.Competing)
                {
                    if (planned.InputSpent <= 0 || planned.InputSpent > amount || planned.Output <= 0)
                    {
                        failure = $"Preserved competing workflow leg {index + 1} exceeded current verified funds.";
                        return WorkflowRefreshResult.Failed;
                    }
                    var preservedEdge = edge with
                    {
                        Rate = new Rational(planned.RateNumerator, planned.RateDenominator),
                        SourceBook = planned.SourceBook,
                    };
                    TryImproveRefreshedLeg(improve, preservedEdge, edges, amount,
                        planned.InputSpent, planned.Output,
                        out var currentEdge, out var currentInput, out var currentOutput);
                    var preserved = FromEdge(index, planned.Role, planned.ExpectedGold, currentEdge,
                        amount, currentInput, currentOutput, checked(amount - currentInput));
                    refreshed.Add((preserved, currentEdge));
                    amount = preserved.Output;
                }
                else
                {
                    var conversion = edge.Rate.ConvertWholeLots(amount, edge.InputLimit);
                    if (conversion.InputSpent <= 0 || conversion.Output <= 0)
                    {
                        failure = $"Fresh workflow leg {index + 1} produced no executable whole lot.";
                        return WorkflowRefreshResult.Failed;
                    }
                    TryImproveRefreshedLeg(improve, edge, edges, amount,
                        conversion.InputSpent, conversion.Output,
                        out var legEdge, out var legInput, out var legOutput);
                    var plan = FromEdge(index, planned.Role, planned.ExpectedGold, legEdge,
                        amount, legInput, legOutput, checked(amount - legInput));
                    refreshed.Add((plan, legEdge));
                    amount = legOutput;
                }

                if (planned.Role == WorkflowLegRole.ChaosRealization)
                    plannedChaosRealized = amount;
            }

            var profit = restorationIndex >= 0
                ? checked(plannedChaosRealized - plannedRestorationSpend)
                : checked(plannedChaosRealized - startingPrincipal);
            if (workflow.CurrentLegIndex == 0 && profit < minimumProfitChaos)
            {
                failure = $"Refreshed closed-cycle profit {profit} is below {minimumProfitChaos} Chaos.";
                return WorkflowRefreshResult.Failed;
            }

            // Between the first leg and the realization of Chaos the floor is zero, not minimumProfitChaos.
            // The principal is committed to the intermediate currency by now, so refusing a thin-but-positive
            // plan strands it for a few Chaos of ambition; refusing a negative one is the whole point,
            // because that plan sells the position into thin depth and then spends the operator's own Chaos
            // to close, ending the cycle poorer than it started. Immediate depth thins and refills within
            // seconds - on 2026-08-14 a workflow holding 149 scarabs replanned against 60 lots of depth for
            // a projected -327 Chaos, 24 seconds after the same book showed roughly 140 - so this is
            // retryable on the same reasoning as an unavailable restoration edge above: reprobe, and let the
            // bounded budget hand it to the operator with a named reason if the book does not come back.
            //
            // Past the realization leg this does not apply, and deliberately so. Restoration is not an
            // opportunity to decline: the Chaos is already in hand against an outstanding principal debt,
            // OutstandingPrincipal and the partial-attempt machinery exist to grind that debt down at
            // whatever the book costs, and refusing an adverse restoration does not avoid a loss - it just
            // leaves the principal unrestored. WorkflowRestorationRetriesUnavailableQuotes pins that.
            if (workflow.CurrentLegIndex > 0 &&
                workflow.CurrentLegIndex <= workflow.ChaosRealizationLegIndex &&
                profit < 0)
            {
                failure = $"Refreshed closed-cycle profit {profit} Chaos is negative; the remaining plan " +
                    "would realize and close at a loss.";
                return WorkflowRefreshResult.RetryableUnavailable;
            }

            foreach (var item in refreshed) next.Legs[item.Plan.Index] = item.Plan;
            if (workflow.CurrentLegIndex == 0)
            {
                next.StartingPrincipal = startingPrincipal;
                if (restorationIndex >= 0)
                {
                    next.RestorationPrincipal = startingPrincipal;
                    next.RestoredPrincipal = 0;
                    next.OutstandingPrincipal = startingPrincipal;
                }
            }
            next.CurrentInputAmount = refreshed[0].Plan.InputAvailable;
            next.BenchmarkChaos = restorationIndex >= 0 ? plannedRestorationSpend : startingPrincipal;
            next.PlannedRealizedChaos = plannedChaosRealized;
            next.PlannedRestorationSpendChaos = plannedRestorationSpend;
            next.PlannedProfitChaos = profit;
            next.PlannedSettlementAmount = next.Legs[^1].Output;
            next.PlannedProfitAmount = profit;
            next.ValuedProfitChaos = profit;
            next.CurrentProbeSessionId = probeSessionId;
            next.UpdatedAtUtc = now;
            next.Detail = restorationIndex >= 0
                ? $"Fresh closed cycle verified: {plannedChaosRealized} Chaos realized, " +
                    $"{plannedRestorationSpend} Chaos to restore {next.OutstandingPrincipal} Divine, {profit} Chaos projected profit."
                : $"Fresh closed cycle verified at {profit} planned Chaos profit.";
            next.PlanFingerprint = ComputeFingerprint(next);
            var firstRefreshed = refreshed[0];
            currentLeg = new RouteLegResult(
                firstRefreshed.Edge,
                firstRefreshed.Plan.InputAvailable,
                firstRefreshed.Plan.InputSpent,
                firstRefreshed.Plan.Output,
                firstRefreshed.Plan.InputRemainder,
                firstRefreshed.Plan.ExpectedGold);
            failure = string.Empty;
            return WorkflowRefreshResult.Refreshed;
        }
        catch (OverflowException exception)
        {
            failure = $"Closed-cycle refresh arithmetic overflowed: {exception.Message}";
            return WorkflowRefreshResult.Failed;
        }
    }

    private static DirectedExchangeEdge? SelectFreshEdge(
        WorkflowLegPlan planned,
        IEnumerable<DirectedExchangeEdge> edges) => edges
        .Where(candidate => candidate.From.Metadata == planned.FromMetadata &&
            candidate.To.Metadata == planned.ToMetadata &&
            candidate.From.Hash == planned.FromHash && candidate.To.Hash == planned.ToHash &&
            candidate.ExecutionIntent == planned.ExecutionIntent)
        .OrderByDescending(candidate => candidate.Rate)
        .ThenByDescending(candidate => candidate.CapturedAt)
        .FirstOrDefault();

    private static WorkflowLegPlan FromEdge(
        int index,
        WorkflowLegRole role,
        long? expectedGold,
        DirectedExchangeEdge edge,
        long inputAvailable,
        long inputSpent,
        long output,
        long inputRemainder) => new()
    {
        Index = index,
        Role = role,
        FromMetadata = edge.From.Metadata,
        FromName = edge.From.Name,
        FromHash = edge.From.Hash,
        ToMetadata = edge.To.Metadata,
        ToName = edge.To.Name,
        ToHash = edge.To.Hash,
        RateNumerator = edge.Rate.Numerator,
        RateDenominator = edge.Rate.Denominator,
        ExecutionIntent = edge.ExecutionIntent,
        SourceBook = edge.SourceBook,
        InputAvailable = inputAvailable,
        InputSpent = inputSpent,
        Output = output,
        InputRemainder = inputRemainder,
        ExpectedGold = expectedGold,
    };

    private static long LeastCommonMultiple(long left, long right)
    {
        var value = (BigInteger)(left / Rational.GreatestCommonDivisor(left, right)) * right;
        if (value > long.MaxValue) throw new OverflowException("Required common quote lot exceeds Int64.");
        return (long)value;
    }

    public static void Validate(WorkflowExecutionState workflow, TrackedOrderState? tracked)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        if (workflow.SchemaVersion != WorkflowExecutionState.CurrentSchemaVersion ||
            workflow.WorkflowId == Guid.Empty || workflow.OriginProbeSessionId == Guid.Empty ||
            workflow.CurrentProbeSessionId == Guid.Empty ||
            string.IsNullOrWhiteSpace(workflow.League) || string.IsNullOrWhiteSpace(workflow.PlanFingerprint) ||
            string.IsNullOrWhiteSpace(workflow.TerminalChaosMetadata) ||
            string.IsNullOrWhiteSpace(workflow.StartingMetadata) || string.IsNullOrWhiteSpace(workflow.ChaosMetadata) ||
            workflow.ClosureMode is not WorkflowClosureMode.LegacyTerminalChaos and not WorkflowClosureMode.ClosedCycle and
                not WorkflowClosureMode.SameAssetCycle ||
            workflow.StartedAtUtc == default || workflow.UpdatedAtUtc == default ||
            workflow.StartingPrincipal <= 0 || workflow.BenchmarkChaos <= 0 ||
            workflow.RestorationPrincipal < 0 || workflow.RestoredPrincipal < 0 ||
            workflow.OutstandingPrincipal < 0 || workflow.PlannedRestorationSpendChaos < 0 ||
            workflow.CumulativeActualRestorationChaosSpent < 0 || workflow.ActualChaosRealized < 0 ||
            string.IsNullOrWhiteSpace(workflow.SettlementMetadata) ||
            string.IsNullOrWhiteSpace(workflow.ProfitMetadata) ||
            workflow.PlannedSettlementAmount <= 0 || workflow.ActualSettlementAmount < 0 ||
            workflow.PlannedProfitAmount <= 0 || workflow.ActualProfitAmount < 0 ||
            workflow.ValuedProfitChaos <= 0 || workflow.ProfitValuationNumerator < 0 ||
            workflow.ProfitValuationDenominator < 0 || workflow.CumulativeCycleSettlementAmount < 0 ||
            workflow.CurrentInputAmount <= 0 || workflow.Legs.Count is < 2 or > 3 ||
            workflow.StartingPrincipal != workflow.Legs[0].InputSpent ||
            workflow.PlanFingerprint != ComputeFingerprint(workflow))
        {
            throw new InvalidDataException("Workflow state failed identity, economics, or fingerprint validation.");
        }
        if (workflow.ClosureMode != WorkflowClosureMode.SameAssetCycle &&
            (workflow.ChaosRealizationLegIndex < 0 || workflow.ChaosRealizationLegIndex >= workflow.Legs.Count))
            throw new InvalidDataException("Workflow Chaos realization cursor was invalid.");

        if (workflow.ClosureMode == WorkflowClosureMode.LegacyTerminalChaos)
        {
            if (workflow.TerminalChaosMetadata != workflow.Legs[^1].ToMetadata ||
                workflow.ChaosMetadata != workflow.TerminalChaosMetadata ||
                workflow.ChaosRealizationLegIndex != workflow.Legs.Count - 1 ||
                workflow.Legs[^1].Role != WorkflowLegRole.ChaosRealization ||
                workflow.Legs.Any(leg => leg.Role == WorkflowLegRole.PrincipalRestoration) ||
                workflow.PlannedRealizedChaos != workflow.Legs[^1].Output ||
                workflow.PlannedProfitChaos != checked(workflow.PlannedRealizedChaos - workflow.BenchmarkChaos) ||
                workflow.RestorationPrincipal != 0 || workflow.RestoredPrincipal != 0 ||
                workflow.OutstandingPrincipal != 0 || workflow.PlannedRestorationSpendChaos != 0 ||
                workflow.CumulativeActualRestorationChaosSpent != 0)
            {
                throw new InvalidDataException("Legacy terminal-Chaos workflow economics were invalid.");
            }
        }
        else if (workflow.ClosureMode == WorkflowClosureMode.ClosedCycle)
        {
            ValidateClosedCycle(workflow);
        }
        else
        {
            ValidateSameAssetCycle(workflow);
        }

        var currentIndex = Math.Min(workflow.CurrentLegIndex, workflow.Legs.Count - 1);
        var expectedCurrentInput = workflow.CurrentLegIndex >= workflow.Legs.Count
            ? workflow.Legs[^1].Output
            : workflow.Legs[currentIndex].Role is WorkflowLegRole.PrincipalRestoration or WorkflowLegRole.CycleClosure
                ? workflow.Legs[currentIndex].InputAvailable
                : workflow.CurrentLegIndex == 0
                    ? workflow.StartingPrincipal
                    : workflow.Legs[workflow.CurrentLegIndex - 1].Output;
        if (workflow.CurrentInputAmount != expectedCurrentInput)
        {
            throw new InvalidDataException("Workflow current input did not match its durable leg cursor.");
        }
        for (var index = 0; index < workflow.Legs.Count; index++)
        {
            var leg = workflow.Legs[index];
            if (leg.Index != index || leg.Role is not WorkflowLegRole.Conversion and
                    not WorkflowLegRole.ChaosRealization and not WorkflowLegRole.PrincipalRestoration and
                    not WorkflowLegRole.CycleClosure ||
                leg.FromHash == 0 || leg.ToHash == 0 ||
                string.IsNullOrWhiteSpace(leg.FromMetadata) || string.IsNullOrWhiteSpace(leg.ToMetadata) ||
                leg.FromMetadata == leg.ToMetadata || leg.RateNumerator <= 0 || leg.RateDenominator <= 0 ||
                leg.InputAvailable <= 0 || leg.InputSpent <= 0 || leg.Output <= 0 ||
                leg.InputSpent > leg.InputAvailable || leg.InputRemainder != leg.InputAvailable - leg.InputSpent ||
                leg.InputSpent % leg.RateDenominator != 0 ||
                checked(leg.InputSpent / leg.RateDenominator * leg.RateNumerator) != leg.Output ||
                leg.InputAvailable > SingleLegStagingController.MaximumAmount ||
                leg.InputSpent > SingleLegStagingController.MaximumAmount ||
                leg.Output > SingleLegStagingController.MaximumAmount ||
                index > 0 && (workflow.Legs[index - 1].ToMetadata != leg.FromMetadata ||
                    leg.Role is not WorkflowLegRole.PrincipalRestoration and not WorkflowLegRole.CycleClosure &&
                    workflow.Legs[index - 1].Output != leg.InputAvailable))
            {
                throw new InvalidDataException($"Workflow leg {index + 1} failed exact plan validation.");
            }
        }
        var indexValid = workflow.Phase switch
        {
            WorkflowExecutionPhase.Completed => workflow.CurrentLegIndex == workflow.Legs.Count && workflow.CurrentAttemptId is null,
            WorkflowExecutionPhase.ReadyForLeg => workflow.CurrentLegIndex >= 0 && workflow.CurrentLegIndex < workflow.Legs.Count &&
                workflow.CurrentAttemptId is null && tracked?.IsUnresolved != true,
            WorkflowExecutionPhase.LegActive => workflow.CurrentLegIndex >= 0 && workflow.CurrentLegIndex < workflow.Legs.Count &&
                workflow.CurrentAttemptId is not null && tracked is not null && tracked.IsUnresolved &&
                tracked.AttemptId == workflow.CurrentAttemptId &&
                TrackedMatchesLeg(workflow, tracked, workflow.Legs[workflow.CurrentLegIndex]),
            WorkflowExecutionPhase.Stopped or WorkflowExecutionPhase.Ambiguous =>
                workflow.CurrentLegIndex >= 0 && workflow.CurrentLegIndex <= workflow.Legs.Count && workflow.CurrentAttemptId is null,
            _ => false,
        };
        if (!indexValid)
        {
            throw new InvalidDataException("Workflow phase, leg cursor, and tracked order were inconsistent.");
        }
    }

    private static void ValidateSameAssetCycle(WorkflowExecutionState workflow)
    {
        var closure = workflow.Legs.Where(leg => leg.Role == WorkflowLegRole.CycleClosure).ToArray();
        if (workflow.Legs.Count != 2 || closure.Length != 1 || closure[0].Index != workflow.Legs.Count - 1 ||
            workflow.Legs[0].FromMetadata != workflow.StartingMetadata ||
            workflow.Legs[^1].ToMetadata != workflow.StartingMetadata ||
            workflow.SettlementMetadata != workflow.StartingMetadata ||
            workflow.ProfitMetadata != workflow.StartingMetadata ||
            workflow.ChaosRealizationLegIndex != -1 ||
            workflow.RestorationPrincipal != 0 || workflow.RestoredPrincipal != 0 ||
            workflow.OutstandingPrincipal != 0 || workflow.PlannedRestorationSpendChaos != 0 ||
            workflow.CumulativeActualRestorationChaosSpent != 0 || workflow.ActualChaosRealized != 0 ||
            workflow.Phase != WorkflowExecutionPhase.Completed &&
                workflow.PlannedSettlementAmount != checked(
                    workflow.CumulativeCycleSettlementAmount + closure[0].Output) ||
            workflow.PlannedProfitAmount != checked(workflow.PlannedSettlementAmount - workflow.StartingPrincipal) ||
            workflow.PlannedProfitAmount <= 0 || workflow.PlannedProfitChaos != workflow.ValuedProfitChaos ||
            workflow.ProfitValuationNumerator <= 0 || workflow.ProfitValuationDenominator <= 0)
        {
            throw new InvalidDataException("Same-asset cycle identity or unit-aware economics were invalid.");
        }
        if (workflow.Phase == WorkflowExecutionPhase.Completed &&
            (workflow.ActualSettlementAmount != workflow.CumulativeCycleSettlementAmount ||
             workflow.ActualProfitAmount != checked(workflow.ActualSettlementAmount - workflow.StartingPrincipal) ||
             workflow.ActualProfitAmount <= 0))
            throw new InvalidDataException("Completed same-asset cycle lacked positive actual settlement profit.");
    }

    private static void ValidateClosedCycle(WorkflowExecutionState workflow)
    {
        var realization = workflow.Legs[workflow.ChaosRealizationLegIndex];
        if (workflow.Legs[0].FromMetadata != workflow.StartingMetadata ||
            workflow.Legs[^1].ToMetadata != workflow.StartingMetadata ||
            realization.Role != WorkflowLegRole.ChaosRealization ||
            realization.ToMetadata != workflow.ChaosMetadata ||
            workflow.TerminalChaosMetadata != workflow.ChaosMetadata ||
            workflow.PlannedRealizedChaos != realization.Output)
        {
            throw new InvalidDataException("Closed-cycle identity or Chaos realization economics were invalid.");
        }

        var restorationLegs = workflow.Legs.Where(leg => leg.Role == WorkflowLegRole.PrincipalRestoration).ToArray();
        if (workflow.RestorationPrincipal == 0)
        {
            if (workflow.StartingMetadata != workflow.ChaosMetadata || restorationLegs.Length != 0 ||
                workflow.ChaosRealizationLegIndex != workflow.Legs.Count - 1 ||
                workflow.RestoredPrincipal != 0 || workflow.OutstandingPrincipal != 0 ||
                workflow.PlannedRestorationSpendChaos != 0 ||
                workflow.CumulativeActualRestorationChaosSpent != 0 ||
                workflow.PlannedProfitChaos != checked(workflow.PlannedRealizedChaos - workflow.StartingPrincipal))
            {
                throw new InvalidDataException("Chaos-funded closed-cycle economics were invalid.");
            }
            return;
        }

        if (workflow.StartingMetadata == workflow.ChaosMetadata || restorationLegs.Length != 1 ||
            restorationLegs[0].Index != workflow.Legs.Count - 1 ||
            restorationLegs[0].ExecutionIntent != QuoteExecutionIntent.Immediate ||
            restorationLegs[0].FromMetadata != workflow.ChaosMetadata ||
            workflow.ChaosRealizationLegIndex != workflow.Legs.Count - 2 ||
            workflow.RestorationPrincipal != workflow.StartingPrincipal ||
            checked(workflow.RestoredPrincipal + workflow.OutstandingPrincipal) != workflow.RestorationPrincipal ||
            workflow.PlannedRestorationSpendChaos < workflow.CumulativeActualRestorationChaosSpent ||
            workflow.PlannedProfitChaos != checked(
                workflow.PlannedRealizedChaos - workflow.PlannedRestorationSpendChaos))
        {
            throw new InvalidDataException("Divine restoration economics were invalid.");
        }

        if (workflow.Phase == WorkflowExecutionPhase.Completed)
        {
            if (workflow.OutstandingPrincipal != 0 || workflow.RestoredPrincipal != workflow.RestorationPrincipal ||
                workflow.ActualChaosRealized <= 0)
                throw new InvalidDataException("Completed restoration did not restore the exact authenticated principal.");
        }
        else if (restorationLegs[0].Output != workflow.OutstandingPrincipal)
        {
            throw new InvalidDataException("Restoration leg did not target the exact outstanding principal.");
        }
    }

    public static string ComputeFingerprint(WorkflowExecutionState workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        var legs = string.Join("|", workflow.Legs.Select(leg =>
            $"{leg.Index}:{leg.Role}:{leg.FromMetadata}:{leg.FromHash}>{leg.ToMetadata}:{leg.ToHash}:" +
            $"{leg.ExecutionIntent}:{leg.SourceBook}:{leg.RateNumerator}/{leg.RateDenominator}:" +
            $"{leg.InputAvailable}:{leg.InputSpent}:{leg.Output}:{leg.InputRemainder}:{leg.ExpectedGold?.ToString() ?? "unknown"}"));
        var canonical = $"{workflow.SchemaVersion}:{workflow.ClosureMode}:{workflow.CurrentProbeSessionId:D}:" +
            $"{workflow.CurrentLegIndex}:{workflow.CurrentInputAmount}:" +
            $"{workflow.TerminalChaosMetadata}:{workflow.StartingPrincipal}:" +
            $"{workflow.BenchmarkChaos}:{workflow.PlannedRealizedChaos}:{workflow.PlannedProfitChaos}:" +
            $"{workflow.StartingMetadata}:{workflow.ChaosMetadata}:{workflow.ChaosRealizationLegIndex}:" +
            $"{workflow.RestorationPrincipal}:{workflow.RestoredPrincipal}:{workflow.OutstandingPrincipal}:" +
            $"{workflow.PlannedRestorationSpendChaos}:{workflow.CumulativeActualRestorationChaosSpent}:" +
            $"{workflow.ActualChaosRealized}:{workflow.SettlementMetadata}:{workflow.PlannedSettlementAmount}:" +
            $"{workflow.ActualSettlementAmount}:{workflow.ProfitMetadata}:{workflow.PlannedProfitAmount}:" +
            $"{workflow.ActualProfitAmount}:{workflow.ValuedProfitChaos}:{workflow.ProfitValuationNumerator}/" +
            $"{workflow.ProfitValuationDenominator}:{workflow.CumulativeCycleSettlementAmount}|{legs}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public static bool LegacyVersionThreeFingerprintMatches(WorkflowExecutionState workflow) =>
        workflow.PlanFingerprint == ComputeLegacyVersionThreeFingerprint(workflow);

    public static string ComputeLegacyVersionThreeFingerprint(WorkflowExecutionState workflow)
    {
        var legs = string.Join("|", workflow.Legs.Select(leg =>
            $"{leg.Index}:{leg.Role}:{leg.FromMetadata}:{leg.FromHash}>{leg.ToMetadata}:{leg.ToHash}:" +
            $"{leg.ExecutionIntent}:{leg.SourceBook}:{leg.RateNumerator}/{leg.RateDenominator}:" +
            $"{leg.InputAvailable}:{leg.InputSpent}:{leg.Output}:{leg.InputRemainder}:{leg.ExpectedGold?.ToString() ?? "unknown"}"));
        var canonical = $"3:{workflow.ClosureMode}:{workflow.CurrentProbeSessionId:D}:" +
            $"{workflow.CurrentLegIndex}:{workflow.CurrentInputAmount}:" +
            $"{workflow.TerminalChaosMetadata}:{workflow.StartingPrincipal}:" +
            $"{workflow.BenchmarkChaos}:{workflow.PlannedRealizedChaos}:{workflow.PlannedProfitChaos}:" +
            $"{workflow.StartingMetadata}:{workflow.ChaosMetadata}:{workflow.ChaosRealizationLegIndex}:" +
            $"{workflow.RestorationPrincipal}:{workflow.RestoredPrincipal}:{workflow.OutstandingPrincipal}:" +
            $"{workflow.PlannedRestorationSpendChaos}:{workflow.CumulativeActualRestorationChaosSpent}:" +
            $"{workflow.ActualChaosRealized}|{legs}";
        return Hash(canonical);
    }

    public static bool LegacyVersionTwoFingerprintMatches(WorkflowExecutionState workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        return workflow.PlanFingerprint == ComputeLegacyVersionTwoFingerprint(workflow);
    }

    public static string ComputeLegacyVersionTwoFingerprint(WorkflowExecutionState workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        var legs = string.Join("|", workflow.Legs.Select(leg =>
            $"{leg.Index}:{leg.FromMetadata}:{leg.FromHash}>{leg.ToMetadata}:{leg.ToHash}:" +
            $"{leg.ExecutionIntent}:{leg.SourceBook}:{leg.RateNumerator}/{leg.RateDenominator}:" +
            $"{leg.InputAvailable}:{leg.InputSpent}:{leg.Output}:{leg.InputRemainder}:{leg.ExpectedGold?.ToString() ?? "unknown"}"));
        var canonical = $"{workflow.CurrentProbeSessionId:D}:{workflow.CurrentLegIndex}:{workflow.CurrentInputAmount}:" +
            $"{workflow.TerminalChaosMetadata}:{workflow.StartingPrincipal}:" +
            $"{workflow.BenchmarkChaos}:{workflow.PlannedRealizedChaos}:{workflow.PlannedProfitChaos}|{legs}";
        return Hash(canonical);
    }

    public static bool LegacyVersionOneFingerprintMatches(WorkflowExecutionState workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        var economicsFingerprint = ComputeLegacyVersionOneFingerprint(workflow);
        var legOnlyCanonical = string.Join("|", workflow.Legs.Select(leg =>
            $"{leg.Index}:{leg.FromMetadata}:{leg.FromHash}>{leg.ToMetadata}:{leg.ToHash}:" +
            $"{leg.ExecutionIntent}:{leg.SourceBook}:{leg.RateNumerator}/{leg.RateDenominator}:" +
            $"{leg.InputAvailable}:{leg.InputSpent}:{leg.Output}:{leg.InputRemainder}"));
        return workflow.PlanFingerprint == economicsFingerprint || workflow.PlanFingerprint == Hash(legOnlyCanonical);
    }

    public static string ComputeLegacyVersionOneFingerprint(WorkflowExecutionState workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        var withEconomics = string.Join("|", workflow.Legs.Select(leg =>
            $"{leg.Index}:{leg.FromMetadata}:{leg.FromHash}>{leg.ToMetadata}:{leg.ToHash}:" +
            $"{leg.ExecutionIntent}:{leg.SourceBook}:{leg.RateNumerator}/{leg.RateDenominator}:" +
            $"{leg.InputAvailable}:{leg.InputSpent}:{leg.Output}:{leg.InputRemainder}:{leg.ExpectedGold?.ToString() ?? "unknown"}"));
        var economicsCanonical = $"{workflow.TerminalChaosMetadata}:{workflow.StartingPrincipal}:" +
            $"{workflow.BenchmarkChaos}:{workflow.PlannedRealizedChaos}:{workflow.PlannedProfitChaos}|{withEconomics}";
        return Hash(economicsCanonical);
    }

    public static bool LegacyActiveTrackedMatches(
        WorkflowExecutionState workflow,
        TrackedOrderState tracked,
        string legacyFingerprint)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(tracked);
        if (workflow.Phase != WorkflowExecutionPhase.LegActive || workflow.CurrentAttemptId != tracked.AttemptId ||
            workflow.CurrentLegIndex < 0 || workflow.CurrentLegIndex >= workflow.Legs.Count ||
            tracked.League != workflow.League || tracked.CandidateSignature != legacyFingerprint ||
            tracked.ProbeSessionId == Guid.Empty)
        {
            return false;
        }
        var leg = workflow.Legs[workflow.CurrentLegIndex];
        return tracked.OfferedMetadata == leg.FromMetadata && tracked.WantedMetadata == leg.ToMetadata &&
            tracked.OfferedHash == leg.FromHash && tracked.WantedHash == leg.ToHash &&
            tracked.OfferedAmount == leg.InputSpent && tracked.WantedAmount == leg.Output &&
            PlacedRatioAllowed(tracked, leg);
    }

    public static void MigrateLegacySemantics(WorkflowExecutionState workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        if (workflow.Legs.Count == 0) throw new InvalidDataException("Legacy workflow had no legs.");
        workflow.SchemaVersion = WorkflowExecutionState.CurrentSchemaVersion;
        workflow.ClosureMode = WorkflowClosureMode.LegacyTerminalChaos;
        workflow.StartingMetadata = workflow.Legs[0].FromMetadata;
        workflow.ChaosMetadata = workflow.TerminalChaosMetadata;
        workflow.ChaosRealizationLegIndex = workflow.Legs.Count - 1;
        workflow.RestorationPrincipal = 0;
        workflow.RestoredPrincipal = 0;
        workflow.OutstandingPrincipal = 0;
        workflow.PlannedRestorationSpendChaos = 0;
        workflow.CumulativeActualRestorationChaosSpent = 0;
        workflow.ActualChaosRealized = workflow.Phase == WorkflowExecutionPhase.Completed
            ? workflow.PlannedRealizedChaos
            : 0;
        foreach (var leg in workflow.Legs) leg.Role = WorkflowLegRole.Conversion;
        workflow.Legs[^1].Role = WorkflowLegRole.ChaosRealization;
        ApplyUnitEconomicsForExistingWorkflow(workflow);
    }

    public static void MigrateVersionThreeUnitEconomics(WorkflowExecutionState workflow)
    {
        workflow.SchemaVersion = WorkflowExecutionState.CurrentSchemaVersion;
        ApplyUnitEconomicsForExistingWorkflow(workflow);
    }

    private static void ApplyUnitEconomicsForExistingWorkflow(WorkflowExecutionState workflow)
    {
        workflow.SettlementMetadata = workflow.Legs[^1].ToMetadata;
        workflow.PlannedSettlementAmount = workflow.Legs[^1].Output;
        workflow.ProfitMetadata = workflow.ChaosMetadata;
        workflow.PlannedProfitAmount = workflow.PlannedProfitChaos;
        workflow.ValuedProfitChaos = workflow.PlannedProfitChaos;
        workflow.ProfitValuationNumerator = 0;
        workflow.ProfitValuationDenominator = 0;
        workflow.CumulativeCycleSettlementAmount = 0;
        if (workflow.Phase == WorkflowExecutionPhase.Completed)
        {
            workflow.ActualSettlementAmount = workflow.RestorationPrincipal > 0
                ? workflow.RestorationPrincipal
                : workflow.ActualChaosRealized > 0
                    ? workflow.ActualChaosRealized
                    : workflow.PlannedRealizedChaos;
            workflow.ActualProfitAmount = workflow.RestorationPrincipal > 0
                ? Math.Max(0, workflow.ActualChaosRealized - workflow.CumulativeActualRestorationChaosSpent)
                : Math.Max(0, workflow.ActualSettlementAmount - workflow.StartingPrincipal);
        }
        else
        {
            workflow.ActualSettlementAmount = 0;
            workflow.ActualProfitAmount = 0;
        }
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static WorkflowLegPlan FromRouteLeg(int index, RouteLegResult leg, WorkflowLegRole role) => new()
    {
        Index = index,
        Role = role,
        FromMetadata = leg.Edge.From.Metadata,
        FromName = leg.Edge.From.Name,
        FromHash = leg.Edge.From.Hash,
        ToMetadata = leg.Edge.To.Metadata,
        ToName = leg.Edge.To.Name,
        ToHash = leg.Edge.To.Hash,
        RateNumerator = leg.Edge.Rate.Numerator,
        RateDenominator = leg.Edge.Rate.Denominator,
        ExecutionIntent = leg.Edge.ExecutionIntent,
        SourceBook = leg.Edge.SourceBook,
        InputAvailable = leg.InputAvailable,
        InputSpent = leg.InputSpent,
        Output = leg.Output,
        InputRemainder = leg.InputRemainder,
        ExpectedGold = leg.ExpectedGold,
    };

    private static bool TrackedMatchesLeg(
        WorkflowExecutionState workflow,
        TrackedOrderState tracked,
        WorkflowLegPlan leg) =>
        tracked.AttemptId != Guid.Empty && tracked.League == workflow.League &&
        tracked.ProbeSessionId == workflow.CurrentProbeSessionId &&
        tracked.CandidateSignature == workflow.PlanFingerprint &&
        tracked.OfferedMetadata == leg.FromMetadata &&
        tracked.WantedMetadata == leg.ToMetadata && tracked.OfferedHash == leg.FromHash &&
        tracked.WantedHash == leg.ToHash && tracked.OfferedAmount == leg.InputSpent &&
        tracked.WantedAmount == leg.Output && PlacedRatioAllowed(tracked, leg);

    /// <summary>
    /// A tracked state that knows its placed ratio must prove it equals the leg rate. A state that
    /// never learned one may only be an armed intent or an ambiguity record; rejecting those would
    /// discard the very evidence that makes an uncertain placement reconcilable.
    /// </summary>
    private static bool PlacedRatioAllowed(TrackedOrderState tracked, WorkflowLegPlan leg) =>
        tracked.PlacedOfferedRatioPart is null && tracked.PlacedWantedRatioPart is null
            ? tracked.Status is TrackedOrderStatus.Armed or TrackedOrderStatus.Ambiguous
            : tracked.PlacedOfferedRatioPart is > 0 && tracked.PlacedWantedRatioPart is > 0 &&
              new Rational(tracked.PlacedWantedRatioPart.Value, tracked.PlacedOfferedRatioPart.Value) ==
                  new Rational(leg.RateNumerator, leg.RateDenominator);

    public static WorkflowExecutionState Clone(WorkflowExecutionState state) => new()
    {
        SchemaVersion = state.SchemaVersion,
        WorkflowId = state.WorkflowId,
        League = state.League,
        OriginProbeSessionId = state.OriginProbeSessionId,
        CurrentProbeSessionId = state.CurrentProbeSessionId,
        PlanFingerprint = state.PlanFingerprint,
        Phase = state.Phase,
        CurrentLegIndex = state.CurrentLegIndex,
        CurrentAttemptId = state.CurrentAttemptId,
        CurrentInputAmount = state.CurrentInputAmount,
        TerminalChaosMetadata = state.TerminalChaosMetadata,
        StartingPrincipal = state.StartingPrincipal,
        BenchmarkChaos = state.BenchmarkChaos,
        PlannedRealizedChaos = state.PlannedRealizedChaos,
        PlannedProfitChaos = state.PlannedProfitChaos,
        ClosureMode = state.ClosureMode,
        StartingMetadata = state.StartingMetadata,
        ChaosMetadata = state.ChaosMetadata,
        ChaosRealizationLegIndex = state.ChaosRealizationLegIndex,
        RestorationPrincipal = state.RestorationPrincipal,
        RestoredPrincipal = state.RestoredPrincipal,
        OutstandingPrincipal = state.OutstandingPrincipal,
        PlannedRestorationSpendChaos = state.PlannedRestorationSpendChaos,
        CumulativeActualRestorationChaosSpent = state.CumulativeActualRestorationChaosSpent,
        ActualChaosRealized = state.ActualChaosRealized,
        SettlementMetadata = state.SettlementMetadata,
        PlannedSettlementAmount = state.PlannedSettlementAmount,
        ActualSettlementAmount = state.ActualSettlementAmount,
        ProfitMetadata = state.ProfitMetadata,
        PlannedProfitAmount = state.PlannedProfitAmount,
        ActualProfitAmount = state.ActualProfitAmount,
        ValuedProfitChaos = state.ValuedProfitChaos,
        ProfitValuationNumerator = state.ProfitValuationNumerator,
        ProfitValuationDenominator = state.ProfitValuationDenominator,
        CumulativeCycleSettlementAmount = state.CumulativeCycleSettlementAmount,
        Legs = state.Legs.Select(leg => new WorkflowLegPlan
        {
            Index = leg.Index,
            Role = leg.Role,
            FromMetadata = leg.FromMetadata,
            FromName = leg.FromName,
            FromHash = leg.FromHash,
            ToMetadata = leg.ToMetadata,
            ToName = leg.ToName,
            ToHash = leg.ToHash,
            RateNumerator = leg.RateNumerator,
            RateDenominator = leg.RateDenominator,
            ExecutionIntent = leg.ExecutionIntent,
            SourceBook = leg.SourceBook,
            InputAvailable = leg.InputAvailable,
            InputSpent = leg.InputSpent,
            Output = leg.Output,
            InputRemainder = leg.InputRemainder,
            ExpectedGold = leg.ExpectedGold,
        }).ToList(),
        StartedAtUtc = state.StartedAtUtc,
        UpdatedAtUtc = state.UpdatedAtUtc,
        Detail = state.Detail,
    };
}
