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
- `Probing`: catalogue, current-panel capture, normalization, SDK diagnostics, stable sampling, and verified three-market coordination.
- `Persistence`: league bankroll/audit and canonical latest-rate storage.
- `Input`: normalized picker/Place Order and tracked-row collection calibration. `Orders`: dry-run staging, one verified placement, and exact tracked-order collection. `Validation` remains gated for cancellation/full workflow.

## Invariants

- Mouse/keyboard calls exist only in verified probing, staging, one-leg placement, and exact tracked-order collection. Cancellation and full workflow remain unimplemented.
- All input permissions and hotkeys default off/unbound.
- Automated probing requires probing, verified movement, verified clicks, and query permissions simultaneously; every permission is rechecked while running.
- Every input effect requires foreground Path of Exile, the same league/area, expected panel/picker side and locked pair, no held modifiers, unchanged commanded cursor, and an unexpired state deadline.
- Query keys require `FocusedInputElement` to remain inside the active picker. Exact metadata is re-read immediately before the sole option click.
- Owned synthetic keys/buttons enter a release-pending state until every release succeeds.
- Staging recalculates the best candidate at hotkey time, selects only its first leg, types exact verified ASCII integers, and presses Enter exactly once with verified wanted-field focus to lock the ratio. It never targets/clicks Place Order.
- Standalone dry-run staging requires placement/full-workflow permissions off. The user-authorized placement sequence requires placement on and full workflow off while reusing the same stable quote, exact amount, Enter-lock, and order-ID gates.
- This SDK exposes no supported Place Order element/enabled flag. No-placement proof is the absence of any placement target/effect plus the unchanged exact order-ID set.
- Placement uses one explicit hotkey authorization while `Allow Order Placement` is enabled. That sequence probes all markets, refreshes the selected first leg, restages and locks amounts, revalidates an expiring candidate token at click time, and then performs exactly one calibrated click.
- Before the click, offered principal is reserved and the full placement intent is atomically committed inside the canonical bankroll file. After the click, pending, completed-uncollected, and ambiguous outcomes remain unresolved and block all further trading.
- A new order must be exactly baseline plus one positive unique ID with exact nonzero hashes, metadata, original offered amount, whole-lot ratio, plausible timestamp, and status/amount invariants. Any uncertainty is durable ambiguity and is never retried.
- Collection targets only canonical `CompletedUncollected` state. It matches one unique visible row by completed status, exact amounts and ratio, persists `CollectionArmed`, Ctrl-right-clicks once, proves only that tracked order disappeared, and requires a phase-bound exact ownership increase before crediting.
- Every unrelated order's full SDK snapshot must remain unchanged through collection settlement. Any post-click interruption, ownership mismatch, row ambiguity, or persistence uncertainty remains unresolved and is never retried automatically.
- Enabling any Lite input permission disables the full `FaustusController`; inability to verify exclusion turns all Lite input permissions off.
- Currency identity is exact metadata; names are display-only.
- Spend is capped by both the isolated ledger and observed live ownership.
- Starting settings only enter the ledger through the arm-then-apply reset action.
- A canonical pair sorts its two metadata identities. Reverse captures replace the same league/pair cache record.
- A capture's selected orientation is offered to wanted. `WantedItemStock` is immediate selected / competing reverse. `OfferedItemStock` is competing selected / immediate reverse, with its raw ratio reversed for selected orientation.
- Immediate `ListedCount` is output units and is converted to exact whole-lot input depth. Competing queue counts only same-rate input units; queue amounts from different currencies are never compared for ranking.
- Calculator comparisons use exact integer arithmetic. Unsold target residual has zero realized-Chaos profit value.
- Quotes in one calculation must share league, session, area instance, and freshness.
- Automated captures remain private until three exact canonical pairs have stable rates and liquidity fingerprints; the complete session replaces the cache atomically.
- Expected gold is separate and nullable. Live candidates report it as unknown until a verified estimator is available; unknown is never treated as Chaos profit.
- User-approved scope override (2026-07-31): routes may contain up to two sequential competing legs. Accepted routes rank by fewer competing legs first, then higher realized Chaos profit.
- Corrupt state/cache files block their feature and are not silently overwritten during load.

## State

- Bankroll/workflow canonical state: `config/FaustusControllerLite/FaustusControllerLite/bankroll-<league>.json`, bankroll schema 4 with tracked-order schema 4.
- Audit: `config/FaustusControllerLite/FaustusControllerLite/execution-audit-<league>.jsonl`, schema 1 events.
- Latest rates: `config/FaustusControllerLite/FaustusControllerLite/latest-rates.json`, schema 1, one record per league/canonical pair.
- SDK report: `config/FaustusControllerLite/FaustusControllerLite/sdk-diagnostic.txt`.
- Picker/Place Order/collection calibration: `config/FaustusControllerLite/FaustusControllerLite/picker-calibration.json`, schema 5 normalized panel/row coordinates and layout evidence.

## Roadmap

- Milestone 1: implemented.
- Milestone 2: implemented and live SDK validation passed on 2026-07-31.
- Milestone 3: implemented and live calculator validation passed on 2026-07-31.
- Milestone 4: implemented; controlled live probing validation passed on 2026-07-31.
- Milestone 5: implemented; conservative live staging behavior accepted on 2026-07-31.
- Milestone 6: implemented and live-validated on 2026-07-31 with one exactly matched pending order.
- Milestone 8: fully live-validated on 2026-07-31. The exact 1,920 Alteration → 320 Chaos order was collected once and canonical available Chaos became 320. Final inventory-to-stash transfer proved inventory 320 → 0, visible Currency Stash 2,082 → 2,402, aggregate owned 2,402 → 2,402, and unchanged non-Chaos inventory. Canonical status is `Stashed` and unresolved is false.
- Milestone 7: fully live-validated on 2026-08-01. The no-fill path adopted, timed out, canceled, collected the right return, credited once, and stashed exactly. The partial path adopted a completed 2 Chaos order with 5 Alterations received and 1 Chaos returned, collected left then right under separate authorizations, withheld credit until both proofs, atomically credited both assets, and stashed both sequentially. An observed post-left-click SDK mutation (`wantedStack=0`, `gold=0`) was reconciled from exact row/inventory/stash evidence without retrying input; the append-only ambiguity record was retained.
- Milestone 9 is next; complete workflow and recovery validation remain pending.
