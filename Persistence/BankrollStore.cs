using FaustusControllerLite.Domain;
using Newtonsoft.Json;

namespace FaustusControllerLite.Persistence;

public sealed class BankrollStore
{
    private readonly string _directory;

    public BankrollStore(string directory)
    {
        _directory = directory;
    }

    public BankrollState? Load(string league)
    {
        var path = GetStatePath(league);
        if (!File.Exists(path))
        {
            return null;
        }

        var state = JsonConvert.DeserializeObject<BankrollState>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Bankroll state was empty.");
        if (state.SchemaVersion != BankrollState.CurrentSchemaVersion ||
            !state.IsInitialized ||
            !string.Equals(state.League, league, StringComparison.Ordinal) ||
            state.SeededChaos < 0 || state.SeededDivine < 0 ||
            state.AvailableChaos < 0 || state.AvailableDivine < 0)
        {
            throw new InvalidDataException("Bankroll state failed schema or value validation.");
        }

        return state;
    }

    public void Save(BankrollState state)
    {
        Directory.CreateDirectory(_directory);
        var path = GetStatePath(state.League);
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, JsonConvert.SerializeObject(state, Formatting.Indented));
        File.Move(temporaryPath, path, true);
    }

    public void AppendAudit(BankrollAuditEvent auditEvent)
    {
        Directory.CreateDirectory(_directory);
        File.AppendAllText(
            GetAuditPath(auditEvent.League),
            JsonConvert.SerializeObject(auditEvent, Formatting.None) + Environment.NewLine);
    }

    private string GetStatePath(string league) => Path.Combine(_directory, $"bankroll-{Sanitize(league)}.json");
    private string GetAuditPath(string league) => Path.Combine(_directory, $"execution-audit-{Sanitize(league)}.jsonl");

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
    }
}

public sealed record BankrollAuditEvent(
    int SchemaVersion,
    string EventType,
    string League,
    DateTimeOffset OccurredAtUtc,
    long Chaos,
    long Divine)
{
    public static BankrollAuditEvent Seeded(BankrollState state) => new(
        1,
        "BankrollSeededOrReset",
        state.League,
        state.UpdatedAtUtc,
        state.AvailableChaos,
        state.AvailableDivine);
}
