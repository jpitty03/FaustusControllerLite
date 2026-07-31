using ExileCore;
namespace FaustusControllerLite.Probing;

public sealed class CurrencyCatalogue
{
    private readonly Dictionary<string, CurrencyIdentity> _byMetadata;
    private readonly Dictionary<string, CurrencyIdentity> _byLabel;

    public CurrencyCatalogue(IEnumerable<CurrencyIdentity> currencies)
    {
        Items = currencies
            .GroupBy(currency => currency.Metadata, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(currency => currency.Name, StringComparer.Ordinal)
            .ThenBy(currency => currency.Metadata, StringComparer.Ordinal)
            .ToArray();
        _byMetadata = Items.ToDictionary(currency => currency.Metadata, StringComparer.Ordinal);
        _byLabel = Items
            .GroupBy(currency => currency.Name, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
    }

    public IReadOnlyList<CurrencyIdentity> Items { get; }

    public bool TryGetByMetadata(string metadata, out CurrencyIdentity? currency) =>
        _byMetadata.TryGetValue(metadata, out currency);

    public bool TryGetByLabel(string label, out CurrencyIdentity? currency) =>
        _byLabel.TryGetValue(label, out currency);

    public bool TryGetUniqueByName(string name, out CurrencyIdentity? currency)
    {
        var matches = Items.Where(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        currency = matches.Length == 1 ? matches[0] : null;
        return currency != null;
    }
}

public sealed class CurrencyCatalogueBuilder
{
    public bool TryBuild(GameController gameController, out CurrencyCatalogue? catalogue, out string failureReason)
    {
        var entries = gameController.Files.CurrencyExchange.EntriesList;
        if (entries.Count == 0)
        {
            catalogue = null;
            failureReason = "not loaded yet";
            return false;
        }

        catalogue = new CurrencyCatalogue(entries
            .Select(entry => entry.BaseItemType)
            .Where(item => item != null && !string.IsNullOrWhiteSpace(item.Metadata))
            .Select(item => new CurrencyIdentity(item.Metadata, item.Hash, item.BaseName)));
        if (catalogue.Items.Count == 0)
        {
            catalogue = null;
            failureReason = "loaded with no valid metadata entries";
            return false;
        }

        failureReason = string.Empty;
        return true;
    }
}
