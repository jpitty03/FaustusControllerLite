using ExileCore;
using System.Numerics;

namespace FaustusControllerLite.Probing;

public enum RawBookSide
{
    WantedItem,
    OfferedItem
}

public sealed record RawStockLevel(RawBookSide Side, int Get, int Give, int ListedCount);

public sealed record NormalizedStockLevel(
    RawBookSide SourceSide,
    CurrencyIdentity From,
    CurrencyIdentity To,
    Rational Rate,
    int ListedCount);

public sealed record MarketCapture(
    Guid CaptureId,
    Guid SessionId,
    DateTimeOffset CapturedAtUtc,
    string League,
    int AreaInstanceId,
    CurrencyIdentity OfferedCurrency,
    CurrencyIdentity WantedCurrency,
    int MarketRateGet,
    int MarketRateGive,
    IReadOnlyList<RawStockLevel> WantedItemStock,
    IReadOnlyList<RawStockLevel> OfferedItemStock)
{
    public CurrencyPairKey Pair => new(OfferedCurrency, WantedCurrency);
}

public static class CurrentMarketReader
{
    public static bool TryCapture(
        GameController gameController,
        Guid sessionId,
        out MarketCapture? capture,
        out string failure,
        bool requireSelectedMarketHead = true)
    {
        capture = null;
        try
        {
            var panel = gameController.Game.IngameState.IngameUi.CurrencyExchangePanel;
            if (!panel.IsVisible)
            {
                failure = "Currency Exchange panel is not visible.";
                return false;
            }

            var offered = panel.OfferedItemType;
            var wanted = panel.WantedItemType;
            if (offered is null || wanted is null ||
                string.IsNullOrWhiteSpace(offered.Metadata) ||
                string.IsNullOrWhiteSpace(wanted.Metadata))
            {
                failure = "Select both offered and wanted currencies before capturing.";
                return false;
            }

            if (string.Equals(offered.Metadata, wanted.Metadata, StringComparison.Ordinal))
            {
                failure = "Offered and wanted currencies must be different.";
                return false;
            }

            var serverData = gameController.Game.IngameState.ServerData;
            if (string.IsNullOrWhiteSpace(serverData.League))
            {
                failure = "Current league is unavailable.";
                return false;
            }

            var league = serverData.League;
            var areaInstanceId = serverData.InstanceId;
            var offeredMetadata = offered.Metadata;
            var wantedMetadata = wanted.Metadata;
            var marketRateGet = panel.MarketRateGet;
            var marketRateGive = panel.MarketRateGive;
            var wantedStock = panel.WantedItemStock.Select(level => new RawStockLevel(
                RawBookSide.WantedItem,
                level.Get,
                level.Give,
                level.ListedCount)).ToArray();
            var offeredStock = panel.OfferedItemStock.Select(level => new RawStockLevel(
                RawBookSide.OfferedItem,
                level.Get,
                level.Give,
                level.ListedCount)).ToArray();

            var finalOffered = panel.OfferedItemType;
            var finalWanted = panel.WantedItemType;
            var finalServerData = gameController.Game.IngameState.ServerData;
            if (!panel.IsVisible ||
                finalOffered is null || finalWanted is null ||
                !string.Equals(finalOffered.Metadata, offeredMetadata, StringComparison.Ordinal) ||
                !string.Equals(finalWanted.Metadata, wantedMetadata, StringComparison.Ordinal) ||
                !string.Equals(finalServerData.League, league, StringComparison.Ordinal) ||
                finalServerData.InstanceId != areaInstanceId ||
                panel.MarketRateGet != marketRateGet || panel.MarketRateGive != marketRateGive)
            {
                failure = "Currency Exchange pair, rate, league, or area changed during capture.";
                return false;
            }

            var topImmediate = wantedStock.FirstOrDefault(level => level.Get > 0 && level.Give > 0);
            if (requireSelectedMarketHead && marketRateGet > 0 && marketRateGive > 0 && topImmediate is not null &&
                new Rational(marketRateGet, marketRateGive) != new Rational(topImmediate.Get, topImmediate.Give))
            {
                failure = "Selected market rate did not match the immediate book head.";
                return false;
            }

            capture = new MarketCapture(
                Guid.NewGuid(),
                sessionId,
                DateTimeOffset.UtcNow,
                league,
                areaInstanceId,
                new CurrencyIdentity(offered.Metadata, offered.Hash, offered.BaseName),
                new CurrencyIdentity(wanted.Metadata, wanted.Hash, wanted.BaseName),
                marketRateGet,
                marketRateGive,
                wantedStock,
                offeredStock);
            failure = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            failure = $"SDK market read failed: {exception.GetType().Name}: {exception.Message}";
            return false;
        }
    }
}

public static class MarketCaptureNormalizer
{
    public static Rational? MarketRate(MarketCapture capture) =>
        capture.MarketRateGet > 0 && capture.MarketRateGive > 0
            ? new Rational(capture.MarketRateGet, capture.MarketRateGive)
            : null;

    public static IReadOnlyList<NormalizedStockLevel> CreateLevels(MarketCapture capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        var levels = new List<NormalizedStockLevel>(
            checked((capture.WantedItemStock.Count + capture.OfferedItemStock.Count) * 2));
        AddNormalizedLevels(levels, capture, capture.WantedItemStock, reverseRawRate: false);
        AddNormalizedLevels(levels, capture, capture.OfferedItemStock, reverseRawRate: true);
        return levels;
    }

    public static IReadOnlyList<DirectedExchangeEdge> CreateEdges(MarketCapture capture)
    {
        var edges = new List<DirectedExchangeEdge>(4);
        AddBookDirections(
            edges,
            capture,
            capture.WantedItemStock,
            QuoteBookSource.WantedItemStock,
            selectedIntent: QuoteExecutionIntent.Immediate,
            reverseIntent: QuoteExecutionIntent.Competing,
            reverseRawRate: false);
        AddBookDirections(
            edges,
            capture,
            capture.OfferedItemStock,
            QuoteBookSource.OfferedItemStock,
            selectedIntent: QuoteExecutionIntent.Competing,
            reverseIntent: QuoteExecutionIntent.Immediate,
            reverseRawRate: true);
        return edges;
    }

    private static void AddNormalizedLevels(
        ICollection<NormalizedStockLevel> destination,
        MarketCapture capture,
        IEnumerable<RawStockLevel> rawLevels,
        bool reverseRawRate)
    {
        foreach (var level in rawLevels)
        {
            if (level.Get <= 0 || level.Give <= 0 || level.ListedCount < 0)
            {
                continue;
            }

            var selectedRate = Rate(level, reverseRawRate);
            destination.Add(new NormalizedStockLevel(
                level.Side,
                capture.OfferedCurrency,
                capture.WantedCurrency,
                selectedRate,
                level.ListedCount));
            destination.Add(new NormalizedStockLevel(
                level.Side,
                capture.WantedCurrency,
                capture.OfferedCurrency,
                selectedRate.Reverse(),
                level.ListedCount));
        }
    }

    private static void AddBookDirections(
        ICollection<DirectedExchangeEdge> edges,
        MarketCapture capture,
        IReadOnlyList<RawStockLevel> levels,
        QuoteBookSource sourceBook,
        QuoteExecutionIntent selectedIntent,
        QuoteExecutionIntent reverseIntent,
        bool reverseRawRate)
    {
        var valid = levels
            .Where(level => level.Get > 0 && level.Give > 0 && level.ListedCount >= 0)
            .ToArray();
        if (valid.Length == 0)
        {
            return;
        }

        var pair = capture.Pair;
        var session = capture.SessionId.ToString("D");
        var area = capture.AreaInstanceId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        AddDirection(
            edges,
            valid,
            capture.OfferedCurrency,
            capture.WantedCurrency,
            pair,
            selectedIntent,
            sourceBook,
            capture,
            session,
            area,
            reverseRawRate);
        AddDirection(
            edges,
            valid,
            capture.WantedCurrency,
            capture.OfferedCurrency,
            pair,
            reverseIntent,
            sourceBook,
            capture,
            session,
            area,
            !reverseRawRate);
    }

    private static void AddDirection(
        ICollection<DirectedExchangeEdge> edges,
        IReadOnlyList<RawStockLevel> levels,
        CurrencyIdentity from,
        CurrencyIdentity to,
        CurrencyPairKey pair,
        QuoteExecutionIntent intent,
        QuoteBookSource sourceBook,
        MarketCapture capture,
        string session,
        string area,
        bool reverseRawRate)
    {
        var selectedRate = Rate(levels[0], reverseRawRate);
        var immediateDepth = intent == QuoteExecutionIntent.Immediate
            ? ImmediateInputDepth(levels, selectedRate, reverseRawRate)
            : 0;
        var queueAhead = intent == QuoteExecutionIntent.Competing
            ? CompetingQueueAhead(levels, selectedRate, reverseRawRate)
            : 0;
        edges.Add(new DirectedExchangeEdge(
            from,
            to,
            pair,
            selectedRate,
            intent,
            immediateDepth,
            queueAhead,
            capture.CapturedAtUtc,
            session,
            area,
            sourceBook,
            capture.CaptureId));
    }

    private static long ImmediateInputDepth(
        IEnumerable<RawStockLevel> levels,
        Rational selectedRate,
        bool reverseRawRate)
    {
        BigInteger selectedRateOutput = 0;
        BigInteger inputDepth = 0;
        foreach (var level in levels)
        {
            var levelRate = Rate(level, reverseRawRate);
            if (levelRate < selectedRate)
            {
                continue;
            }

            if (levelRate == selectedRate)
            {
                selectedRateOutput += level.ListedCount;
                continue;
            }

            var levelLots = level.ListedCount / levelRate.Numerator;
            inputDepth += (BigInteger)levelLots * levelRate.Denominator;
        }

        var selectedLots = selectedRateOutput / selectedRate.Numerator;
        inputDepth += selectedLots * selectedRate.Denominator;
        return ToLong(inputDepth, "Immediate input depth exceeds Int64.");
    }

    private static long CompetingQueueAhead(
        IEnumerable<RawStockLevel> levels,
        Rational selectedRate,
        bool reverseRawRate)
    {
        BigInteger queue = 0;
        foreach (var level in levels)
        {
            if (Rate(level, reverseRawRate) == selectedRate)
            {
                queue += level.ListedCount;
            }
        }

        return ToLong(queue, "Competing queue exceeds Int64.");
    }

    private static long ToLong(BigInteger value, string message)
    {
        if (value < 0 || value > long.MaxValue)
        {
            throw new OverflowException(message);
        }

        return (long)value;
    }

    private static Rational Rate(RawStockLevel level, bool reverse) =>
        reverse ? new Rational(level.Give, level.Get) : new Rational(level.Get, level.Give);
}

public sealed record CoherentQuoteMatrix(
    IReadOnlyList<MarketCapture> Captures,
    IReadOnlyList<DirectedExchangeEdge> Edges);

public static class QuoteMatrixBuilder
{
    public static bool TryBuild(
        IEnumerable<MarketCapture> availableCaptures,
        string league,
        Guid sessionId,
        int areaInstanceId,
        CurrencyIdentity chaos,
        CurrencyIdentity divine,
        CurrencyIdentity target,
        DateTimeOffset now,
        TimeSpan maximumAge,
        out CoherentQuoteMatrix? matrix,
        out string failure)
    {
        ArgumentNullException.ThrowIfNull(availableCaptures);
        ArgumentNullException.ThrowIfNull(chaos);
        ArgumentNullException.ThrowIfNull(divine);
        ArgumentNullException.ThrowIfNull(target);
        if (string.IsNullOrWhiteSpace(league) || sessionId == Guid.Empty || maximumAge < TimeSpan.Zero)
        {
            throw new ArgumentException("League, session, and non-negative quote age are required.");
        }

        var requiredPairs = new HashSet<CurrencyPairKey>
        {
            new(chaos, divine),
            new(chaos, target),
            new(divine, target)
        };
        var captures = availableCaptures
            .Where(capture => string.Equals(capture.League, league, StringComparison.Ordinal) &&
                requiredPairs.Contains(capture.Pair))
            .ToArray();
        if (captures.Length != 3 || captures.Select(capture => capture.Pair).Distinct().Count() != 3)
        {
            matrix = null;
            failure = "Exactly one latest capture is required for Divine/Chaos, target/Chaos, and target/Divine.";
            return false;
        }

        var edges = new List<DirectedExchangeEdge>(12);
        foreach (var capture in captures)
        {
            if (capture.SessionId != sessionId)
            {
                matrix = null;
                failure = $"{capture.Pair} belongs to a different probe session.";
                return false;
            }

            if (capture.AreaInstanceId != areaInstanceId)
            {
                matrix = null;
                failure = $"{capture.Pair} belongs to a different area instance.";
                return false;
            }

            var age = now - capture.CapturedAtUtc;
            if (age < TimeSpan.Zero || age > maximumAge)
            {
                matrix = null;
                failure = $"{capture.Pair} is stale or future-dated.";
                return false;
            }

            var marketRate = MarketCaptureNormalizer.MarketRate(capture);
            var topImmediate = capture.WantedItemStock.FirstOrDefault(level => level.Get > 0 && level.Give > 0);
            if (marketRate is null || topImmediate is null ||
                marketRate.Value != new Rational(topImmediate.Get, topImmediate.Give))
            {
                matrix = null;
                failure = $"{capture.Pair} has no coherent positive selected market rate.";
                return false;
            }

            IReadOnlyList<DirectedExchangeEdge> captureEdges;
            try
            {
                captureEdges = MarketCaptureNormalizer.CreateEdges(capture);
            }
            catch (OverflowException exception)
            {
                matrix = null;
                failure = $"{capture.Pair} normalization overflowed: {exception.Message}";
                return false;
            }

            if (captureEdges.Count != 4)
            {
                matrix = null;
                failure = $"{capture.Pair} does not have both readable book sides.";
                return false;
            }

            edges.AddRange(captureEdges);
        }

        matrix = new CoherentQuoteMatrix(captures, edges);
        failure = string.Empty;
        return true;
    }
}
