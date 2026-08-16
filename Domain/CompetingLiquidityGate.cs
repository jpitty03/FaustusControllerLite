using System.Numerics;

namespace FaustusControllerLite.Domain;

/// <summary>
/// Whether a competing price is a market at all, rather than one order posted at a silly number.
///
/// A competing sell order is priced at the head of the competing ladder and then waits. Nothing in that
/// pricing asks whether the head is reachable, so a single troll order sets the price for the whole
/// holding and the resulting ask never fills. In the 2026-08-15 book the
/// <c>Divine Orb / Expedition Scarab</c> pair carried exactly one competing level - 1 Divine per Scarab,
/// three units listed - against an immediate side of 450 Scarabs per Divine. Sixty Scarabs worth 47 Chaos
/// on their own healthy Chaos book were priced at 11,880 Chaos and posted against nobody.
///
/// Two independent conditions decide it, because credibility and price are separate failures. A queue
/// below the floor means nobody is standing behind that price; this is the same rule and the same default
/// as <see cref="MarketSweepScore.DefaultMinCycleQueue"/>, which the market sweep already applies for the
/// same reason. A rate far above the same-direction immediate rate means the price is unmoored from the
/// only rate in that direction with a provably resting counterparty. On the 2026-08-15 book the trolled
/// rows sat at 450x and 200x their own immediate rate behind queues of 3 and 1, while every legitimate
/// row sat between 1.001x and 11.4x behind queues of 80 to 8689.
/// </summary>
public static class CompetingLiquidityGate
{
    /// <summary>
    /// The smallest queue a competing sell may rest behind and still be believed. Shared with the market
    /// sweep rather than picked again: both features are asking the same question of the same books.
    /// </summary>
    public const long DefaultMinCompetingQueue = MarketSweepScore.DefaultMinCycleQueue;

    /// <summary>
    /// How many times the same-direction immediate rate a competing rate may reach before it stops being a
    /// price and starts being a dare. The default sits an order of magnitude above the widest legitimate
    /// row measured (11.4x) and an order of magnitude below the narrowest troll (200x), so it rejects the
    /// unfillable without touching real business.
    /// </summary>
    public const long DefaultMaxCompetingSpread = 25;

    /// <summary>
    /// Whether <paramref name="competing"/> may be priced against. An edge that is not a competing edge
    /// passes untouched: an immediate order crosses the spread against depth that is already resting, so it
    /// has nothing to prove here and <see cref="QuoteExecutionIntent.Immediate"/> sizing is unaffected.
    ///
    /// <paramref name="immediate"/> is the same-direction immediate edge, or null when the capture had
    /// none. Null fails the spread half whenever that half is enabled: no immediate edge means no proven
    /// counterparty anywhere in this direction, which is strictly worse evidence than a wide one.
    ///
    /// Thresholds are inclusive, and each half is released by its permissive extreme - a queue floor of 0
    /// or a spread cap of <see cref="long.MaxValue"/> - so either can be disabled without the other.
    /// </summary>
    public static bool IsBelievable(
        DirectedExchangeEdge competing,
        DirectedExchangeEdge? immediate,
        long minimumQueue,
        long maximumSpread,
        out string reason)
    {
        ArgumentNullException.ThrowIfNull(competing);
        if (minimumQueue < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumQueue));
        }

        if (maximumSpread < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumSpread));
        }

        reason = string.Empty;
        if (competing.ExecutionIntent != QuoteExecutionIntent.Competing)
        {
            return true;
        }

        if (competing.CompetingQueueAhead < minimumQueue)
        {
            reason = $"competing queue {competing.CompetingQueueAhead} at {competing.Rate} is below the " +
                $"{minimumQueue} minimum, so nobody is standing behind that price";
            return false;
        }

        if (maximumSpread == long.MaxValue)
        {
            return true;
        }

        if (immediate is null)
        {
            reason = $"competing rate {competing.Rate} has no immediate rate in the same direction to " +
                "anchor it, so nothing proves a counterparty exists";
            return false;
        }

        // Cross-multiplied in BigInteger for the same reason Rational.CompareTo is: every part is a long,
        // so their products are not. This is a place / do-not-place decision, so no float may enter it.
        var competingSide = (BigInteger)competing.Rate.Numerator * immediate.Rate.Denominator;
        var immediateSide = (BigInteger)maximumSpread * immediate.Rate.Numerator * competing.Rate.Denominator;
        if (competingSide > immediateSide)
        {
            reason = $"competing rate {competing.Rate} is more than {maximumSpread}x the immediate rate " +
                $"{immediate.Rate} in the same direction, so it is not anchored to a resting counterparty";
            return false;
        }

        return true;
    }
}
