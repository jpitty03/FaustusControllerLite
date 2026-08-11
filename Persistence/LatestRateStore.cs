using FaustusControllerLite.Probing;
using System.Text.Json;

namespace FaustusControllerLite.Persistence;

public sealed class LatestRateFile
{
    public const int CurrentSchemaVersion = 1;
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public DateTimeOffset WrittenAtUtc { get; set; }
    public List<MarketCaptureDto> Captures { get; set; } = [];
}

public sealed class MarketCaptureDto
{
    public Guid CaptureId { get; set; }
    public Guid SessionId { get; set; }
    public DateTimeOffset CapturedAtUtc { get; set; }
    public string League { get; set; } = string.Empty;
    public int AreaInstanceId { get; set; }
    public CurrencyDto Offered { get; set; } = new();
    public CurrencyDto Wanted { get; set; } = new();
    public int MarketRateGet { get; set; }
    public int MarketRateGive { get; set; }
    public List<StockLevelDto> WantedItemStock { get; set; } = [];
    public List<StockLevelDto> OfferedItemStock { get; set; } = [];
}

public sealed class CurrencyDto
{
    public string Metadata { get; set; } = string.Empty;
    public uint Hash { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class StockLevelDto
{
    public string Side { get; set; } = string.Empty;
    public int Get { get; set; }
    public int Give { get; set; }
    public int ListedCount { get; set; }
}

public sealed class LatestRateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly Dictionary<string, MarketCapture> _captures = new(StringComparer.Ordinal);

    public IReadOnlyCollection<MarketCapture> Captures => _captures.Values;

    public bool Store(MarketCapture capture)
    {
        Validate(capture);
        var key = Key(capture.League, capture.Pair);
        var replaced = _captures.ContainsKey(key);
        _captures[key] = capture;
        return replaced;
    }

    public void Load(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        var file = JsonSerializer.Deserialize<LatestRateFile>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException("Latest-rate file was empty.");
        if (file.SchemaVersion != LatestRateFile.CurrentSchemaVersion)
        {
            throw new InvalidDataException($"Unsupported latest-rate schema {file.SchemaVersion}.");
        }

        var loaded = new Dictionary<string, MarketCapture>(StringComparer.Ordinal);
        foreach (var dto in file.Captures)
        {
            var capture = FromDto(dto);
            Validate(capture);
            loaded[Key(capture.League, capture.Pair)] = capture;
        }

        _captures.Clear();
        foreach (var pair in loaded)
        {
            _captures.Add(pair.Key, pair.Value);
        }
    }

    public void Save(string path)
    {
        AtomicWrite(path, Serialize(_captures.Values));
    }

    public void StoreBatchAtomically(string path, IReadOnlyCollection<MarketCapture> captures)
    {
        ArgumentNullException.ThrowIfNull(captures);
        if (captures.Count is not 2 and not 3)
        {
            throw new ArgumentException(
                "An automated latest-rate batch must contain exactly two or three captures.",
                nameof(captures));
        }

        foreach (var capture in captures)
        {
            Validate(capture);
        }

        if (captures.Select(capture => capture.CaptureId).Distinct().Count() != captures.Count ||
            captures.Select(capture => capture.Pair).Distinct().Count() != captures.Count ||
            captures.Select(capture => capture.SessionId).Distinct().Count() != 1 ||
            captures.Select(capture => capture.League).Distinct(StringComparer.Ordinal).Count() != 1 ||
            captures.Select(capture => capture.AreaInstanceId).Distinct().Count() != 1)
        {
            throw new InvalidDataException("Atomic latest-rate batches require unique pairs in one session, league, and area.");
        }

        var replacement = new Dictionary<string, MarketCapture>(_captures, StringComparer.Ordinal);
        foreach (var capture in captures)
        {
            replacement[Key(capture.League, capture.Pair)] = capture;
        }

        AtomicWrite(path, Serialize(replacement.Values));
        _captures.Clear();
        foreach (var pair in replacement)
        {
            _captures.Add(pair.Key, pair.Value);
        }
    }

    private static string Serialize(IEnumerable<MarketCapture> captures)
    {
        var file = new LatestRateFile
        {
            WrittenAtUtc = DateTimeOffset.UtcNow,
            Captures = captures
                .OrderBy(capture => capture.League, StringComparer.Ordinal)
                .ThenBy(capture => capture.Pair.ToString(), StringComparer.Ordinal)
                .Select(ToDto)
                .ToList()
        };
        return JsonSerializer.Serialize(file, JsonOptions);
    }

    private static string Key(string league, CurrencyPairKey pair) => $"{league}\n{pair}";

    private static void Validate(MarketCapture capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        if (capture.CaptureId == Guid.Empty || capture.SessionId == Guid.Empty)
        {
            throw new InvalidDataException("Latest-rate capture IDs must be non-empty.");
        }

        if (string.IsNullOrWhiteSpace(capture.League) || capture.CapturedAtUtc == default)
        {
            throw new InvalidDataException("Latest-rate capture league and timestamp are required.");
        }

        if (capture.WantedItemStock.Any(level => level.Side != RawBookSide.WantedItem) ||
            capture.OfferedItemStock.Any(level => level.Side != RawBookSide.OfferedItem))
        {
            throw new InvalidDataException("Latest-rate stock rows do not match their stored book sides.");
        }

        _ = capture.Pair;
    }

    private static MarketCaptureDto ToDto(MarketCapture capture) => new()
    {
        CaptureId = capture.CaptureId,
        SessionId = capture.SessionId,
        CapturedAtUtc = capture.CapturedAtUtc,
        League = capture.League,
        AreaInstanceId = capture.AreaInstanceId,
        Offered = Currency(capture.OfferedCurrency),
        Wanted = Currency(capture.WantedCurrency),
        MarketRateGet = capture.MarketRateGet,
        MarketRateGive = capture.MarketRateGive,
        WantedItemStock = capture.WantedItemStock.Select(Stock).ToList(),
        OfferedItemStock = capture.OfferedItemStock.Select(Stock).ToList()
    };

    private static MarketCapture FromDto(MarketCaptureDto value) => new(
        value.CaptureId,
        value.SessionId,
        value.CapturedAtUtc,
        value.League,
        value.AreaInstanceId,
        Currency(value.Offered),
        Currency(value.Wanted),
        value.MarketRateGet,
        value.MarketRateGive,
        value.WantedItemStock.Select(Stock).ToArray(),
        value.OfferedItemStock.Select(Stock).ToArray());

    private static CurrencyDto Currency(CurrencyIdentity value) => new()
    {
        Metadata = value.Metadata,
        Hash = value.Hash,
        Name = value.Name
    };

    private static CurrencyIdentity Currency(CurrencyDto value) =>
        new(value.Metadata, value.Hash, value.Name);

    private static StockLevelDto Stock(RawStockLevel value) => new()
    {
        Side = value.Side.ToString(),
        Get = value.Get,
        Give = value.Give,
        ListedCount = value.ListedCount
    };

    private static RawStockLevel Stock(StockLevelDto value) => new(
        Enum.Parse<RawBookSide>(value.Side, ignoreCase: false),
        value.Get,
        value.Give,
        value.ListedCount);

    private static void AtomicWrite(string path, string content)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Latest-rate path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, content);
        File.Move(temporaryPath, path, overwrite: true);
    }
}
