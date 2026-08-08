using ExileCore;
using FaustusControllerLite.Domain;
using FaustusControllerLite.Orders;

namespace FaustusControllerLite.Probing;

public enum CurrencyTargetKind
{
    Currency,
    Scarab,
}

public sealed record CurrencyTargetDescriptor(
    CurrencyIdentity Identity,
    CurrencyTargetKind Kind,
    string ClassName,
    int MaxStackSize,
    string SelectorLabel)
{
    public string Metadata => Identity.Metadata;
    public uint Hash => Identity.Hash;
    public string Name => Identity.Name;
}

public sealed record CurrencyCatalogueEntry(
    CurrencyIdentity Identity,
    CurrencyTargetKind? Kind,
    string ClassName,
    int MaxStackSize);

public sealed class CurrencyCatalogue
{
    private readonly Dictionary<string, CurrencyIdentity> _byMetadata;
    private readonly Dictionary<string, CurrencyCatalogueEntry> _entriesByMetadata;
    private readonly Dictionary<string, CurrencyTargetDescriptor> _descriptorsByMetadata;
    private readonly Dictionary<string, CurrencyTargetDescriptor> _targetsByMetadata;
    private readonly Dictionary<string, CurrencyTargetDescriptor> _targetsByLabel;

    public CurrencyCatalogue(IEnumerable<CurrencyIdentity> currencies)
        : this(currencies.Select(currency => new CurrencyCatalogueEntry(
            currency, Classify(currency.Metadata), string.Empty, 0)))
    {
    }

    public CurrencyCatalogue(IEnumerable<CurrencyCatalogueEntry> entries)
    {
        var normalized = entries
            .GroupBy(entry => entry.Identity.Metadata, StringComparer.Ordinal)
            .Select(group =>
            {
                var first = group.First();
                if (group.Any(entry => entry.Identity.Hash != first.Identity.Hash ||
                    entry.Identity.Name != first.Identity.Name || entry.Kind != first.Kind ||
                    entry.ClassName != first.ClassName || entry.MaxStackSize != first.MaxStackSize))
                {
                    throw new InvalidDataException(
                        $"Currency exchange metadata {group.Key} had conflicting catalogue records.");
                }
                return first;
            })
            .OrderBy(entry => entry.Identity.Name, StringComparer.Ordinal)
            .ThenBy(entry => entry.Identity.Metadata, StringComparer.Ordinal)
            .ToArray();

        Items = normalized.Select(entry => entry.Identity).ToArray();
        _byMetadata = Items.ToDictionary(currency => currency.Metadata, StringComparer.Ordinal);
        _entriesByMetadata = normalized.ToDictionary(entry => entry.Identity.Metadata, StringComparer.Ordinal);

        var classified = normalized.Where(entry => entry.Kind is not null).ToArray();
        var selectable = classified.Where(entry => entry.Identity.Metadata != BankrollState.ChaosMetadata &&
            entry.Identity.Metadata != BankrollState.DivineMetadata).ToArray();
        var duplicateNames = selectable
            .GroupBy(entry => entry.Identity.Name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        SupportedTargets = selectable.Select(entry => new CurrencyTargetDescriptor(
                entry.Identity,
                entry.Kind!.Value,
                entry.ClassName,
                entry.MaxStackSize > 0 ? entry.MaxStackSize : 0,
                duplicateNames.Contains(entry.Identity.Name) ? entry.Identity.DisplayLabel : entry.Identity.Name))
            .ToArray();
        _descriptorsByMetadata = classified.ToDictionary(
            entry => entry.Identity.Metadata,
            entry => new CurrencyTargetDescriptor(
                entry.Identity, entry.Kind!.Value, entry.ClassName,
                entry.MaxStackSize > 0 ? entry.MaxStackSize : 0, entry.Identity.Name),
            StringComparer.Ordinal);
        _targetsByMetadata = SupportedTargets.ToDictionary(target => target.Metadata, StringComparer.Ordinal);
        _targetsByLabel = SupportedTargets.ToDictionary(target => target.SelectorLabel, StringComparer.Ordinal);
    }

    public IReadOnlyList<CurrencyIdentity> Items { get; }
    public IReadOnlyList<CurrencyTargetDescriptor> SupportedTargets { get; }

    public bool TryGetByMetadata(string metadata, out CurrencyIdentity? currency) =>
        _byMetadata.TryGetValue(metadata, out currency);

    public bool TryGetEntryByMetadata(string metadata, out CurrencyCatalogueEntry? entry) =>
        _entriesByMetadata.TryGetValue(metadata, out entry);

    public bool TryGetByLabel(string label, out CurrencyIdentity? currency)
    {
        var found = _targetsByLabel.TryGetValue(label, out var descriptor);
        currency = descriptor?.Identity;
        return found;
    }

    public bool TryGetTargetByMetadata(string metadata, out CurrencyTargetDescriptor? target) =>
        _targetsByMetadata.TryGetValue(metadata, out target);

    public bool TryGetDescriptorByMetadata(string metadata, out CurrencyTargetDescriptor? target) =>
        _descriptorsByMetadata.TryGetValue(metadata, out target);

    public bool TryGetTargetByLabel(string label, out CurrencyTargetDescriptor? target) =>
        _targetsByLabel.TryGetValue(label, out target);

    public bool TryGetUniqueByName(string name, out CurrencyIdentity? currency)
    {
        var matches = Items.Where(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        currency = matches.Length == 1 ? matches[0] : null;
        return currency != null;
    }

    public bool TryGetUniqueTargetByName(string name, out CurrencyTargetDescriptor? target)
    {
        var matches = SupportedTargets
            .Where(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        target = matches.Length == 1 ? matches[0] : null;
        return target is not null;
    }

    public static CurrencyTargetKind? Classify(string metadata)
    {
        if (metadata.StartsWith(StashCustodyPolicy.CurrencyPrefix, StringComparison.Ordinal))
            return CurrencyTargetKind.Currency;
        if (metadata.StartsWith(StashCustodyPolicy.ScarabPrefix, StringComparison.Ordinal))
            return CurrencyTargetKind.Scarab;
        return null;
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

        try
        {
            catalogue = new CurrencyCatalogue(entries
                .Select(entry => entry.BaseItemType)
                .Where(item => item != null && !string.IsNullOrWhiteSpace(item.Metadata))
                .Select(item => new CurrencyCatalogueEntry(
                    new CurrencyIdentity(item.Metadata, item.Hash, item.BaseName),
                    CurrencyCatalogue.Classify(item.Metadata),
                    item.ClassName ?? string.Empty,
                    item.CurrencyInfo?.MaxStackSize is > 0 ? item.CurrencyInfo.MaxStackSize : 0)));
        }
        catch (Exception exception)
        {
            catalogue = null;
            failureReason = $"invalid exchange catalogue: {exception.Message}";
            return false;
        }
        if (catalogue.Items.Count == 0 || catalogue.SupportedTargets.Count == 0)
        {
            catalogue = null;
            failureReason = "loaded with no valid supported target entries";
            return false;
        }

        failureReason = string.Empty;
        return true;
    }
}
