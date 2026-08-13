using FaustusControllerLite.Probing;

namespace FaustusControllerLite.Domain;

/// <summary>
/// One direction of a book at one instant: the immediate price you can take right now and the competing
/// price you would have to match to join the queue, each with the depth behind it.
/// A rate is stored as its two integer parts so the observation never loses exactness on a round trip;
/// zero for both parts means that side of the book was empty, which is different from a rate of zero.
/// </summary>
public sealed record DirectedBookObservation(
    long ImmediateRateNumerator,
    long ImmediateRateDenominator,
    long ImmediateInputDepth,
    long CompetingRateNumerator,
    long CompetingRateDenominator,
    long CompetingQueueAhead)
{
    public static DirectedBookObservation Empty { get; } = new(0, 0, 0, 0, 0, 0);

    public bool HasImmediate => ImmediateRateNumerator > 0 && ImmediateRateDenominator > 0;

    public bool HasCompeting => CompetingRateNumerator > 0 && CompetingRateDenominator > 0;

    public bool HasBothSides => HasImmediate && HasCompeting;

    public Rational? ImmediateRate =>
        HasImmediate ? new Rational(ImmediateRateNumerator, ImmediateRateDenominator) : null;

    public Rational? CompetingRate =>
        HasCompeting ? new Rational(CompetingRateNumerator, CompetingRateDenominator) : null;

    /// <summary>
    /// Depth that a maker order on this side could actually trade against: both sides must exist, because a
    /// competing price with nothing immediate behind it has nobody to fill it.
    /// </summary>
    public long TradableDepth =>
        HasBothSides ? Math.Min(ImmediateInputDepth, CompetingQueueAhead) : 0;

    public void Validate(string direction)
    {
        if (ImmediateRateNumerator < 0 || ImmediateRateDenominator < 0 ||
            CompetingRateNumerator < 0 || CompetingRateDenominator < 0 ||
            ImmediateInputDepth < 0 || CompetingQueueAhead < 0)
        {
            throw new InvalidDataException($"Observed {direction} book had a negative value.");
        }

        if (ImmediateRateNumerator > 0 != ImmediateRateDenominator > 0 ||
            CompetingRateNumerator > 0 != CompetingRateDenominator > 0)
        {
            throw new InvalidDataException($"Observed {direction} book had a half-present rate.");
        }

        if (!HasImmediate && ImmediateInputDepth != 0)
        {
            throw new InvalidDataException($"Observed {direction} book had depth without an immediate rate.");
        }

        if (!HasCompeting && CompetingQueueAhead != 0)
        {
            throw new InvalidDataException($"Observed {direction} book had a queue without a competing rate.");
        }
    }
}

/// <summary>
/// A single sweep sample of one pair. Both directions are kept because one capture already yields both, and
/// churn is only measurable by comparing consecutive samples of the same direction.
/// </summary>
public sealed record MarketObservation(
    DateTimeOffset ObservedAtUtc,
    string League,
    int AreaInstanceId,
    CurrencyIdentity Offered,
    CurrencyIdentity Wanted,
    DirectedBookObservation Forward,
    DirectedBookObservation Reverse)
{
    public CurrencyPairKey Pair => new(Offered, Wanted);

    /// <summary>
    /// The direction whose input currency is <paramref name="from"/>. Throws when the currency is not part of
    /// this observation, so a caller can never silently read the wrong side of the book.
    /// </summary>
    public DirectedBookObservation Direction(CurrencyIdentity from)
    {
        ArgumentNullException.ThrowIfNull(from);
        if (Offered.Equals(from))
        {
            return Forward;
        }

        if (Wanted.Equals(from))
        {
            return Reverse;
        }

        throw new ArgumentException("Currency is not part of this observation.", nameof(from));
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(League))
        {
            throw new InvalidDataException("Observation league is required.");
        }

        if (ObservedAtUtc == default)
        {
            throw new InvalidDataException("Observation timestamp is required.");
        }

        if (Offered.Equals(Wanted))
        {
            throw new InvalidDataException("Observation currencies must be different.");
        }

        Forward.Validate("forward");
        Reverse.Validate("reverse");
    }

    /// <summary>
    /// Projects a capture into an observation using the same four edges the planner already reads, so the
    /// sweep never re-derives depth or queue arithmetic. A capture whose books are empty yields an
    /// observation with empty directions rather than nothing: knowing a pair is dead is itself a result.
    /// </summary>
    public static MarketObservation FromCapture(MarketCapture capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        var edges = MarketCaptureNormalizer.CreateEdges(capture);
        var observation = new MarketObservation(
            capture.CapturedAtUtc,
            capture.League,
            capture.AreaInstanceId,
            capture.OfferedCurrency,
            capture.WantedCurrency,
            Book(edges, capture.OfferedCurrency),
            Book(edges, capture.WantedCurrency));
        observation.Validate();
        return observation;
    }

    private static DirectedBookObservation Book(
        IReadOnlyList<DirectedExchangeEdge> edges,
        CurrencyIdentity from)
    {
        var immediate = Edge(edges, from, QuoteExecutionIntent.Immediate);
        var competing = Edge(edges, from, QuoteExecutionIntent.Competing);
        return new DirectedBookObservation(
            immediate?.Rate.Numerator ?? 0,
            immediate?.Rate.Denominator ?? 0,
            immediate?.ImmediateInputDepth ?? 0,
            competing?.Rate.Numerator ?? 0,
            competing?.Rate.Denominator ?? 0,
            competing?.CompetingQueueAhead ?? 0);
    }

    private static DirectedExchangeEdge? Edge(
        IReadOnlyList<DirectedExchangeEdge> edges,
        CurrencyIdentity from,
        QuoteExecutionIntent intent) =>
        edges.SingleOrDefault(edge => edge.From.Equals(from) && edge.ExecutionIntent == intent);
}
