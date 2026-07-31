# FaustusControllerLite

## Build

```powershell
dotnet build .\Plugins\Source\FaustusControllerLite\FaustusControllerLite.csproj --no-restore
dotnet run --project .\Plugins\Tests\FaustusControllerLite.Tests\FaustusControllerLite.Tests.csproj --no-restore
```

Both commands must finish with zero warnings. The test project is outside `Plugins/Source` so ExileCore does not discover it as a plugin.

## Layout

- `Core`: permission composition and lifecycle support.
- `Domain`: metadata identity, exact rational rates, normalized quote edges, route calculation, and bankroll models.
- `Probing`: catalogue, current-panel capture, normalization, and SDK diagnostics.
- `Persistence`: league bankroll/audit and canonical latest-rate storage.
- `Input`, `Orders`, `Validation`: safety boundaries for later gated milestones.

## Invariants

- The current implementation is read-only. It contains no mouse or keyboard calls.
- All input permissions and hotkeys default off/unbound.
- Currency identity is exact metadata; names are display-only.
- Spend is capped by both the isolated ledger and observed live ownership.
- Starting settings only enter the ledger through the arm-then-apply reset action.
- A canonical pair sorts its two metadata identities. Reverse captures replace the same league/pair cache record.
- A capture's selected orientation is offered to wanted. `WantedItemStock` is immediate. `OfferedItemStock` has its raw ratio reversed to become competing in selected orientation.
- Calculator comparisons use exact integer arithmetic. Unsold target residual has zero realized-Chaos profit value.
- Quotes in one calculation must share league, session, area instance, and freshness.
- Corrupt state/cache files block their feature and are not silently overwritten during load.

## State

- Bankroll: `Config/FaustusControllerLite/bankroll-<league>.json`, schema 1.
- Audit: `Config/FaustusControllerLite/execution-audit-<league>.jsonl`, schema 1 events.
- Latest rates: `Config/FaustusControllerLite/latest-rates.json`, schema 1, one record per league/canonical pair.
- SDK report: `Config/FaustusControllerLite/sdk-diagnostic.txt`.

## Roadmap

- Milestone 1: implemented.
- Milestone 2: implemented, pending live SDK validation.
- Milestone 3: implemented and covered by pure tests.
- Milestones 4-9: intentionally blocked by the plan's live-read gate. Do not add input until `TESTING-read-only.md` has plausible evidence for every required SDK state.
