namespace FaustusControllerLite;

public enum QuoteExecutionIntent
{
    Immediate,
    Competing,
}

public enum QuoteBookSource
{
    Synthetic,
    WantedItemStock,
    OfferedItemStock,

    /// <summary>
    /// A competing rate the planner invented one minimum unit better than the live competing head.
    /// It is deliberately distinct from <see cref="Synthetic"/>, which is only the neutral default
    /// for an edge that never came from a captured book, so a leg carrying this value always means
    /// "improved" and never merely "unattributed".
    /// </summary>
    ImprovedCompeting,
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
        QuoteExecutionIntent executionIntent,
        DateTimeOffset capturedAt,
        string sessionId,
        string areaId,
        IEnumerable<QuoteLevel> levels,
        QuoteBookSource sourceBook = QuoteBookSource.Synthetic,
        Guid captureId = default)
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
        ExecutionIntent = executionIntent;
        CapturedAt = capturedAt;
        SessionId = sessionId;
        AreaId = areaId;
        Levels = levels.ToArray();
        SourceBook = sourceBook;
        CaptureId = captureId;
    }

    public CurrencyIdentity From { get; }

    public CurrencyIdentity To { get; }

    public CurrencyPairKey Pair { get; }

    public Rational SelectedRate { get; }

    public QuoteExecutionIntent ExecutionIntent { get; }

    public DateTimeOffset CapturedAt { get; }

    public string SessionId { get; }

    public string AreaId { get; }

    public IReadOnlyList<QuoteLevel> Levels { get; }

    public QuoteBookSource SourceBook { get; }

    public Guid CaptureId { get; }
}

public sealed record DirectedExchangeEdge(
    CurrencyIdentity From,
    CurrencyIdentity To,
    CurrencyPairKey Pair,
    Rational Rate,
    QuoteExecutionIntent ExecutionIntent,
    long ImmediateInputDepth,
    long CompetingQueueAhead,
    DateTimeOffset CapturedAt,
    string SessionId,
    string AreaId,
    QuoteBookSource SourceBook = QuoteBookSource.Synthetic,
    Guid CaptureId = default)
{
    public long InputLimit => ExecutionIntent == QuoteExecutionIntent.Immediate ? ImmediateInputDepth : long.MaxValue;
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
            if (snapshot.ExecutionIntent == QuoteExecutionIntent.Immediate)
            {
                if (level.Rate < snapshot.SelectedRate)
                {
                    continue;
                }

                depth = checked(depth + level.InputDepth);
            }
            else
            {
                if (level.Rate != snapshot.SelectedRate)
                {
                    continue;
                }

                queue = checked(queue + level.ListedCount);
            }
        }

        return new DirectedExchangeEdge(
            snapshot.From,
            snapshot.To,
            snapshot.Pair,
            snapshot.SelectedRate,
            snapshot.ExecutionIntent,
            depth,
            queue,
            snapshot.CapturedAt,
            snapshot.SessionId,
            snapshot.AreaId,
            snapshot.SourceBook,
            snapshot.CaptureId);
    }
}
