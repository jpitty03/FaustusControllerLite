using System.Globalization;

namespace FaustusControllerLite.Domain;

public sealed record MarketSweepScoreSettings(
    int ChurnIntervalCapMinutes = MarketSweepScore.DefaultChurnIntervalCapMinutes,
    long DepthCap = MarketSweepScore.DefaultDepthCap);

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
/// only reorders what is on screen — it never changes what was measured or how the score was computed.
/// </summary>
public enum MarketSweepBoardSort
{
    Score,
    Margin,
    Depth,
    Churn,
    Turnover,
    History,
}

public sealed record MarketSweepRow(
    CurrencyPairKey Pair,
    CurrencyIdentity From,
    CurrencyIdentity To,
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
    /// The sweep board deliberately does not use this. It gets a column of its own and prints names in full -
    /// see <see cref="BoardPairLength"/>.
    /// </remarks>
    public const int BoardNameLength = 14;

    /// <summary>
    /// Widest pair cell the board prints, in characters. The columns are fixed pixel offsets, so an
    /// over-long cell overlaps the next column rather than wrapping.
    /// </summary>
    /// <remarks>
    /// Sized so it never fires on real data: a sweep only ever captures hub-to-spoke, and the longest
    /// tradable name (39) against the longer bankroll name (10) is exactly 50. It is a guard for pairs the
    /// sweep does not produce - spoke to spoke - which degrade to a clipped cell instead of printing over
    /// the margin column.
    /// </remarks>
    public const int BoardPairLength = 50;

    public static IReadOnlyList<string> ColumnHeadings { get; } =
        ["pair", "margin%", "imm", "queue", "tradable", "churn/min", "turn/min", "fills:no", "min", "score"];

    public static MarketSweepBoardSort NextSort(MarketSweepBoardSort sort) => sort switch
    {
        MarketSweepBoardSort.Score => MarketSweepBoardSort.Margin,
        MarketSweepBoardSort.Margin => MarketSweepBoardSort.Depth,
        MarketSweepBoardSort.Depth => MarketSweepBoardSort.Churn,
        MarketSweepBoardSort.Churn => MarketSweepBoardSort.Turnover,
        MarketSweepBoardSort.Turnover => MarketSweepBoardSort.History,
        _ => MarketSweepBoardSort.Score,
    };

    public static string DescribeSort(MarketSweepBoardSort sort) => sort switch
    {
        MarketSweepBoardSort.Margin => "margin",
        MarketSweepBoardSort.Depth => "tradable depth",
        MarketSweepBoardSort.Churn => "churn",
        MarketSweepBoardSort.Turnover => "depth turnover",
        MarketSweepBoardSort.History => "traded history",
        _ => "score",
    };

    /// <summary>
    /// Reorders ranked rows by one column, descending, with the same ordinal signature tie-break
    /// <see cref="Rank"/> uses so a redraw never reshuffles equal rows. An unknown churn sorts last rather
    /// than as zero: a pair observed once has no velocity, which is not the same as a still one.
    /// </summary>
    public static IReadOnlyList<MarketSweepRow> Sort(
        IReadOnlyList<MarketSweepRow> rows,
        MarketSweepBoardSort sort)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return rows
            .OrderByDescending(row => sort switch
            {
                MarketSweepBoardSort.Margin => row.MarginFraction,
                MarketSweepBoardSort.Depth => row.TradableDepth,
                MarketSweepBoardSort.Churn => row.Churn.MovesPerMinute ?? double.NegativeInfinity,
                MarketSweepBoardSort.Turnover => row.Churn.DepthTurnoverPerMinute ?? double.NegativeInfinity,
                MarketSweepBoardSort.History => row.Execution.Orders,
                _ => row.Score,
            })
            .ThenBy(row => row.Signature, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// One row as display cells, in <see cref="ColumnHeadings"/> order. Formatting is invariant so the board
    /// reads the same on every machine, and every factor of the score gets its own cell so a ranking can be
    /// argued with instead of trusted.
    /// </summary>
    public static IReadOnlyList<string> FormatRow(MarketSweepRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return
        [
            FormatPair(row.From, row.To),
            (row.MarginFraction * 100).ToString("0.0", CultureInfo.InvariantCulture),
            row.ImmediateInputDepth.ToString(CultureInfo.InvariantCulture),
            row.CompetingQueueAhead.ToString(CultureInfo.InvariantCulture),
            row.TradableDepth.ToString(CultureInfo.InvariantCulture),
            row.Churn.Describe(),
            row.Churn.DescribeTurnover(),
            row.Execution.Describe(),
            row.ExpectedMinutes.ToString("0.0", CultureInfo.InvariantCulture),
            row.Score.ToString("0.000", CultureInfo.InvariantCulture),
        ];
    }

    /// <summary>
    /// The pair cell, both names in full.
    /// </summary>
    /// <remarks>
    /// Names are not abbreviated here even though the column is fixed-width, because tradable names share
    /// long prefixes: twenty essences begin "Deafening Essence of" and seven scarabs begin "Horned Scarab
    /// of". A clip short enough to keep the column narrow collapses each of those families into one
    /// indistinguishable string, which defeats the point of ranking them against each other.
    /// </remarks>
    public static string FormatPair(CurrencyIdentity from, CurrencyIdentity to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        var pair = $"{from.Name}>{to.Name}";
        return pair.Length <= BoardPairLength ? pair : pair[..(BoardPairLength - 1)] + "~";
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
