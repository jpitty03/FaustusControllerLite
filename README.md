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
5. Assign the two calibration-wizard, adoption, recovery, and workflow hotkeys.
6. Seed a small test bankroll.
7. Run the six-step calibration wizard with small tracked test orders.
8. Resolve the test orders with the operational lifecycle hotkeys.
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
| `CompetingOrderWaitMinutes` | 1-3600 | Production time before a pending order becomes `TimedOut`; wizard step 5 overrides this with five seconds. |
| `EnableDirectDivineCycles` | on/off | Opts into two-competing-leg Divine-to-target-to-Divine cycles. |
| `MaximumDirectDivinePrincipal` | 1-1000 | Maximum Divine that one direct cycle may lock. |
| `EnableCompetingPriceImprovement` | on/off | Prices arbitrage competing legs one minimum unit ahead of the competing head. |
| `MinimumSaleChaos` | 1-5000 | Minimum estimated Chaos value for a sell-sweep holding. |
| `ContinuousWorkflowRetrySeconds` | 2-90 | Base delay before a no-route scan or bounded transient pre-click reprobe. |
| `MaximumQuoteAgeSeconds` | 1-3600 | Maximum accepted market and ownership age. |

Changing `StartingChaos` or `StartingDivine` does not change the current bankroll. Apply a safe
fresh-state reset to use the new values.

## Hotkeys to Assign

All hotkeys are unbound by default.

| Hotkey setting | Purpose |
| --- | --- |
| `CalibrationWizardStartHotkey` | Starts or restarts the six-step calibration wizard. |
| `CalibrationWizardNextHotkey` | Attempts the current wizard step and advances only after persistence. |
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

Bind unique keys to `CalibrationWizardStartHotkey` and `CalibrationWizardNextHotkey`. Start/restart is
accepted only with no workflow or sell-sweep authorization and no active input operation. While the
wizard has an incomplete step active, normal probe, placement, full-workflow, and sell-sweep starts
are blocked. The Complete confirmation remains visible but no longer blocks normal operation.
Adoption, cancellation, collection, and stash-transfer actions remain available because they are
needed to prepare and resolve the terminal calibration orders.

Press `CalibrationWizardStartHotkey`, then follow the expanded overlay. It shows `Step N/6`, the exact
instruction and live prerequisite, and the latest green confirmation or red error. A failed attempt
stays on the same step. `CalibrationWizardNextHotkey` while inactive only reports how to start.

1. **Wanted picker:** close the picker, hover the wanted-currency picker button, and press Next. Within
   five seconds, manually click without moving the cursor. The wizard advances only if the UI proves
   that the wanted picker opened and the calibration file was saved. Close the picker afterward.
2. **Offered picker:** repeat the same proof over the offered-currency picker button. Opening the
   wanted side is an error and does not advance. Close the picker after confirmation.
3. **Place Order button:** select a valid pair and amounts, hover Place Order, and press Next. Do not
   click the button. The normalized target and panel aspect ratio must persist before advancement.
4. **Tracked collection slot:** create or adopt a tiny order and let it reach exact
   `CompletedUncollected` with remaining wanted proceeds greater than zero. Hover the tracked row's
   left wanted-proceeds slot and press Next. Do not click the slot. Resolve and stash this test order
   before preparing the cancellation test.
5. **Tracked cancel button:** create a tiny unattractive order and adopt it while step 5 is active.
   The wizard gives this calibration order a five-second timeout without changing
   `CompetingOrderWaitMinutes`. Wait for exact plugin status `TimedOut`, hover the tracked row's
   right-edge cancel X, and press Next. Do not click the X.
6. **Canceled return slot:** use `CancelTimedOutOrderHotkey`, wait for exact `CanceledUncollected` or
   `CompletedUncollected` with remaining offered return greater than zero, hover the terminal row's
   right offered-return slot, and press Next. Do not click the slot.

Complete means all six targets were persisted, not that test-order custody is resolved. Collect and
stash every calibration test order before starting production automation.

`AdoptPendingOrderHotkey`, `CancelTimedOutOrderHotkey`, `CollectTrackedOrderHotkey`, and
`StashCollectedCurrencyHotkey` remain necessary operational lifecycle bindings. Changing area resets
only the runtime wizard and leaves saved calibration intact.

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

Before any Place Order click, transient market capture, stable-book, unavailable-quote, staging-quote,
or final live-quote failures keep authorization active and schedule a fresh coherent probe after the
configured cooldown. The budget allows 10 reprobes for one leg's complete preparation cycle. If the
next transient preparation also fails, authorization stops with the last reason. A new workflow
hotkey authorization, successful placement, or planner-complete no-route scan resets the budget.

Focus or modifier loss, manual cursor movement, permission or UI changes, area or target changes,
invalid calibration, persistence failures, canonical-state disagreement, and every uncertain or
completed click boundary still stop immediately and are never retried automatically.

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

## Competing Price Improvement

Enable `EnableCompetingPriceImprovement` to place each arbitrage competing order one minimum unit
better than the current competing head, so it fills from the front of the queue instead of the back.
The checkbox is off by default and applies to every arbitrage competing leg, in both normal
closed-cycle routes and direct Divine cycles. The sell sweep is excluded and always uses its own
quoted rates.

The improvement is the smallest one that exists: for a leg quoted as `I` offered for `O` wanted, the
planner tries offering `I + 1` for the same `O` and wanting `O - 1` for the same `I`, then keeps
whichever of the two sacrifices less. The chosen price must still stay strictly short of the
immediate price, and the improved route must still clear `MinimumProfitChaos`.

Any leg that cannot be improved safely keeps its original quoted rate. The planner evaluates the
improved and original variants of a route side by side and rejects the ones that would cross the
spread or lose money, so turning the feature on can never make a route worse than it would have been
with the checkbox off. When more than one variant survives, the one with the most improved legs is
ranked first, and the candidate status line reports `improved N/M competing legs`.

Improved legs hold a rate that no live book row carries, so staging and placement validate them by
bracket instead of by exact rate match: the rate must remain strictly better than the live competing
head and strictly short of the live immediate price. If either bound closes, the market moved and the
plugin re-probes rather than placing. Reprobing an already-improved leg does not improve it again.

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
| Calibration order never becomes `TimedOut` | Keep the row visible and verify it was adopted while wizard step 5 was active. |
| Cancel calibration finds no control | Reload schema 6 and hover directly inside the X. |
| Calibration finds multiple controls | Reposition the cursor in the center of the X. |
| Transient pre-click quote unavailable | Authorization remains active for up to 10 fresh reprobes; inspect `retry N/10` in the runtime log. |
| Retry limit exhausted | Restore stable market/UI conditions and press `FullWorkflowHotkey` for a new authorization. |
| Ambiguous order or custody | Do not place another order or reset state. Reconcile it manually. |

Diagnostics are written to:

- `config/FaustusControllerLite/FaustusControllerLite/sdk-diagnostic.txt`
- `config/FaustusControllerLite/FaustusControllerLite/workflow-runtime.log`
- `config/FaustusControllerLite/FaustusControllerLite/execution-audit-<league>.jsonl`
- `config/FaustusControllerLite/FaustusControllerLite/bankroll-<league>.json`

Do not use forced reset as normal recovery. It abandons accounting but does not move items or cancel
orders.

For sell sweep behavior, see [SELL-SWEEP.md](SELL-SWEEP.md).
