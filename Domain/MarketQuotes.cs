namespace FaustusControllerLite;

public enum QuoteProvenance
{
    Immediate,
    Competing,
}

public sealed record QuoteLevel
{
    public QuoteLevel(Rational rate, long inputDepth, long listedCount)
    {
        if (inputDepth < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(inputDepth));
        }

        if (listedCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(listedCount));
        }

        Rate = rate;
        InputDepth = inputDepth;
        ListedCount = listedCount;
    }

    public Rational Rate { get; }

    public long InputDepth { get; }

    public long ListedCount { get; }
}

public sealed record QuoteSnapshot
{
    public QuoteSnapshot(
        CurrencyIdentity from,
        CurrencyIdentity to,
        Rational selectedRate,
        QuoteProvenance provenance,
        DateTimeOffset capturedAt,
        string sessionId,
        string areaId,
        IEnumerable<QuoteLevel> levels)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        ArgumentNullException.ThrowIfNull(levels);
        if (from.Equals(to))
        {
            throw new ArgumentException("A quote must connect distinct currencies.", nameof(to));
        }

        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("Session is required.", nameof(sessionId));
        }

        if (string.IsNullOrWhiteSpace(areaId))
        {
            throw new ArgumentException("Area is required.", nameof(areaId));
        }

        From = from;
        To = to;
        Pair = new CurrencyPairKey(from, to);
        SelectedRate = selectedRate;
        Provenance = provenance;
        CapturedAt = capturedAt;
        SessionId = sessionId;
        AreaId = areaId;
        Levels = levels.ToArray();
    }

    public CurrencyIdentity From { get; }

    public CurrencyIdentity To { get; }

    public CurrencyPairKey Pair { get; }

    public Rational SelectedRate { get; }

    public QuoteProvenance Provenance { get; }

    public DateTimeOffset CapturedAt { get; }

    public string SessionId { get; }

    public string AreaId { get; }

    public IReadOnlyList<QuoteLevel> Levels { get; }
}

public sealed record DirectedExchangeEdge(
    CurrencyIdentity From,
    CurrencyIdentity To,
    CurrencyPairKey Pair,
    Rational Rate,
    QuoteProvenance Provenance,
    long ImmediateInputDepth,
    long CompetingQueueAhead,
    DateTimeOffset CapturedAt,
    string SessionId,
    string AreaId)
{
    public long InputLimit => Provenance == QuoteProvenance.Immediate ? ImmediateInputDepth : long.MaxValue;
}

public static class QuoteNormalizer
{
    public static DirectedExchangeEdge Normalize(QuoteSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        long depth = 0;
        long queue = 0;
        foreach (var level in snapshot.Levels)
        {
            if (level.Rate < snapshot.SelectedRate)
            {
                continue;
            }

            if (snapshot.Provenance == QuoteProvenance.Immediate)
            {
                depth = checked(depth + level.InputDepth);
            }
            else
            {
                queue = checked(queue + level.ListedCount);
            }
        }

        return new DirectedExchangeEdge(
            snapshot.From,
            snapshot.To,
            snapshot.Pair,
            snapshot.SelectedRate,
            snapshot.Provenance,
            depth,
            queue,
            snapshot.CapturedAt,
            snapshot.SessionId,
            snapshot.AreaId);
    }
}
