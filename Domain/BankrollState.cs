namespace FaustusControllerLite.Domain;

public sealed class BankrollState
{
    public const int CurrentSchemaVersion = 7;
    public const string ChaosMetadata = "Metadata/Items/Currency/CurrencyRerollRare";
    public const string DivineMetadata = "Metadata/Items/Currency/CurrencyModValues";

    private Dictionary<string, NonCoreBalanceState> _nonCoreBalances = new(StringComparer.Ordinal);
    private List<global::FaustusControllerLite.Orders.TrackedOrderState> _restingOrders = [];

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

    /// <summary>
    /// The single order an input controller currently owns - arming, clicking, cancelling,
    /// collecting, or stashing. At most one exists at any moment, which is what keeps every
    /// click-boundary proof in <c>Orders</c> a statement about one order.
    /// </summary>
    public global::FaustusControllerLite.Orders.TrackedOrderState? TrackedOrder { get; set; }

    /// <summary>
    /// Orders that are placed and simply waiting. No controller owns one, no input is armed against
    /// one, and each is observation-only until the sweep promotes it into <see cref="TrackedOrder"/>
    /// to settle it. Resting is the only thing a multi-order sweep does in parallel; settlement stays
    /// strictly serial. See <see cref="TrackedOrderRestPolicy"/> for which statuses may rest here.
    /// </summary>
    public List<global::FaustusControllerLite.Orders.TrackedOrderState> RestingOrders
    {
        get => _restingOrders;
        set => _restingOrders = value ?? [];
    }

    public WorkflowExecutionState? Workflow { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }

    /// <summary>
    /// Every order the canonical state knows about, active first. Callers that need to reason about
    /// custody, reservations, or capacity want this rather than <see cref="TrackedOrder"/> alone.
    /// </summary>
    public IEnumerable<global::FaustusControllerLite.Orders.TrackedOrderState> AllOrders
    {
        get
        {
            if (TrackedOrder is { } active)
            {
                yield return active;
            }

            foreach (var resting in RestingOrders)
            {
                yield return resting;
            }
        }
    }

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

    /// <summary>
    /// Whether canonical state currently blocks trading. The active slot blocks while it is
    /// unresolved, exactly as before; a resting order blocks only when it is ambiguous. A resting
    /// <c>Pending</c> order is a placed order doing its job, not an unresolved one.
    /// </summary>
    public bool ComputeUnresolved() =>
        TrackedOrder?.IsUnresolved == true ||
        RestingOrders.Any(global::FaustusControllerLite.Orders.TrackedOrderRestPolicy.BlocksTrading);

    public List<global::FaustusControllerLite.Orders.TrackedOrderState> CloneRestingOrders() =>
        [.. RestingOrders];

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
