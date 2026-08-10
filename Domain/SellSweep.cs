using FaustusControllerLite.Orders;
using FaustusControllerLite.Probing;

namespace FaustusControllerLite.Domain;

public enum SellSweepPhase
{
    ReadyForCandidate,
    OrderLive,
    Completed,
    Stopped,
    Ambiguous,
}

public enum SellSweepCandidateOutcome
{
    Pending,
    Sold,
    Skipped,
    Failed,
}

public enum SellSweepDirectiveKind
{
    None,
    RescanAndPlanCurrentCandidate,
    PlaceCurrentCandidate,
    ObserveCurrentOrder,
    AuthorizeCancellation,
    RecoverCancellationWithoutRetry,
    AuthorizeSettlementCollection,
    RecoverSettlementCollectionWithoutRetry,
    AuthorizeStashReturn,
    RecoverStashReturnWithoutRetry,
    AdvanceToNextCandidate,
    ManualReconciliationRequired,
}

public sealed class SellSweepCandidate
{
    public int Index { get; set; }
    public string Metadata { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public long HoldingAtScan { get; set; }
    public long PlannedProceedsChaos { get; set; }
    public string PlannedSignature { get; set; } = string.Empty;
    public string PlannedProceedsMetadata { get; set; } = string.Empty;
    public long PlannedInputSpent { get; set; }
    public long PlannedOutput { get; set; }
    public long PlannedInputRemainder { get; set; }
    public Rational? PlannedProceedsToChaosRate { get; set; }
    public SellSweepCandidateOutcome Outcome { get; set; } = SellSweepCandidateOutcome.Pending;
    public long RealizedProceedsChaos { get; set; }
    public string Detail { get; set; } = string.Empty;
}

public sealed class SellSweepState
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public Guid SweepId { get; set; }
    public string League { get; set; } = string.Empty;
    public Guid OriginProbeSessionId { get; set; }
    public SellSweepExecutionMode ExecutionMode { get; set; } = SellSweepExecutionMode.MostCurrency;
    public SellSweepPhase Phase { get; set; }
    public int CurrentIndex { get; set; }
    public Guid? CurrentAttemptId { get; set; }

    /// <summary>
    /// The quote the current candidate is prepared against. Empty means the candidate still needs
    /// a fresh re-plan before any placement input is authorized.
    /// </summary>
    public string PreparedSignature { get; set; } = string.Empty;

    public long MinimumSaleChaos { get; set; }
    public long RealizedProceedsChaos { get; set; }
    public List<SellSweepCandidate> Candidates { get; set; } = [];
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public string Detail { get; set; } = string.Empty;

    public bool IsActive => Phase is SellSweepPhase.ReadyForCandidate or SellSweepPhase.OrderLive;

    public SellSweepCandidate? Current =>
        CurrentIndex >= 0 && CurrentIndex < Candidates.Count ? Candidates[CurrentIndex] : null;
}

/// <summary>
/// One holding paired with the planner's verdict for it. The caller does the market evaluation;
/// this record is the pure input the plan is built from.
/// </summary>
public sealed record SellSweepEvaluation(
    string Metadata,
    string Name,
    long Holding,
    SellCandidateResult Result);

/// <summary>
/// One recognised holding, unpriced. A just-in-time sweep has to rank before it knows value, so
/// the queue is built from these and each entry is priced only when the sweep reaches it.
/// </summary>
public sealed record SellSweepHolding(
    string Metadata,
    string Name,
    long Holding);

public static class SellSweepPlanner
{
    /// <summary>
    /// Orders accepted candidates by realizable proceeds, descending. Selling the most valuable
    /// holding first means an interruption costs the least remaining value, and it is a total
    /// order (metadata breaks ties) so the same stash always produces the same plan.
    /// </summary>
    public static SellSweepState Build(
        string league,
        Guid probeSessionId,
        long minimumSaleChaos,
        IReadOnlyList<SellSweepEvaluation> evaluations,
        DateTimeOffset now,
        SellSweepExecutionMode executionMode = SellSweepExecutionMode.MostCurrency)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(league);
        ArgumentNullException.ThrowIfNull(evaluations);
        if (probeSessionId == Guid.Empty)
        {
            throw new ArgumentException("A sweep requires a probe session.", nameof(probeSessionId));
        }
        if (minimumSaleChaos < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumSaleChaos));
        }

        var ordered = evaluations
            .Where(evaluation => evaluation.Result.Accepted)
            .OrderByDescending(evaluation => evaluation.Result.Best!.ProceedsChaos)
            .ThenBy(evaluation => evaluation.Metadata, StringComparer.Ordinal)
            .Select((evaluation, index) => new SellSweepCandidate
            {
                Index = index,
                Metadata = evaluation.Metadata,
                Name = evaluation.Name,
                HoldingAtScan = evaluation.Holding,
                PlannedProceedsChaos = evaluation.Result.Best!.ProceedsChaos,
                PlannedSignature = evaluation.Result.Best!.Signature,
                Outcome = SellSweepCandidateOutcome.Pending,
                Detail = $"Planned {evaluation.Result.Best!.InputSpent} of {evaluation.Holding} " +
                    $"{evaluation.Name} for {evaluation.Result.Best!.Output} " +
                    $"{evaluation.Result.Best!.Proceeds.Name} " +
                    $"({evaluation.Result.Best!.ProceedsChaos} Chaos).",
            })
            .ToList();

        var skipped = evaluations.Count - ordered.Count;
        var state = new SellSweepState
        {
            SweepId = Guid.NewGuid(),
            League = league,
            OriginProbeSessionId = probeSessionId,
            ExecutionMode = executionMode,
            Phase = ordered.Count == 0 ? SellSweepPhase.Completed : SellSweepPhase.ReadyForCandidate,
            CurrentIndex = 0,
            MinimumSaleChaos = minimumSaleChaos,
            Candidates = ordered,
            StartedAtUtc = now,
            UpdatedAtUtc = now,
            Detail = ordered.Count == 0
                ? $"No holding cleared the {minimumSaleChaos} Chaos minimum in " +
                    $"{SellSweepExecutionModes.ToLabel(executionMode)} mode; {skipped} skipped."
                : $"{ordered.Count} candidates planned in {SellSweepExecutionModes.ToLabel(executionMode)} mode, " +
                    $"{skipped} skipped below the " +
                    $"{minimumSaleChaos} Chaos minimum; each needs a fresh re-plan before placement.",
        };
        return state;
    }

    /// <summary>
    /// Builds an unpriced queue for a just-in-time sweep. Proceeds are unknowable before the sweep
    /// probes each candidate own markets, so ordering falls back to stack quantity descending with
    /// metadata breaking ties: a total order, so the same stash always produces the same queue.
    /// Every candidate starts with an empty <see cref="SellSweepState.PreparedSignature"/>, which
    /// is exactly what makes the coordinator demand a fresh probe before authorizing placement.
    /// </summary>
    /// <param name="smallestFirst">
    /// Reverses the quantity ordering so the smallest stack is swept first. This is the operator's
    /// choice for a first live test - the cheapest stack is the cheapest mistake - and it changes
    /// nothing except order: the same holdings are queued, and ties still break on metadata, so the
    /// queue stays a total order and the same stash still produces the same plan.
    /// </param>
    public static SellSweepState BuildQueue(
        string league,
        Guid probeSessionId,
        long minimumSaleChaos,
        IReadOnlyList<SellSweepHolding> holdings,
        DateTimeOffset now,
        bool smallestFirst = false,
        SellSweepExecutionMode executionMode = SellSweepExecutionMode.MostCurrency)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(league);
        ArgumentNullException.ThrowIfNull(holdings);
        if (probeSessionId == Guid.Empty)
        {
            throw new ArgumentException("A sweep requires a probe session.", nameof(probeSessionId));
        }
        if (minimumSaleChaos < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumSaleChaos));
        }

        // An empty or negative stack is not a candidate: it can never satisfy the whole-lot rule,
        // and queueing it would spend a probe to learn nothing.
        var eligible = holdings.Where(holding => holding.Holding > 0);
        var ordered = (smallestFirst
                ? eligible.OrderBy(holding => holding.Holding)
                : eligible.OrderByDescending(holding => holding.Holding))
            .ThenBy(holding => holding.Metadata, StringComparer.Ordinal)
            .Select((holding, index) => new SellSweepCandidate
            {
                Index = index,
                Metadata = holding.Metadata,
                Name = holding.Name,
                HoldingAtScan = holding.Holding,
                PlannedProceedsChaos = 0,
                PlannedSignature = string.Empty,
                Outcome = SellSweepCandidateOutcome.Pending,
                Detail = $"Queued {holding.Holding} {holding.Name}; unpriced until the sweep probes it.",
            })
            .ToList();

        var empty = holdings.Count - ordered.Count;
        return new SellSweepState
        {
            SweepId = Guid.NewGuid(),
            League = league,
            OriginProbeSessionId = probeSessionId,
            ExecutionMode = executionMode,
            Phase = ordered.Count == 0 ? SellSweepPhase.Completed : SellSweepPhase.ReadyForCandidate,
            CurrentIndex = 0,
            MinimumSaleChaos = minimumSaleChaos,
            Candidates = ordered,
            StartedAtUtc = now,
            UpdatedAtUtc = now,
            Detail = ordered.Count == 0
                ? $"Nothing to sweep in {SellSweepExecutionModes.ToLabel(executionMode)} mode: " +
                    "the visible tab holds no recognised stack."
                : $"{ordered.Count} candidates queued {(smallestFirst ? "smallest" : "largest")} " +
                    $"stack first ({empty} empty skipped); " +
                    $"each is probed and priced against the {minimumSaleChaos} Chaos minimum " +
                    $"in {SellSweepExecutionModes.ToLabel(executionMode)} mode immediately before its own placement.",
        };
    }

    /// <summary>
    /// The rejected holdings, so the UI can say why a stack was passed over instead of leaving
    /// the operator to guess whether it was skipped or missed.
    /// </summary>
    public static IReadOnlyList<string> DescribeSkipped(
        IReadOnlyList<SellSweepEvaluation> evaluations)
    {
        ArgumentNullException.ThrowIfNull(evaluations);
        return evaluations
            .Where(evaluation => !evaluation.Result.Accepted)
            .OrderBy(evaluation => evaluation.Metadata, StringComparer.Ordinal)
            .Select(evaluation =>
                $"{evaluation.Name} x{evaluation.Holding}: {evaluation.Result.RejectionReason} " +
                $"({evaluation.Result.Detail})")
            .ToArray();
    }
}

public static class SellSweepCoordinator
{
    /// <summary>
    /// The single-live-order rule. Placement is reachable only from
    /// <see cref="SellSweepPhase.ReadyForCandidate"/>, which is reachable only once the previous
    /// candidate's order has left the tracked state entirely. There is deliberately no path that
    /// authorizes a placement while an order is outstanding.
    /// </summary>
    public static SellSweepDirectiveKind Decide(SellSweepState sweep, TrackedOrderState? tracked)
    {
        ArgumentNullException.ThrowIfNull(sweep);
        if (!sweep.IsActive) return SellSweepDirectiveKind.None;
        if (sweep.Current is null) return SellSweepDirectiveKind.ManualReconciliationRequired;

        if (sweep.Phase == SellSweepPhase.ReadyForCandidate)
        {
            // An unresolved order here belongs to no candidate this sweep is positioned on:
            // placing another would put two orders live. Refuse rather than guess.
            if (tracked?.IsUnresolved == true)
            {
                return SellSweepDirectiveKind.ManualReconciliationRequired;
            }
            return string.IsNullOrEmpty(sweep.PreparedSignature)
                ? SellSweepDirectiveKind.RescanAndPlanCurrentCandidate
                : SellSweepDirectiveKind.PlaceCurrentCandidate;
        }

        if (tracked is null || sweep.CurrentAttemptId != tracked.AttemptId)
        {
            return SellSweepDirectiveKind.ManualReconciliationRequired;
        }

        return tracked.Status switch
        {
            // The placement controller persists Armed and binds this matching attempt immediately
            // before its sole click. The sweep observes that boundary; it must not misclassify its
            // own same-frame durable intent as a foreign order.
            TrackedOrderStatus.Armed => SellSweepDirectiveKind.ObserveCurrentOrder,
            TrackedOrderStatus.Pending => SellSweepDirectiveKind.ObserveCurrentOrder,
            TrackedOrderStatus.TimedOut => SellSweepDirectiveKind.AuthorizeCancellation,
            TrackedOrderStatus.CancelArmed or TrackedOrderStatus.CancelClicked =>
                SellSweepDirectiveKind.RecoverCancellationWithoutRetry,
            TrackedOrderStatus.CompletedUncollected or TrackedOrderStatus.CanceledUncollected =>
                SellSweepDirectiveKind.AuthorizeSettlementCollection,
            TrackedOrderStatus.CollectionArmed when tracked.CollectionAssetIntent is not null =>
                SellSweepDirectiveKind.RecoverSettlementCollectionWithoutRetry,
            TrackedOrderStatus.CollectionArmed => SellSweepDirectiveKind.ManualReconciliationRequired,
            TrackedOrderStatus.Collected => SellSweepDirectiveKind.AuthorizeStashReturn,
            TrackedOrderStatus.StashTransferArmed =>
                SellSweepDirectiveKind.RecoverStashReturnWithoutRetry,
            TrackedOrderStatus.Ambiguous when tracked.StashTransferIntent is not null =>
                SellSweepDirectiveKind.RecoverStashReturnWithoutRetry,
            TrackedOrderStatus.Ambiguous when tracked.CollectionAssetIntent is not null =>
                SellSweepDirectiveKind.RecoverSettlementCollectionWithoutRetry,
            // Proceeds are provably in the stash: this candidate is done and only now may the
            // sweep move on. Advancing at Collected would leave currency in the inventory while
            // the next candidate starts staging against it.
            TrackedOrderStatus.Stashed => SellSweepDirectiveKind.AdvanceToNextCandidate,
            TrackedOrderStatus.Ambiguous =>
                SellSweepDirectiveKind.ManualReconciliationRequired,
            _ => SellSweepDirectiveKind.None,
        };
    }

    public static SellSweepState MarkPrepared(
        SellSweepState sweep,
        SellMarketQuote quote,
        Guid probeSessionId,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(sweep);
        ArgumentNullException.ThrowIfNull(quote);
        if (probeSessionId == Guid.Empty || sweep.OriginProbeSessionId != probeSessionId)
        {
            throw new ArgumentException(
                "A prepared sweep requires its unchanged sweep-wide probe session.", nameof(probeSessionId));
        }
        if (sweep.Phase != SellSweepPhase.ReadyForCandidate || sweep.CurrentAttemptId is not null)
        {
            throw new InvalidOperationException("A sweep can prepare only its current unplaced candidate.");
        }
        var next = Clone(sweep);
        var candidate = next.Current
            ?? throw new InvalidOperationException("The sweep is not positioned on a candidate.");
        if (candidate.Outcome != SellSweepCandidateOutcome.Pending ||
            !quote.Edge.From.Metadata.Equals(candidate.Metadata, StringComparison.Ordinal) ||
            quote.Edge.ExecutionIntent != SellSweepExecutionModes.ToExecutionIntent(next.ExecutionMode) ||
            quote.InputSpent <= 0 || quote.Output <= 0 || quote.InputSpent > candidate.HoldingAtScan)
        {
            throw new InvalidOperationException("The quote does not prepare the current pending sweep holding.");
        }
        next.PreparedSignature = quote.Signature;
        candidate.PlannedProceedsChaos = quote.ProceedsChaos;
        candidate.PlannedSignature = quote.Signature;
        candidate.PlannedProceedsMetadata = quote.Proceeds.Metadata;
        candidate.PlannedInputSpent = quote.InputSpent;
        candidate.PlannedOutput = quote.Output;
        candidate.PlannedInputRemainder = quote.InputRemainder;
        candidate.PlannedProceedsToChaosRate = quote.ProceedsToChaosRate;
        next.UpdatedAtUtc = now;
        next.Detail = $"{candidate.Name} re-planned at {quote.Signature} for {quote.ProceedsChaos} Chaos " +
            $"in {SellSweepExecutionModes.ToLabel(next.ExecutionMode)} mode.";
        return next;
    }

    public static SellSweepState MarkPlaced(
        SellSweepState sweep,
        Guid attemptId,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(sweep);
        if (attemptId == Guid.Empty)
        {
            throw new ArgumentException("A placed order requires an attempt id.", nameof(attemptId));
        }
        if (sweep.Phase != SellSweepPhase.ReadyForCandidate)
        {
            throw new InvalidOperationException(
                "A sell sweep places an order only from ReadyForCandidate; one order is live at a time.");
        }
        var next = Clone(sweep);
        var candidate = next.Current
            ?? throw new InvalidOperationException("The sweep is not positioned on a candidate.");
        if (next.CurrentAttemptId is not null || candidate.Outcome != SellSweepCandidateOutcome.Pending ||
            string.IsNullOrWhiteSpace(next.PreparedSignature) ||
            !string.Equals(next.PreparedSignature, candidate.PlannedSignature, StringComparison.Ordinal) ||
            candidate.PlannedInputSpent <= 0 || candidate.PlannedOutput <= 0 ||
            candidate.PlannedProceedsToChaosRate is null)
        {
            throw new InvalidOperationException("A sell sweep order requires an exact current preparation.");
        }
        next.Phase = SellSweepPhase.OrderLive;
        next.CurrentAttemptId = attemptId;
        next.UpdatedAtUtc = now;
        next.Detail = $"Order live for {candidate.Name}; no further placement until it settles.";
        return next;
    }

    public static SellSweepState Advance(
        SellSweepState sweep,
        SellSweepCandidateOutcome outcome,
        long realizedProceedsChaos,
        string detail,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(sweep);
        if (outcome == SellSweepCandidateOutcome.Pending)
        {
            throw new ArgumentException("Advancing requires a terminal outcome.", nameof(outcome));
        }
        if (realizedProceedsChaos < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(realizedProceedsChaos));
        }
        var next = Clone(sweep);
        var candidate = next.Current
            ?? throw new InvalidOperationException("The sweep is not positioned on a candidate.");
        if (candidate.Outcome != SellSweepCandidateOutcome.Pending ||
            outcome == SellSweepCandidateOutcome.Sold && next.Phase != SellSweepPhase.OrderLive ||
            outcome is SellSweepCandidateOutcome.Skipped or SellSweepCandidateOutcome.Failed &&
                (next.Phase != SellSweepPhase.ReadyForCandidate || realizedProceedsChaos != 0))
        {
            throw new InvalidOperationException("The requested sweep advancement does not match its current phase.");
        }
        candidate.Outcome = outcome;
        candidate.RealizedProceedsChaos = realizedProceedsChaos;
        candidate.Detail = detail ?? string.Empty;
        next.RealizedProceedsChaos = checked(next.RealizedProceedsChaos + realizedProceedsChaos);
        next.CurrentAttemptId = null;
        next.PreparedSignature = string.Empty;
        next.CurrentIndex++;
        next.UpdatedAtUtc = now;
        if (next.CurrentIndex >= next.Candidates.Count)
        {
            next.Phase = SellSweepPhase.Completed;
            next.Detail = $"Sweep complete: {next.Candidates.Count(entry => entry.Outcome == SellSweepCandidateOutcome.Sold)} " +
                $"of {next.Candidates.Count} sold for {next.RealizedProceedsChaos} Chaos.";
        }
        else
        {
            next.Phase = SellSweepPhase.ReadyForCandidate;
            next.Detail = $"{candidate.Name} {outcome}; next candidate {next.Current!.Name} " +
                "requires a fresh re-plan before placement.";
        }
        return next;
    }

    public static SellSweepState ClearPreparationForRetry(
        SellSweepState sweep,
        string reason,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(sweep);
        if (sweep.Phase != SellSweepPhase.ReadyForCandidate || sweep.CurrentAttemptId is not null ||
            sweep.Current is not { Outcome: SellSweepCandidateOutcome.Pending })
        {
            throw new InvalidOperationException("Only an unplaced pending sweep candidate can be re-probed.");
        }

        var next = Clone(sweep);
        var candidate = next.Current!;
        next.PreparedSignature = string.Empty;
        candidate.PlannedProceedsChaos = 0;
        candidate.PlannedSignature = string.Empty;
        candidate.PlannedProceedsMetadata = string.Empty;
        candidate.PlannedInputSpent = 0;
        candidate.PlannedOutput = 0;
        candidate.PlannedInputRemainder = 0;
        candidate.PlannedProceedsToChaosRate = null;
        next.UpdatedAtUtc = now;
        next.Detail = $"{candidate.Name} requires a fresh two-market probe: {reason}";
        return next;
    }

    public static bool TryCalculateRealizedProceedsChaos(
        SellSweepState sweep,
        TrackedOrderState tracked,
        out long realizedProceedsChaos,
        out string failure)
    {
        ArgumentNullException.ThrowIfNull(sweep);
        ArgumentNullException.ThrowIfNull(tracked);
        realizedProceedsChaos = 0;
        var candidate = sweep.Current;
        if (sweep.Phase != SellSweepPhase.OrderLive || candidate is null ||
            candidate.Outcome != SellSweepCandidateOutcome.Pending ||
            sweep.CurrentAttemptId is null || sweep.CurrentAttemptId != tracked.AttemptId ||
            tracked.Status != TrackedOrderStatus.Stashed)
        {
            failure = "Realized proceeds require the matching stashed sweep attempt.";
            return false;
        }
        if (!string.Equals(candidate.Metadata, tracked.OfferedMetadata, StringComparison.Ordinal) ||
            !string.Equals(candidate.PlannedProceedsMetadata, tracked.WantedMetadata, StringComparison.Ordinal) ||
            candidate.PlannedProceedsToChaosRate is not { } valuationRate ||
            tracked.TerminalReceivedWantedAmount is not { } received || received < 0 ||
            received > tracked.WantedAmount)
        {
            failure = "Terminal proceeds do not match the prepared candidate economics.";
            return false;
        }

        try
        {
            realizedProceedsChaos = valuationRate.FloorMultiply(received);
            failure = string.Empty;
            return true;
        }
        catch (OverflowException exception)
        {
            failure = $"Realized proceeds overflowed: {exception.Message}";
            return false;
        }
    }

    public static SellSweepState Stop(SellSweepState sweep, string reason, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(sweep);
        var next = Clone(sweep);
        next.Phase = SellSweepPhase.Stopped;
        next.PreparedSignature = string.Empty;
        next.UpdatedAtUtc = now;
        next.Detail = reason ?? string.Empty;
        return next;
    }

    public static SellSweepState MarkAmbiguous(SellSweepState sweep, string reason, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(sweep);
        var next = Clone(sweep);
        next.Phase = SellSweepPhase.Ambiguous;
        next.PreparedSignature = string.Empty;
        next.UpdatedAtUtc = now;
        next.Detail = reason ?? string.Empty;
        var candidate = next.Current;
        if (candidate is not null)
        {
            candidate.Outcome = SellSweepCandidateOutcome.Failed;
            candidate.Detail = reason ?? string.Empty;
        }
        return next;
    }

    public static SellSweepState Clone(SellSweepState sweep)
    {
        ArgumentNullException.ThrowIfNull(sweep);
        return new SellSweepState
        {
            SchemaVersion = sweep.SchemaVersion,
            SweepId = sweep.SweepId,
            League = sweep.League,
            OriginProbeSessionId = sweep.OriginProbeSessionId,
            ExecutionMode = sweep.ExecutionMode,
            Phase = sweep.Phase,
            CurrentIndex = sweep.CurrentIndex,
            CurrentAttemptId = sweep.CurrentAttemptId,
            PreparedSignature = sweep.PreparedSignature,
            MinimumSaleChaos = sweep.MinimumSaleChaos,
            RealizedProceedsChaos = sweep.RealizedProceedsChaos,
            StartedAtUtc = sweep.StartedAtUtc,
            UpdatedAtUtc = sweep.UpdatedAtUtc,
            Detail = sweep.Detail,
            Candidates = sweep.Candidates.Select(candidate => new SellSweepCandidate
            {
                Index = candidate.Index,
                Metadata = candidate.Metadata,
                Name = candidate.Name,
                HoldingAtScan = candidate.HoldingAtScan,
                PlannedProceedsChaos = candidate.PlannedProceedsChaos,
                PlannedSignature = candidate.PlannedSignature,
                PlannedProceedsMetadata = candidate.PlannedProceedsMetadata,
                PlannedInputSpent = candidate.PlannedInputSpent,
                PlannedOutput = candidate.PlannedOutput,
                PlannedInputRemainder = candidate.PlannedInputRemainder,
                PlannedProceedsToChaosRate = candidate.PlannedProceedsToChaosRate,
                Outcome = candidate.Outcome,
                RealizedProceedsChaos = candidate.RealizedProceedsChaos,
                Detail = candidate.Detail,
            }).ToList(),
        };
    }
}

/// <summary>
/// The stash asset family a sweep sells. The settings dropdown stores the label, so the label is
/// parsed back to the exact enum here rather than string-compared at any decision point, and an
/// unrecognised label is a refusal instead of a silent default.
/// </summary>
public static class SellSweepKinds
{
    public const string ScarabLabel = "Scarabs";
    public const string CurrencyLabel = "Currency";

    public static IReadOnlyList<string> Labels { get; } = new[] { ScarabLabel, CurrencyLabel };

    public static string ToLabel(CurrencyTargetKind kind) => kind switch
    {
        CurrencyTargetKind.Scarab => ScarabLabel,
        CurrencyTargetKind.Currency => CurrencyLabel,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static bool TryParse(string label, out CurrencyTargetKind kind)
    {
        if (string.Equals(label, ScarabLabel, StringComparison.Ordinal))
        {
            kind = CurrencyTargetKind.Scarab;
            return true;
        }
        if (string.Equals(label, CurrencyLabel, StringComparison.Ordinal))
        {
            kind = CurrencyTargetKind.Currency;
            return true;
        }

        kind = default;
        return false;
    }

    /// <summary>
    /// The stash tab type that holds this family, which is also the tab ctrl+shift+click returns it
    /// to. A sweep only reads a scan whose tab type matches, so a Currency tab left visible can
    /// never be mistaken for an empty scarab tab.
    /// </summary>
    public static string HomeTabType(CurrencyTargetKind kind) => kind switch
    {
        CurrencyTargetKind.Scarab => StashCustodyPolicy.FragmentTabType,
        CurrencyTargetKind.Currency => StashCustodyPolicy.CurrencyTabType,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}

/// <summary>
/// Order-slot capacity. The SDK exposes placed orders but no maximum anywhere, so the cap is a
/// supplied constant; placement refuses at capacity rather than sending a click the client will
/// reject. Only live orders count - completed and cancelled rows still occupy the list but not a
/// slot, so they are excluded before comparing.
/// </summary>
public static class ExchangeOrderCapacity
{
    public const int MaxExchangeOrders = 10;

    public static int CountLive(IEnumerable<PlacedOrderSnapshot> orders)
    {
        ArgumentNullException.ThrowIfNull(orders);
        return orders.Count(order => !order.IsCompleted && !order.IsCanceled);
    }

    public static bool IsAtCapacity(int liveOrders)
    {
        if (liveOrders < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(liveOrders));
        }

        return liveOrders >= MaxExchangeOrders;
    }
}
