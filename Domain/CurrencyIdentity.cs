namespace FaustusControllerLite;

public sealed class CurrencyIdentity : IEquatable<CurrencyIdentity>, IComparable<CurrencyIdentity>
{
    public CurrencyIdentity(string metadata, string? name = null)
        : this(metadata, 0, name)
    {
    }

    public CurrencyIdentity(string metadata, uint hash, string? name)
    {
        if (string.IsNullOrWhiteSpace(metadata))
        {
            throw new ArgumentException("Currency metadata is required.", nameof(metadata));
        }

        Metadata = metadata;
        Hash = hash;
        Name = string.IsNullOrWhiteSpace(name) ? metadata : name;
    }

    public string Metadata { get; }

    public uint Hash { get; }

    public string Name { get; }

    public string DisplayLabel => $"{Name} [{Metadata}]";

    public int CompareTo(CurrencyIdentity? other) =>
        other is null ? 1 : StringComparer.Ordinal.Compare(Metadata, other.Metadata);

    public bool Equals(CurrencyIdentity? other) =>
        other is not null && StringComparer.Ordinal.Equals(Metadata, other.Metadata);

    public override bool Equals(object? obj) => obj is CurrencyIdentity other && Equals(other);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Metadata);

    public override string ToString() => Name;
}

public readonly record struct CurrencyPairKey
{
    public CurrencyPairKey(CurrencyIdentity first, CurrencyIdentity second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);

        if (first.Equals(second))
        {
            throw new ArgumentException("A currency pair requires two distinct currencies.", nameof(second));
        }

        if (first.CompareTo(second) < 0)
        {
            First = first;
            Second = second;
        }
        else
        {
            First = second;
            Second = first;
        }
    }

    public CurrencyIdentity First { get; }

    public CurrencyIdentity Second { get; }

    public bool Contains(CurrencyIdentity currency) => First.Equals(currency) || Second.Equals(currency);

    public CurrencyIdentity Other(CurrencyIdentity currency)
    {
        if (First.Equals(currency))
        {
            return Second;
        }

        if (Second.Equals(currency))
        {
            return First;
        }

        throw new ArgumentException("Currency is not part of this pair.", nameof(currency));
    }

    public override string ToString() => $"{First.Metadata}|{Second.Metadata}";
}
