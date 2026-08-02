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
- `Input`: normalized picker/Place Order and tracked-row calibration. `Orders`: verified placement, cancellation, collection, and stash custody. `Domain/WorkflowExecution.cs` is the pure persisted full-workflow coordinator.

## Invariants

- Full workflow composes the existing verified probe, staging, placement, lifecycle, cancellation, collection, and stash controllers; the coordinator sends no input itself.
- All input permissions and hotkeys default off/unbound.
- Automated probing requires probing, verified movement, verified clicks, and query permissions simultaneously; every permission is rechecked while running.
- Every input effect requires foreground Path of Exile, the same league/area, expected panel/picker side and locked pair, no held modifiers, unchanged commanded cursor, and an unexpired state deadline.
- Every effect also requires no popup. Full-workflow permission snapshots are checked before any controller tick, and `AllowFullWorkflow` never authorizes a low-level controller without a current workflow-hotkey authorization.
- Query keys require `FocusedInputElement` to remain inside the active picker. Exact metadata is re-read immediately before the sole option click.
- Owned synthetic keys/buttons enter a release-pending state until every release succeeds.
- Staging recalculates the best candidate at hotkey time, selects only its first leg, types exact verified ASCII integers, and presses Enter exactly once with verified wanted-field focus to lock the ratio. It never targets/clicks Place Order.
- Standalone dry-run staging requires placement/full-workflow permissions off. The user-authorized placement sequence requires placement on and full workflow off while reusing the same stable quote, exact amount, Enter-lock, and order-ID gates.
- This SDK exposes no supported Place Order element/enabled flag. No-placement proof is the absence of any placement target/effect plus the unchanged exact order-ID set.
- Placement uses one explicit hotkey authorization while `Allow Order Placement` is enabled. That sequence probes all markets, refreshes the selected first leg, restages and locks amounts, revalidates an expiring candidate token at click time, and then performs exactly one calibrated click.
- Before the click, offered principal is reserved and the full placement intent is atomically committed inside the canonical bankroll file. After the click, pending, completed-uncollected, and ambiguous outcomes remain unresolved and block all further trading.
- A new order must be exactly baseline plus one positive unique ID with exact nonzero hashes, metadata, original offered amount, whole-lot ratio, plausible timestamp, and status/amount invariants. Any uncertainty is durable ambiguity and is never retried.
- An order that executes immediately ends completed even when the book filled only part of it; the unfilled offered amount is a collectible return, not ambiguity. A fill may improve on the placed limit ratio, so terminal amounts are proved by bounds — the filled portion's placed-ratio entitlement at or below the received amount, which is at or below the whole order's wanted amount — never by exact ratio equality. A completed row that filled nothing is a misread; a canceled one is the ordinary no-fill return.
- An ambiguity record is durable evidence and must persist even though it never learned a placed ratio; only a live pending or terminal state must prove its placed ratio equals the leg rate.
- Every refused canonical tracked-order write records `TrackedOrderPersistRejected` with its reason, and no controller failure is reported as an empty string. A silently discarded durable write is a defect.
- An armed placement whose click outlived the placement controller's observation window is reconciled from the order list alone: baseline plus one order matching the armed pair, amount, ratio, and click time binds pending or terminal state; anything else is durable ambiguity. Reconciliation is observation only and never clicks.
- Pending-row cancellation aligns the durable SDK identity to one parallel visible row using status, total amounts, and an exact or correctly rounded decimal ratio; live partial-fill amounts do not replace the original placed economics.
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
- A workflow snapshots two or three exact legs and their economics. Its cursor, current input, probe session, rates, intents, amounts, benchmark, terminal Chaos, expected gold, and profit are fingerprinted and validated.
- Every workflow leg receives a fresh coherent three-market probe, remaining-path replan, selected-market refresh, exact restage, and final live validation. It never switches to a different route.
- Only an exact full fill with all settlement assets collected and stashed advances. Partial/no fill is reconciled and stops the current route.
- The workflow hotkey is a transient start/stop toggle for continuous trading; a second press stops local input only and never cancels or forgets a server order. Authorization is never persisted, and reload, area/target/permission change, controller failure, or ambiguity requires another press.
- While authorized, a completed or safely stopped workflow starts a fresh scan only after the durable `Stashed` transition leaves no unresolved order, reservation, uncollected settlement, or pending batch. Only a planner-complete no-route probe retries, after a jittered 10s (2-90s configurable, -2..+2s) cooldown; operational failures always stop instead.
- Every continuous-authorization revocation writes a `ContinuousAuthorizationRevoked` runtime record with its reason, and every loop-decision change plus a 30s heartbeat records the blocking component and next scan. A silently idle authorized loop is a defect. A placement latch that no controller has owned for 5s holds no armed input and is released; nothing else auto-recovers.
- Fresh-state reset is arm-then-apply, stops continuous trading, reseeds from configured seeds, and clears resolved workflow/tracked state and transient failures. It is refused while any order or settlement is unresolved and never deletes rates, calibration, audit, or unresolved evidence.
- Settlement collection is capacity-batched on both wanted-proceeds and offered-return slots. Each batch is durably armed, exact row/inventory/ownership evidence is verified, the batch is credited once, then stashed before another batch is collected.
- Reload never restores transient input authorization. Pending/cancellation states are observation-only until explicitly reauthorized; interrupted collection/stash intents accept exact pre-state or exact post-state and never repeat an uncertain click.

## State

- Bankroll/workflow canonical state: `config/FaustusControllerLite/FaustusControllerLite/bankroll-<league>.json`, bankroll schema 5 with tracked-order schema 5 and workflow schema 2.
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
- Milestone 10 (continuous workflow): implemented on 2026-08-02. First live run placed leg 1, timed out, canceled, collected the return, and stashed exactly, then sat idle instead of rescanning. Diagnosis: an area change revoked authorization while setting only `_operationStatus`, never `_lastFailure`, so `AppendFailureDiagnosticIfNeeded` and the `DriveFullWorkflow` early return were both silent and the runtime log ended at `WorkflowAuthorizationStarted` (`latest-rates.json` was never rewritten after the initial probe). Every revocation and loop decision is now recorded. The second live run then wedged differently: a 300 Chaos → 480 Regret order executed immediately, filling 295 for 473 Regret (one more than the placed 5:8 ratio entitles) and returning 5 Chaos. The matcher demanded an exact full fill and called it ambiguous; that ambiguity record could not persist because the workflow leg check demanded placed ratio parts an ambiguity record never has; and the rejection was invisible because the controller's failure text was empty and overwrote the real reason while revoking authorization. Canonical state froze at `Armed` with 300 Chaos reserved and no log line anywhere. Partial immediate fills are now terminal, ambiguity records persist, refused writes are recorded, and an armed placement is reconcilable from the order list. Live re-validation of the loop, immediate terminal restart, and fresh-state reset is still pending.
- Milestone 9: implemented and controlled-live validated on 2026-08-01. The suite passes 68/68 and the plugin builds with zero warnings. Live evidence includes exact multi-leg completion, capacity-batched proceeds (`1160 + 670`), competing timeout/cancellation with offered-return custody, restart recovery, and zero-side-effect rejection paths. Organic simultaneous partial-fill proceeds/return remains unforced; all runtime permissions, hotkeys, and the plugin are disabled after validation.
