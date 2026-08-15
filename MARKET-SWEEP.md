# Market Sweep — scoped sweeps

Operator-facing behaviour lives in `README.md` under "Market Sweep Board". This file is the
engineering record: what each phase decided, what must not regress, and how to prove it.

## Goal

Let the operator refresh the handful of cycles actually worth trading without paying for a survey of
every book. A full sweep of 263 resolvable names is 527 captures and 16-20 minutes of exchange
panel; the rows that matter move far faster than that, and most of the sweep exists to re-confirm
that a constantly unprofitable pair is still unprofitable.

## Confirmed Decisions

1. **"Profitable" is the board's own filter**: `IsHealthy && Multiplier > 1`, identical to
   `EnableCycleHealthFilter` (`FaustusControllerLite.cs`, `RefreshMarketSweepBoard`). No separate
   threshold setting - a second definition of profitable that drifts from the one on screen is worse
   than no scoping at all.
2. **The predicate is restated in the filter, not read off the filtered rows.** With
   `EnableCycleHealthFilter` off, `_boardRows` holds losing cycles too; the hotkey must mean the
   same thing regardless of a display toggle.
3. **The scope is frozen at press time.** `TryStart` copies the built steps, so observations landing
   during a run re-rank the board without growing or shrinking the queue that is executing.
4. **An empty profitable set refuses.** Falling back to a full sweep would turn a press meant to
   cost two minutes into twenty.
5. **Idle sweeping stays full-scope.** It is the only mechanism that re-observes a pair the board
   has written off, and so the only thing keeping a scoped sweep from narrowing to its own history.

## Design

### Phase 1 - profitable-scope sweep (landed)

The feature is a filtered target list handed to machinery that already existed. `MarketSweepQueue.Build`
takes `IReadOnlyList<TradableEntry>`; hand it fewer entries and step ordering, the leading hub step,
`Progress`, `Advance`, the stop toggle, the probe driver and the yield-to-trading gates are unchanged.

- `MarketSweepQueue.SelectProfitableTargets(targets, cycles)` - pure. Collects the metadata of every
  cycle passing the predicate into an ordinal set, then returns the entries of `targets` that hit it,
  **in the input list's order**. Order is load-bearing: `Build` groups a Chaos pass and a Divine pass
  off it, which is what selects the offered side twice per sweep instead of twice per pair.
- `MarketSweepScope { Full, Profitable }` and `HandleMarketSweepHotkey(MarketSweepScope)` in
  `FaustusControllerLite.cs`. Every guard ahead of the filter - running-toggle,
  `EnableMarketSweepBoard`, `TryGetHotkeyConflict`, `BuildMarketSweepPlan` - is shared verbatim.
- The empty set is refused ahead of `TryStart`, which would otherwise report "No enabled tradable
  resolved to an exchange target" and send the operator to `tradables.json` for a problem that lives
  on the board.
- `_marketSweepScope` exists only to name the sweep in the arm, stop and completion status lines. A
  lone probe with no queue behind it is an idle sample and is never named with the last armed scope.
- `ProfitableMarketSweepHotkey` - unbound like every hotkey, and registered in the
  `TryGetHotkeyConflict` binding table.

### Phase 2 - trading what the sweep finds (landed)

`TradeDuringSweep` lets a running sweep hand a target to the ordinary full workflow. The sweep is the
driver: a trade is an interrupt, not a mode change, and the sweep resumes afterwards. The advisory
wall stands - the board contributes a *target name*, exactly what the operator types by hand today,
and nothing it measured enters an economic decision. The route is planned from a fresh coherent
three-market probe in exact integers against `MinimumProfitChaos`.

Decisions, continuing the list above:

6. **The operator authorizes, never the checkbox.** Trading is armed by pressing a sweep hotkey with
   the box ticked and dies with that sweep. Nothing is persisted, so `CLAUDE.md`'s transient
   authorization invariant holds across a reload.
7. **The threshold is `MinimumProfitChaos`.** No new setting, for the reason decision 1 gave: the
   board pre-filter and the planner must apply one bar or the screen and the executor drift.
8. **Only `TTT` rows.** Mode is the gate, not profit. Only for an all-taker row do `Multiplier`,
   `Lot`, `GainStart` and `ProfitChaos` describe the execution being armed; an `MTT` row's profit
   includes a maker leg that will not be posted.
9. **The executed route is forced all-taker** (`MaximumCompetingEdges: 0`). No queue wait, no
   `CompetingOrderWaitMinutes` timeout, no cancellation path - that is what `TTT` means.
10. **One cycle per trade session.** The loop trades one route to durable `Stashed` and hands the
    panel back rather than scanning again. Discovery belongs to the sweep.
11. **Only a queued sweep trades.** Idle sweeping is unattended, and unattended autonomous spending
    is a far larger commitment than this checkbox.
12. **One trade per target per press.** The target is claimed at pick time, before anything can
    refuse it, so a failure for a reason the board cannot see is reported once instead of retried
    every window.

Implementation:

- `Domain/MarketSweepAutoTrade.cs` - pure. `SelectTradableCycle(cycles, minimumProfitChaos, declined)`
  takes `DescribeModes() == "TTT" && IsHealthy && Multiplier > 1 && Lot > 0 && ProfitChaos >= floor`
  and not declined, best by `ProfitChaos` descending, tie-broken on `Signature` ordinal - the same
  comparator shape as `MarketSweepCycleScore.Rank`, so ranking never reshuffles between refreshes.
  Applied to the unfiltered `_boardRows` (phase-1 decision 2): `EnableCycleHealthFilter` and the sort
  column are display choices and must not change what is traded.
- `TryAuthorizeFullWorkflow(origin, out failure)` and `BeginAuthorizedWorkflow()` are extracted from
  `HandleFullWorkflowHotkey`, which now calls them in sequence. This is the load-bearing choice in the
  feature: a hand-written second copy of those preconditions is a copy that silently falls behind.
  `origin` separates the two callers in the `WorkflowAuthorizationStarted` diagnostic.
- **Arming happens at a capture boundary**, in `DriveMarketSweep` ahead of step selection, never mid
  capture. The plan called for a `SuspendMarketSweepForTrade` that cancelled the probe in flight;
  `AutomatedProbeController.Cancel` only *begins* a release, so `IsRunning` can still be true on the
  next tick and would block authorization on "an input operation is active". Arming at the boundary
  makes suspension implicit instead: the queue is never stopped, the existing yield gate holds it, and
  `_marketSweepQueue.Current` resumes the step that was next. `Advance` was never called, so nothing
  is skipped.
- **Session end is observed, not signalled.** `_fullWorkflowAuthorized` is cleared from more than
  twenty places - completion, permission change, ambiguity, recovery, reset - and each means the same
  thing here, so `DriveMarketSweep` polls `_sweepTradeActive && !_fullWorkflowAuthorized` as the one
  observation point rather than hooking every site. Unresolved canonical state stops the sweep instead
  of resuming it.
- `EndAuthorizedWorkflow(reason)` ends a loop that has finished, as distinct from
  `StopFullWorkflowLocal`, which records its reason as the current failure - correct for a stop and
  wrong for a finish. It is called from `StartNewWorkflowScan`, the only boundary at which nothing is
  owed and nothing is in flight.
- `MaximumCompetingEdgesForCurrentRoute` is read by the planner request, `PlacementPreparationToken`
  and `ValidatePlacementPreparation` alike, so a token planned all-taker cannot be placed under the
  ordinary cap. `RoutePlannerRequest.DefaultMaximumCompetingEdges` names the 2026-07-31 scope override
  so the deviating caller can say "the ordinary cap" without restating the number.
- A sticky note under the board header carries why nothing armed, because `_marketSweepStatus` is
  overwritten by the next capture within a tick - a refusal written there is invisible in practice.
  The three silent cases are named separately (no all-taker row, best row under the floor, every
  qualifying target already spent), and an unresolved-state refusal names the blocking thing: a
  persisted workflow is finished with the full-workflow hotkey, an unresolved order with the lifecycle
  hotkeys, unreadable state with a forced reset. Added after the first live run traded nothing and the
  reason - a workflow left active at leg 2 since 2026-08-12 - was recoverable only from
  `workflow-runtime.log`.
- `StopMarketSweep` reports that a trade it started is still running. Stopping the survey does not
  stop the trade, and an operator who thinks the plugin is idle while an order is live is the failure
  mode worth spending a status line on.

## Invariants To Preserve

- A scoped sweep is advisory, exactly like a full one: it appends to `market-observations-<league>.jsonl`
  and writes nothing else. No bankroll state, no `latest-rates.json`, no order.
- A sweep-owned trade changes nothing about how a route is judged. The board supplies a target name;
  every number comes from a fresh coherent probe in exact integers, under the same
  `MinimumProfitChaos` a hand-set target gets.
- Autonomous trading is authorized by a sweep press and dies with that sweep. It is never persisted,
  and the auto path never bypasses a single full-workflow precondition.
- A sweep-owned trade is armed only between captures, never during one, and never stops the queue.
- The hub step `Divine>Chaos` leads every sweep at every scope. It is the leg that closes each cycle,
  no target pass can produce it, and the health verdict on every row depends on its measured turnover
  (two observations inside `ChurnIntervalCapMinutes`).
- Scope never changes mid-run. Either sweep hotkey stops a running sweep.
- Idle sweeping builds the complete step list, whatever the last scoped sweep looked at.
- Every hotkey appears in the `TryGetHotkeyConflict` binding table.

## Regression Testing

Each phase appends its own block here when it lands. Later phases do not replace earlier blocks -
run every block that has landed, in order, since a later phase can regress an earlier one.

Bench commands, run from the repo:

```
cd Plugins/Source/FaustusControllerLite
dotnet build FaustusControllerLite.csproj --no-restore

cd ../../Tests/FaustusControllerLite.Tests
dotnet run --project FaustusControllerLite.Tests.csproj --no-restore
```

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)` and `172/172 tests passed`. Any warning is a
regression - the project has been kept warning-clean, so a new one means something was silently
reinterpreted.

### Phase 1 - profitable-scope sweep

Bench, no game required:

1. Build and test as above. Confirm `market sweep profitable scope selects only board targets`
   appears and passes, alongside the pre-existing `market sweep queue is offered grouped and single
   entry`.
2. Mutation - loosen the threshold: change `cycle.Multiplier > 1` to `>= 1` in
   `MarketSweepQueue.SelectProfitableTargets`. The new test must fail with `Expected 2, got 3` (the
   break-even fixture is admitted). Restore.
3. Mutation - drop the health conjunct: reduce the predicate to `cycle.Multiplier > 1`. The same test
   must fail the same way (the wide-but-thin fixture is admitted). Restore.
4. Mutation - conflict table: delete the
   `Binding(nameof(Settings.ProfitableMarketSweepHotkey), ...)` line, then verify in game at step 10
   below that a duplicate binding is no longer refused. Restore. A hotkey absent from that table is
   a silent double-fire.

In game. Preconditions: exchange panel open, `EnableMarketSweepBoard` on, `MarketSweepHotkey` and
`ProfitableMarketSweepHotkey` bound to **different** keys.

5. On a cold board, press `ProfitableMarketSweepHotkey` first. Status must read `No profitable cycle
   is on the board yet; run a full sweep first.`, and no capture may start - the progress counter
   stays absent and the cursor does not move.
6. Press `MarketSweepHotkey`. Status reads `Market sweep armed: N captures queued (resolved ...)`.
   Let it run until the board has rows; stopping it early is fine.
7. Press `ProfitableMarketSweepHotkey`. Status reads `Profitable sweep armed: N captures over M
   profitable targets`, with `N == M * 2 + 1`. Cross-check `M` against the count of distinct target
   names among the board rows showing a positive `chaos/hr`.
8. The first step must be the hub `Divine>Chaos` pair, same as a full sweep.
9. Every subsequent `Sweeping <pair>` line must name a target that was on the board when you pressed.
   A name that was not is the filter failing open.
10. Bind both sweep hotkeys to the **same** key and press it. It must refuse with the conflict
    message rather than starting a sweep. Restore distinct bindings.
11. Press `ProfitableMarketSweepHotkey` mid-run: it stops, and the status reads `Profitable sweep
    stopped by hotkey.` Start another and press `MarketSweepHotkey` instead: it also stops, with the
    same scope name, rather than switching to a full sweep.
12. Let a scoped sweep run to the end. Status reads `Profitable sweep complete: X recorded, Y
    skipped.`
13. Start a scoped sweep, then start the full workflow. The sweep must yield with `Market sweep
    yielded the exchange panel to trading.`
14. With `SweepWhileIdle` on and no sweep queued, confirm idle sampling still names pairs that are
    *not* on the board. Idle sweeping is full-scope; if it only ever samples profitable targets, the
    board can never rediscover anything.
15. Confirm `market-observations-<league>.jsonl` grew by the recorded-capture count, and that
    `latest-rates.json` and `bankroll-<league>.json` are byte-identical either side of the sweep.

### Phase 2 - trading what the sweep finds

Bench, no game required:

1. Build and test as above. Confirm `market sweep auto trade selects only executable cycles` and
   `market sweep auto trade ranks by profit with ordinal tie break` appear and pass.
2. Mutation - relax the mode gate: change `cycle.DescribeModes() == "TTT"` to `!= "MMM"` in
   `MarketSweepAutoTrade.IsTradable`. Expect `FAIL market sweep auto trade selects only executable
   cycles: Expected Metadata/Items/Currency/CurrencyKept, got Metadata/Items/Currency/CurrencyMaker`
   and `170/171`. Restore. This is the mutation that matters most: an `MTT` row's `ProfitChaos` is
   not an all-taker number. (Run 2026-08-14.)
3. Mutation - exclusive floor: change `cycle.ProfitChaos >= minimumProfitChaos` to `>`. Expect `FAIL
   market sweep auto trade selects only executable cycles: A cycle exactly at the floor must be
   accepted.` and `170/171`. Restore. (Run 2026-08-14.)
4. Mutation - drop the `MaximumCompetingEdges: 0` argument from the planner request. **No unit test
   catches this.** It must be caught in game at step 10, which is why step 10 exists. Restore.

In game. Preconditions: every full-workflow permission on, `ActiveFeature = Arbitrage`, exchange +
stash + inventory visible, calibration complete, `EnableMarketSweepBoard` on, `TradeDuringSweep`
**off** to begin, and a bankroll you are willing to lose.

Throughout, the board carries a line under its header naming why nothing was traded. It is the first
thing to read at every step below: yellow is a refusal you can act on, grey is "nothing qualified".

5. Box off, run a sweep that puts a profitable `TTT` row on the board. Nothing is placed - this is
   the proof that the checkbox is the only thing that arms it.
6. Tick the box with one workflow permission *disabled*, then sweep. On a qualifying row the status
   must read `<cycle> not traded: <permission reason>` and the sweep must keep going - no placement,
   no authorization.
7. Full permissions, box on, sweep running. On the first qualifying row the status names the trade
   (`Trading D>Vaal Orb>C>D: 214 chaos estimated, sweep held at ...`) and the workflow probes that
   target. Confirm `WorkflowAuthorizationStarted` records `Market sweep (<cycle>)` as its origin.
8. Confirm the sweep progress counter does **not** move while the trade runs, and that the pair it
   was about to capture is the pair captured on resume. A jump means the abandoned step was skipped.
9. Let the cycle finish. On durable `Stashed` the status reads `Sweep trade finished: <cycle>.`, the
   failure line stays `None`, authorization is revoked, and sweeping resumes.
10. **Watch the placed orders.** Every leg must be an immediate order that fills on placement. A
    pending order resting in the queue means `MaximumCompetingEdges: 0` never reached the planner.
11. Confirm the workflow does not start a second route on its own - it must hand back after one.
12. Raise `MinimumProfitChaos` above every board row and sweep again. No trade arms and the sweep
    runs to completion normally.
13. Force a planner disagreement: let a row qualify whose books have since moved. The status reads
    `Sweep trade for <cycle> placed nothing: no route cleared the planner from a fresh coherent
    probe.`, the sweep resumes, and that target is not re-picked.
14. After any trade, confirm the same target is not armed again for the rest of that press even when
    its row is still the best on the board, and that a fresh press will consider it again.
15. Press the sweep hotkey mid-trade. The sweep stops and says the trade it started is still running;
    the trade must **not** be orphaned - the tracked order is still tracked and resolvable.
16. `SweepWhileIdle` on, no sweep queued, a qualifying row on the board: nothing arms. Idle sampling
    is unattended and must never trade.
17. Stop the sweep and reload the plugin. Nothing is authorized and no trade arms until a fresh sweep
    press - the authorization did not persist.
18. Switch `ActiveFeature` to `SellSweep` and sweep. The refusal must name the feature mode rather
    than reading as a permission problem.
19. Confirm `market-observations-<league>.jsonl` grew only by sweep captures, and that every bankroll
    movement in `bankroll-<league>.json` is accounted for by the trade the overlay named.

### Phase 2a - surviving a trade that stops between its legs

On 2026-08-14 a sweep-owned trade bought 149 Ambush Scarabs for 3 Divine, failed to plan its selling
leg, dropped authorization without recording why, and the sweep announced the trade "finished" and
carried on capturing pairs for another minute. The scarabs were never at risk - they were collected
and stashed, and canonical state was clean - but nothing was going to move them, and because
`TryArmSweepTrade` refuses while a workflow is active, no later trade could arm either. Five changes
came out of it, and this block is what proves they hold.

The shape to keep in mind: a workflow is *parked*, not broken, when it sits at `ReadyForLeg` with
its principal in the intermediate currency and no authorization. Canonical state calls that settled
because nothing is owed and nothing is uncollected, so every "is anything outstanding" check says
no. Only the workflow itself knows the cycle is half-done.

Bench, no game required:

1. Build and test as above. Confirm `workflow refuses a losing mid cycle replan before realization`
   appears and passes alongside `workflow restoration retries unavailable quotes`.
2. Mutation - drop `workflow.CurrentLegIndex <= workflow.ChaosRealizationLegIndex` from the new guard
   in `TryRefreshClosedRemainingPlan`. Expect `FAIL workflow restoration retries unavailable quotes:
   Expected Refreshed, got RetryableUnavailable` and `171/172`. Restore. This is the mutation that
   matters most: past the realization leg the Chaos is already in hand against an outstanding
   principal debt, and refusing an adverse restoration does not avoid a loss - it leaves the
   principal unrestored forever. (Run 2026-08-14.)
3. Mutation - raise the mid-cycle floor: change `profit < 0` to `profit < minimumProfitChaos` in the
   same guard. Expect `FAIL workflow refuses a losing mid cycle replan before realization: Expected
   Refreshed, got RetryableUnavailable` and `171/172`. Restore. A committed principal is not an
   opportunity to hold out for a better price on. (Run 2026-08-14.)
4. Mutations - the other four fixes are plugin-side and have no unit coverage. They are caught in
   game at steps 6, 7, 8 and 9 below, which is why those steps exist.

In game. Same preconditions as Phase 2, plus `TradeDuringSweep` **on**.

5. Run a normal sweep-owned trade to completion. Everything in Phase 2 steps 7-11 must still hold -
   these fixes must be invisible when nothing goes wrong.
6. **The parked-workflow stop.** Interrupt a trade between its legs so authorization drops with the
   workflow still active - the easiest reliable way is to disable a workflow permission during
   leg 1, or to close the stash while leg 2 is being prepared. Required: the sweep **stops**, and
   the sweep status names the workflow id, which leg of how many, and what it is holding, ending
   with `Finish or stop it with the full-workflow hotkey before sweeping again.` The old behaviour
   said `<cycle> finished; resuming <scope> at N/M` and kept capturing; if you see that, the check in
   `EndSweepTradeSession` is gone.
7. **The named reason.** For that same abandonment, open `workflow-runtime.log` and find the
   `ContinuousAuthorizationRevoked` event at the moment authorization dropped. It must exist, and its
   text must name the workflow, the leg, and the actual preparation failure. `Candidate was
   recalculated.` is the failure signature of the old bug: it means the staging controller's routine
   invalidation overwrote the real reason. Nothing else in the log distinguished the two.
8. **The transient ownership read.** Trigger the failure that started this: open the currency picker
   or move the mouse across the inventory as leg 2 is being prepared, so the live ownership read is
   unstable for a tick. Required: the status reads `Transient pre-click market failure: ...
   Reprobing in Ns; retry 1/10.` and the workflow continues once the read settles. A single hard stop
   here is the pre-fix behaviour - it is what stranded the scarabs.
9. **The losing replan.** Harder to force deliberately; take it opportunistically on a thin book.
   When the selling leg can only clear part of the holding and buying the principal back would cost
   more than the sale realizes, the status must name the negative profit and reprobe rather than
   place. Watch that the reprobe budget is bounded: after ten it must revoke with
   `Transient workflow preparation retry limit exhausted after 10/10 reprobes: <reason>` and the
   sweep must stop per step 6, not resume.
10. Confirm across all of the above that no bankroll movement happened that the overlay did not name,
    and that a parked workflow is still resolvable by hand with `FullWorkflowHotkey` afterwards.

## Open Risks

- A scoped sweep inherits whatever the board believed at press time, including a stale row that
  turned unprofitable hours ago. That row is re-observed and drops off after the sweep, which is the
  correct outcome but costs two captures to learn.
- Nothing prompts the operator to run a full sweep periodically. Leaning only on the scoped hotkey
  narrows the board to its own history; `SweepWhileIdle` mitigates this only while the plugin is
  actually idle.
- Phase 2: a loop that is profitable all-taker but displayed as `MTT` is invisible to auto trading.
  `ChooseMode` assigns `Make` whenever a leg's spread clears `MakerSpreadThresholdPercent`, and the
  `take` column shows exactly these rows. Selecting one needs an all-taker `Lot` and gain the board
  does not currently compute. Phase 3 candidate.
- Phase 2: a stale row costs a probe and a panel round trip before the planner refuses it. The cost is
  bounded and the declined set stops it repeating within a press, but a board left unswept for hours
  will spend probes learning it is out of date.
- Phase 2: the declined set is cleared on every sweep press, which is deliberate - a press is the
  operator saying "go" again - but it does mean a target that keeps failing is retried once per press
  for as long as its row survives.
- Phase 2a: a parked workflow stops the sweep, which is the right call but is also a full stop on
  unattended progress. An operator who arms a sweep and walks away can return to one abandoned cycle
  and a survey that halted at the same moment. There is no automatic resumption and deliberately so -
  resuming a half-executed cycle without a human looking at it is the decision this whole feature is
  not authorized to make.
- Phase 2a: the mid-cycle floor is zero, not `MinimumProfitChaos`. Between the first leg and the
  realization leg a plan that clears one Chaos is accepted, because the principal is already
  committed and the alternative is stranding it. A cycle can therefore finish having earned far less
  than the floor that admitted it - the floor gates entry, not completion.
- Phase 2a: making a mid-cycle inventory-capacity refusal retryable trades a hard stop for up to ten
  reprobes. If the real cause is persistent - an inventory genuinely full of the leg currency - the
  operator waits out the budget before being told. That is still better than the stop it replaced,
  which said `Candidate was recalculated.` and left the position parked, but it is not free.
- Phase 2a: none of the plugin-side fixes have unit coverage. `EndSweepTradeSession`, the revocation
  reason, the spend-cap retryability and the staging-invalidation guard are all verified only by
  steps 6-9 above, in game, by hand.
