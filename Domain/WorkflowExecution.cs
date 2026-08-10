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
}

public enum WorkflowLegRole
{
    Conversion = 1,
    ChaosRealization = 2,
    PrincipalRestoration = 3,
}

public enum WorkflowRefreshResult
{
    Refreshed,
    RetryableUnavailable,
    Failed,
}

public sealed class WorkflowExecutionState
{
    public const int CurrentSchemaVersion = 3;

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

        var chaosRealizationLegIndex = candidate.ChaosRealizationLegIndex >= 0
            ? candidate.ChaosRealizationLegIndex
            : candidate.Legs.Count - 1;

        var state = new WorkflowExecutionState
        {
            WorkflowId = Guid.NewGuid(),
            League = league,
            OriginProbeSessionId = probeSessionId,
            CurrentProbeSessionId = probeSessionId,
            Phase = WorkflowExecutionPhase.ReadyForLeg,
            CurrentLegIndex = 0,
            CurrentInputAmount = candidate.StartingPrincipal,
            TerminalChaosMetadata = candidate.Path[chaosRealizationLegIndex + 1].Metadata,
            StartingPrincipal = candidate.StartingPrincipal,
            BenchmarkChaos = candidate.BenchmarkChaos,
            PlannedRealizedChaos = candidate.RealizedChaos,
            PlannedProfitChaos = candidate.ProfitChaos,
            ClosureMode = WorkflowClosureMode.ClosedCycle,
            StartingMetadata = candidate.Path[0].Metadata,
            ChaosMetadata = candidate.Path[chaosRealizationLegIndex + 1].Metadata,
            ChaosRealizationLegIndex = chaosRealizationLegIndex,
            RestorationPrincipal = candidate.RestorationPrincipal,
            RestoredPrincipal = 0,
            OutstandingPrincipal = candidate.RestorationPrincipal,
            PlannedRestorationSpendChaos = candidate.PlannedRestorationSpendChaos,
            CumulativeActualRestorationChaosSpent = 0,
            ActualChaosRealized = 0,
            StartedAtUtc = now,
            UpdatedAtUtc = now,
            Detail = "Exact route snapshot persisted; current leg requires fresh probing before placement.",
            Legs = candidate.Legs.Select((leg, index) => FromRouteLeg(
                index,
                leg,
                index == chaosRealizationLegIndex
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
            failure = string.Empty;
            return true;
        }

        next.CurrentLegIndex++;
        next.CurrentInputAmount = tracked.TerminalReceivedWantedAmount.GetValueOrDefault();
        if (next.CurrentLegIndex == next.Legs.Count)
        {
            next.Phase = WorkflowExecutionPhase.Completed;
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
        out string failure)
    {
        return workflow.ClosureMode == WorkflowClosureMode.LegacyTerminalChaos
            ? TryRefreshLegacyRemainingPlan(workflow, edges, probeSessionId, spendCap, minimumProfitChaos,
                now, out next, out currentLeg, out failure)
            : TryRefreshClosedRemainingPlan(workflow, edges, probeSessionId, spendCap, minimumProfitChaos,
                now, out next, out currentLeg, out failure);
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
        out string failure)
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
                    var preserved = FromEdge(index, planned.Role, planned.ExpectedGold,
                        edge with
                        {
                            Rate = new Rational(planned.RateNumerator, planned.RateDenominator),
                            SourceBook = planned.SourceBook,
                        },
                        amount, planned.InputSpent, planned.Output, checked(amount - planned.InputSpent));
                    refreshed.Add((preserved, edge with
                    {
                        Rate = new Rational(planned.RateNumerator, planned.RateDenominator),
                        SourceBook = planned.SourceBook,
                    }));
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
                    var plan = FromEdge(index, planned.Role, planned.ExpectedGold, edge,
                        amount, conversion.InputSpent, conversion.Output, conversion.InputRemainder);
                    refreshed.Add((plan, edge));
                    amount = conversion.Output;
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
            workflow.ClosureMode is not WorkflowClosureMode.LegacyTerminalChaos and not WorkflowClosureMode.ClosedCycle ||
            workflow.StartedAtUtc == default || workflow.UpdatedAtUtc == default ||
            workflow.StartingPrincipal <= 0 || workflow.BenchmarkChaos <= 0 ||
            workflow.RestorationPrincipal < 0 || workflow.RestoredPrincipal < 0 ||
            workflow.OutstandingPrincipal < 0 || workflow.PlannedRestorationSpendChaos < 0 ||
            workflow.CumulativeActualRestorationChaosSpent < 0 || workflow.ActualChaosRealized < 0 ||
            workflow.CurrentInputAmount <= 0 || workflow.Legs.Count is < 2 or > 3 ||
            workflow.StartingPrincipal != workflow.Legs[0].InputSpent ||
            workflow.PlanFingerprint != ComputeFingerprint(workflow))
        {
            throw new InvalidDataException("Workflow state failed identity, economics, or fingerprint validation.");
        }
        if (workflow.ChaosRealizationLegIndex < 0 || workflow.ChaosRealizationLegIndex >= workflow.Legs.Count)
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
        else
        {
            ValidateClosedCycle(workflow);
        }

        var currentIndex = Math.Min(workflow.CurrentLegIndex, workflow.Legs.Count - 1);
        var expectedCurrentInput = workflow.CurrentLegIndex >= workflow.Legs.Count
            ? workflow.Legs[^1].Output
            : workflow.Legs[currentIndex].Role == WorkflowLegRole.PrincipalRestoration
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
                    not WorkflowLegRole.ChaosRealization and not WorkflowLegRole.PrincipalRestoration ||
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
                    leg.Role != WorkflowLegRole.PrincipalRestoration &&
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
            $"{workflow.ActualChaosRealized}|{legs}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
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
