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
| `EnableMarketSweepBoard` | on/off | Draws the advisory market sweep board and allows sweep captures. |
| `SweepWhileIdle` | on/off | Lets the sweep probe one pair per idle window when nothing else is running. |
| `IdleSweepIntervalSeconds` | 5-600 | Delay between idle sweep captures. One pair per window, never a burst. |
| `SweepBoardRowCount` | 5-40 | Rows drawn on the board. |
| `VelocityHistoryDays` | 1-30 | Observation retention. Older records are pruned on load. |
| `ChurnIntervalCapMinutes` | 5-240 | Longest gap between two observations that still counts as a churn interval. Must stay **above** `SweepStalePairMinutes` or every idle-generated interval is discarded. |
| `SweepStalePairMinutes` | 5-1440 | Age at which a pair becomes eligible for an idle re-sweep. Must stay **below** `ChurnIntervalCapMinutes`. |
| `SweepDepthCap` | 1-100000 | Upper bound on tradable depth in the score, so one very deep book cannot dominate. |
| `EnableCycleHealthFilter` | on/off | Hides cycles that cannot be executed in the mode each leg was assigned, and cycles whose multiplier does not clear 1.000. The hidden count stays in the board header either way. |
| `MinCycleQueue` | 0-10000 | Smallest competing queue a **maker** cycle leg may show and still be believed. A taker leg does not queue and is not asked. |
| `MakerSpreadThresholdPercent` | 100-1000 | Smallest spread, as a percentage, that makes a leg worth queuing for instead of crossing. 200 = a 2.00x spread. Lower it to post more maker orders. |
| `MakerLegMinutesCap` | 1-240 | Longest queue drain a maker leg may be worth. Past this the leg is crossed however wide its spread. |
| `TakerLegSeconds` | 5-600 | Real click time charged to a taker leg, used in `min` and therefore in `chaos/hr`. Hand-timed at about a minute; replace it with your own median once you have run a few. |
| `TradeDuringSweep` | on/off | Lets a sweep you started trade the profitable all-taker cycles it finds, one at a time, under `MinimumProfitChaos`. Ticking it authorizes nothing on its own. |

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
| `MarketSweepHotkey` | Starts or stops a full sweep of the enabled categories. |
| `ProfitableMarketSweepHotkey` | Starts or stops a sweep of only the targets currently showing a profitable cycle. |
| `MarketSweepBoardSortHotkey` | Cycles the board sort column. |

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

## Market Sweep Board

The board is a survey, not an automation. It walks `tradables.json`, records what each book looked
like at the time, and ranks the **complete arbitrage cycles** those books make possible. It never
places an order, never writes `latest-rates.json`, and never feeds the route planner. You read the
board and set `TargetCurrency` yourself.

Enable `EnableMarketSweepBoard` to draw it. Press `MarketSweepHotkey` to sweep every enabled
category, or enable `SweepWhileIdle` to have the plugin probe the single stalest pair whenever it is
genuinely idle. The sweep always loses the exchange panel: it refuses to start while any workflow,
sell sweep, placement or collection is active, and abandons a capture already in flight the moment
one of them wants the panel.

Categories map one to one onto the keys in `tradables.json`. `SweepCurrency` is on by default and the
rest are off, because a full sweep of all 263 resolvable names is roughly 526 captures and 16-20
minutes. Chaos Orb and Divine Orb are excluded as the bankroll currencies, and a handful of names in
the file are not exchangeable at all; the board reports `resolved N/M` and lists whatever it could
not resolve so the file can be corrected. One extra capture per sweep, `Divine Orb > Chaos Orb`, is
taken first: it is the leg that closes every cycle on the board.

### Sweeping only what pays

`ProfitableMarketSweepHotkey` runs the identical sweep restricted to the targets that currently show
a profitable cycle — a healthy cycle whose multiplier clears 1.0, the same test
`EnableCycleHealthFilter` applies to the board. A dozen live opportunities is 25 captures and a
minute or two, against 526 and twenty minutes, so the rows you actually trade can be refreshed as
often as they move.

Three things worth knowing:

- **The set is read off the board, and frozen when you press.** Nothing is re-planned mid-run:
  captures landing during the sweep re-rank the board, but the queue keeps walking the targets that
  qualified at press time. With no profitable rows the hotkey refuses and says so rather than
  starting a sweep of the hub alone.
- **It cannot discover anything.** A target that turns profitable after its last observation is
  invisible to it, because it was not on the board when you pressed. Discovery stays with
  `MarketSweepHotkey` and with `SweepWhileIdle`, which keeps picking the stalest pair from the
  *whole* enabled list for exactly this reason. Leaning only on the profitable sweep narrows the
  board to what it already knew.
- **The hub leg is swept too**, as with any sweep, and that is the point rather than overhead:
  measured turnover on the closing leg needs two observations inside `ChurnIntervalCapMinutes`, and
  that leg gates the health verdict on every row.

Either hotkey stops a sweep already running; neither switches scope mid-run.

### A row is a loop, not a leg

Every row is a three-leg cycle that starts and ends on the same bankroll currency:

```
D>Vaal Orb>C>D     Divine -> Vaal Orb -> Chaos -> Divine
C>Vaal Orb>D>C     Chaos  -> Vaal Orb -> Divine -> Chaos
```

Each target quoted against **both** hubs produces both directions.

### Maker or taker, decided per leg

A *maker* leg posts at the competing head and waits for someone to cross it. A *taker* leg crosses the
immediate head right now. The maker rate is always the better of the two — each book carries only two
independent prices, and the four rates the sweep stores are those two seen from both directions, exact
reciprocals on every capture taken so far. So an all-maker loop always shows the largest `mult` on
paper.

It is on paper because it only pays if **all three** orders fill, and measured fill history sits near
60% per order. An all-maker loop that has to wait out three queues is not competing with an all-taker
loop of the same size; it is competing with three or four all-taker loops run back to back in the same
hour.

So the board decides each leg on its own. A leg is **made** only when all four of these hold:

- its spread (maker ÷ taker) is at least `MakerSpreadThresholdPercent` (default 200%, i.e. 2.00x) —
  the queue has to be worth standing in;
- it passes the health filter below;
- its expected drain is under `MakerLegMinutesCap` (default 30) — a 1.72x spread behind a queue of
  31200 draining at 83 a minute is over six hours of waiting, and no spread pays for that;
- it has tradable depth.

Otherwise it is **taken**, at `TakerLegSeconds` (default 60) of real click time. A leg with no
immediate quote at all, or an empty immediate side, cannot be taken and stays a maker leg whatever its
spread.

At the shipped threshold this takes nearly everything: on the 2026-08-13 sweep it made 11 of 810 legs.
That is the intent. The best all-taker cycle that sweep returned 3170 chaos/hour against 846 for the
best all-maker one, before any fill discount at all.

### Why cycles and not legs

Ranking legs individually — which is what this board used to do — cannot see a lopsided pair. It
shows you the easy side and never checks the return. `Tainted Armourer's Scrap` had the widest edge
on the board at 2.84x with a maker queue of **3782 in and 19 out**: easy in, stuck out. Scoring the
closed loop makes that visible, because the loop has to pay for both sides.

### The health filter

A cycle is believed only when **every** leg can actually be executed in the mode it was assigned. For a
**maker** leg that means real queue behind its price and measured turnover:

- `CompetingQueueAhead >= MinCycleQueue` (default 10) — someone is already standing behind that
  price, so it is a book rather than one troll order. The number comes from the data: the widest-spread
  trap cycles carried up-leg queues of 1, 2, 4 and 11, while cycles that survived a second sweep
  carried 14 and up. It is a setting because that boundary moves with the league.
- `turn/min > 0` on that leg — **measured**, not inferred. A leg with no second observation inside
  `ChurnIntervalCapMinutes` has no evidence that anyone traded it.

For a **taker** leg neither question applies: it does not queue, so it needs only an immediate quote
with depth behind it. Asking the maker question of a leg nobody intends to queue behind was hiding
real money — nine executable cycles on the 2026-08-13 sweep, led by a 2.01x Kalguuran Scarab loop with
immediate depth on all three legs.

Cycles that fail, and cycles whose `mult` does not clear 1.000, are hidden and counted in the header
(`133 cycles, 71 hidden as thin or unprofitable`), so nothing disappears silently. Turn
`EnableCycleHealthFilter` off to see everything; the header then reads `health filter off`.

⚠ **A first sweep shows an empty board, and that is correct.** Turnover needs two observations of a
pair, so until the hub leg `Divine>Chaos` has been swept twice, every cycle reads thin. The board says
so in place of the rows rather than leaving you to guess.

### Columns

| Column | Meaning |
| --- | --- |
| `cycle` | The loop, hub initial + target name + bridge initial + hub initial. Target names are printed in full: scarab and essence names share long prefixes — twenty essences begin `Deafening Essence of` — so an abbreviated cell would collapse a whole family into one indistinguishable string. |
| `mode` | One letter per leg, in leg order: `M` made, `T` taken. `MTT` means queue for the first leg and cross the other two. Read `mult` back through this — a row reading `MMM` is the all-maker product, and any other mode is a mixed one. |
| `mult` | The product of the rate each leg's **assigned mode** actually gets, computed exactly and converted to a decimal only for display. Above 1.000 is a profit before depth is considered. This is the number the policy would get, not the best number the book could theoretically show. |
| `take` | The all-taker product — what you can execute right now, with no waiting on any leg. `0.000` means at least one leg has no immediate quote, so an instant run of this loop is not defined. Compare it against `mult`: if they are equal the row is already `TTT`, and if `take` alone clears 1.000 the loop is instant money. |
| `lot` | How much of the starting currency the cycle can actually carry, propagated leg by leg and capped at each leg's own depth — the tradable depth on a made leg, the resting immediate depth on a taken one. Capping only the opening trade would report a gain you cannot take. |
| `gain` | `lot x (mult - 1)`, in the cycle's **own** starting currency. |
| `chaos/hr` | `gain` converted to chaos through the hub's own maker rate, divided by the cycle time. **The only column that ranks Divine-start and Chaos-start rows against each other**, and the default sort. `-` when the cycle does not profit. |
| `min` | Expected minutes for the whole loop: the sum of its three legs. A **taken** leg costs `TakerLegSeconds` of click time and nothing else. A **made** leg costs its drain estimate — the measured median fill time where there is one; failing that `queue / turn per minute`, how long the queue in front of you takes to drain; failing that head churn; failing everything, `ChurnIntervalCapMinutes`. |
| `queues` | The competing queue on each of the three legs, in order. |
| `turn` | Units of depth appearing or disappearing per minute on each of the three legs, in order. `-` for a leg observed only once. |
| `fills:no` | Measured fills and no-fills for the opening pair from `execution-audit-<league>.jsonl`. `-` means never traded. |

The last three columns keep the per-leg evidence on the row, so a ranking can be argued with rather
than trusted — the same principle the old leg board was built on. A cycle whose `mult` is spectacular
and whose `queues` read `181/4/3000` is telling you exactly where it will get stuck.

`MarketSweepBoardSortHotkey` cycles the sort column: chaos/hr, multiplier, instant multiplier, gain,
lot, cycle minutes, traded history. Sorting on instant multiplier puts the rows you can execute this
minute on top. Sorting only reorders what is on screen; it never changes what was measured. Cycle
minutes sorts *ascending* — the fastest loop leads — and **`gain` and `lot` are quoted in each row's
own starting currency**, so sorting by either compares numbers that are not in the same unit. That is
useful for "how much does this one move", but only `chaos/hr` is a profitability ranking.

### What velocity can and cannot see

The plugin never observes trades, only book snapshots. Churn is inferred from how the head rate and
the depths move between two consecutive observations of the same pair, which has two honest limits: a
long gap undercounts, because several moves collapse into one observation, and a pair observed once
has no value at all. Intervals longer than `ChurnIntervalCapMinutes` are therefore excluded rather
than averaged in, and shorter intervals are weighted more heavily.

The two signals answer different questions. Churn says how often the price is being rewritten;
turnover says how much is actually changing hands. A book quietly consumed at an
unchanged price is invisible to the first and obvious to the second, which is why the fill estimate
prefers turnover: the queue in front of a maker order is what has to drain before the order is
reached, and turnover measures that draining in the same units as the queue. It is an approximation
worth being honest about — turnover sums both sides of the direction and counts rows being *added*
the same as rows being consumed, so it runs optimistic on a book that is filling up.

**Two settings that can silently cancel each other out.** Idle sweeping revisits a pair no sooner
than `SweepStalePairMinutes`, and any interval longer than `ChurnIntervalCapMinutes` is discarded
rather than averaged in. If the stale threshold is at or above the cap, every measurement an idle
sweep produces is thrown away, the `turn` column stays `-` forever, and the health filter therefore
hides every cycle. The defaults (cap 90, stale
30) are set well apart, but a settings file saved before this was fixed keeps its old values — the
board draws a yellow warning line when it sees the two the wrong way round.

Measured fill history from the audit log is a confidence multiplier on the inferred signal, never the
ranking on its own; it exists only for pairs you have actually traded. If the audit log cannot be
read the board still ranks, on the inferred signal alone.

Observations are appended to `market-observations-<league>.jsonl` in the plugin config directory, one
line per capture, pruned to `VelocityHistoryDays` on load. A corrupt file blocks that league's sweep
and is left on disk untouched rather than being silently overwritten.

### Trading what the sweep finds

`TradeDuringSweep` lets a sweep act on what it finds instead of leaving the row on screen for you to
notice. Tick it, press either sweep hotkey, and the moment a capture puts a `TTT` cycle on the board
whose estimated profit clears `MinimumProfitChaos`, the sweep holds where it is, the ordinary full
workflow trades that one cycle through to `Stashed`, and the sweep resumes at the step it was about
to capture.

The board still decides nothing economic. All it contributes is a **target name** - the one thing
you supply by hand today. The route is planned from a fresh coherent three-market probe in exact
integers, against the same `MinimumProfitChaos`, exactly as it is when you set `TargetCurrency`
yourself. A row that has gone stale costs one probe and is refused; it cannot be traded on.

- **The checkbox alone authorizes nothing.** Trading is armed by pressing a sweep hotkey while the
  box is ticked, and it dies with that sweep. Nothing is persisted, so a reload leaves no standing
  authority to spend and no trade arms until you press again.
- **Every full-workflow precondition still applies** - all ten permissions, exclusive Lite
  ownership, foreground client, the panels visible, calibration complete, readable state,
  `ActiveFeature = Arbitrage`. It runs the same authorization the hotkey runs, not a copy of it. A
  refusal is printed with its reason on the sweep line and the sweep carries on.
- **Only `TTT` rows.** That is the claim that all three legs can be crossed this second, and it is
  what makes the rest of the row mean anything: only for a `TTT` row do `mult`, `lot` and `gain`
  describe an all-taker execution. An `MTT` row's profit includes a maker leg that this will not
  post, so it is a number for a different trade. A loop that is profitable all-taker but displayed
  as `MTT` is therefore invisible here - read the `take` column and trade it by hand.
- **Every leg is crossed.** The route is planned with no competing legs at all, so there is no queue
  wait, no `CompetingOrderWaitMinutes` timeout and no cancellation path. `EnableDirectDivineCycles`
  routes need two competing legs and so never arise here.
- **One cycle, then back to sweeping.** The workflow hands the panel back after a single route
  rather than scanning for another. Discovery belongs to the sweep.
- **One trade per target per press.** A target is spent the moment it is picked, whether the trade
  paid, refused, or found no route, so a single press cannot spend twice on the same name. The next
  press starts clean.
- **Idle sweeping never trades.** `SweepWhileIdle` runs unattended, and unattended autonomous
  spending is a much larger commitment than this checkbox.
- **Finding nothing is the ordinary case.** With no qualifying row the sweep simply keeps sweeping:
  nothing is placed and nothing is revoked.
- **It always says why.** A line under the board header carries the reason nothing was traded - no
  all-taker row, the best one under the floor, every qualifying target already spent this sweep, or
  the specific refusal. A refusal is drawn in yellow; the ordinary "nothing qualifies" note in grey.

Stopping the sweep mid-trade stops the survey only - the trade it started keeps running, and the
status line says so. Stop that with `FullWorkflowHotkey`.

If a trade stops **between its legs** - the principal already converted, the cycle not closed - the
sweep stops too, and says which workflow is parked, at which leg, and what it is holding. That state
looks settled to every other check in the plugin: no order is owed, nothing is uncollected, and the
currency is safely in your stash. Only the workflow knows the cycle is half-done, and nothing will
drive it further on its own, so the sweep will not quietly carry on around it. Finish or stop the
parked workflow with `FullWorkflowHotkey` before sweeping again.

Between its first leg and the leg that realizes Chaos, a workflow that has to replan will accept any
plan that still pays something, rather than holding out for `MinimumProfitChaos` - the principal is
already committed, and stranding it to chase the floor is the worse outcome. It will not accept a
plan that closes at a loss: that reprobes for a while, and if the book does not come back it stops
and tells you. Past the realizing leg this no longer applies, because restoring the principal is a
debt to settle at whatever it costs, not a trade to decline.

## Reading the Overlay

Two lines in the status block describe what a run is actually doing.

**`Path:`** is the trade itself, hop by hop, drawn only when there is something to draw:

```
Workflow: LegActive | leg 2/3 | authorized
Path: 2 Divine Orb > 1520 Primal Crysta~ > [720 Chaos Orb] > 4 Divine Orb
```

The brackets mark the hop in flight, so it should always agree with the leg number on the
`Workflow:` line, and it advances as each leg settles. Amounts come off the live leg plans rather than
the original quote, so a leg that refreshes onto a moved book updates its number here. Names are
clipped to 14 characters (`Primal Crysta~`) so a full cycle fits one line.

The line is cyan while a workflow is running. With no workflow but a route the planner has accepted, it
reads `Path: planned ...` in gray. With neither, it is not drawn at all.

Note that the path in `workflow-runtime.log` is deliberately *not* the same string: logs keep full,
unabbreviated names and the `->` separator. Do not expect the two to match character for character.

**`Tracked order:`** gains a `| timeout m:ss` countdown while an order is `Pending`, running against
`CompetingOrderWaitMinutes`:

```
Tracked order: Pending | order 41 | timeout 3:41
```

Only `Pending` counts down. `Armed` has no deadline yet - it is written the moment a placement matches
a real order - and terminal statuses only carry the field forward as history.

Past the deadline the line reads `| timeout expired` in yellow rather than a negative clock. That is a
real state, not a rounding artifact: the flip to `TimedOut` happens on the next lifecycle observation,
which needs a readable exchange panel, so the deadline can pass while the status has not yet caught up.
If it sits at `expired`, the bot cannot see the panel.

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
For market sweep scoping and its regression steps, see [MARKET-SWEEP.md](MARKET-SWEEP.md).
