namespace FaustusControllerLite.Domain;

public sealed class BankrollState
{
    public const int CurrentSchemaVersion = 6;
    public const string ChaosMetadata = "Metadata/Items/Currency/CurrencyRerollRare";
    public const string DivineMetadata = "Metadata/Items/Currency/CurrencyModValues";

    private Dictionary<string, NonCoreBalanceState> _nonCoreBalances = new(StringComparer.Ordinal);

    public static BankrollState Uninitialized => new();

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string League { get; set; } = string.Empty;
    public bool IsInitialized { get; set; }
    public long SeededChaos { get; set; }
    public long SeededDivine { get; set; }
    public long AvailableChaos { get; set; }
    public long AvailableDivine { get; set; }
    public long ReservedChaos { get; set; }
    public long ReservedDivine { get; set; }
    public long CompletedUncollectedChaos { get; set; }
    public long CompletedUncollectedDivine { get; set; }
    public Dictionary<string, NonCoreBalanceState> NonCoreBalances
    {
        get => _nonCoreBalances;
        set => _nonCoreBalances = value is null
            ? null!
            : new Dictionary<string, NonCoreBalanceState>(value, StringComparer.Ordinal);
    }
    public bool HasUnresolvedOrder { get; set; }
    public global::FaustusControllerLite.Orders.TrackedOrderState? TrackedOrder { get; set; }
    public WorkflowExecutionState? Workflow { get; set; }
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

    public long GetAvailable(string metadata) => metadata switch
    {
        ChaosMetadata => AvailableChaos,
        DivineMetadata => AvailableDivine,
        _ => NonCoreBalances.TryGetValue(metadata, out var balance) ? balance.Available : 0,
    };

    public long GetReserved(string metadata) => metadata switch
    {
        ChaosMetadata => ReservedChaos,
        DivineMetadata => ReservedDivine,
        _ => NonCoreBalances.TryGetValue(metadata, out var balance) ? balance.Reserved : 0,
    };

    public long GetCompletedUncollected(string metadata) => metadata switch
    {
        ChaosMetadata => CompletedUncollectedChaos,
        DivineMetadata => CompletedUncollectedDivine,
        _ => NonCoreBalances.TryGetValue(metadata, out var balance) ? balance.CompletedUncollected : 0,
    };

    public Dictionary<string, NonCoreBalanceState> CloneNonCoreBalances() =>
        NonCoreBalances.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Clone(),
            StringComparer.Ordinal);
}

public sealed class NonCoreBalanceState
{
    public long Available { get; set; }
    public long Reserved { get; set; }
    public long CompletedUncollected { get; set; }

    public NonCoreBalanceState Clone() => new()
    {
        Available = Available,
        Reserved = Reserved,
        CompletedUncollected = CompletedUncollected,
    };
}
