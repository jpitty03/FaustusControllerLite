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
    PromoteRestingOrderForSettlement,
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

/// <summary>
/// One order the sweep has placed and not yet closed out. A slot is the durable link between a
/// candidate and the attempt that sold it, so several candidates can be resting at once without
/// the sweep having to guess which tracked order belongs to which holding.
/// </summary>
public sealed class SellSweepSlot
{
    public int CandidateIndex { get; set; }
    public Guid AttemptId { get; set; }
    public string PreparedSignature { get; set; } = string.Empty;
    public string OfferedMetadata { get; set; } = string.Empty;
    public DateTimeOffset PlacedAtUtc { get; set; }
}

public sealed class SellSweepState
{
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public Guid SweepId { get; set; }
    public string League { get; set; } = string.Empty;
    public Guid OriginProbeSessionId { get; set; }
    public SellSweepExecutionMode ExecutionMode { get; set; } = SellSweepExecutionMode.MostCurrency;
    public SellSweepPhase Phase { get; set; }
    /// <summary>
    /// The next candidate to price. Placement consumes a candidate and moves this on, so a slotted
    /// candidate is always behind the cursor and is never re-priced or re-placed.
    /// </summary>
    public int CurrentIndex { get; set; }

    /// <summary>
    /// The orders this sweep has out. Empty means nothing is placed; one entry is exactly the old
    /// single-order behaviour. Placement is still serial - only resting is parallel.
    /// </summary>
    public List<SellSweepSlot> Slots { get; set; } = [];

    /// <summary>
    /// The quote the current candidate is prepared against. Empty means the candidate still needs
    /// a fresh re-plan before any placement input is authorized. Singular because only one
    /// placement is ever in flight.
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

    public SellSweepSlot? FindSlot(Guid attemptId) =>
        attemptId == Guid.Empty ? null : Slots.FirstOrDefault(slot => slot.AttemptId == attemptId);

    /// <summary>
    /// Whether this item already has an order resting. Two orders on the same item share a queue,
    /// so the second would be priced against the first - the sweep would compete with itself.
    /// </summary>
    public bool IsMetadataSlotted(string metadata) =>
        Slots.Any(slot => string.Equals(slot.OfferedMetadata, metadata, StringComparison.Ordinal));

    public SellSweepCandidate? CandidateFor(SellSweepSlot slot)
    {
        ArgumentNullException.ThrowIfNull(slot);
        return slot.CandidateIndex >= 0 && slot.CandidateIndex < Candidates.Count
            ? Candidates[slot.CandidateIndex]
            : null;
    }
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
    /// The single-order form, kept so every caller that has no resting set - and the arbitrage
    /// workflow, which is deliberately never concurrent - reads exactly as it did.
    /// </summary>
    public static SellSweepDirectiveKind Decide(SellSweepState sweep, TrackedOrderState? tracked) =>
        Decide(sweep, tracked, [], 1);

    /// <summary>
    /// One directive per tick, in priority order:
    /// <list type="number">
    /// <item>anything ambiguous, or a slot with no order behind it, requires an operator;</item>
    /// <item>the active slot - the one order an input controller owns - runs today's per-status
    /// switch unchanged, and anything but a plain observation owns the tick outright because
    /// settlement is strictly serial;</item>
    /// <item>a resting order that has reached a terminal state is promoted and settled before any
    /// new order is placed, which keeps reserved principal and uncollected proceeds low;</item>
    /// <item>only then, with a free slot and a candidate whose item is not already resting, is a
    /// placement authorized;</item>
    /// <item>otherwise the sweep observes.</item>
    /// </list>
    /// With <paramref name="maxConcurrentSweepOrders"/> at 1 and an empty resting set this is the
    /// old single-order machine step for step.
    /// </summary>
    public static SellSweepDirectiveKind Decide(
        SellSweepState sweep,
        TrackedOrderState? tracked,
        IReadOnlyList<TrackedOrderState> resting,
        int maxConcurrentSweepOrders)
    {
        ArgumentNullException.ThrowIfNull(sweep);
        ArgumentNullException.ThrowIfNull(resting);
        if (maxConcurrentSweepOrders < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxConcurrentSweepOrders));
        }
        if (!sweep.IsActive) return SellSweepDirectiveKind.None;

        // Ambiguity is never a controller's to resolve, wherever it sits.
        if (resting.Any(order => TrackedOrderRestPolicy.BlocksTrading(order)))
        {
            return SellSweepDirectiveKind.ManualReconciliationRequired;
        }

        var observing = false;
        if (tracked is not null)
        {
            var activeSlot = sweep.FindSlot(tracked.AttemptId);
            if (activeSlot is null)
            {
                // An unresolved order that belongs to no slot is not this sweep's to act on.
                // Refuse rather than guess, exactly as the single-order machine did. A *resolved*
                // one is the order the sweep just stashed and already advanced past: harmless
                // leftover in the active slot, and never a reason to stop the sweep.
                if (tracked.IsUnresolved)
                {
                    return SellSweepDirectiveKind.ManualReconciliationRequired;
                }
            }
            else
            {
                var active = MapActiveOrder(tracked);
                if (active != SellSweepDirectiveKind.ObserveCurrentOrder) return active;
                // A placement click is durably armed and in flight; nothing may start around it.
                if (tracked.Status == TrackedOrderStatus.Armed) return active;
                observing = true;
            }
        }

        // Every slot must be accounted for by the active order or a resting one. A slot with no
        // order behind it means a row the sweep placed is gone, which is an operator's problem.
        foreach (var slot in sweep.Slots)
        {
            if (tracked is not null && tracked.AttemptId == slot.AttemptId) continue;
            if (!resting.Any(order => order.AttemptId == slot.AttemptId))
            {
                return SellSweepDirectiveKind.ManualReconciliationRequired;
            }
        }

        // Settle before placing: an order that has already stopped trading is holding custody the
        // sweep could be banking instead of adding more exposure on top of it.
        if ((tracked is null || sweep.FindSlot(tracked.AttemptId) is null) &&
            NextSettlementSlot(sweep, resting) is not null)
        {
            return SellSweepDirectiveKind.PromoteRestingOrderForSettlement;
        }

        if (sweep.Slots.Count < maxConcurrentSweepOrders &&
            sweep.Current is { Outcome: SellSweepCandidateOutcome.Pending } candidate &&
            !sweep.IsMetadataSlotted(candidate.Metadata))
        {
            return string.IsNullOrEmpty(sweep.PreparedSignature)
                ? SellSweepDirectiveKind.RescanAndPlanCurrentCandidate
                : SellSweepDirectiveKind.PlaceCurrentCandidate;
        }

        return observing || sweep.Slots.Count > 0
            ? SellSweepDirectiveKind.ObserveCurrentOrder
            : SellSweepDirectiveKind.ManualReconciliationRequired;
    }

    /// <summary>
    /// The resting order the sweep would promote into the active slot next. Oldest first, so a
    /// slot that has been holding custody longest is banked first. Null when nothing is ready.
    /// </summary>
    public static TrackedOrderState? NextSettlementSlot(
        SellSweepState sweep,
        IReadOnlyList<TrackedOrderState> resting)
    {
        ArgumentNullException.ThrowIfNull(sweep);
        ArgumentNullException.ThrowIfNull(resting);
        return sweep.Slots
            .OrderBy(slot => slot.PlacedAtUtc)
            .Select(slot => resting.FirstOrDefault(order =>
                order.AttemptId == slot.AttemptId &&
                TrackedOrderRestPolicy.NeedsSettlement(order.Status)))
            .FirstOrDefault(order => order is not null);
    }

    private static SellSweepDirectiveKind MapActiveOrder(TrackedOrderState tracked)
    {
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

    /// <summary>
    /// Whether a fresh placement may be prepared or sent. The limit lives here rather than at each
    /// call site so a slot can never be opened past it, whichever path asks.
    /// </summary>
    private static bool HasFreeSlot(SellSweepState sweep, int maxConcurrentSweepOrders)
    {
        if (maxConcurrentSweepOrders < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxConcurrentSweepOrders));
        }
        return sweep.IsActive && sweep.Slots.Count < maxConcurrentSweepOrders &&
            sweep.Slots.Count < ExchangeOrderCapacity.MaxExchangeOrders;
    }

    public static SellSweepState MarkPrepared(
        SellSweepState sweep,
        SellMarketQuote quote,
        Guid probeSessionId,
        DateTimeOffset now,
        int maxConcurrentSweepOrders = 1)
    {
        ArgumentNullException.ThrowIfNull(sweep);
        ArgumentNullException.ThrowIfNull(quote);
        if (probeSessionId == Guid.Empty || sweep.OriginProbeSessionId != probeSessionId)
        {
            throw new ArgumentException(
                "A prepared sweep requires its unchanged sweep-wide probe session.", nameof(probeSessionId));
        }
        if (!HasFreeSlot(sweep, maxConcurrentSweepOrders))
        {
            throw new InvalidOperationException("A sweep can prepare only its current unplaced candidate.");
        }
        var next = Clone(sweep);
        var candidate = next.Current
            ?? throw new InvalidOperationException("The sweep is not positioned on a candidate.");
        if (next.IsMetadataSlotted(candidate.Metadata) ||
            candidate.Outcome != SellSweepCandidateOutcome.Pending ||
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
        DateTimeOffset now,
        int maxConcurrentSweepOrders = 1)
    {
        ArgumentNullException.ThrowIfNull(sweep);
        if (attemptId == Guid.Empty)
        {
            throw new ArgumentException("A placed order requires an attempt id.", nameof(attemptId));
        }
        if (!HasFreeSlot(sweep, maxConcurrentSweepOrders))
        {
            throw new InvalidOperationException(
                "A sell sweep places an order only into a free slot; every slot is already resting.");
        }
        var next = Clone(sweep);
        var candidate = next.Current
            ?? throw new InvalidOperationException("The sweep is not positioned on a candidate.");
        if (next.FindSlot(attemptId) is not null || next.IsMetadataSlotted(candidate.Metadata) ||
            candidate.Outcome != SellSweepCandidateOutcome.Pending ||
            string.IsNullOrWhiteSpace(next.PreparedSignature) ||
            !string.Equals(next.PreparedSignature, candidate.PlannedSignature, StringComparison.Ordinal) ||
            candidate.PlannedInputSpent <= 0 || candidate.PlannedOutput <= 0 ||
            candidate.PlannedProceedsToChaosRate is null)
        {
            throw new InvalidOperationException("A sell sweep order requires an exact current preparation.");
        }
        next.Slots.Add(new SellSweepSlot
        {
            CandidateIndex = candidate.Index,
            AttemptId = attemptId,
            PreparedSignature = next.PreparedSignature,
            OfferedMetadata = candidate.Metadata,
            PlacedAtUtc = now,
        });
        // Placement consumes the candidate: the cursor moves on so the next tick prices the next
        // holding rather than re-pricing one that is already resting.
        next.CurrentIndex++;
        next.PreparedSignature = string.Empty;
        next.Phase = SellSweepPhase.OrderLive;
        next.UpdatedAtUtc = now;
        next.Detail = $"Order live for {candidate.Name}; {next.Slots.Count} of " +
            $"{maxConcurrentSweepOrders} slots resting.";
        return next;
    }

    /// <summary>
    /// Closes out one candidate. A <see cref="SellSweepCandidateOutcome.Sold"/> outcome closes the
    /// slot the attempt belongs to and leaves every other slot resting; a skip or failure retires
    /// the candidate the cursor is on, which was never placed, and moves the cursor past it.
    /// </summary>
    public static SellSweepState Advance(
        SellSweepState sweep,
        SellSweepCandidateOutcome outcome,
        long realizedProceedsChaos,
        string detail,
        DateTimeOffset now) =>
        Advance(sweep, null, outcome, realizedProceedsChaos, detail, now);

    public static SellSweepState Advance(
        SellSweepState sweep,
        Guid? attemptId,
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

        // A sold candidate is identified by the attempt that sold it, never by the cursor: with
        // several orders resting the cursor has long since moved past the one that just settled.
        SellSweepSlot? slot = null;
        if (outcome == SellSweepCandidateOutcome.Sold)
        {
            slot = attemptId is { } id ? next.FindSlot(id) : next.Slots.SingleOrDefault();
            if (slot is null)
            {
                throw new InvalidOperationException(
                    "A sold sweep candidate must name the resting slot its attempt closed.");
            }
        }
        else if (attemptId is not null)
        {
            throw new InvalidOperationException("Only a sold sweep candidate closes a slot.");
        }

        var candidate = (slot is null ? next.Current : next.CandidateFor(slot))
            ?? throw new InvalidOperationException("The sweep is not positioned on a candidate.");
        if (candidate.Outcome != SellSweepCandidateOutcome.Pending ||
            outcome is SellSweepCandidateOutcome.Skipped or SellSweepCandidateOutcome.Failed &&
                realizedProceedsChaos != 0)
        {
            throw new InvalidOperationException("The requested sweep advancement does not match its current phase.");
        }
        candidate.Outcome = outcome;
        candidate.RealizedProceedsChaos = realizedProceedsChaos;
        candidate.Detail = detail ?? string.Empty;
        next.RealizedProceedsChaos = checked(next.RealizedProceedsChaos + realizedProceedsChaos);
        // Any retirement invalidates the preparation, whichever candidate was retired. The cursor's
        // own candidate dies with it; a slot closing means seconds of settlement have passed, and the
        // driver's half of the preparation - the staged leg and the placement token - is cleared
        // unconditionally when that happens. Keeping the durable half alone would leave the sweep
        // authorizing a placement whose plan no longer exists.
        next.PreparedSignature = string.Empty;
        if (slot is null)
        {
            // The cursor's candidate was never placed, so the cursor moves past it here. A sold one
            // was already consumed by placement, which moved the cursor at the time.
            next.CurrentIndex++;
        }
        else
        {
            next.Slots.Remove(slot);
        }
        next.UpdatedAtUtc = now;
        if (next.CurrentIndex >= next.Candidates.Count && next.Slots.Count == 0)
        {
            next.Phase = SellSweepPhase.Completed;
            next.Detail = $"Sweep complete: {next.Candidates.Count(entry => entry.Outcome == SellSweepCandidateOutcome.Sold)} " +
                $"of {next.Candidates.Count} sold for {next.RealizedProceedsChaos} Chaos.";
        }
        else
        {
            next.Phase = next.Slots.Count > 0
                ? SellSweepPhase.OrderLive
                : SellSweepPhase.ReadyForCandidate;
            next.Detail = next.Current is { } upcoming
                ? $"{candidate.Name} {outcome}; next candidate {upcoming.Name} " +
                    "requires a fresh re-plan before placement."
                : $"{candidate.Name} {outcome}; {next.Slots.Count} order(s) still resting.";
        }
        return next;
    }

    public static SellSweepState ClearPreparationForRetry(
        SellSweepState sweep,
        string reason,
        DateTimeOffset now,
        int maxConcurrentSweepOrders = 1)
    {
        ArgumentNullException.ThrowIfNull(sweep);
        if (!HasFreeSlot(sweep, maxConcurrentSweepOrders) ||
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
        // The candidate is reached through the slot the attempt closed, never through the cursor:
        // with several orders resting the cursor is already past the one that just stashed.
        var slot = sweep.FindSlot(tracked.AttemptId);
        var candidate = slot is null ? null : sweep.CandidateFor(slot);
        if (!sweep.IsActive || candidate is null ||
            candidate.Outcome != SellSweepCandidateOutcome.Pending ||
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
        // Every candidate the sweep still owns fails, not just the one the cursor is on: a
        // resting order's custody is exactly as unprovable as the active one's.
        var owned = next.Slots
            .Select(next.CandidateFor)
            .Append(next.Current)
            .Where(entry => entry is { Outcome: SellSweepCandidateOutcome.Pending });
        foreach (var candidate in owned)
        {
            candidate!.Outcome = SellSweepCandidateOutcome.Failed;
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
            PreparedSignature = sweep.PreparedSignature,
            MinimumSaleChaos = sweep.MinimumSaleChaos,
            RealizedProceedsChaos = sweep.RealizedProceedsChaos,
            StartedAtUtc = sweep.StartedAtUtc,
            UpdatedAtUtc = sweep.UpdatedAtUtc,
            Detail = sweep.Detail,
            Slots = sweep.Slots.Select(slot => new SellSweepSlot
            {
                CandidateIndex = slot.CandidateIndex,
                AttemptId = slot.AttemptId,
                PreparedSignature = slot.PreparedSignature,
                OfferedMetadata = slot.OfferedMetadata,
                PlacedAtUtc = slot.PlacedAtUtc,
            }).ToList(),
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
