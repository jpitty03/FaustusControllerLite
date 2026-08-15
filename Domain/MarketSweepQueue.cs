using System.Globalization;

namespace FaustusControllerLite.Domain;

/// <summary>
/// One probe: the pinned offered currency and the currency being sampled against it.
///
/// It carries the wanted identity directly rather than a <see cref="TradableEntry"/> because the hub step
/// has no tradable entry to carry. <c>TradablesCatalogue</c> excludes Chaos Orb and Divine Orb as bankroll
/// currencies, so the one pair that closes every cycle can never be resolved as a target - see
/// <see cref="MarketSweepQueue.Build"/>. The name is kept alongside the identity only for display.
/// </summary>
public sealed record MarketSweepStep(CurrencyIdentity Offered, CurrencyIdentity Wanted, string Name)
{
    public CurrencyPairKey Pair => new(Offered, Wanted);

    public string Describe() => $"{Offered.Name}>{Name}";
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
    /// The hub pair first, then two passes over the enabled targets: every Chaos-offered pair, then every
    /// Divine-offered pair. A target that is itself one of the two hub currencies is dropped from both
    /// passes, even though resolution already excludes them: the hub step samples that book once, and a
    /// target pass would sample the same canonical pair a second time.
    /// </summary>
    /// <remarks>
    /// The leading Divine-offered hub step costs one extra offered-side selection for the whole sweep,
    /// because it breaks the Chaos-then-Divine grouping the rest of the ordering exists to preserve. It is
    /// worth it: the hub book closes every cycle the board scores, so a sweep abandoned partway through the
    /// Chaos pass still yields usable cycles for the targets it reached. Captured at the start of the Divine
    /// pass instead, an interrupted sweep would yield none at all.
    ///
    /// Neither hub currency is a resolvable target - <c>TradablesCatalogue</c> excludes both as bankroll
    /// currencies - so this pair can only ever enter the sweep from here.
    /// </remarks>
    public static IReadOnlyList<MarketSweepStep> Build(
        IReadOnlyList<TradableEntry> targets,
        CurrencyIdentity chaos,
        CurrencyIdentity divine)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(chaos);
        ArgumentNullException.ThrowIfNull(divine);

        var steps = new List<MarketSweepStep>((targets.Count * 2) + 1);
        if (!chaos.Equals(divine))
        {
            steps.Add(new MarketSweepStep(divine, chaos, chaos.Name));
        }

        foreach (var offered in new[] { chaos, divine })
        {
            foreach (var target in targets)
            {
                if (!target.Target.Identity.Equals(chaos) && !target.Target.Identity.Equals(divine))
                {
                    steps.Add(new MarketSweepStep(offered, target.Target.Identity, target.Name));
                }
            }
        }

        return steps;
    }

    /// <summary>
    /// The subset of <paramref name="targets"/> that some cycle on the board currently makes money on.
    ///
    /// This is the same test the board's own health filter applies, restated here rather than read off
    /// the already-filtered rows, because <c>EnableCycleHealthFilter</c> can be off - and when it is, the
    /// board holds every losing loop too. A scoped sweep must mean the same thing either way.
    ///
    /// The input order is preserved, not the board's ranking. <see cref="Build"/> depends on it: the
    /// Chaos-then-Divine grouping is what selects the offered side twice per sweep instead of twice per
    /// pair, and that saving is the whole reason a scoped sweep is worth pressing.
    /// </summary>
    /// <remarks>
    /// A target qualifies on <em>any</em> profitable cycle. The same target is scored twice - once
    /// starting in Divine and once in Chaos (see <c>MarketSweepCycleScore.Rank</c>) - and one profitable
    /// direction is a reason to re-observe the pair, whatever the other direction reads.
    /// </remarks>
    public static IReadOnlyList<TradableEntry> SelectProfitableTargets(
        IReadOnlyList<TradableEntry> targets,
        IReadOnlyList<MarketSweepCycle> cycles)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(cycles);

        var profitable = new HashSet<string>(StringComparer.Ordinal);
        foreach (var cycle in cycles)
        {
            if (cycle.IsHealthy && cycle.Multiplier > 1)
            {
                profitable.Add(cycle.Target.Metadata);
            }
        }

        return targets
            .Where(target => profitable.Contains(target.Target.Identity.Metadata))
            .ToArray();
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

        // The hub step is always present, so a bare count would never be zero and a sweep with no enabled
        // categories would quietly run one probe and stop. The refusal has to be about targets.
        var steps = Build(targets, chaos, divine);
        if (steps.Count <= 1)
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
