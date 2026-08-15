using System.Globalization;

namespace FaustusControllerLite.Domain;

public sealed record MarketSweepScoreSettings(
    int ChurnIntervalCapMinutes = MarketSweepScore.DefaultChurnIntervalCapMinutes,
    long DepthCap = MarketSweepScore.DefaultDepthCap,
    long PrincipalChaos = 0,
    long PrincipalDivine = 0,
    long MinCycleQueue = MarketSweepScore.DefaultMinCycleQueue,
    double MakerSpreadThreshold = MarketSweepScore.DefaultMakerSpreadThreshold,
    double MakerLegMinutesCap = MarketSweepScore.DefaultMakerLegMinutesCap,
    double TakerLegMinutes = MarketSweepScore.DefaultTakerLegMinutes);

/// <summary>
/// Movement of one direction of one book between consecutive observations. <see cref="MovesPerMinute"/> is
/// null when a single observation is all we have: a pair swept once has no measurable velocity, and rendering
/// that as zero would make a never-compared pair indistinguishable from a dead one.
/// </summary>
public sealed record MarketChurn(double? MovesPerMinute, double? DepthTurnoverPerMinute, int Intervals)
{
    public static MarketChurn Unknown { get; } = new(null, null, 0);

    public string Describe() =>
        MovesPerMinute is { } moves ? moves.ToString("0.00", CultureInfo.InvariantCulture) : "-";

    /// <summary>
    /// Units of depth appearing or disappearing per minute, across both sides of this direction. It is the
    /// finer of the two velocity signals: a book can be traded steadily without its head price ever moving,
    /// and that case reads as zero churn but non-zero turnover.
    /// </summary>
    public string DescribeTurnover() =>
        DepthTurnoverPerMinute is { } turnover ? turnover.ToString("0.0", CultureInfo.InvariantCulture) : "-";
}

/// <summary>
/// Which column the board is ordered by. Every option is a column already on the row, so changing the sort
/// only reorders what is on screen — it never changes what was measured or how a cycle was scored.
/// </summary>
public enum MarketSweepBoardSort
{
    ChaosPerHour,
    Multiplier,
    Taker,
    Gain,
    Lot,
    CycleMinutes,
    History,
}

public sealed record MarketSweepRow(
    CurrencyPairKey Pair,
    CurrencyIdentity From,
    CurrencyIdentity To,
    Rational? CompetingRate,
    Rational? ImmediateRate,
    double MarginFraction,
    long ImmediateInputDepth,
    long CompetingQueueAhead,
    long TradableDepth,
    MarketChurn Churn,
    PairExecutionStatistics Execution,
    double ExpectedMinutes,
    double Score,
    DateTimeOffset ObservedAtUtc,
    int ObservationCount)
{
    public string Signature => $"{From.Metadata}>{To.Metadata}";

    public string Label => $"{From.Name}>{To.Name}";
}

/// <summary>
/// Turns the observation series and the measured execution history into one ranked row per directed pair.
///
/// The estimate is <c>margin x tradable depth x fill confidence / expected minutes</c>. Every factor is kept
/// on the row as its own column so the ranking can be argued with rather than trusted: a wide margin on a
/// book one unit deep and a narrow margin on a deep fast book both show why they scored what they scored.
///
/// This is advisory. Nothing here feeds the route planner, which continues to choose routes exactly as it
/// does today from its own fresh coherent probe.
/// </summary>
public static class MarketSweepScore
{
    /// <summary>
    /// Intervals longer than this are dropped rather than averaged in. It has to exceed the interval at which
    /// pairs are actually revisited or every measured gap is discarded and churn stays unknown forever, which
    /// is why it is well above <c>SweepStalePairMinutes</c>'s default rather than merely different from it.
    /// </summary>
    public const int DefaultChurnIntervalCapMinutes = 90;

    /// <summary>
    /// Depth beyond this adds nothing to the estimate. Depth is denominated in the direction's own input
    /// currency, so this cap is a blunt instrument that mostly stops one very deep Chaos book from dominating
    /// the board; it is not a claim that 1000 Chaos and 1000 Divine of depth are comparable.
    /// </summary>
    public const long DefaultDepthCap = 1000;

    /// <summary>
    /// The smallest competing queue a cycle leg may show and still be believed. It is drawn from the data
    /// rather than picked: in the 2026-08-13 sweep the trap rows - the widest spreads on the board - carried
    /// up-leg queues of 1, 2, 4 and 11, while the cycles that survived a second sweep carried 14, 25, 72,
    /// 267, 409, 657 and 2281. A queue of one is one order, and one order at an absurd price is not a market.
    /// It is a setting because that boundary moves with the league.
    /// </summary>
    public const long DefaultMinCycleQueue = 10;

    /// <summary>
    /// How much wider the maker rate must be than the taker rate before a leg is worth posting an order and
    /// waiting, instead of crossing the spread immediately.
    ///
    /// Because each book carries only two independent prices, the maker rate is <em>always</em> at least the
    /// taker rate on any single leg, so an all-maker cycle always shows the largest multiplier. That is why a
    /// bare "prefer the bigger number" rule collapses into always waiting. The question a threshold answers is
    /// the real one: is this particular leg's extra width worth its wait and its chance of never filling.
    ///
    /// The default is deliberately high. Across the 810 hub-touching legs of the 2026-08-13 sweep it makes
    /// only 11 of them maker legs, which is close to "always cross" - the position the measured data supports.
    /// A cycle executed all-taker is three clicks and about three minutes; the same cycle worked all-maker is
    /// three queued orders at a historically ~60% fill rate, and an unfilled middle leg leaves the bankroll
    /// stranded in a currency it never wanted. On sweep #6 the best all-taker cycle returned 3170 chaos/hour
    /// against 846 for the best all-maker one, before any fill discount at all.
    /// </summary>
    public const double DefaultMakerSpreadThreshold = 2.0;

    /// <summary>
    /// A maker leg expected to take longer than this is taken instead, however wide it is. It is a separate
    /// condition from the spread because width and speed are independent: the Regal Orb bridge leg of the
    /// 2026-08-13 sweep carried a spread of 1.72 behind a queue of 31200 draining at 83 a minute, which is
    /// over six hours of waiting for an edge that a single click captures most of.
    /// </summary>
    public const double DefaultMakerLegMinutesCap = 30;

    /// <summary>
    /// What a taker leg costs in time: the clicking, not the waiting. A taker leg has no queue to drain, so
    /// <see cref="MarketSweepRow.ExpectedMinutes"/> - which is entirely a drain estimate - does not describe
    /// it at all, and charging it the drain time would rank instant cycles as though they were slow ones.
    ///
    /// One minute is a hand-timed placeholder. It should be replaced by a measurement from
    /// <c>execution-audit-&lt;league&gt;.jsonl</c> once enough taker legs have been executed to have a median.
    /// </summary>
    public const double DefaultTakerLegMinutes = 1.0;

    /// <summary>
    /// Measures how much one direction of a book moved. Long gaps undercount — several moves collapse into a
    /// single observed change — so intervals longer than the cap are dropped, and the remaining intervals are
    /// weighted by 1/dt so that the shortest, most trustworthy gaps dominate the estimate.
    /// </summary>
    public static MarketChurn MeasureChurn(
        IReadOnlyList<MarketObservation> observations,
        CurrencyIdentity from,
        int intervalCapMinutes)
    {
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentNullException.ThrowIfNull(from);
        if (intervalCapMinutes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(intervalCapMinutes));
        }

        double weight = 0;
        double weightedMoves = 0;
        double weightedTurnover = 0;
        var intervals = 0;
        for (var index = 1; index < observations.Count; index++)
        {
            var previous = observations[index - 1];
            var current = observations[index];
            var minutes = (current.ObservedAtUtc - previous.ObservedAtUtc).TotalMinutes;
            if (minutes <= 0 || minutes > intervalCapMinutes)
            {
                continue;
            }

            var before = previous.Direction(from);
            var after = current.Direction(from);
            double moves = 0;
            if (before.ImmediateRate != after.ImmediateRate)
            {
                moves++;
            }

            if (before.CompetingRate != after.CompetingRate)
            {
                moves++;
            }

            var turnover =
                (double)Math.Abs(after.ImmediateInputDepth - before.ImmediateInputDepth) +
                Math.Abs(after.CompetingQueueAhead - before.CompetingQueueAhead);

            var intervalWeight = 1.0 / minutes;
            weight += intervalWeight;
            weightedMoves += intervalWeight * (moves / minutes);
            weightedTurnover += intervalWeight * (turnover / minutes);
            intervals++;
        }

        return intervals == 0
            ? MarketChurn.Unknown
            : new MarketChurn(weightedMoves / weight, weightedTurnover / weight, intervals);
    }

    /// <summary>
    /// The maker edge for this direction: how much better the competing price is than taking the immediate
    /// price right now. The comparison itself is exact — <see cref="Rational"/> cross-multiplies in
    /// BigInteger — and only the reported fraction is a double, because it is used for display and ordering
    /// and never for an amount. A direction whose competing price is not strictly better has no maker edge at
    /// all and reports zero rather than a negative number.
    /// </summary>
    public static double MarginFraction(DirectedBookObservation book)
    {
        ArgumentNullException.ThrowIfNull(book);
        if (book.ImmediateRate is not { } immediate || book.CompetingRate is not { } competing ||
            competing <= immediate)
        {
            return 0;
        }

        var immediateValue = (double)immediate.Numerator / immediate.Denominator;
        var competingValue = (double)competing.Numerator / competing.Denominator;
        return immediateValue <= 0 ? 0 : (competingValue - immediateValue) / immediateValue;
    }

    /// <summary>
    /// Longest currency name <see cref="Abbreviate"/> emits. Sized for the overlay's single-line workflow
    /// path, where four hops and their amounts share one row, so a name has to be cut to fit.
    /// </summary>
    /// <remarks>
    /// The sweep board deliberately does not use this. Its cycle cell prints the target's name in full and
    /// clips per cell instead - see <see cref="MarketSweepCycle.LabelLength"/>.
    /// </remarks>
    public const int BoardNameLength = 14;

    /// <summary>
    /// The board's column headings, in the order <see cref="FormatRow"/> emits cells.
    /// </summary>
    /// <remarks>
    /// The last three columns carry the per-leg evidence behind the first six - one queue and one turnover
    /// per leg, and the traded history of the pair the cycle opens with - so a ranking can be argued with
    /// instead of trusted. That is the same principle the leg board was built on; what changed is that the
    /// unit being argued about is now a closed loop rather than one side of a book.
    /// </remarks>
    public static IReadOnlyList<string> ColumnHeadings { get; } =
        ["cycle", "mode", "mult", "take", "lot", "gain", "chaos/hr", "min", "queues", "turn", "fills:no"];

    public static MarketSweepBoardSort NextSort(MarketSweepBoardSort sort) => sort switch
    {
        MarketSweepBoardSort.ChaosPerHour => MarketSweepBoardSort.Multiplier,
        MarketSweepBoardSort.Multiplier => MarketSweepBoardSort.Taker,
        MarketSweepBoardSort.Taker => MarketSweepBoardSort.Gain,
        MarketSweepBoardSort.Gain => MarketSweepBoardSort.Lot,
        MarketSweepBoardSort.Lot => MarketSweepBoardSort.CycleMinutes,
        MarketSweepBoardSort.CycleMinutes => MarketSweepBoardSort.History,
        _ => MarketSweepBoardSort.ChaosPerHour,
    };

    public static string DescribeSort(MarketSweepBoardSort sort) => sort switch
    {
        MarketSweepBoardSort.Multiplier => "multiplier",
        MarketSweepBoardSort.Taker => "instant multiplier",
        MarketSweepBoardSort.Gain => "gain",
        MarketSweepBoardSort.Lot => "lot",
        MarketSweepBoardSort.CycleMinutes => "cycle minutes",
        MarketSweepBoardSort.History => "traded history",
        _ => "chaos/hr",
    };

    /// <summary>
    /// Reorders ranked cycles by one column, descending, with the same ordinal signature tie-break
    /// <see cref="MarketSweepCycleScore.Rank"/> uses so a redraw never reshuffles equal rows.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="MarketSweepBoardSort.CycleMinutes"/> is negated, because every other column wants its
    /// largest value first and this one wants its smallest: the cycle worth looking at is the one that
    /// closes soonest.
    /// </para>
    /// <para>
    /// Only <see cref="MarketSweepBoardSort.ChaosPerHour"/> ranks across denominations.
    /// <see cref="MarketSweepBoardSort.Gain"/> and <see cref="MarketSweepBoardSort.Lot"/> are quoted in each
    /// cycle's own starting currency, so a Divine-start row and a Chaos-start row sorted by gain are being
    /// compared on numbers that are not in the same unit. That is deliberate - it answers "how much does
    /// this one move" - but it is not a profitability ranking, and only chaos/hr is.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<MarketSweepCycle> Sort(
        IReadOnlyList<MarketSweepCycle> cycles,
        MarketSweepBoardSort sort)
    {
        ArgumentNullException.ThrowIfNull(cycles);
        return cycles
            .OrderByDescending(cycle => sort switch
            {
                MarketSweepBoardSort.Multiplier => cycle.Multiplier,
                MarketSweepBoardSort.Taker => cycle.TakerMultiplier,
                MarketSweepBoardSort.Gain => cycle.GainStart,
                MarketSweepBoardSort.Lot => cycle.Lot,
                MarketSweepBoardSort.CycleMinutes => -cycle.CycleMinutes,
                MarketSweepBoardSort.History => cycle.Execution.Orders,
                _ => cycle.ChaosPerHour,
            })
            .ThenBy(cycle => cycle.Signature, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// One cycle as display cells, in <see cref="ColumnHeadings"/> order. Formatting is invariant so the
    /// board reads the same on every machine.
    /// </summary>
    public static IReadOnlyList<string> FormatRow(MarketSweepCycle cycle)
    {
        ArgumentNullException.ThrowIfNull(cycle);
        return
        [
            cycle.Label,
            cycle.DescribeModes(),
            cycle.Multiplier.ToString("0.000", CultureInfo.InvariantCulture),
            cycle.TakerMultiplier.ToString("0.000", CultureInfo.InvariantCulture),
            cycle.Lot.ToString(CultureInfo.InvariantCulture),
            cycle.GainStart.ToString("0.0", CultureInfo.InvariantCulture),
            cycle.ProfitChaos <= 0 ? "-" : cycle.ChaosPerHour.ToString("0.0", CultureInfo.InvariantCulture),
            cycle.CycleMinutes.ToString("0.0", CultureInfo.InvariantCulture),
            cycle.DescribeQueues(),
            cycle.DescribeTurnover(),
            cycle.Execution.Describe(),
        ];
    }

    public static string Abbreviate(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return name.Length <= BoardNameLength ? name : name[..(BoardNameLength - 1)] + "~";
    }

    public static IReadOnlyList<MarketSweepRow> Rank(
        IReadOnlyDictionary<CurrencyPairKey, IReadOnlyList<MarketObservation>> observations,
        ExecutionHistoryStatistics history,
        MarketSweepScoreSettings settings)
    {
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(settings);

        var rows = new List<MarketSweepRow>(observations.Count * 2);
        foreach (var entry in observations)
        {
            if (entry.Value.Count == 0)
            {
                continue;
            }

            var latest = entry.Value[^1];
            rows.Add(Row(entry.Value, latest, latest.Offered, latest.Wanted, history, settings));
            rows.Add(Row(entry.Value, latest, latest.Wanted, latest.Offered, history, settings));
        }

        // Deterministic: score first, then the directed signature, so a redraw never reshuffles ties.
        return rows
            .OrderByDescending(row => row.Score)
            .ThenBy(row => row.Signature, StringComparer.Ordinal)
            .ToArray();
    }

    private static MarketSweepRow Row(
        IReadOnlyList<MarketObservation> series,
        MarketObservation latest,
        CurrencyIdentity from,
        CurrencyIdentity to,
        ExecutionHistoryStatistics history,
        MarketSweepScoreSettings settings)
    {
        var book = latest.Direction(from);
        var churn = MeasureChurn(series, from, settings.ChurnIntervalCapMinutes);
        var execution = history.For(latest.Pair);
        var margin = MarginFraction(book);
        var depth = Math.Min(book.TradableDepth, settings.DepthCap);
        var expectedMinutes = ExpectedMinutes(
            execution, churn, book.CompetingQueueAhead, settings.ChurnIntervalCapMinutes);
        var score = margin <= 0 || depth <= 0
            ? 0
            : margin * depth * execution.FillConfidence / expectedMinutes;
        return new MarketSweepRow(
            latest.Pair,
            from,
            to,
            book.CompetingRate,
            book.ImmediateRate,
            margin,
            book.ImmediateInputDepth,
            book.CompetingQueueAhead,
            book.TradableDepth,
            churn,
            execution,
            expectedMinutes,
            score,
            latest.ObservedAtUtc,
            series.Count);
    }

    /// <summary>
    /// How long a maker order on this pair is expected to sit. Measured fill times win outright.
    ///
    /// Failing those, the queue is the thing that has to drain before our order is reached, and depth turnover
    /// is a measurement of exactly that draining, in the same units as the queue itself — so
    /// <c>queue / turnover</c> is the most direct inference available. It is an approximation and worth being
    /// honest about: turnover sums both sides of the direction and counts rows being *added* the same as rows
    /// being consumed, so it runs optimistic on a book that is filling up rather than emptying.
    ///
    /// Only when no depth moved at all do we fall back to head movement, which says merely that someone is
    /// repricing rather than how much is trading. A pair with neither signal is assumed as slow as the churn
    /// window allows rather than being flattered by an optimistic default.
    /// </summary>
    private static double ExpectedMinutes(
        PairExecutionStatistics execution,
        MarketChurn churn,
        long competingQueueAhead,
        int intervalCapMinutes)
    {
        if (execution.MedianFillMinutes is { } measured && measured > 0)
        {
            return measured;
        }

        if (churn.DepthTurnoverPerMinute is { } turnover && turnover > 0 && competingQueueAhead > 0)
        {
            return Math.Clamp(competingQueueAhead / turnover, 1.0, intervalCapMinutes);
        }

        if (churn.MovesPerMinute is { } moves && moves > 0)
        {
            return Math.Clamp(1.0 / moves, 1.0, intervalCapMinutes);
        }

        return intervalCapMinutes;
    }
}
