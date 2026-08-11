# FaustusControllerLite

FaustusControllerLite automates Currency Exchange arbitrage and sell sweeps. It tracks every order,
collection, and stash transfer in durable state so interrupted workflows can resume safely.

Everything starts disabled. Complete this guide before enabling full automation.

## Requirements

- Disable the full `FaustusController` plugin.
- Open Path of Exile in the league you intend to use.
- Have enough Chaos, Divine, and gold for the configured bankroll.
- Keep the Currency Exchange, stash, and inventory visible while operating.
- Keep Path of Exile foreground and release Control, Shift, and Alt.
- Use a stable resolution, window mode, UI scale, and exchange layout.
- Assign a unique key to every hotkey you use.

The current custody implementation requires a visible Currency or Fragment premium stash tab. A
normal affinity tab is not yet supported.

## Quick Setup

1. Enable `FaustusControllerLite` and disable all `Allow...` permissions.
2. Set `ActiveFeature = Arbitrage`.
3. Open the Currency Exchange and wait for `Catalogue: ready`.
4. Select `TargetCurrency`.
5. Assign the calibration, adoption, recovery, and workflow hotkeys.
6. Seed a small test bankroll.
7. Calibrate both picker buttons and the Place Order button.
8. Use small test orders to calibrate collection, cancel, and return controls.
9. Resolve every test order until `Tracked order` is `Stashed` or `None`.
10. Safely reseed the production bankroll.
11. Enable the full-workflow permissions.
12. Press `FullWorkflowHotkey` once.

## Important Settings

| Setting | Range | Purpose |
| --- | ---: | --- |
| `StartingChaos` | 1-5000 | Chaos committed when a fresh-state reset is applied. |
| `StartingDivine` | 1-1000 | Divine committed when a fresh-state reset is applied. |
| `MinimumProfitChaos` | 1-5000 | Minimum post-restoration profit required for a new route. |
| `CompetingOrderWaitMinutes` | 1-3600 | Time before a pending order becomes `TimedOut`. |
| `EnableDirectDivineCycles` | on/off | Opts into two-competing-leg Divine-to-target-to-Divine cycles. |
| `MaximumDirectDivinePrincipal` | 1-1000 | Maximum Divine that one direct cycle may lock. |
| `MinimumSaleChaos` | 1-5000 | Minimum estimated Chaos value for a sell-sweep holding. |
| `ContinuousWorkflowRetrySeconds` | 2-90 | Delay before retrying a route or restoration. |
| `MaximumQuoteAgeSeconds` | 1-3600 | Maximum accepted market and ownership age. |

Changing `StartingChaos` or `StartingDivine` does not change the current bankroll. Apply a safe
fresh-state reset to use the new values.

## Hotkeys to Assign

All hotkeys are unbound by default.

| Hotkey setting | Purpose |
| --- | --- |
| `CalibratePickerButtonHotkey` | Calibrates offered and wanted picker buttons. |
| `CalibratePlaceOrderHotkey` | Calibrates Place Order without clicking it. |
| `CalibrateCollectionHotkey` | Calibrates the bought-currency slot. |
| `CalibrateCancelHotkey` | Calibrates the pending-row cancel X. |
| `CalibrateReturnSlotHotkey` | Calibrates the offered-currency return slot. |
| `AdoptPendingOrderHotkey` | Adds one exact existing order to plugin tracking. |
| `CollectTrackedOrderHotkey` | Collects one verified settlement batch. |
| `StashCollectedCurrencyHotkey` | Stashes one collected batch. |
| `CancelTimedOutOrderHotkey` | Cancels the exact tracked timed-out order. |
| `FullWorkflowHotkey` | Starts, stops, or resumes arbitrage automation. |
| `DumpSdkReadsHotkey` | Writes a diagnostic dump. |

## Seed a Test Bankroll

1. Set small `StartingChaos` and `StartingDivine` values that cover your test orders.
2. Press `ArmFreshStateReset` in plugin settings.
3. Press `ApplyArmedFreshStateReset` within 10 seconds.
4. Confirm the overlay `Bankroll` line shows the expected amounts.

The bankroll is accounting state, not an inventory scan. Only seed currency you physically own and
intend the plugin to use.

## Calibration

Calibration is saved in:

`config/FaustusControllerLite/FaustusControllerLite/picker-calibration.json`

### Offered Picker

1. Open the exchange and close the currency picker.
2. Hover the offered-currency picker button.
3. Press `CalibratePickerButtonHotkey`.
4. Without moving the cursor, manually click that button within five seconds.
5. Close the picker after the overlay reports success.

### Wanted Picker

Repeat the offered-picker steps over the wanted-currency picker button.

The overlay should show:

`Picker calibration: offered=ready, wanted=ready`

### Place Order

1. Select any valid pair and amounts so Place Order is visible.
2. Hover the Place Order button.
3. Press `CalibratePlaceOrderHotkey`.
4. Do not click Place Order.
5. Confirm `Place Order calibration: ready`.

### Collection Slot

This requires one tracked order in `CompletedUncollected` state.

Schema 7 records the live asset slot's row-relative position and size so collection works across
scaled 2560x1440 and 1920x1080 layouts. Older collection/return calibration is cleared once and must
be recorded again.

1. Manually place a tiny order offering seeded Chaos or Divine for the selected target.
2. Let it fill and leave the completed row visible.
3. Press `AdoptPendingOrderHotkey`.
4. Confirm the overlay reports `CompletedUncollected`.
5. Hover the completed row's left bought-currency slot.
6. Press `CalibrateCollectionHotkey`.
7. Do not click the slot.

### Cancel Button

This requires one tracked order in `TimedOut` state. `TimedOut` is a plugin status, not game text.

Before starting:

- Bind `AdoptPendingOrderHotkey`, `CalibrateCancelHotkey`, and `CancelTimedOutOrderHotkey`.
- Set `CompetingOrderWaitMinutes = 1` for calibration.
- Set the timeout before adopting the order.
- Resolve any previous tracked order first.

Procedure:

1. Manually place a tiny, unattractive order offering seeded Chaos or Divine for the selected target.
2. Ensure it is the only matching core-to-target order.
3. Press `AdoptPendingOrderHotkey`.
4. Confirm the overlay reports `Pending adopted`.
5. Keep the exchange and pending row visible until the overlay reports `TimedOut`.
6. Hover directly inside the small right-edge cancel X.
7. Press `CalibrateCancelHotkey`.
8. Do not click the X.
9. Confirm `Recorded exact pending-row cancel X calibration; no click occurred.`

Schema 6 records the live control's row-relative position and size. It supports scaled layouts such
as 2560x1440 and 1920x1080. Older cancel calibration is cleared once and must be recorded again.

If the test order fills, resolve it and create another order at a less attractive rate.

### Return Slot

1. After cancel calibration, enable the manual cancellation permissions below.
2. Press `CancelTimedOutOrderHotkey` once.
3. Wait for `CanceledUncollected` with returned offered currency.
4. Hover the terminal row's right offered-currency slot.
5. Press `CalibrateReturnSlotHotkey`.
6. Do not click the slot.

Hover directly inside the visible returned-currency slot. Schema 7 records the actual scaled slot,
not only the cursor position.

## Resolve Test Orders

Use only one manual permission profile at a time.

### Cancel

Enable:

- `AllowVerifiedMouseMovement`
- `AllowVerifiedClicks`
- `AllowOrderCancellation`

Disable placement, collection, stash transfer, full workflow, and sell sweep. Press
`CancelTimedOutOrderHotkey` once.

### Collect

Enable:

- `AllowVerifiedMouseMovement`
- `AllowVerifiedClicks`
- `AllowQueryInput`
- `AllowOrderCollection`

Disable placement, cancellation, stash transfer, full workflow, and sell sweep. Press
`CollectTrackedOrderHotkey` once per batch.

### Stash

Enable:

- `AllowVerifiedMouseMovement`
- `AllowVerifiedClicks`
- `AllowQueryInput`
- `AllowOrderCollection`
- `AllowStashTransfer`

Disable placement, cancellation, full workflow, and sell sweep. Press
`StashCollectedCurrencyHotkey` after each collected batch.

Do not collect another batch until the current batch has verified stash custody. Continue until the
tracked order reaches `Stashed`.

## Production Bankroll

After every test order is resolved:

1. Disable all input permissions.
2. Set the intended production `StartingChaos` and `StartingDivine`.
3. Press `ArmFreshStateReset`.
4. Press `ApplyArmedFreshStateReset` within 10 seconds.
5. Confirm the expected bankroll, no active workflow, and no tracked order.

## Full Workflow Permissions

Enable:

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

Disable `AllowSellSweep` and keep `ActiveFeature = Arbitrage`.

Before starting, verify the exchange, stash, and inventory are visible; the picker and popups are
closed; all calibration is ready; and `Last failure` is `None`.

Press `FullWorkflowHotkey` once to start.

## Direct Divine Cycles

Enable `EnableDirectDivineCycles` to probe and prioritize routes such as:

`739 Divine -> 1 Mirror of Kalandra -> 740 Divine`

Direct mode probes only Divine/Chaos and Divine/target. The Divine/Chaos quote values the profit for
`MinimumProfitChaos`; it is not an execution leg. Actual workflow profit remains denominated in
Divine.

Set `MaximumDirectDivinePrincipal` to the most Divine the plugin may commit. The opening and closing
orders are both competing limits and execute sequentially. If the closing order times out with the
target returned, the same durable workflow waits, reprobes, and retries closure instead of starting
an unrelated route.

Use a suitably long `CompetingOrderWaitMinutes` for low-volume currencies and ensure enough exchange
gold is available. Disable direct mode to restore normal three-market Chaos-cycle probing and
competing-leg-first ranking.

## Stop and Resume

Pressing `FullWorkflowHotkey` again stops local automation. It does not forget or automatically
cancel a server-side order.

To resume after stopping, changing area, or reloading the plugin:

1. Restore the required UI and permission profile.
2. Press `FullWorkflowHotkey` once.

The plugin resumes the durable workflow phase instead of creating a replacement route.

## Troubleshooting

| Problem | Action |
| --- | --- |
| Hotkey does nothing | Bind it, enable the plugin, and verify the active feature. |
| Hotkey conflict | Assign every action a unique binding. |
| Adoption finds zero orders | Verify offered currency, selected target, and visible order row. |
| Adoption finds multiple orders | Leave only one matching Chaos/Divine-to-target order. |
| Order never becomes `TimedOut` | Keep the row visible and verify the timeout was set before adoption. |
| Cancel calibration finds no control | Reload schema 6 and hover directly inside the X. |
| Calibration finds multiple controls | Reposition the cursor in the center of the X. |
| Quote unavailable | Leave authorization active; the workflow retries safely. |
| Ambiguous order or custody | Do not place another order or reset state. Reconcile it manually. |

Diagnostics are written to:

- `config/FaustusControllerLite/FaustusControllerLite/sdk-diagnostic.txt`
- `config/FaustusControllerLite/FaustusControllerLite/workflow-runtime.log`
- `config/FaustusControllerLite/FaustusControllerLite/execution-audit-<league>.jsonl`
- `config/FaustusControllerLite/FaustusControllerLite/bankroll-<league>.json`

Do not use forced reset as normal recovery. It abandons accounting but does not move items or cancel
orders.

For sell sweep behavior, see [SELL-SWEEP.md](SELL-SWEEP.md).
