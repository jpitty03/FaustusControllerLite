namespace FaustusControllerLite.Domain;

/// <summary>
/// Picks the one cycle on the board a sweep may stop and trade.
///
/// The board is advisory and stays that way: nothing here is an economic input. The only thing this hands
/// back to the plugin is a <em>target name</em> - precisely what the operator supplies by hand today when
/// they read a row and set <c>TargetCurrency</c> themselves. The trade is then planned from a fresh coherent
/// three-market probe in exact integers, so a stale or optimistic board row costs a probe and is refused,
/// never traded on.
///
/// Pure: no clock, no store, no game handle, no ranking of its own beyond the board's.
/// </summary>
public static class MarketSweepAutoTrade
{
    /// <summary>
    /// The best cycle worth interrupting the sweep for, or null when there is none - which is the ordinary
    /// case and simply means the sweep keeps sweeping.
    /// </summary>
    /// <param name="cycles">Ranked board cycles, unfiltered by any display toggle.</param>
    /// <param name="minimumProfitChaos">The same chaos floor the route planner applies to the real route.</param>
    /// <param name="declined">Target metadata this sweep has already tried and had refused.</param>
    /// <remarks>
    /// <para>
    /// <c>TTT</c> is the gate, not profit. Only for an all-taker row do <see cref="MarketSweepCycle.Multiplier"/>,
    /// <see cref="MarketSweepCycle.Lot"/> and <see cref="MarketSweepCycle.ProfitChaos"/> describe an execution
    /// we could actually perform this second; an <c>MTT</c> row's profit includes a maker leg that would sit
    /// in a queue, and the trade this arms posts no maker legs at all. Gating on an <c>MTT</c> row's profit
    /// would be gating on a number for a different trade.
    /// </para>
    /// <para>
    /// The predicate is restated here rather than read off the board's already-filtered rows, exactly as in
    /// <see cref="MarketSweepQueue.SelectProfitableTargets"/>: <c>EnableCycleHealthFilter</c> and the sort
    /// column are display choices, and neither may change what gets traded.
    /// </para>
    /// <para>
    /// Ranking mirrors <c>MarketSweepCycleScore.Rank</c>'s own comparator - profit first, ordinal
    /// <see cref="MarketSweepCycle.Signature"/> to break ties - so two rows that score identically never
    /// alternate between refreshes. Among <c>TTT</c> rows every cycle costs the same three taker legs, so
    /// ordering by profit and ordering by chaos per hour are the same ordering.
    /// </para>
    /// </remarks>
    public static MarketSweepCycle? SelectTradableCycle(
        IReadOnlyList<MarketSweepCycle> cycles,
        long minimumProfitChaos,
        IReadOnlySet<string> declined)
    {
        ArgumentNullException.ThrowIfNull(cycles);
        ArgumentNullException.ThrowIfNull(declined);

        MarketSweepCycle? best = null;
        foreach (var cycle in cycles)
        {
            if (!IsTradable(cycle, minimumProfitChaos, declined))
            {
                continue;
            }

            if (best is null || Compare(cycle, best) < 0)
            {
                best = cycle;
            }
        }

        return best;
    }

    /// <summary>
    /// Whether this row is one the sweep may act on. <see cref="MarketSweepCycle.IsHealthy"/> is the board's
    /// own verdict and for an all-taker cycle reduces to every leg having a resting quote with depth behind
    /// it (<see cref="MarketSweepCycleLeg.IsExecutable"/>); the lot and multiplier tests reject a row that
    /// closes profitably on paper but has nothing to trade.
    /// </summary>
    public static bool IsTradable(
        MarketSweepCycle cycle,
        long minimumProfitChaos,
        IReadOnlySet<string> declined)
    {
        ArgumentNullException.ThrowIfNull(cycle);
        ArgumentNullException.ThrowIfNull(declined);

        return cycle.DescribeModes() == "TTT" &&
               cycle.IsHealthy &&
               cycle.Multiplier > 1 &&
               cycle.Lot > 0 &&
               cycle.ProfitChaos >= minimumProfitChaos &&
               !declined.Contains(cycle.Target.Metadata);
    }

    private static int Compare(MarketSweepCycle left, MarketSweepCycle right)
    {
        var byProfit = right.ProfitChaos.CompareTo(left.ProfitChaos);
        return byProfit != 0 ? byProfit : StringComparer.Ordinal.Compare(left.Signature, right.Signature);
    }
}
