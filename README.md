# FaustusControllerLite

FaustusControllerLite automates Currency Exchange arbitrage and stash sell sweeps using exact,
persisted order state. It starts fail-closed: the plugin, every input permission, and every hotkey
are disabled or unbound by default.

This guide covers a clean first-time setup. Complete the calibration and test-order sections before
enabling the full workflow.

## Requirements

- A working ExileAPI installation loading `Plugins/Source/FaustusControllerLite`.
- Access to the in-game Currency Exchange in the intended league.
- Enough exchange gold for probing, calibration orders, and production orders.
- Chaos and Divine physically available for the bankroll amount you configure.
- Free inventory space and at least one free Currency Exchange order slot.
- A visible Currency stash tab during arbitrage custody operations.
- Correct stash affinities for non-currency targets such as scarabs.
- Stable game resolution, window mode, UI scale, and exchange layout after calibration.

Plugin settings are persisted in:

`config/global/FaustusControllerLite_settings.json`

## Safety Rules

- Disable the full `FaustusController` plugin. It must not run alongside Lite input automation.
- Use unique hotkeys. A duplicate binding causes both actions to refuse input.
- Keep Path of Exile in the foreground while the plugin is operating.
- Keep the Currency Exchange, stash, and inventory visible. Keep the currency picker closed unless
  the plugin opens it.
- Release Control, Shift, and Alt before calibration or automation.
- Do not move the mouse while the plugin is moving it.
- Do not change the target, feature, permissions, league, area, or UI layout during a workflow.
- Never delete state files to clear an unresolved order. Reconcile the exchange, inventory, and
  stash first.

## First-Time Checklist

1. Disable `FaustusController` and enable `FaustusControllerLite`.
2. Leave every `Allow...` permission disabled.
3. Enter the intended league and open the Currency Exchange, stash, and inventory.
4. Wait for the overlay to report `Catalogue: ready (...)`.
5. Set `ActiveFeature` to `Arbitrage` and select `TargetCurrency`.
6. Assign unique hotkeys, including all calibration and recovery hotkeys.
7. Initialize a small test bankroll.
8. Calibrate the offered picker, wanted picker, and Place Order button.
9. Use small test orders to calibrate collection, cancellation, and the offered-return slot.
10. Resolve and stash every test-order asset.
11. Safely reseed the intended production bankroll.
12. Enable the full-workflow permission profile.
13. Verify the required UI and overlay state, then press `FullWorkflowHotkey` once.

## Configure the Plugin

### Feature and Target

Set:

- `ActiveFeature = Arbitrage`
- `TargetCurrency` to the currency or scarab to trade

The target list is populated from the live Currency Exchange catalogue. Wait for the catalogue to
be ready before selecting a target. The plugin persists exact metadata, not only the display name.

Do not change the feature or target while a workflow, tracked order, or recovery operation exists.

### Strategy

Important settings:

| Setting | Default | Purpose |
| --- | ---: | --- |
| `StartingChaos` | `0` | Chaos committed to the canonical bankroll when reset is applied. |
| `StartingDivine` | `0` | Divine committed to the canonical bankroll when reset is applied. |
| `MinimumProfitChaos` | `5` | Minimum post-restoration profit for a new route. |
| `CompetingOrderWaitMinutes` | `5` | Time before a resting order is eligible for cancellation. |
| `ContinuousWorkflowRetrySeconds` | `10` | Base delay before a no-trade/restoration retry. |
| `MaximumQuoteAgeSeconds` | `60` | Maximum accepted quote and ownership age. |
| `StableRateSampleCount` | `3` | Matching book samples required before accepting a quote. |
| `CursorTweenSpeed` | `1600` | Verified cursor movement speed. |

`StartingChaos` and `StartingDivine` do not alter an already loaded bankroll. They take effect only
after a safe or forced fresh-state reset. Seed only currency that is physically available and
intentionally committed to the plugin.

### Hotkeys

All hotkeys are unbound by default. Assign a different key or controller signature to each action.

Required for calibration and full operation:

| Setting | Purpose |
| --- | --- |
| `CalibratePickerButtonHotkey` | Calibrates both picker buttons, one at a time. |
| `CalibratePlaceOrderHotkey` | Records the Place Order button without clicking it. |
| `CalibrateCollectionHotkey` | Records the bought-currency slot on a completed row. |
| `CalibrateCancelHotkey` | Records the cancel X on a timed-out row. |
| `CalibrateReturnSlotHotkey` | Records the offered-currency return slot on a terminal row. |
| `AdoptPendingOrderHotkey` | Adopts one exact existing test order into canonical tracking. |
| `CollectTrackedOrderHotkey` | Collects or reconciles one terminal asset batch. |
| `StashCollectedCurrencyHotkey` | Stashes or reconciles the current collected batch. |
| `CancelTimedOutOrderHotkey` | Cancels the exact canonical timed-out order. |
| `FullWorkflowHotkey` | Starts, stops, or resumes arbitrage automation. |
| `DumpSdkReadsHotkey` | Writes a diagnostic dump for troubleshooting. |

Optional/manual-operation hotkeys include `ProbeMarketsHotkey`, `CaptureCurrentPairHotkey`,
`ExecuteSingleLegHotkey`, `PlaceStagedLegHotkey`, and `SellSweepHotkey`.

## Initialize a Test Bankroll

The bankroll is durable accounting state, not an automatic inventory scan.

1. Set `StartingChaos` and `StartingDivine` to small amounts that cover the test orders below.
2. In the plugin settings, press `ArmFreshStateReset`.
3. Press `ApplyArmedFreshStateReset` within 10 seconds.
4. Verify the overlay `Bankroll:` line shows the current league and expected seed.

A safe reset is refused while a workflow, sell sweep, unresolved order, unreadable state, or input
operation exists. It preserves calibration, rates, audits, and runtime diagnostics.

Do not use the forced reset for normal setup. A forced reset abandons accounting and moves no
in-game items.

## Calibration

Calibration is saved in:

`config/FaustusControllerLite/FaustusControllerLite/picker-calibration.json`

Use the same game resolution, window mode, UI scale, and exchange layout intended for operation.
Recalibrate after a material layout or aspect-ratio change.

### 1. Offered-Currency Picker Button

1. Open the Currency Exchange and close the currency picker.
2. Hover the offered-currency picker button.
3. Press `CalibratePickerButtonHotkey`.
4. Without moving the cursor, manually click that picker button within five seconds.
5. Wait for `Recorded normalized offered picker button calibration.`
6. Close the picker.

The game must remain foreground, the exchange and cursor geometry must remain stable, and modifiers
must be released.

### 2. Wanted-Currency Picker Button

Repeat the same process while hovering the wanted-currency picker button. Wait for:

`Recorded normalized wanted picker button calibration.`

The overlay should now show:

`Picker calibration: offered=ready, wanted=ready`

### 3. Place Order Button

1. Make the Place Order button visible by selecting any valid pair and entering harmless amounts.
2. Hover the actual Place Order button.
3. Press `CalibratePlaceOrderHotkey`.
4. Do **not** click Place Order during calibration.
5. Verify `Recorded normalized Place Order target without clicking it.`
6. Verify `Place Order calibration: ready` on the overlay.

### 4. Collection Slot

This calibration requires a canonical tracked order in `CompletedUncollected` state.

1. With all automation permissions still disabled, manually place a very small order offering seeded
   Chaos or Divine for the selected target.
2. Let it fill and leave the completed row visible.
3. Press `AdoptPendingOrderHotkey`. Despite the name, it can adopt the exact terminal row.
4. Verify the tracked status is `CompletedUncollected`.
5. Hover the completed row's **left bought/wanted-currency slot**.
6. Press `CalibrateCollectionHotkey`.
7. Verify `Recorded collection slot offset for exact tracked order ...` and
   `Collection calibration: ready`.

Calibration records the location only; it does not collect the order.

### 5. Cancel Button

This calibration requires a canonical tracked order in exact `TimedOut` state.

Calibration schema 6 records the live cancel control's row-relative center and dimensions, so the
same layout can scale between 2560x1440 and 1920x1080. Older point-only cancel calibration is
cleared on upgrade and must be recorded again.

1. Resolve and stash the completed calibration order as described below.
2. Manually place a tiny resting order offering seeded Chaos or Divine for the selected target.
3. Press `AdoptPendingOrderHotkey` while that is the one exact matching order.
4. Keep the exchange visible until the overlay reports `TimedOut`.
5. Hover directly inside the pending row's small **right-edge cancel X**. Calibration must resolve
   the cursor to one unique visible square leaf control.
6. Press `CalibrateCancelHotkey`.
7. Do not click the X during calibration.
8. Verify `Recorded exact pending-row cancel X calibration; no click occurred.`

The timeout is captured when the order is adopted. Set `CompetingOrderWaitMinutes` before adoption.

### 6. Offered-Return Slot

After the cancel button is calibrated:

1. Enable the manual cancellation permissions listed below.
2. Press `CancelTimedOutOrderHotkey` once.
3. Wait for `CanceledUncollected` with a positive offered return.
4. Hover the terminal row's **right offered-currency return slot**.
5. Press `CalibrateReturnSlotHotkey`.
6. Verify `Recorded canceled offered-return slot calibration; no click occurred.`

If the test order fills completely, it cannot supply return-slot calibration. Create another small
resting order.

## Resolve the Calibration Orders

Only one collection batch may be pending stash custody at a time.

### Manual Cancellation Permissions

Enable:

- `AllowVerifiedMouseMovement`
- `AllowVerifiedClicks`
- `AllowOrderCancellation`

Keep placement, collection, stash transfer, full workflow, and sell sweep disabled. Press
`CancelTimedOutOrderHotkey` once and wait for terminal state.

### Manual Collection Permissions

Enable:

- `AllowVerifiedMouseMovement`
- `AllowVerifiedClicks`
- `AllowQueryInput`
- `AllowOrderCollection`

Keep placement, cancellation, stash transfer, full workflow, and sell sweep disabled. Press
`CollectTrackedOrderHotkey` once per authorized collection/reconciliation step.

### Manual Stash Permissions

Enable:

- `AllowVerifiedMouseMovement`
- `AllowVerifiedClicks`
- `AllowQueryInput`
- `AllowOrderCollection`
- `AllowStashTransfer`

Keep placement, cancellation, full workflow, and sell sweep disabled. Press
`StashCollectedCurrencyHotkey` after each collected batch. Do not collect another batch until the
current batch has verified stash custody.

Continue until the tracked order reaches `Stashed` and no canonical custody remains unresolved.

## Apply the Production Bankroll

After all test orders are resolved:

1. Disable all input permissions.
2. Set the intended production `StartingChaos` and `StartingDivine`.
3. Press `ArmFreshStateReset`.
4. Press `ApplyArmedFreshStateReset` within 10 seconds.
5. Confirm the overlay shows the intended seeded balances, no active workflow, and no tracked order.

## Enable Full Arbitrage

Set `ActiveFeature = Arbitrage`.

Enable all of these:

- `AllowAutomatedProbing`
- `AllowVerifiedMouseMovement`
- `AllowVerifiedClicks`
- `AllowQueryInput`
- `AllowAmountInput`
- `AllowOrderPlacement`
- `AllowOrderCancellation`
- `AllowOrderCollection`
- `AllowStashTransfer`
- `AllowFullWorkflow`

Disable:

- `AllowSellSweep`

The permission snapshot is captured when the workflow is authorized. Changing any permission while
authorized stops local workflow input before the next controller step.

## Required UI Before Starting

Verify all of the following:

- Path of Exile is foreground.
- Currency Exchange is visible.
- Currency picker is closed.
- No popup is visible.
- Stash and inventory are visible.
- The full `FaustusController` plugin is disabled.
- The bankroll and tracked-order files loaded without errors.
- No unrelated order or input operation is unresolved.
- The overlay reports:
  - `Active feature: Arbitrage`
  - `Exchange panel: visible`
  - `Catalogue: ready (...)`
  - the intended target metadata
  - `Last failure: None`
  - the intended bankroll seed
  - picker offered/wanted ready
  - Place Order ready
  - Collection ready

## Start, Stop, and Resume

Press `FullWorkflowHotkey` once to authorize automation.

For a new workflow, the plugin probes the required markets, persists an exact route before clicking,
refreshes each market before placement, tracks one order at a time, collects and stashes exact
settlement assets, and restores Divine principal before completing a Divine-funded cycle.

If no route is accepted, authorization remains active and the plugin retries after the configured
delay. A restoration waits and reprobes rather than abandoning outstanding principal.

Pressing `FullWorkflowHotkey` a second time stops **local automation**. It does not forget or
automatically cancel a server-side order.

To resume:

1. Restore the exact full-workflow permission profile and required UI.
2. Press `FullWorkflowHotkey` once.
3. The plugin resumes the durable phase and leg instead of creating a replacement route.

An area change or plugin reload revokes local authorization but preserves durable workflow and order
state. Reopen the required UI and press the workflow hotkey once to resume.

## Troubleshooting

Read `Operation`, `Last failure`, `Workflow`, `Continuous trading`, and `Tracked order` on the
overlay before pressing another hotkey.

Common failures:

- **Hotkey conflict:** assign unique hotkeys.
- **Catalogue unavailable:** open the Currency Exchange and wait for a readable catalogue.
- **Calibration missing:** complete all six captures above.
- **Full controller conflict:** disable `FaustusController`.
- **Permission changed:** restore the exact profile and reauthorize.
- **Area changed:** reopen the exchange/stash/inventory and reauthorize.
- **Quote unavailable:** leave authorization active; the workflow retries safely.
- **Ambiguous custody/order:** do not place another order or reset state. Reconcile the exact live
  order, inventory, and stash first.

Press `DumpSdkReadsHotkey` for a live diagnostic. Useful evidence is written to:

- `config/FaustusControllerLite/FaustusControllerLite/sdk-diagnostic.txt`
- `config/FaustusControllerLite/FaustusControllerLite/workflow-runtime.log`
- `config/FaustusControllerLite/FaustusControllerLite/execution-audit-<league>.jsonl`
- `config/FaustusControllerLite/FaustusControllerLite/bankroll-<league>.json`

## Forced Reset Warning

`ArmForcedFreshStateReset` and `ApplyArmedForcedFreshStateReset` are last-resort recovery controls.
They quarantine canonical files and abandon the listed accounting, but they do not move an item or
cancel an exchange order. Use them only after manually reconciling every listed custody item and
preserving the audit evidence.

For sell-sweep-specific behavior, see [SELL-SWEEP.md](SELL-SWEEP.md).
