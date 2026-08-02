namespace FaustusControllerLite.Domain;

public sealed class BankrollState
{
    public const int CurrentSchemaVersion = 5;

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
    public string TargetMetadata { get; set; } = string.Empty;
    public long AvailableTarget { get; set; }
    public long ReservedTarget { get; set; }
    public long CompletedUncollectedTarget { get; set; }
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
}
