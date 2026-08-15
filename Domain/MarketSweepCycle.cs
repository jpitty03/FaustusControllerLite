using System.Globalization;
using System.Numerics;

namespace FaustusControllerLite.Domain;

/// <summary>
/// One maker leg of a cycle, carrying the evidence the health filter reads. It is a projection of
/// <see cref="MarketSweepRow"/>, not a second measurement: every field here was computed by
/// <see cref="MarketSweepScore.Rank"/> and is reused unchanged.
/// </summary>
/// <summary>
/// How a leg would be executed. <see cref="Make"/> posts an order at the competing head and waits for the
/// queue in front of it to drain; <see cref="Take"/> crosses the spread against what is already resting.
/// </summary>
public enum MarketSweepLegMode
{
    Take,
    Make,
}

public sealed record MarketSweepCycleLeg(
    CurrencyIdentity From,
    CurrencyIdentity To,
    Rational MakerRate,
    Rational? TakerRate,
    long TradableDepth,
    long ImmediateInputDepth,
    long CompetingQueueAhead,
    double? TurnoverPerMinute,
    double ExpectedMinutes,
    MarketSweepLegMode Mode = MarketSweepLegMode.Make)
{
    /// <summary>
    /// Whether this leg is a book we could actually work, rather than a single order posted at a silly price.
    ///
    /// Two independent conditions, and both matter. A queue below the floor means nobody is standing behind
    /// that price, which is the signature of the troll orders that dominate the raw margin ranking - the
    /// widest spreads in the 2026-08-13 sweep sat on queues of 1 to 4. Zero measured turnover means the book
    /// has not been *seen* to move, which needs two observations inside the churn cap; an inferred speed is
    /// not evidence, so an unmeasured leg fails rather than being flattered by a fallback.
    /// </summary>
    public bool IsHealthy(long minimumQueue) =>
        CompetingQueueAhead >= minimumQueue && TurnoverPerMinute is > 0;

    /// <summary>
    /// How much wider the maker rate is than the taker rate. Always at least 1: each book carries only two
    /// independent prices, so the maker side of a direction can never be worse than the taker side.
    ///
    /// Computed in double rather than <see cref="Rational"/> because it is a threshold comparison, not money -
    /// nothing downstream multiplies a spread into a lot or a gain. The rates themselves stay exact.
    /// </summary>
    public double Spread =>
        TakerRate is { } taker && taker.Numerator != 0
            ? (double)MakerRate.Numerator * taker.Denominator /
              ((double)MakerRate.Denominator * taker.Numerator)
            : 1.0;

    /// <summary>Whether there is anything resting to cross against.</summary>
    public bool CanTake => TakerRate is not null && ImmediateInputDepth > 0;

    /// <summary>
    /// Which side of the spread this leg would be worked from.
    ///
    /// Making is chosen only when it earns its cost: the extra width clears
    /// <see cref="MarketSweepScoreSettings.MakerSpreadThreshold"/>, the book is healthy enough to believe,
    /// and the queue in front drains inside <see cref="MarketSweepScoreSettings.MakerLegMinutesCap"/>. All
    /// three, because width and speed and credibility are independent - a 1.7x spread behind a six-hour queue
    /// is not an opportunity. A leg with nothing to cross against is made by default; that is not a judgement
    /// that making is good there, only that taking is not available, and <see cref="IsExecutable"/> is what
    /// decides whether the resulting cycle is shown at all.
    /// </summary>
    public MarketSweepLegMode ChooseMode(MarketSweepScoreSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var worthMaking = Spread >= settings.MakerSpreadThreshold &&
                          IsHealthy(settings.MinCycleQueue) &&
                          ExpectedMinutes <= settings.MakerLegMinutesCap &&
                          TradableDepth > 0;
        return !CanTake || worthMaking ? MarketSweepLegMode.Make : MarketSweepLegMode.Take;
    }

    /// <summary>The rate this leg trades at under its assigned <see cref="Mode"/>.</summary>
    public Rational Rate => Mode == MarketSweepLegMode.Make ? MakerRate : TakerRate ?? MakerRate;

    /// <summary>
    /// The depth the assigned mode can actually consume. A maker order is bounded by both sides of the book
    /// (<see cref="TradableDepth"/> is their minimum) because it needs the queue to drain <em>and</em> input
    /// to arrive; a taker order only needs what is resting right now.
    /// </summary>
    public long Depth => Mode == MarketSweepLegMode.Make ? TradableDepth : ImmediateInputDepth;

    /// <summary>
    /// What this leg costs in time. <see cref="ExpectedMinutes"/> is entirely a queue-drain estimate, so it
    /// describes a maker leg and says nothing at all about a taker one - charging a taker leg the drain time
    /// would rank a three-click cycle as though it were a three-hour wait.
    /// </summary>
    public double Minutes(MarketSweepScoreSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return Mode == MarketSweepLegMode.Make ? ExpectedMinutes : settings.TakerLegMinutes;
    }

    /// <summary>
    /// Whether this leg can be worked at all under its assigned mode. This is the health question made
    /// mode-aware, and it is the substantive change: queue depth and measured turnover are what a
    /// <em>maker</em> order needs, because they are what the wait depends on. A taker leg does not wait, so
    /// asking it for a queue hides cycles that are executable this second - nine of them on the 2026-08-13
    /// sweep, including a 2.01x Kalguuran Scarab loop with depth on all three legs.
    /// </summary>
    public bool IsExecutable(long minimumQueue) =>
        Mode == MarketSweepLegMode.Make
            ? IsHealthy(minimumQueue) && TradableDepth > 0
            : CanTake;
}

/// <summary>
/// A complete round trip back to the currency it started in: <c>Divine &gt; Target &gt; Chaos &gt; Divine</c>,
/// or its mirror. This is what the board ranks.
///
/// The board used to rank each directed leg on its own, which cannot see a lopsided pair - it shows the side
/// with 3782 units of queue and never checks that the return side has 19. Scoring the closed loop is the only
/// way that asymmetry becomes visible, because the loop has to pay for both.
///
/// Each book carries only two independent prices - the four stored rates are those two viewed from both
/// directions, verified as exact reciprocals on all 875 captures of the 2026-08-13 sweep. So on any single
/// leg the maker rate is never worse than the taker rate, and an all-maker cycle always shows the largest
/// <see cref="Multiplier"/>. That does not make it the best cycle: the maker multiplier is only collected if
/// every leg fills, and the measured fill rate is about 60%. <see cref="Multiplier"/> is therefore the
/// multiplier of the <em>chosen</em> mix (see <see cref="MarketSweepCycleLeg.ChooseMode"/>), and
/// <see cref="TakerMultiplier"/> sits beside it as the number that could be executed this second.
/// </summary>
public sealed record MarketSweepCycle(
    CurrencyIdentity Start,
    CurrencyIdentity Target,
    IReadOnlyList<MarketSweepCycleLeg> Legs,
    double Multiplier,
    double TakerMultiplier,
    long Lot,
    double GainStart,
    double ProfitChaos,
    double CycleMinutes,
    PairExecutionStatistics Execution,
    bool IsHealthy)
{
    /// <summary>
    /// The widest real label a sweep can produce: <c>D&gt;</c> + the longest tradable name (39) + <c>&gt;C&gt;D</c>
    /// is 45 characters, so the guard provably never fires on swept data. It exists only so that a
    /// hand-constructed or future longer name degrades to a clipped cell instead of printing over the next
    /// column.
    /// </summary>
    public const int LabelLength = 46;

    /// <summary>
    /// One character per leg in cycle order - <c>M</c> where the policy would post an order, <c>T</c> where it
    /// would cross the spread. <c>TTT</c> is a cycle you can execute right now.
    /// </summary>
    public string DescribeModes() =>
        string.Concat(Legs.Select(static leg => leg.Mode == MarketSweepLegMode.Make ? "M" : "T"));

    /// <summary>Chaos-equivalent profit per hour: the one figure that ranks both start currencies together.</summary>
    public double ChaosPerHour => CycleMinutes <= 0 ? 0 : ProfitChaos / CycleMinutes * 60;

    /// <summary>Ordinal tie-break, so a redraw never reshuffles rows that score identically.</summary>
    public string Signature => $"{Start.Metadata}>{Target.Metadata}";

    /// <summary>
    /// <c>D&gt;Vaal Orb&gt;C&gt;D</c>. The hubs abbreviate to their initial and the target prints in full,
    /// because the target is the only part that varies and the tradable names share long prefixes - twenty of
    /// them begin "Deafening Essence of".
    /// </summary>
    public string Label
    {
        get
        {
            var start = Initial(Start);
            var bridge = Legs.Count >= 2 ? Initial(Legs[1].To) : "?";
            var label = $"{start}>{Target.Name}>{bridge}>{start}";
            return label.Length <= LabelLength ? label : string.Concat(label.AsSpan(0, LabelLength - 1), "~");
        }
    }

    /// <summary>Per-leg competing queues, in cycle order, so the health verdict can be argued with.</summary>
    public string DescribeQueues() =>
        string.Join("/", Legs.Select(leg => leg.CompetingQueueAhead.ToString(CultureInfo.InvariantCulture)));

    /// <summary>Per-leg measured turnover, in cycle order. A leg reading <c>-</c> is why a cycle reads thin.</summary>
    public string DescribeTurnover() => string.Join(
        "/",
        Legs.Select(leg => leg.TurnoverPerMinute is { } turnover
            ? turnover.ToString("0.0", CultureInfo.InvariantCulture)
            : "-"));

    private static string Initial(CurrencyIdentity currency) =>
        currency.Name.Length == 0 ? "?" : currency.Name[..1];
}

/// <summary>
/// Builds cycles out of the directed rows <see cref="MarketSweepScore.Rank"/> has already produced. Pure: it
/// holds no store, reads no clock, and is handed the bankroll rather than reaching for it.
/// </summary>
public static class MarketSweepCycleScore
{
    /// <summary>
    /// Every cycle the swept books can close, ordered by chaos per hour with the ordinal signature tie-break.
    ///
    /// A target produces cycles only when it is quoted against <em>both</em> hubs, and the hub pair itself
    /// must have been swept - it is the leg that closes every loop, and no target pass can ever produce it
    /// because <c>TradablesCatalogue</c> excludes both bankroll currencies. <c>MarketSweepQueue.Build</c>
    /// emits it as the first step of every sweep for exactly this reason; before the hub pair has been
    /// observed once, this returns nothing at all rather than guessing at the closing rate.
    /// </summary>
    public static IReadOnlyList<MarketSweepCycle> Rank(
        IReadOnlyList<MarketSweepRow> rows,
        CurrencyIdentity chaos,
        CurrencyIdentity divine,
        MarketSweepScoreSettings settings)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(chaos);
        ArgumentNullException.ThrowIfNull(divine);
        ArgumentNullException.ThrowIfNull(settings);
        if (chaos.Equals(divine))
        {
            throw new ArgumentException("A cycle needs two distinct hub currencies.", nameof(divine));
        }

        var byDirection = new Dictionary<(string From, string To), MarketSweepRow>();
        foreach (var row in rows)
        {
            byDirection[(row.From.Metadata, row.To.Metadata)] = row;
        }

        // The conversion that lets a Divine-denominated gain be ranked against a Chaos-denominated one. It is
        // the hub book's own maker rate, not a configured constant, so the column moves with the market.
        if (!TryLeg(byDirection, divine, chaos, out var hubToChaos))
        {
            return [];
        }

        var cycles = new List<MarketSweepCycle>();
        foreach (var target in Bridges(rows, chaos, divine))
        {
            Add(cycles, byDirection, divine, target, chaos, hubToChaos.MakerRate, settings.PrincipalDivine, true, settings);
            Add(cycles, byDirection, chaos, target, divine, hubToChaos.MakerRate, settings.PrincipalChaos, false, settings);
        }

        cycles.Sort(static (left, right) =>
        {
            var byRate = right.ChaosPerHour.CompareTo(left.ChaosPerHour);
            return byRate != 0 ? byRate : StringComparer.Ordinal.Compare(left.Signature, right.Signature);
        });
        return cycles;
    }

    /// <summary>
    /// Targets quoted off both hubs, in ordinal metadata order. A target reachable from only one hub cannot
    /// bridge them, so it can never appear in a cycle no matter how wide its spread is - which is most of what
    /// the leg board was ranking.
    /// </summary>
    private static IReadOnlyList<CurrencyIdentity> Bridges(
        IReadOnlyList<MarketSweepRow> rows,
        CurrencyIdentity chaos,
        CurrencyIdentity divine)
    {
        var offChaos = new HashSet<string>(StringComparer.Ordinal);
        var offDivine = new HashSet<string>(StringComparer.Ordinal);
        var identities = new Dictionary<string, CurrencyIdentity>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (row.To.Equals(chaos) || row.To.Equals(divine))
            {
                continue;
            }

            if (row.From.Equals(chaos))
            {
                offChaos.Add(row.To.Metadata);
            }
            else if (row.From.Equals(divine))
            {
                offDivine.Add(row.To.Metadata);
            }
            else
            {
                continue;
            }

            identities[row.To.Metadata] = row.To;
        }

        offChaos.IntersectWith(offDivine);
        return offChaos
            .Order(StringComparer.Ordinal)
            .Select(metadata => identities[metadata])
            .ToArray();
    }

    private static void Add(
        List<MarketSweepCycle> cycles,
        Dictionary<(string From, string To), MarketSweepRow> byDirection,
        CurrencyIdentity start,
        CurrencyIdentity target,
        CurrencyIdentity bridge,
        Rational chaosPerDivine,
        long principal,
        bool startsInDivine,
        MarketSweepScoreSettings settings)
    {
        if (!TryLeg(byDirection, start, target, out var up) ||
            !TryLeg(byDirection, target, bridge, out var across) ||
            !TryLeg(byDirection, bridge, start, out var close))
        {
            return;
        }

        // Each leg picks its own side of the spread. The decision is per leg and not per cycle because the
        // three legs are unrelated books: the 2026-08-13 Regal loop wants a maker order on its 1.33x opening
        // leg and a taker click on its hub leg, whose spread is 1.005.
        MarketSweepCycleLeg[] legs = [up, across, close];
        for (var index = 0; index < legs.Length; index++)
        {
            legs[index] = legs[index] with { Mode = legs[index].ChooseMode(settings) };
        }

        var multiplier = Product(legs, static leg => leg.Rate);

        // What the whole loop is worth if every leg is crossed immediately. Zero when any leg has no resting
        // side at all, which reads as "not executable now" rather than as a bad price.
        var takerMultiplier = legs.Any(static leg => leg.TakerRate is null)
            ? 0
            : Product(legs, static leg => leg.TakerRate!.Value);

        var lot = Lot(legs, principal);
        var gain = lot * (multiplier - 1.0);
        cycles.Add(new MarketSweepCycle(
            start,
            target,
            legs,
            multiplier,
            takerMultiplier,
            lot,
            gain,
            startsInDivine ? gain * chaosPerDivine.Numerator / chaosPerDivine.Denominator : gain,
            legs.Sum(leg => leg.Minutes(settings)),
            byDirection[(start.Metadata, target.Metadata)].Execution,
            legs.All(leg => leg.IsExecutable(settings.MinCycleQueue))));
    }

    /// <summary>
    /// The largest whole starting amount that survives every leg's tradable depth.
    ///
    /// Capping only the first leg would report a gain that cannot be taken: the amount entering leg two is the
    /// amount leaving leg one, so a deep opening followed by a shallow bridge is still a shallow cycle. The
    /// running rate stays exact in <see cref="BigInteger"/> because the cap divides by it - a double here
    /// would round the lot up about as often as down, and a lot is a claim about what the book can absorb.
    /// </summary>
    /// <summary>
    /// The product of one rate per leg, exact until the final conversion.
    ///
    /// BigInteger because <see cref="Rational"/> has no multiply and a naive long product of three rates
    /// overflows on real books: the hub leg alone is ~195/1 and a scarab leg can carry a four-digit
    /// denominator.
    /// </summary>
    private static double Product(
        IReadOnlyList<MarketSweepCycleLeg> legs,
        Func<MarketSweepCycleLeg, Rational> select)
    {
        var numerator = BigInteger.One;
        var denominator = BigInteger.One;
        foreach (var leg in legs)
        {
            var rate = select(leg);
            numerator *= rate.Numerator;
            denominator *= rate.Denominator;
        }

        return (double)numerator / (double)denominator;
    }

    private static long Lot(IReadOnlyList<MarketSweepCycleLeg> legs, long principal)
    {
        if (principal <= 0)
        {
            return 0;
        }

        var cap = (BigInteger)principal;
        var numerator = BigInteger.One;
        var denominator = BigInteger.One;
        foreach (var leg in legs)
        {
            // Units entering this leg per unit committed at the start, so the leg's own depth converts back
            // into a limit on the starting amount. Both the depth and the rate are the ones the leg's
            // assigned mode uses - a taker leg is bounded by what is resting, not by the tradable minimum.
            var allowed = (BigInteger)leg.Depth * denominator / numerator;
            if (allowed < cap)
            {
                cap = allowed;
            }

            numerator *= leg.Rate.Numerator;
            denominator *= leg.Rate.Denominator;
        }

        return cap <= 0 ? 0 : (long)cap;
    }

    /// <summary>
    /// Projects one directed row into a leg. A row with no competing rate is a book with nothing on the maker
    /// side, which cannot be a leg of anything - and maker is the only shape that pays.
    /// </summary>
    private static bool TryLeg(
        Dictionary<(string From, string To), MarketSweepRow> byDirection,
        CurrencyIdentity from,
        CurrencyIdentity to,
        out MarketSweepCycleLeg leg)
    {
        leg = null!;
        if (!byDirection.TryGetValue((from.Metadata, to.Metadata), out var row) ||
            row.CompetingRate is not { } rate)
        {
            return false;
        }

        leg = new MarketSweepCycleLeg(
            from,
            to,
            rate,
            row.ImmediateRate,
            row.TradableDepth,
            row.ImmediateInputDepth,
            row.CompetingQueueAhead,
            row.Churn.DepthTurnoverPerMinute,
            row.ExpectedMinutes);
        return true;
    }
}
