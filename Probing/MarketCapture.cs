using ExileCore;

namespace FaustusControllerLite.Probing;

public enum RawBookSide
{
    WantedItem,
    OfferedItem
}

public sealed record RawStockLevel(RawBookSide Side, int Get, int Give, int ListedCount);

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
        out string failure)
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

            var serverData = gameController.Game.IngameState.ServerData;
            if (string.IsNullOrWhiteSpace(serverData.League))
            {
                failure = "Current league is unavailable.";
                return false;
            }

            capture = new MarketCapture(
                Guid.NewGuid(),
                sessionId,
                DateTimeOffset.UtcNow,
                serverData.League,
                serverData.InstanceId,
                new CurrencyIdentity(offered.Metadata, offered.Hash, offered.BaseName),
                new CurrencyIdentity(wanted.Metadata, wanted.Hash, wanted.BaseName),
                panel.MarketRateGet,
                panel.MarketRateGive,
                panel.WantedItemStock.Select(level => new RawStockLevel(
                    RawBookSide.WantedItem,
                    level.Get,
                    level.Give,
                    level.ListedCount)).ToArray(),
                panel.OfferedItemStock.Select(level => new RawStockLevel(
                    RawBookSide.OfferedItem,
                    level.Get,
                    level.Give,
                    level.ListedCount)).ToArray());
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
    public static IReadOnlyList<DirectedExchangeEdge> CreateEdges(MarketCapture capture)
    {
        var edges = new List<DirectedExchangeEdge>(4);
        AddBook(edges, capture, capture.WantedItemStock, QuoteProvenance.Immediate, reverseRawRate: false);
        AddBook(edges, capture, capture.OfferedItemStock, QuoteProvenance.Competing, reverseRawRate: true);
        return edges;
    }

    private static void AddBook(
        ICollection<DirectedExchangeEdge> edges,
        MarketCapture capture,
        IReadOnlyList<RawStockLevel> levels,
        QuoteProvenance provenance,
        bool reverseRawRate)
    {
        var valid = levels
            .Where(level => level.Get > 0 && level.Give > 0 && level.ListedCount >= 0)
            .ToArray();
        if (valid.Length == 0)
        {
            return;
        }

        var selectedRate = Rate(valid[0], reverseRawRate);
        long inputDepth = 0;
        long queueAhead = 0;
        foreach (var level in valid)
        {
            var levelRate = Rate(level, reverseRawRate);
            if (levelRate < selectedRate)
            {
                continue;
            }

            if (provenance == QuoteProvenance.Immediate)
            {
                inputDepth = checked(inputDepth + checked(levelRate.Denominator * level.ListedCount));
            }
            else
            {
                queueAhead = checked(queueAhead + level.ListedCount);
            }
        }

        var pair = capture.Pair;
        var session = capture.SessionId.ToString("D");
        var area = capture.AreaInstanceId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        edges.Add(new DirectedExchangeEdge(
            capture.OfferedCurrency,
            capture.WantedCurrency,
            pair,
            selectedRate,
            provenance,
            inputDepth,
            queueAhead,
            capture.CapturedAtUtc,
            session,
            area));

        var reverseDepth = provenance == QuoteProvenance.Immediate
            ? checked(selectedRate.Numerator * (inputDepth / selectedRate.Denominator))
            : 0;
        edges.Add(new DirectedExchangeEdge(
            capture.WantedCurrency,
            capture.OfferedCurrency,
            pair,
            selectedRate.Reverse(),
            provenance,
            reverseDepth,
            queueAhead,
            capture.CapturedAtUtc,
            session,
            area));
    }

    private static Rational Rate(RawStockLevel level, bool reverse) =>
        reverse ? new Rational(level.Give, level.Get) : new Rational(level.Get, level.Give);
}
