# Read-Only Validation

Keep every `Allow*` setting disabled. Bind only `CaptureCurrentPairHotkey` and `DumpSdkReadsHotkey` for these checks.

## Settings And Ledger

1. Load Lite independently and confirm `Enable`, every permission, and every hotkey are off/unbound by default.
2. Confirm Chaos and Divine seeds are zero and minimum profit is 5 Chaos.
3. Wait for the catalogue status to become ready. Confirm Orb of Alteration resolves to exactly one metadata path.
4. Change the target, reload, and confirm both display name and exact metadata persist.
5. Change seed settings without pressing either reset button. Confirm the ledger does not change.
6. Press arm, wait over ten seconds, then apply. Confirm no reset occurs.
7. Arm and apply within ten seconds. Confirm state and one audit event are written.

## SDK Diagnostic

1. Exchange closed: run both hotkeys. Capture must fail safely and the diagnostic must still contain independent sections.
2. Exchange open with an incomplete pair: confirm null selection evidence and no rate-cache update.
3. Select Divine offered and Chaos wanted. Compare `MarketRateGet`, `MarketRateGive`, and the first five rows of both stock books with the report. In `Exact normalized rates`, confirm Wanted rows show Divine to Chaos as `Get/Give`, Offered rows show Divine to Chaos as `Give/Get`, and each reverse row is the exact reciprocal.
4. Capture the pair, note its capture ID/timestamp/stocks in `Config/FaustusControllerLite/latest-rates.json`, reverse the pair, and capture again. Confirm the record count remains one for this league/canonical pair and the stored orientation, capture ID, timestamp, rates, and stocks are replaced.
5. Open wanted and offered pickers separately. Confirm side, metadata, owned count, option rectangle, and catalogue match.
6. Open stash and inventory. Confirm both visibility reads are plausible.
7. Inspect a disposable pending/completed order. Record the matching `PlayerOrderId` and order-element tree; do not infer click indices.

Milestone 2 passes only when all seven checks above are plausible. Do not enable any `Allow*` setting.

## Milestone 3 Three-Market Matrix

1. Keep every `Allow*` setting disabled. Set `MaximumQuoteAgeSeconds` high enough to complete the manual sequence without rushing.
2. With the isolated bankroll uninitialized or seeded at zero, manually capture Divine/Chaos, target/Chaos, and target/Divine in one area. Confirm no executable candidate is accepted from zero capital.
3. Initialize a small test bankroll no larger than owned Chaos/Divine. Open the `I have` picker and leave it open long enough for fresh exact Chaos and Divine ownership reads, then close it without selecting anything. The `I want` picker is never accepted as ownership evidence because it reports zero counts.
4. Capture all three canonical markets again in the same area/session. Confirm the status reports either a fully sized exact candidate or explicit rejection reasons; a real opportunity is not required.
5. In the SDK diagnostic, inspect the four executable edges for one pair. Confirm Wanted stock is immediate selected / competing reverse and Offered stock is competing selected / immediate reverse.
6. For an immediate edge, manually verify input depth: aggregate listed output at the selected exact rate, form whole numerator-sized lots, and multiply by the denominator. Better-rate rows use their own exact lots.
7. Confirm a competing edge's queue includes only listed input units at exactly its selected rate. Do not compare queue quantities across different currencies.
8. Confirm each candidate shows starting principal, whole-lot leg amounts, explicit remainders, terminal realized Chaos, Chaos benchmark, realized Chaos profit, and expected gold as `unknown`.
9. Confirm an unsold target remainder does not increase realized Chaos profit or satisfy `MinimumProfitChaos`.
10. Repeat one pair in reverse orientation. Confirm it replaces the canonical record and produces equivalent directed rates/intents rather than double-reversing them.
11. Let any one of the three captures exceed `MaximumQuoteAgeSeconds`. Confirm the complete matrix is rejected even if a possible path would not use that market.
12. Change area and confirm prior captures and ownership observations cannot form a coherent matrix.
13. Confirm no mouse movement, clicking, typing, placement, cancellation, or collection occurs throughout the checkpoint.
14. If multiple routes exceed `MinimumProfitChaos`, confirm ranking prefers zero competing legs over one, and one over two, before comparing profit within the same competing-leg count.

Input automation remains blocked until all reads above are plausible in game.

## Milestone 4 Verified Automated Probing

Use disposable bindings such as function keys. Keep placement, cancellation, collection, amount input, and full-workflow permissions disabled throughout.

1. Bind `CalibratePickerButtonHotkey` and `ProbeMarketsHotkey`. Leave all input permissions disabled.
2. Open the exchange with its picker closed. Hover the `I have` picker button, press calibration, then manually click that same button within five seconds. Close it and repeat for `I want`.
3. Confirm the overlay reports both calibration sides ready and `picker-calibration.json` contains two normalized points between zero and one.
4. Change resolution/window geometry, reopen the exchange, and confirm calibration remains normalized to the panel. Recalibrate if the UI layout itself changed.
5. Press the probe hotkey while any one of probing, movement, click, or query permission is disabled. Confirm no mouse or keyboard input occurs.
6. Enable exactly those four permissions. If full `FaustusController` is enabled, confirm Lite disables it. If exclusion cannot be verified, confirm Lite turns all its input permissions off.
7. With the exchange closed, picker already open, Path of Exile unfocused, or a modifier held, press probe. Confirm it refuses without input.
8. Open the exchange, close the picker, release modifiers, and press probe. Do not touch mouse or keyboard. Confirm the cursor tweens to calibrated buttons, each picker opens on the expected side, quoted search receives verified picker focus, and exactly one metadata-matched option is clicked per side.
9. Confirm the probe selects Divine/Chaos, Chaos/target, and Divine/target in one area/session and reports the configured count of identical rate/liquidity samples for each.
10. Confirm only after all three pairs finish that `latest-rates.json` replaces all three records with one shared session ID and area ID. Repeating a successful session must retain one record per canonical pair.
11. Record the three capture IDs. Start another run and move the mouse manually. Confirm immediate cancellation, no later click, and unchanged capture IDs.
12. Repeat the failed-run check separately for Alt-Tab, closing the exchange, changing area, opening an unexpected picker, changing target, toggling any required permission off, and holding Ctrl/Shift/Alt. Each failure must retain the prior three capture IDs.
13. During query entry, confirm only the picker search receives text. Loss of `FocusedInputElement` must cancel before the next key.
14. Change a live book while sampling if practical. Confirm stability resets rather than publishing changing stock/depth/queue data.
15. Press the probe hotkey during a run. Confirm cancellation, release of all synthetic input, no partial cache publication, and no subsequent cursor movement/clicks.
16. Confirm order placement, cancellation, collection, and full workflow remain unavailable and no order is created.

Milestone 4 passes only when successful publication and every interruption test are plausible. Do not advance to order staging before this gate passes.

## Milestone 5 Dry-Run Single-Leg Staging

`ExecuteSingleLegHotkey` stages only the first leg of the current best accepted candidate. It does not execute or place that leg.

1. Restart ExileAPI so Loader uses the current assembly. Give calibration, probing, capture, diagnostic, staging, and workflow distinct hotkeys; duplicate keyboard/controller chords must block input.
2. Open the exchange and run the SDK diagnostic. Confirm `Amount inputs` reports both SDK elements, valid geometry, and exact digits when manually populated. Clear them afterward.
3. Produce a fresh accepted candidate. If needed for testing, set `MinimumProfitChaos` to zero, refresh `I have` ownership, and run a complete probe; do not bypass bankroll/live-ownership caps.
4. Enable verified movement, verified clicks, query input, and amount input. Keep placement and full workflow disabled. Automated probing may be disabled after the candidate exists.
5. Record every current `PlayerOrderId`, candidate path, and exact first-leg amounts.
6. Press `ExecuteSingleLegHotkey` once. Confirm the verified selector chooses exactly the candidate's first-leg pair, including the already-selected-pair case.
7. Confirm the cursor clicks only the offered and wanted SDK amount fields. Each field must receive exact focused-input ancestry before Ctrl+A, Backspace, or any digit.
8. Confirm the displayed values exactly match `InputSpent` and `Output`; leading zeros, formatted text, or conflicting digit descendants must fail.
9. Confirm Enter is sent exactly once while the wanted amount input still has verified focus, locking the typed ratio. Confirm no Place Order target is approached or clicked. Exact order IDs must remain unchanged after Enter.
10. Confirm stable full-liquidity quote samples pass both before and after typing. Any rate change, immediate depth loss, or worsening competing queue must cancel.
11. Wait for the three-second observation. `Staged` is valid only if the complete positive/unique `PlayerOrderId` set is unchanged. Additions, removals, duplicates, or unreadable orders must fail.
12. Repeat separately while moving the mouse, Alt-Tabbing, changing area/pair/target, opening a picker, changing a required permission, enabling placement/full workflow, or holding a modifier. Confirm immediate cancellation and no order.
13. Arm calibration and then start staging; confirm pending calibration is discarded rather than observing automated clicks.
14. Press capture, probe, calibration, workflow, or staging hotkeys during staging. Confirm the operation cancels and no later click/key occurs.
15. Confirm the order list and isolated bankroll remain unchanged after every successful and failed dry run.

Milestone 5 passes only after the staged fields, no-Enter/no-placement behavior, stable quote checks, exact order baseline, and every interruption are plausible.

## Milestone 6 Verified One-Leg Placement

This checkpoint creates one real order. Use the smallest disposable accepted candidate. Never retry after an ambiguous result.

1. Restart ExileAPI. Confirm bankroll and calibration files migrate to schema 2 while preserving prior seeds and picker coordinates.
2. Bind `CalibratePlaceOrderHotkey` and `PlaceStagedLegHotkey` to unique keys. Keep full workflow disabled.
3. Open the exchange, hover the exact Place Order button, and press its calibration hotkey. Confirm the cursor does not click and the overlay reports Place Order calibration ready.
4. Use a deliberately small initialized bankroll no larger than live ownership. Do not test with the normal full bankroll; a manual probe or existing staged form is not required.
5. Record the complete current order-ID set. Enable `Allow Order Placement`, leave full workflow off, and press `PlaceStagedLegHotkey` once. This is the sole authorization for the sequence.
6. Confirm Lite runs a fresh three-market probe, recalculates the full route, refreshes the selected first-leg market again in the same session, restages the pair, enters exact amounts, and presses Enter once to lock them.
7. If the fresh run produces no accepted candidate or any permission/settings/context changes, confirm the sequence stops without clicking. Pressing the placement hotkey again while it runs must cancel rather than authorize another click.
8. Confirm the restaged pair/amounts match the newly displayed candidate and the expiring token is revalidated immediately before clicking.
9. Confirm the cursor then moves to the manually calibrated Place Order target and clicks exactly once after the final full-route callback, live directed quote check, exact fields, baseline IDs, foreground, panel, stash, inventory, and modifier gates pass.
10. Confirm exactly one new order appears. Verify its positive ID, timestamp, exact metadata/hashes, original offered amount, ratio, and pending/completed economics match the staged leg.
11. Inspect `bankroll-<league>.json`: the pre-click intent and reservation must be in the same schema-2 document. Pending leaves offered funds reserved; immediate completion moves verified proceeds to completed-uncollected. Both remain unresolved.
12. Inspect `execution-audit-<league>.jsonl` for one compact armed event followed by matched or ambiguous evidence.
13. Turn placement permission off immediately. Confirm all further staging/placement and bankroll reset attempts are blocked while the tracked order is pending, completed-uncollected, ambiguous, or unreadable.
14. If no order, multiple orders, mismatched economics, panel/focus loss after click, persistence failure, or release uncertainty occurs, confirm status becomes `Ambiguous`, no click retry occurs, and the known ID is retained when available.
15. Restart ExileAPI and confirm unresolved state remains blocked. Do not delete/edit the canonical bankroll or retry the order; later lifecycle milestones provide reconciliation.

Milestone 6 passes only after one tiny order is clicked once, exactly matched, atomically reserved/tracked, and remains blocked for reconciliation. Do not advance to cancellation or collection from an ambiguous result.

### Observed Milestone 6 Evidence

Live placement passed on 2026-07-31 in Allflame:

- One click produced new `PlayerOrderId=4`, status `Pending`.
- Exact economics matched 300 Chaos Orb offered for 1,920 Orb of Alteration wanted (6.4 per Chaos).
- Exchange reported 19,200 gold cost.
- Baseline IDs were 1, 2, and 3; exactly one new ID appeared.
- Canonical schema-2 bankroll moved 300 Chaos from available to reserved and persisted the pending tracked order atomically.
- Audit contains one `OrderPlacementArmed` event followed by one `OrderPlacementMatched` event.
- `HasUnresolvedOrder=true` correctly blocks further staging, placement, and bankroll reset pending lifecycle reconciliation.
- The user subsequently collected the order manually in game. Lite has not verified or accounted for that collection yet, so canonical state intentionally remains pending/reserved until lifecycle and collection reconciliation are implemented.
- The user then manually listed the collected 1,920 Alterations for 320 Chaos. This second order is external/untracked evidence only; Lite must not credit the prospective 20-Chaos gross spread unless later lifecycle and collection checks identify and reconcile it exactly.

## Milestone 8 Verified Collection

Canonical state has been manually reconciled from SDK evidence to exact order 2: completed 1,920 Alterations → 320 Chaos, proceeds uncollected. Prior audit history remains unchanged and a reconciliation event was appended.

1. Restart ExileAPI. Confirm tracked state is `CompletedUncollected`, ID 2, completed-uncollected Chaos is 320, available Chaos is 0, and unresolved remains true.
2. Bind `CalibrateCollectionHotkey` and `CollectTrackedOrderHotkey` to unique keys. Enable verified movement, verified clicks, query input, and collection. Disable placement and full workflow.
3. Open exchange, stash, and inventory with the picker closed. Confirm live order 2 is completed with exact Alteration/Chaos metadata and hashes, original 1,920, remaining 0, received 320, and ratio 1:6/6:1.
4. Hover the center of order 2's left-side bought-currency slot displaying 320 Chaos and press `CalibrateCollectionHotkey`. Confirm no click occurs and collection calibration reports ready. Do not hover row center, amount text, another completed order, or the right-side offered slot.
5. Press `CollectTrackedOrderHotkey` once and do not touch mouse/keyboard. Lite must force a fresh pre-collection `I have Chaos` ownership read, then uniquely identify order 2 by model and row text/economics.
6. Confirm the cursor moves to order 2's calibrated left slot and performs exactly one Ctrl-right-click after canonical `CollectionArmed` persistence. It must not touch completed order 1 or pending order 3.
7. Confirm order 2 disappears while every full SDK snapshot for unrelated orders remains unchanged. Additions, removals, or economics/status changes are ambiguous.
8. Confirm Lite forces a second `I have Chaos` picker read produced after disappearance. The owned count must increase by exactly 320 from the phase-bound baseline, with at least two identical reads.
9. Inspect schema-3 bankroll: completed-uncollected Chaos becomes 0, available Chaos becomes 320, tracked status becomes `Collected`, and `HasUnresolvedOrder=true` until stash transfer finishes.
10. Enable the separate stash-transfer permission. Require exact collected inventory amount, a visible Currency Stash, an unobscured matching stack, and a stable non-target inventory fingerprint before one Ctrl+Shift-right-click.
11. Verify target inventory becomes zero, visible Currency Stash increases by exactly the collected amount, non-target inventory is unchanged, and aggregate owned count is unchanged. Only then persist `Stashed` and clear unresolved.
12. On any post-click failure, missing row, ownership mismatch, unrelated-order change, permission/focus loss, or release uncertainty, confirm durable `Ambiguous` state and no retry. Do not manually edit or transfer again.

Milestone 8 passes only when the uniquely matched reconciled order disappears, owned Chaos increases by exactly 320, canonical bankroll credits 320 once, unrelated orders remain unchanged, and the exact collected currency is verified in stash. `PlayerOrderId` is recorded as a live locator, not durable identity.

### Observed Milestone 8 Evidence

- Live SDK evidence showed `PlayerOrderId` is a mutable list locator: the exact 1,920 Alteration → 320 Chaos order moved from ID 2 to ID 4 after unrelated orders were added. Collection therefore resolves one unique exact economics/creation snapshot and persists the current live ID immediately before input.
- Schema-3 collection calibration succeeded on the exact completed row's left 320-Chaos slot.
- One Ctrl-right-click removed only the exact 1,920 Alteration → 320 Chaos order. The separate completed 150 Chaos → 975 Alteration order and all pending orders remained.
- The first post-click frame arrived 43 ms after arming and transiently failed full unrelated-order equality, correctly persisting `Ambiguous` without credit. Verification now polls for exact stabilization until the existing three-second deadline instead of rejecting the first transient frame.
- Read-only picker evidence recorded owned Chaos 1,557 before collection and 1,877 after collection: exact delta +320.
- Append-only evidence reconciliation changed canonical completed-uncollected Chaos 320 → 0 and available Chaos 0 → 320 exactly once. `Collected` remained unresolved until stash verification.
- Audit sequence is `OrderCollectionArmed`, `OrderCollectionAmbiguous`, then `OrderCollectionManualEvidenceReconciliationCredited`; prior evidence was not rewritten.

### Observed Final Inventory-to-Stash Evidence

- Full-controller test notes confirmed the exchange overlaps the left two inventory columns. Live Lite evidence measured exchange right edge X=1,793; all sixteen collected Chaos stacks began at X=1,836 or farther right, while only non-target blockers occupied covered columns.
- The final M8 phase required a visible `CurrencyStash`, exact target metadata, sixteen readable 20-Chaos stacks totaling 320, and a complete non-target inventory fingerprint.
- Before the sole Ctrl+Shift-right-click: player inventory Chaos=320, visible Currency Stash Chaos=2,082, aggregate owned Chaos=2,402.
- After transfer: player inventory Chaos=0, visible Currency Stash Chaos=2,402, aggregate owned Chaos=2,402. Non-Chaos inventory remained unchanged.
- Canonical schema 3 / tracked schema 2 ended at `Stashed`, available Chaos=320, completed-uncollected Chaos=0, and unresolved=false.
- Durable stash-transfer intent stores inventory/stash/ownership baselines, non-target fingerprint, amount/metadata, area, and timestamp. Reload recovery accepts only the exact pre-state or exact post-state; all other evidence becomes ambiguous.
- Audit appended `CollectedCurrencyStashTransferRequired`, `CollectedCurrencyStashTransferArmed`, and `CollectedCurrencyStashedAndVerified` without rewriting earlier events.

## Milestone 7 No-Fill Lifecycle Evidence

- Manual SDK probing established cancellation is a two-click operation: the pending row's small right-edge X opens `IngameUi.PopUpWindow`; confirmation uses typed `TwoButtonWindowOk`, while `TwoButtonWindowCancel` is separately visible.
- Confirmed no-fill terminal state reports `IsCanceled=true`, `IsCompleted=true`, status text `Order Cancelled`, remaining offered 1/1, and received wanted 0. Only the right offered-return slot icon is collectible.
- One old 1 Chaos → 500 Alteration order was adopted through the explicit read-only adoption hotkey. Adoption atomically changed available Chaos 320 → 319 and reserved Chaos 0 → 1 while persisting creation time, ratio, gold, mutable locator, and the already-expired captured deadline.
- Passive polling moved `Pending → TimedOut` without input. One cancellation authorization persisted `CancelArmed`, clicked the calibrated row X once, verified the typed popup, persisted `CancelClicked`, clicked typed OK once, and observed exact `CanceledUncollected` terminal evidence.
- Terminal accounting changed reserved Chaos 1 → 0 and completed-uncollected Chaos 0 → 1 exactly once.
- Canceled return collection used the calibrated right slot and proved row disappearance, inventory Chaos 0 → 1, visible Currency Stash unchanged at 49, and aggregate owned Chaos 49 → 50 before crediting available Chaos 319 → 320.
- Final stash transfer proved inventory Chaos 1 → 0, visible Currency Stash 49 → 50, and aggregate owned Chaos 50 → 50. Canonical status ended `Stashed`, unresolved=false, and orders=0.
- Audit sequence: `ManualPendingOrderAdoptedForLifecycle`, `TrackedOrderLifecycleTimedOut`, `TrackedOrderCancellationArmed`, `TrackedOrderCancellationConfirmArmed`, `TrackedOrderCancellationCanceledUncollected`, `CanceledReturnCollectionArmed`, `CanceledReturnCollectedAndCredited`, `CollectedCurrencyStashTransferArmed`, `CollectedCurrencyStashedAndVerified`.
- M7 partial-fill validation completed with both left wanted-proceeds and right offered-refund slot behavior. The implementation selects wanted proceeds first, persists progress without credit, selects offered return second, atomically credits both assets only after both proofs, then stashes each asset under a separate authorization.

### Observed Partial-Fill Checkpoint

- SDK/model evidence captured at `2026-08-01T18:22:50.0495349+00:00`: order ID 4, completed, not canceled, 2 original Chaos offered, 1 Chaos remaining, 5 Alterations received, ratio 2:5, gold 50, creation `2026-08-01T18:22:19+00:00`.
- The row visibly contains both collectible slots: 5 Alterations in the calibrated left wanted-proceeds slot and 1 Chaos in the calibrated right offered-return slot.
- Read-only adoption changed available Chaos 320 → 318, reserved Chaos remained 0 after immediate terminal settlement, and completed-uncollected buckets became 1 Chaos + 5 Alterations.
- The left authorization moved exactly 5 Alterations into inventory. The SDK then reported `wantedStack=0` and `gold=0`; the initial strict identity check timed out and persisted `Ambiguous`. A fresh dump proved the left icon hidden, right icon still visible, inventory Alterations 0 → 5, visible stash unchanged at 689, unchanged non-target inventory, and unchanged unrelated orders. Terminal-only identity now accepts observed zeroed gold only on completed rows, and a read-only recovery persisted `WantedAssetCollected=true` without retry or credit.
- The right authorization moved exactly 1 Chaos into inventory and removed the terminal row. Ownership changed Chaos 8 → 9. Canonical credit then occurred atomically: available Chaos 318 → 319, available Alterations 0 → 5, and both completed-uncollected buckets became zero.
- Separate stash authorizations proved Alterations inventory 5 → 0 / stash 689 → 694 / ownership 694 unchanged, then Chaos inventory 1 → 0 / stash 8 → 9 / ownership 9 unchanged.
- Final tracked schema 4 status is `Stashed`; all four per-asset collection/stash flags are true, all reserved and completed-uncollected buckets are zero, and unresolved=false.
- Audit retains `TerminalAssetCollectionArmed`, the initial `CanceledReturnCollectionAmbiguous`, `TerminalAssetCollectionInterruptedPostStateReconciled`, second `TerminalAssetCollectionArmed`, `TerminalAssetsCollectedAndCreditedAtomically`, `TerminalAssetStashProgressVerified`, and `TerminalAssetsStashedAndVerified`.
- Collection requires zero pre-existing inventory units of the current asset so later stash custody can remain exact. Aggregate ownership may be zero before collection.
- Interrupted terminal-asset collection and stash intents accept only exact pre-click or exact post-click evidence without retrying the old click. A simple completed-order collection intent without a durable per-asset baseline remains hard-blocked for manual reconciliation. Completed controllers ignore later area-change cancellation so stale armed snapshots cannot overwrite newer progress.

## Milestone 9 Full Workflow

This checkpoint can place, cancel, collect, and stash real orders. Use only a deliberately small disposable bankroll. The implementation is complete, but this checkpoint has not yet been live-validated.

1. Restart ExileAPI and confirm the overlay reports Milestone 9, bankroll schema 5 loads, no active workflow exists, and all permissions/hotkeys are disabled before setup.
2. Bind only `FullWorkflowHotkey` to a unique key. Confirm every picker, Place Order, collection, return, and cancellation calibration is ready before enabling input.
3. Enable automated probing, verified movement, verified clicks, query input, amount input, placement, cancellation, collection, stash transfer, and full workflow. Confirm the full `FaustusController` is disabled.
4. Open foreground exchange, visible Currency Stash, and inventory with picker closed, no popup, no held modifier, no unresolved unrelated order, and zero matching currency already in inventory.
5. Press the workflow hotkey once. Confirm one coherent three-market probe completes before an exact schema-2 workflow route is persisted. The current leg must then receive a selected-market refresh, remaining-path profitability check, exact restage, and one placement click.
6. Inspect canonical state after placement. The workflow must be `LegActive`, its attempt ID must match the tracked order, and exactly one offered bucket must equal the tracked reservation. No later leg may start while that order is unresolved.
7. For an immediate fill, confirm exact collection and stash custody complete before a new three-market probe begins for the next leg. Confirm the next leg spends only the prior leg's verified output, not unrelated target holdings.
8. For a competing fill, confirm passive polling waits until the persisted deadline. On timeout, verify exactly one row-X click and one typed confirmation click, followed by deterministic collection/stash settlement.
9. Force a partial fill or no fill. Confirm both proceeds and offered return are reconciled and stashed, then workflow phase becomes `Stopped`; no downstream placement occurs.
10. During probing, staging, movement to Place Order, cancellation, collection, and stash transfer, separately disable one permission, open a popup, move the mouse, Alt-Tab, close a panel, or change area. Confirm revocation is observed before the next controller effect and no blind retry occurs.
11. Press the workflow hotkey a second time while a local operation is active. Confirm local automation stops. A pending server order remains tracked and is not represented as canceled.
12. Restart separately in `Pending`, `TimedOut`, `CancelArmed`, `CancelClicked`, terminal-uncollected, `CollectionArmed`, `Collected`, and `StashTransferArmed`. Confirm no input resumes on load. A new workflow-hotkey press may authorize safe continuation, while interrupted intents first classify exact pre-click or post-click evidence without retrying the old click.
13. Force profitability below `MinimumProfitChaos`, stale quotes, insufficient immediate depth, and ledger/live ownership disagreement before a later leg. Confirm the workflow stops before placement and retains its durable cursor/evidence.
14. On exact final completion, confirm workflow phase `Completed`, cursor equals leg count, unresolved is false, every reservation/completed-uncollected bucket is zero, and the audit records planned versus actual terminal Chaos and profit.
15. For proceeds or returns larger than free inventory capacity, confirm Lite repeatedly collects one authenticated-capacity batch, credits it once, stashes it, and resumes the same left/right slot until the exact row disappears. No opposite-slot collection or downstream placement may begin while a batch awaits stash custody.
16. Disable every permission, reset every hotkey to `None`, and disable the plugin immediately after the controlled checkpoint.

Milestone 9 live validation passes only after both an immediate continuation and one competing/timeout recovery path preserve exact accounting, custody, authorization, and no-retry guarantees.

## Observed Milestone 9 Evidence

- A two-leg workflow completed `300 Chaos -> 1,830 Alterations -> 305 Chaos` for exact planned/actual profit of 5 Chaos.
- The first leg exceeded one-click inventory capacity and was collected/stashed automatically in authenticated batches of `1,160` and `670`. Each batch had a durable collection intent, one ledger credit, one verified stash transfer, and zero pending amount before the next click.
- The second leg collected/stashed 305 Chaos and advanced workflow phase to `Completed`, cursor `2/2`, with every reserved/completed-uncollected bucket zero.
- A separate `305 Chaos -> 1,830 Alterations` competing order reached its one-minute test deadline with no fill. Lite persisted both cancellation boundaries, recovered 305 Chaos from the right offered-return slot, credited/stashed it once, and stopped the workflow with balances unchanged.
- Live row matching accepted decimal ratio text, preserved exact competing limit economics through readable head drift, and rejected unreadable/depth/profit states before placement. An organic partial `483 Regrets -> 315 Chaos` row exposed pending cancellation text `1 : 1.53`; the cancellation gate now uses the same integer-only decimal-rounding proof as completed-row matching. The order finished before recovery input and then collected/stashed 315 Chaos exactly once.
- Restart did not restore transient authorization. Canonical reconciliation and tracked schema-5 migration loaded with no state error.
- A simultaneous nonzero wanted-proceeds plus offered-return partial fill was not forced; both independent slot paths and batch progress invariants are covered, and the combined branch remains for organic observation.
- Final isolated bankroll after validation: 305 Chaos, 5 Divine, and 2,319 Alterations; unresolved false. Plugin, permissions, and hotkeys were disabled, with minimum profit restored to 5 and competing wait restored to 6 minutes.

## Observed Milestone 2 Evidence

Live validation passed on 2026-07-31 in Allflame:

- Divine Orb offered and Chaos Orb wanted resolved to exact metadata and hashes.
- Immediate and competing books normalized plausibly in both directions using exact ratios.
- Exchange, stash, and inventory visibility reads were plausible.
- One pending order resolved as `PlayerOrderId=1`, offered Chaos Orb, wanted Orb of Alteration, and offered stack `1/1`.
- `WantedItemStackSize=0` represented no received fill; the parallel order element displayed the requested `500 : 1` ratio and one offered Chaos.
- The SDK returned one order and one parallel order element. The tree was recorded, but no cancel or collection child index was inferred from this single observation.
