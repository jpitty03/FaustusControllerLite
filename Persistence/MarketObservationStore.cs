using FaustusControllerLite.Domain;
using System.Text.Json;

namespace FaustusControllerLite.Persistence;

public sealed class MarketObservationLine
{
    public int SchemaVersion { get; set; } = MarketObservationStore.CurrentSchemaVersion;
    public DateTimeOffset ObservedAtUtc { get; set; }
    public string League { get; set; } = string.Empty;
    public int AreaInstanceId { get; set; }
    public string OfferedMetadata { get; set; } = string.Empty;
    public string OfferedName { get; set; } = string.Empty;
    public string WantedMetadata { get; set; } = string.Empty;
    public string WantedName { get; set; } = string.Empty;
    public long ImmediateRateNumerator { get; set; }
    public long ImmediateRateDenominator { get; set; }
    public long ImmediateInputDepth { get; set; }
    public long CompetingRateNumerator { get; set; }
    public long CompetingRateDenominator { get; set; }
    public long CompetingQueueAhead { get; set; }
    public long ReverseImmediateRateNumerator { get; set; }
    public long ReverseImmediateRateDenominator { get; set; }
    public long ReverseImmediateInputDepth { get; set; }
    public long ReverseCompetingRateNumerator { get; set; }
    public long ReverseCompetingRateDenominator { get; set; }
    public long ReverseCompetingQueueAhead { get; set; }
}

/// <summary>
/// Append-only per-league time series of sweep observations. This is the history the canonical latest-rate
/// cache deliberately does not keep: that file holds exactly one capture per pair and overwrites it every
/// time, so no book movement is visible from it. The sweep therefore keeps its own store and never writes to
/// the canonical one, which also means a sweep capture can never age out or displace the quote an armed
/// arbitrage route is holding.
/// </summary>
public sealed class MarketObservationStore
{
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly string _directory;
    private readonly Dictionary<CurrencyPairKey, List<MarketObservation>> _byPair = new();
    private string _league = string.Empty;

    public MarketObservationStore(string directory)
    {
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));
    }

    public string League => _league;

    public int Count => _byPair.Values.Sum(observations => observations.Count);

    public IReadOnlyCollection<CurrencyPairKey> Pairs => _byPair.Keys;

    public string PathFor(string league) =>
        Path.Combine(_directory, $"market-observations-{league}.jsonl");

    /// <summary>
    /// Observations for one pair, oldest first. An unswept pair returns an empty list rather than throwing,
    /// because "never observed" is an ordinary board state.
    /// </summary>
    public IReadOnlyList<MarketObservation> For(CurrencyPairKey pair) =>
        _byPair.TryGetValue(pair, out var observations) ? observations : [];

    /// <summary>
    /// Every series by pair, oldest first. The board and the idle stale-pair selection both need to read
    /// across all pairs at once. The dictionary itself is a copy, so a pair appended later does not appear
    /// in a view a caller is still ranking from; the series inside it are the live lists.
    /// </summary>
    public IReadOnlyDictionary<CurrencyPairKey, IReadOnlyList<MarketObservation>> Snapshot() =>
        _byPair.ToDictionary(entry => entry.Key, entry => (IReadOnlyList<MarketObservation>)entry.Value);

    /// <summary>
    /// Loads one league transactionally: every line is parsed and validated into fresh state before anything
    /// is swapped in, so a corrupt file throws with the previous in-memory series and the file itself
    /// untouched. Records older than <paramref name="retention"/> are dropped, and the file is rewritten
    /// atomically only when something was actually dropped.
    /// </summary>
    public void Load(string league, TimeSpan retention, DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(league))
        {
            throw new ArgumentException("League is required.", nameof(league));
        }

        if (retention <= TimeSpan.Zero)
        {
            throw new ArgumentException("Observation retention must be positive.", nameof(retention));
        }

        var path = PathFor(league);
        var loaded = new Dictionary<CurrencyPairKey, List<MarketObservation>>();
        var pruned = 0;
        if (File.Exists(path))
        {
            var horizon = nowUtc - retention;
            var lineNumber = 0;
            foreach (var line in File.ReadAllLines(path))
            {
                lineNumber++;
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var observation = Parse(line, league, lineNumber);
                if (observation.ObservedAtUtc < horizon)
                {
                    pruned++;
                    continue;
                }

                Add(loaded, observation);
            }
        }

        foreach (var observations in loaded.Values)
        {
            observations.Sort(static (left, right) => left.ObservedAtUtc.CompareTo(right.ObservedAtUtc));
        }

        _byPair.Clear();
        foreach (var entry in loaded)
        {
            _byPair.Add(entry.Key, entry.Value);
        }

        _league = league;
        if (pruned > 0)
        {
            Rewrite(path);
        }
    }

    /// <summary>
    /// Appends one observation to the loaded league. The line is written before in-memory state moves, so a
    /// failed write never leaves the board showing a sample that is not on disk.
    /// </summary>
    public void Append(MarketObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        observation.Validate();
        if (_league.Length == 0)
        {
            throw new InvalidOperationException("Load an observation league before appending.");
        }

        if (!string.Equals(observation.League, _league, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Observation league '{observation.League}' does not match the loaded league '{_league}'.");
        }

        Directory.CreateDirectory(_directory);
        File.AppendAllText(PathFor(_league), Serialize(observation) + Environment.NewLine);
        Add(_byPair, observation);
        _byPair[observation.Pair].Sort(static (left, right) => left.ObservedAtUtc.CompareTo(right.ObservedAtUtc));
    }

    private void Rewrite(string path)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Observation path has no parent directory.");
        Directory.CreateDirectory(directory);
        var ordered = _byPair.Values
            .SelectMany(observations => observations)
            .OrderBy(observation => observation.ObservedAtUtc)
            .ThenBy(observation => observation.Pair.ToString(), StringComparer.Ordinal)
            .Select(Serialize);
        var temporary = path + ".tmp";
        File.WriteAllLines(temporary, ordered);
        File.Move(temporary, path, overwrite: true);
    }

    private static void Add(
        Dictionary<CurrencyPairKey, List<MarketObservation>> destination,
        MarketObservation observation)
    {
        if (!destination.TryGetValue(observation.Pair, out var observations))
        {
            observations = [];
            destination.Add(observation.Pair, observations);
        }

        observations.Add(observation);
    }

    private static MarketObservation Parse(string line, string league, int lineNumber)
    {
        MarketObservationLine? value;
        try
        {
            value = JsonSerializer.Deserialize<MarketObservationLine>(line, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Observation line {lineNumber} is not valid JSON: {exception.Message}");
        }

        if (value is null)
        {
            throw new InvalidDataException($"Observation line {lineNumber} was empty.");
        }

        if (value.SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Observation line {lineNumber} has unsupported schema {value.SchemaVersion}.");
        }

        if (!string.Equals(value.League, league, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Observation line {lineNumber} belongs to league '{value.League}', not '{league}'.");
        }

        if (string.IsNullOrWhiteSpace(value.OfferedMetadata) || string.IsNullOrWhiteSpace(value.WantedMetadata))
        {
            throw new InvalidDataException($"Observation line {lineNumber} is missing a currency.");
        }

        MarketObservation observation;
        try
        {
            observation = new MarketObservation(
                value.ObservedAtUtc,
                value.League,
                value.AreaInstanceId,
                new CurrencyIdentity(value.OfferedMetadata, 0, value.OfferedName),
                new CurrencyIdentity(value.WantedMetadata, 0, value.WantedName),
                new DirectedBookObservation(
                    value.ImmediateRateNumerator,
                    value.ImmediateRateDenominator,
                    value.ImmediateInputDepth,
                    value.CompetingRateNumerator,
                    value.CompetingRateDenominator,
                    value.CompetingQueueAhead),
                new DirectedBookObservation(
                    value.ReverseImmediateRateNumerator,
                    value.ReverseImmediateRateDenominator,
                    value.ReverseImmediateInputDepth,
                    value.ReverseCompetingRateNumerator,
                    value.ReverseCompetingRateDenominator,
                    value.ReverseCompetingQueueAhead));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException)
        {
            throw new InvalidDataException($"Observation line {lineNumber} is malformed: {exception.Message}");
        }

        try
        {
            observation.Validate();
        }
        catch (InvalidDataException exception)
        {
            throw new InvalidDataException($"Observation line {lineNumber} is malformed: {exception.Message}");
        }

        return observation;
    }

    private static string Serialize(MarketObservation observation) => JsonSerializer.Serialize(
        new MarketObservationLine
        {
            ObservedAtUtc = observation.ObservedAtUtc,
            League = observation.League,
            AreaInstanceId = observation.AreaInstanceId,
            OfferedMetadata = observation.Offered.Metadata,
            OfferedName = observation.Offered.Name,
            WantedMetadata = observation.Wanted.Metadata,
            WantedName = observation.Wanted.Name,
            ImmediateRateNumerator = observation.Forward.ImmediateRateNumerator,
            ImmediateRateDenominator = observation.Forward.ImmediateRateDenominator,
            ImmediateInputDepth = observation.Forward.ImmediateInputDepth,
            CompetingRateNumerator = observation.Forward.CompetingRateNumerator,
            CompetingRateDenominator = observation.Forward.CompetingRateDenominator,
            CompetingQueueAhead = observation.Forward.CompetingQueueAhead,
            ReverseImmediateRateNumerator = observation.Reverse.ImmediateRateNumerator,
            ReverseImmediateRateDenominator = observation.Reverse.ImmediateRateDenominator,
            ReverseImmediateInputDepth = observation.Reverse.ImmediateInputDepth,
            ReverseCompetingRateNumerator = observation.Reverse.CompetingRateNumerator,
            ReverseCompetingRateDenominator = observation.Reverse.CompetingRateDenominator,
            ReverseCompetingQueueAhead = observation.Reverse.CompetingQueueAhead
        },
        JsonOptions);
}
