namespace FaustusControllerLite.Domain;

public sealed class BankrollState
{
    public const int CurrentSchemaVersion = 1;

    public static BankrollState Uninitialized => new();

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string League { get; set; } = string.Empty;
    public bool IsInitialized { get; set; }
    public long SeededChaos { get; set; }
    public long SeededDivine { get; set; }
    public long AvailableChaos { get; set; }
    public long AvailableDivine { get; set; }
    public bool HasUnresolvedOrder { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }

    public static BankrollState Create(string league, long chaos, long divine) => new()
    {
        League = league,
        IsInitialized = true,
        SeededChaos = chaos,
        SeededDivine = divine,
        AvailableChaos = chaos,
        AvailableDivine = divine,
        UpdatedAtUtc = DateTimeOffset.UtcNow
    };
}
