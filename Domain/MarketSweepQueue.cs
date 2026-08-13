using System.Globalization;

namespace FaustusControllerLite.Domain;

/// <summary>One probe: the pinned offered currency and the tradable being sampled against it.</summary>
public sealed record MarketSweepStep(CurrencyIdentity Offered, TradableEntry Target)
{
    public CurrencyIdentity Wanted => Target.Target.Identity;

    public CurrencyPairKey Pair => new(Offered, Wanted);

    public string Describe() => $"{Offered.Name}>{Target.Name}";
}

/// <summary>
/// Orders the sweep and decides what the idle sweeper looks at next.
///
/// The ordering is not cosmetic. <c>BeginSideSelection</c> skips a side entirely when it already shows the
/// wanted metadata, so grouping every Chaos-offered probe together and every Divine-offered probe together
/// means the offered side is selected twice for the whole sweep instead of twice per pair. One capture per
/// unordered pair is enough because <c>MarketCaptureNormalizer.CreateEdges</c> yields both intents in both
/// orientations from a single capture.
///
/// Everything here is pure. The queue holds no controller, sends no input, and decides nothing about whether
/// a probe may run — the driver owns that and passes in what it observes.
/// </summary>
public sealed class MarketSweepQueue
{
    /// <summary>
    /// How much longer a pair whose last observation showed no tradable depth in either direction waits
    /// before being re-probed. It is a demotion, not an exclusion: an empty book is a fact about one moment,
    /// and a pair that acquires liquidity later must still be found eventually.
    /// </summary>
    public const int EmptyBookStaleMultiplier = 6;

    public IReadOnlyList<MarketSweepStep> Steps { get; private set; } = [];

    public int Index { get; private set; }

    public bool IsRunning { get; private set; }

    public int Observed { get; private set; }

    public int Skipped { get; private set; }

    public MarketSweepStep? Current => IsRunning && Index < Steps.Count ? Steps[Index] : null;

    public string Progress => string.Create(
        CultureInfo.InvariantCulture,
        $"{Math.Min(Index + (IsRunning ? 1 : 0), Steps.Count)}/{Steps.Count}");

    /// <summary>
    /// Two passes over the enabled targets: every Chaos-offered pair, then every Divine-offered pair. A
    /// target that is itself one of the two offered currencies is dropped rather than producing a
    /// same-currency pair, even though resolution already excludes them.
    /// </summary>
    public static IReadOnlyList<MarketSweepStep> Build(
        IReadOnlyList<TradableEntry> targets,
        CurrencyIdentity chaos,
        CurrencyIdentity divine)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(chaos);
        ArgumentNullException.ThrowIfNull(divine);

        var steps = new List<MarketSweepStep>(targets.Count * 2);
        foreach (var offered in new[] { chaos, divine })
        {
            foreach (var target in targets)
            {
                if (!target.Target.Identity.Equals(offered))
                {
                    steps.Add(new MarketSweepStep(offered, target));
                }
            }
        }

        return steps;
    }

    public bool TryStart(
        IReadOnlyList<TradableEntry> targets,
        CurrencyIdentity chaos,
        CurrencyIdentity divine,
        bool probeInFlight,
        out string failure)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(chaos);
        ArgumentNullException.ThrowIfNull(divine);

        if (IsRunning)
        {
            failure = "A market sweep is already running.";
            return false;
        }

        if (probeInFlight)
        {
            failure = "A verified pair operation is already running; the sweep never competes for the picker.";
            return false;
        }

        if (chaos.Equals(divine))
        {
            failure = "The sweep needs two distinct offered currencies.";
            return false;
        }

        var steps = Build(targets, chaos, divine);
        if (steps.Count == 0)
        {
            failure = "No enabled tradable resolved to an exchange target.";
            return false;
        }

        Steps = steps;
        Index = 0;
        Observed = 0;
        Skipped = 0;
        IsRunning = true;
        failure = string.Empty;
        return true;
    }

    /// <summary>Moves past the current step. A step that produced no usable capture still advances: the
    /// sweep is a survey, and one unreadable book must not stall the other 500.</summary>
    public void Advance(bool observed)
    {
        if (!IsRunning)
        {
            return;
        }

        if (observed)
        {
            Observed++;
        }
        else
        {
            Skipped++;
        }

        Index++;
        if (Index >= Steps.Count)
        {
            Index = Steps.Count;
            IsRunning = false;
        }
    }

    public void Stop()
    {
        IsRunning = false;
        Steps = [];
        Index = 0;
    }

    /// <summary>
    /// The single stalest eligible step, or null when nothing is due. A pair never observed is maximally
    /// stale and always eligible, so a fresh install fills the board in list order rather than sitting idle.
    /// Ties resolve to the earlier step so repeated calls with an unchanged clock are deterministic.
    /// </summary>
    public static MarketSweepStep? SelectStalest(
        IReadOnlyList<MarketSweepStep> steps,
        IReadOnlyDictionary<CurrencyPairKey, IReadOnlyList<MarketObservation>> observations,
        DateTimeOffset nowUtc,
        int stalePairMinutes)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(observations);
        if (stalePairMinutes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(stalePairMinutes));
        }

        MarketSweepStep? best = null;
        var bestAge = double.NegativeInfinity;
        foreach (var step in steps)
        {
            if (!observations.TryGetValue(step.Pair, out var series) || series.Count == 0)
            {
                return step;
            }

            var latest = series[^1];
            var age = (nowUtc - latest.ObservedAtUtc).TotalMinutes;
            var due = IsDead(latest)
                ? (double)stalePairMinutes * EmptyBookStaleMultiplier
                : stalePairMinutes;
            if (age < due || age <= bestAge)
            {
                continue;
            }

            best = step;
            bestAge = age;
        }

        return best;
    }

    /// <summary>
    /// A pair with no tradable depth in either direction. Depth on one side of a book alone cannot be
    /// traded, so this is the honest test of "nothing here", not a rate check.
    /// </summary>
    public static bool IsDead(MarketObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        return observation.Forward.TradableDepth == 0 && observation.Reverse.TradableDepth == 0;
    }
}
