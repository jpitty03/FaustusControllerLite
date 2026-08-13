using System.Text.Json;
using FaustusControllerLite.Probing;

namespace FaustusControllerLite.Domain;

public enum TradableCategory
{
    Currency,
    DeliriumOrbs,
    Scarabs,
    Fossils,
    Essences,
}

public sealed record TradableName(TradableCategory Category, string Name);

public sealed record TradableEntry(TradableCategory Category, string Name, CurrencyTargetDescriptor Target);

public sealed record UnresolvedTradable(TradableCategory Category, string Name, string Reason);

public sealed record TradablesResolution(
    IReadOnlyList<TradableEntry> Resolved,
    IReadOnlyList<UnresolvedTradable> Unresolved,
    int RequestedCount)
{
    public string Summary => $"resolved {Resolved.Count}/{RequestedCount}";

    public string DescribeUnresolved() => Unresolved.Count == 0
        ? string.Empty
        : string.Join(", ", Unresolved.Select(item => $"{item.Name} ({item.Reason})"));
}

/// <summary>
/// Reads the operator-maintained tradables list and binds each name to an exchange target descriptor.
/// The list is authored by display name because that is what the picker search box accepts; metadata is
/// resolved here so the rest of the sweep only ever deals in identities.
/// </summary>
public static class TradablesCatalogue
{
    public const string FileName = "tradables.json";

    public const string BankrollCurrencyReason = "bankroll currency";
    public const string NotExchangeableReason = "not an exchangeable target";
    public const string AmbiguousNameReason = "ambiguous name";
    public const string MissingFromCatalogueReason = "not in the exchange catalogue";
    public const string UntypeableReason = "picker search cannot type this name";

    private static readonly (string Key, TradableCategory Category)[] CategoryKeys =
    [
        ("Currency", TradableCategory.Currency),
        ("Delirium Orbs", TradableCategory.DeliriumOrbs),
        ("Scarabs", TradableCategory.Scarabs),
        ("Fossils", TradableCategory.Fossils),
        ("Essences", TradableCategory.Essences),
    ];

    public static string DescribeCategory(TradableCategory category) =>
        CategoryKeys.First(entry => entry.Category == category).Key;

    /// <summary>
    /// Parses the list. Throws <see cref="InvalidDataException"/> on any shape the operator should fix:
    /// an unknown category key, a non-string entry, a blank name, or a name repeated anywhere in the file.
    /// </summary>
    public static IReadOnlyList<TradableName> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Tradables list must be an object of category arrays.");
        }

        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!CategoryKeys.Any(entry => string.Equals(entry.Key, property.Name, StringComparison.Ordinal)))
            {
                throw new InvalidDataException($"Tradables list had unknown category '{property.Name}'.");
            }
        }

        var names = new List<TradableName>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, category) in CategoryKeys)
        {
            if (!document.RootElement.TryGetProperty(key, out var array))
            {
                continue;
            }

            if (array.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException($"Tradables category '{key}' must be an array of names.");
            }

            foreach (var element in array.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.String)
                {
                    throw new InvalidDataException($"Tradables category '{key}' contained a non-string entry.");
                }

                var name = element.GetString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(name))
                {
                    throw new InvalidDataException($"Tradables category '{key}' contained a blank name.");
                }

                if (!seen.Add(name))
                {
                    throw new InvalidDataException($"Tradables list repeated '{name}'.");
                }

                names.Add(new TradableName(category, name));
            }
        }

        return names;
    }

    /// <summary>
    /// Reads and parses the list without ever throwing into the tick loop. A missing or malformed file
    /// leaves the sweep unavailable rather than silently sweeping a partial list.
    /// </summary>
    public static bool TryLoad(string path, out IReadOnlyList<TradableName> names, out string failure)
    {
        try
        {
            if (!File.Exists(path))
            {
                names = [];
                failure = $"Tradables list not found at {path}.";
                return false;
            }

            names = Parse(File.ReadAllText(path));
            if (names.Count == 0)
            {
                failure = "Tradables list contained no names.";
                return false;
            }

            failure = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            names = [];
            failure = $"Tradables list unreadable: {exception.Message}";
            return false;
        }
    }

    /// <summary>
    /// Returns the operator-editable list path under the persistence directory, seeding it once from the
    /// copy shipped beside the plugin. Seeding only ever creates a missing file, so operator edits survive
    /// every rebuild.
    /// </summary>
    public static string EnsureOperatorCopy(string persistenceDirectory, string pluginDirectory)
    {
        var path = Path.Combine(persistenceDirectory, FileName);
        if (File.Exists(path))
        {
            return path;
        }

        var seed = Path.Combine(pluginDirectory, FileName);
        if (File.Exists(seed))
        {
            Directory.CreateDirectory(persistenceDirectory);
            File.Copy(seed, path);
        }

        return path;
    }

    /// <summary>
    /// Binds every enabled name to a supported exchange target. Names that cannot bind are reported with a
    /// reason instead of being dropped, so a stale list is visible rather than silently shrinking the sweep.
    /// </summary>
    public static TradablesResolution Resolve(
        IReadOnlyList<TradableName> names,
        CurrencyCatalogue catalogue,
        IReadOnlySet<TradableCategory> enabledCategories)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(catalogue);
        ArgumentNullException.ThrowIfNull(enabledCategories);

        var resolved = new List<TradableEntry>();
        var unresolved = new List<UnresolvedTradable>();
        var requested = 0;
        foreach (var entry in names)
        {
            if (!enabledCategories.Contains(entry.Category))
            {
                continue;
            }

            requested++;
            if (!IsPickerTypeable(entry.Name))
            {
                unresolved.Add(new UnresolvedTradable(entry.Category, entry.Name, UntypeableReason));
                continue;
            }

            if (catalogue.TryGetUniqueTargetByName(entry.Name, out var target) && target is not null)
            {
                resolved.Add(new TradableEntry(entry.Category, entry.Name, target));
                continue;
            }

            unresolved.Add(new UnresolvedTradable(entry.Category, entry.Name, DescribeFailure(catalogue, entry.Name)));
        }

        return new TradablesResolution(resolved, unresolved, requested);
    }

    /// <summary>
    /// Mirrors the picker search alphabet the probe can actually send. A name outside it would cancel the
    /// probe mid-sweep, so it is refused up front instead.
    /// </summary>
    public static bool IsPickerTypeable(string name) =>
        !string.IsNullOrWhiteSpace(name) &&
        name.ToLowerInvariant().All(AutomatedProbeController.CanType);

    private static string DescribeFailure(CurrencyCatalogue catalogue, string name)
    {
        if (catalogue.TryGetUniqueByName(name, out var identity) && identity is not null)
        {
            return identity.Metadata is BankrollState.ChaosMetadata or BankrollState.DivineMetadata
                ? BankrollCurrencyReason
                : NotExchangeableReason;
        }

        var matches = catalogue.Items
            .Count(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
        return matches > 1 ? AmbiguousNameReason : MissingFromCatalogueReason;
    }
}
