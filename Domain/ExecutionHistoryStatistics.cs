using System.Globalization;
using System.Text.Json;

namespace FaustusControllerLite.Domain;

public sealed record PairExecutionStatistics(
    CurrencyPairKey Pair,
    int Fills,
    int NoFills,
    IReadOnlyList<double> FillMinutes)
{
    public int Orders => Fills + NoFills;

    /// <summary>
    /// Laplace-smoothed fill rate, so an untraded pair reads 0.5 rather than 0 and a single lucky fill does
    /// not read as certainty. This is a confidence multiplier on the inferred signal, never a signal on its own.
    /// </summary>
    public double FillConfidence => (Fills + 1.0) / (Orders + 2.0);

    public double? MedianFillMinutes
    {
        get
        {
            if (FillMinutes.Count == 0)
            {
                return null;
            }

            var sorted = FillMinutes.OrderBy(minutes => minutes).ToArray();
            var middle = sorted.Length / 2;
            return sorted.Length % 2 == 1
                ? sorted[middle]
                : (sorted[middle - 1] + sorted[middle]) / 2.0;
        }
    }

    public string Describe() => Orders == 0 ? "-" : $"{Fills}:{NoFills}";
}

/// <summary>
/// Reads back the execution audit log to learn how often orders on a pair actually filled and how long they
/// took. Nothing else in the plugin reads this file: it is written as forensic evidence, by three different
/// serializers, with dozens of event types and many lines that carry no currency at all.
///
/// Two consequences shape this reader. First, an unrecognized or unparsable line is skipped and counted, not
/// thrown on — the audit log is not this feature's schema contract, and refusing to show the board because
/// some unrelated bankroll line changed shape would be the wrong failure. Second, outcomes are resolved per
/// order rather than per line: one order commonly emits both a timeout and a cancellation, and a timed-out
/// order can still fill afterwards, so counting raw events would double-count no-fills and undercount fills.
/// </summary>
public sealed class ExecutionHistoryStatistics
{
    public const string FillEventLifecycle = "TrackedOrderLifecycleCompletedUncollected";
    public const string FillEventArmedReconciliation = "TrackedOrderArmedReconciliationCompletedUncollected";
    public const string FillEventManualAdoption = "ManualLifecycleReconciliationAdoptedCompletedUncollected";
    public const string NoFillEventTimedOut = "TrackedOrderLifecycleTimedOut";
    public const string NoFillEventCanceled = "TrackedOrderLifecycleCanceledUncollected";
    public const string NoFillEventCancellationConfirmed = "TrackedOrderCancellationCanceledUncollected";

    private static readonly HashSet<string> FillEvents = new(StringComparer.Ordinal)
    {
        FillEventLifecycle,
        FillEventArmedReconciliation,
        FillEventManualAdoption,
    };

    private static readonly HashSet<string> NoFillEvents = new(StringComparer.Ordinal)
    {
        NoFillEventTimedOut,
        NoFillEventCanceled,
        NoFillEventCancellationConfirmed,
    };

    private readonly Dictionary<CurrencyPairKey, PairExecutionStatistics> _byPair;

    private ExecutionHistoryStatistics(
        Dictionary<CurrencyPairKey, PairExecutionStatistics> byPair,
        int orderCount,
        int skippedLines)
    {
        _byPair = byPair;
        OrderCount = orderCount;
        SkippedLines = skippedLines;
    }

    public static ExecutionHistoryStatistics Empty { get; } =
        new(new Dictionary<CurrencyPairKey, PairExecutionStatistics>(), 0, 0);

    public int OrderCount { get; }

    /// <summary>Lines that carried no usable order outcome. Expected to be large: most audit lines are not
    /// lifecycle outcomes at all.</summary>
    public int SkippedLines { get; }

    public IReadOnlyCollection<CurrencyPairKey> Pairs => _byPair.Keys;

    public static string PathFor(string directory, string league) =>
        Path.Combine(directory, $"execution-audit-{league}.jsonl");

    /// <summary>
    /// Reads the audit log for one league. A missing file is an empty history, not a failure — the board must
    /// work on a fresh install.
    /// </summary>
    public static ExecutionHistoryStatistics Load(string path)
    {
        if (!File.Exists(path))
        {
            return Empty;
        }

        return Parse(File.ReadLines(path));
    }

    public static ExecutionHistoryStatistics Parse(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var outcomes = new Dictionary<string, (CurrencyPairKey Pair, bool Filled, double? Minutes)>(StringComparer.Ordinal);
        var skipped = 0;
        foreach (var line in lines)
        {
            if (!TryReadOutcome(line, out var key, out var pair, out var filled, out var minutes))
            {
                skipped++;
                continue;
            }

            // A timed-out order that later fills is a fill: the terminal outcome wins over the interim one.
            if (outcomes.TryGetValue(key, out var existing) && (existing.Filled || !filled))
            {
                continue;
            }

            outcomes[key] = (pair, filled, minutes);
        }

        var byPair = new Dictionary<CurrencyPairKey, (int Fills, int NoFills, List<double> Minutes)>();
        foreach (var outcome in outcomes.Values)
        {
            if (!byPair.TryGetValue(outcome.Pair, out var totals))
            {
                totals = (0, 0, []);
            }

            if (outcome.Filled)
            {
                totals.Fills++;
                if (outcome.Minutes is { } minutes)
                {
                    totals.Minutes.Add(minutes);
                }
            }
            else
            {
                totals.NoFills++;
            }

            byPair[outcome.Pair] = totals;
        }

        var statistics = byPair.ToDictionary(
            entry => entry.Key,
            entry => new PairExecutionStatistics(
                entry.Key,
                entry.Value.Fills,
                entry.Value.NoFills,
                entry.Value.Minutes));
        return new ExecutionHistoryStatistics(statistics, outcomes.Count, skipped);
    }

    /// <summary>A pair with no history returns a zero record, whose <see cref="PairExecutionStatistics.FillConfidence"/>
    /// is the neutral 0.5 prior.</summary>
    public PairExecutionStatistics For(CurrencyPairKey pair) =>
        _byPair.TryGetValue(pair, out var statistics)
            ? statistics
            : new PairExecutionStatistics(pair, 0, 0, []);

    private static bool TryReadOutcome(
        string line,
        out string key,
        out CurrencyPairKey pair,
        out bool filled,
        out double? minutes)
    {
        key = string.Empty;
        pair = default;
        filled = false;
        minutes = null;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var eventType = Text(root, "EventType");
            if (eventType.Length == 0)
            {
                return false;
            }

            var isFill = FillEvents.Contains(eventType);
            if (!isFill && !NoFillEvents.Contains(eventType))
            {
                return false;
            }

            var offered = Text(root, "OfferedMetadata");
            var wanted = Text(root, "WantedMetadata");
            if (offered.Length == 0 || wanted.Length == 0 ||
                string.Equals(offered, wanted, StringComparison.Ordinal))
            {
                return false;
            }

            if (!TryTimestamp(root, "ClickedAtUtc", out var clickedAt))
            {
                return false;
            }

            pair = new CurrencyPairKey(new CurrencyIdentity(offered), new CurrencyIdentity(wanted));
            filled = isFill;
            key = string.Create(
                CultureInfo.InvariantCulture,
                $"{Text(root, "PlayerOrderId")}|{clickedAt.UtcTicks}|{offered}|{wanted}");
            if (isFill && TryTimestamp(root, "UpdatedAtUtc", out var updatedAt) && updatedAt > clickedAt)
            {
                minutes = (updatedAt - clickedAt).TotalMinutes;
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string Text(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value))
        {
            return string.Empty;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            _ => string.Empty
        };
    }

    /// <summary>
    /// These timestamps are .NET round-trip ISO-8601 with a variable number of fractional digits
    /// (<c>.5740168+00:00</c> and <c>.46799+00:00</c> both occur in the live file), so they must be parsed by
    /// the framework rather than by any fixed-width format.
    /// </summary>
    private static bool TryTimestamp(JsonElement root, string property, out DateTimeOffset value)
    {
        value = default;
        var text = Text(root, property);
        return text.Length > 0 && DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out value);
    }
}
