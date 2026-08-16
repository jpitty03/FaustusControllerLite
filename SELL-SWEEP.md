# Sell Sweep

Sell an entire holding of one item kind (initially Scarabs) from the visible stash tab, one order
at a time. The sweep captures one execution strategy when it is planned: **Most Currency** uses
competing rates, while **Fastest Fill (Market Rate)** uses immediate head rates. Holdings whose
whole-lot order would not clear the minimum Chaos-equivalent floor are skipped.

## Goal

Given the Currency Exchange, the inventory, and a stash tab of the selected kind all open:

1. Enumerate every distinct item in the visible stash tab.
2. For each, probe the three markets and choose the best sellable one.
3. Place one order at the selected strategy's exact rate.
4. Wait for the fill, collect the proceeds, return leftovers to the stash.
5. Advance to the next item.

## Confirmed Decisions

- **Concurrency: sequential.** One order in flight; the queue drains serially. The queue is shaped
  so the in-flight cap can later rise above one without redesign.
- **Order capacity: the exchange holds 10 orders.** This is a game constant, not an SDK read. The
  live dump shows 3 in use. Placement is refused when the panel already holds 10.
- **Minimum floor applies to the placed order**, not to total holdings: placed lots times rate,
   converted to Chaos-equivalent, must clear the floor. Whole-lot remainder does not count.
- **Most Currency is the default.** It selects only competing target and Divine/Chaos edges and
  keeps the existing resting-order behavior.
- **Fastest Fill never falls back.** It selects only immediate target and Divine/Chaos edges. A
  positive readable head proves the rate exists, but visible immediate depth does not cap sizing:
  every whole lot in the holding is placed at that aggressive limit.
- **A competing head must be a believable market.** Most Currency prices at the head of the
  competing ladder and then waits behind it, so the head has to be reachable before the holding is
  sized against it. A competing market is skipped when its queue is below `MinCompetingQueue` or its
  rate exceeds `MaxCompetingSpread` times the same-direction immediate rate. Rejection is per market,
  not per candidate: a trolled Divine book simply loses to a healthy Chaos one. Fastest Fill is
  untouched - it crosses against depth that is already resting.
- **Unexpected Fastest remainder stays pending.** A partial immediate fill uses the normal
  `CompetingOrderWaitMinutes` deadline and the existing cancellation, collection, and stash flow.
- **Offered items are drawn from the stash automatically.** The exchange pulls offered currency
  inventory-first, then stash. No stash-to-inventory pull step is required; only the return path
  for proceeds and leftovers.

## Verified Findings

Scarabs are already first-class targets. `Probing/CurrencyCatalogue.cs:134-141` classifies purely by
metadata prefix, and the live dump carries 124 `kind=Scarab` entries among 574 supported targets.
Chaos and Divine are deliberately excluded from `SupportedTargets` (`CurrencyCatalogue.cs:69-91`)
because they are the numeraire.

One capture yields both books in both directions. `Probing/MarketCapture.cs:159-179` emits four
`DirectedExchangeEdge` values per pair: immediate and competing, selected and reverse. The sell
direction therefore already exists in every capture the current probe takes.

Whole-lot arithmetic is exact and already implemented. `Domain/Rational.cs:56-73`
(`ConvertWholeLots`) is precisely the 15-scarabs-cannot-fill-a-50-scarab-lot test, and
`Domain/RoutePlanner.cs:275` already rejects a zero-lot conversion with `NoWholeLot`.

Place, wait, collect, and stash are built for one order. Staging, placement, lifecycle polling,
capacity-batched collection, and the Ctrl+right-click inventory-to-stash transfer all exist
and are verified.

## Blockers

### 1. The visible tab type is hard-gated to CurrencyStash (cleared in Phase 2)

`Orders/InventoryStashTransferController.cs:448` compares `visibleStash.InvType.ToString()` against
the literal `CurrencyStash`. `TryReadSnapshot` is the single chokepoint for every collection and
every stash transfer. The live dump reports `type=FragmentStash`, so with the scarab tab open the
existing workflow fails before it starts.

### 2. Custody policy inverts when a non-Currency tab is visible (cleared in Phase 2)

`Orders/TrackedOrderStatus.cs:31-45` resolves custody from metadata alone:

| Metadata prefix | Mode | Proof |
| --- | --- | --- |
| `Metadata/Items/Currency/` | `VisibleCurrencyStashExact` | visible stash rises by exactly N |
| `Metadata/Items/Scarabs/` | `AffinityAggregate` | visible stash is unchanged |

That mapping silently assumes the Currency tab is the visible one. Selling scarabs with the
Fragment tab visible reverses both roles:

| Asset | Destination | Correct mode with Fragment tab visible |
| --- | --- | --- |
| Leftover scarabs | the visible Fragment tab | exact, visible stash rises by N |
| Chaos/Divine proceeds | the Currency tab by affinity | aggregate, visible stash unchanged |

Custody must therefore be a function of metadata *and* visible tab type, not metadata alone. This
generalization preserves every existing proof: with the Currency tab visible the table is
unchanged.

### 3. No order-slot capacity awareness

`panel.Orders` is only ever read for identity baselines and model/element parallelism assertions.
There is no max-orders field anywhere in the SDK dump; only `orders=3 elements=3` with no
denominator. The cap of 10 is supplied as a constant.

### 4. The system is structurally single-order

`Domain/BankrollState.cs:32` persists one nullable `TrackedOrder`, one file per league, and every
controller refuses a second. Sequential-first scope avoids changing this.

### 5. Stash item geometry is never read

`Probing/SdkDiagnosticProbe.cs:123-147` iterates visible stash items and has `GetClientRect`
available on each, but prints only per-metadata totals. No per-item rects or per-slot stack sizes
are captured anywhere.

## Economics

For a holding of N units of item S, with captured edges for S-to-Chaos, S-to-Divine, and the
Chaos/Divine pair:

1. Map the captured sweep mode to one intent: competing for Most Currency, immediate for Fastest
   Fill. Select only that intent for both candidate proceeds and the Divine-to-Chaos benchmark.
2. `ConvertWholeLots(N)` gives whole lots, `InputSpent`, `Output`, and the remainder. Fastest Fill
   first requires positive immediate depth, then deliberately sizes against all `N` rather than
   capping input by displayed depth.
3. Reject when `InputSpent` is zero: a single lot cannot be filled. This is the case of holding 15
   units when the lot size is 50.
4. Convert `Output` to Chaos-equivalent. Chaos proceeds count directly; Divine proceeds are valued
   through the mode-matching Chaos/Divine rate.
5. Choose the edge with the greater Chaos-equivalent proceeds.
6. Reject the item when the chosen proceeds fall below the configured floor.

All arithmetic stays on `Rational` and `long` with `checked` semantics. No floating point.

## Design

### Phase 1 - read-only

No input of any kind. Establishes the scan and the economics before anything clicks.

- `Stash/StashTabScan.cs` reads the visible tab: `InvType`, per-metadata totals, per-item rects and
  stack sizes.
- `Domain/SellCandidate.cs` is pure and testable. Holdings plus captured edges in, chosen market and
  accept/reject reason out. Mirrors the reject-with-a-named-reason shape of `RoutePlanner`.
- The SDK diagnostic gains a sell-queue section: each item, quantity, both candidate rates, chosen
  market, Chaos-equivalent proceeds, and the accept or skip reason.

Acceptance: the dump lists a plausible queue for the open scarab tab, and every skip reason is
explicable by hand. No `Allow*` setting is enabled.

Status: landed. `Probing/StashTabScan.cs` reads the visible tab, `Domain/SellCandidate.cs` holds
`FaustusSellPlanner.Evaluate` with `SellRejectionReason` covering every refusal, and
`SdkDiagnosticProbe` renders the `Sell queue` section from the persisted captures. Eight planner
tests cover best-proceeds selection, whole-lot sizing, the minimum-sale floor, the required Divine
benchmark, unusable and stale quotes, immediate-book exclusion, freshest-capture preference, and
request validation. Plugin builds clean; suite is 91/91. Still read-only - no input path touches it.

### Phase 2 - custody generalization (landed)

- `StashCustodyPolicy.TryResolve` takes the visible tab type alongside the metadata. Home tab
  visible means exact; any other readable custody tab means aggregate. The old metadata-only
  overload is kept and now defined as `TryResolve(metadata, CurrencyTabType)`, so every legacy
  currency path reproduces its original mode by construction.
- `TryReadSnapshot` accepts any custody tab type (`CurrencyStash` or `FragmentStash`) instead of
  the literal `CurrencyStash`, and records the observed `VisibleTabType` on the snapshot.
- Custody is now resolved *after* the snapshot, since it is a property of the tab that is actually
  visible. `Arm` gates on `IsSupported` and derives the mode itself; the caller reads it back from
  the new `CustodyMode` property to describe the run.
- Persisted intents cannot re-derive the arm-time tab, so `TrackedOrderStore` and `BankrollStore`
  validate with `IsResolvableCustody` (asset supported, mode in range). The load-bearing check
  stays at recovery, where `ClassifyRecovery` re-derives the mode from the live visible tab and a
  mismatch forces `Ambiguous`.

Acceptance: the existing currency workflow runs once with no behavioural change.

### Phase 2.5 - mutually exclusive feature mode gate (landed)

The two workflows are separate features, not two entry points into one. `Domain/FeatureMode.cs`
holds the whole rule:

- `FeatureMode` is `Arbitrage` or `SellSweep`. `FeatureModeGate.IsInScope` maps every action to a
  feature, and `Shared` actions (calibration, diagnostics, rate probing, recovery) stay in scope
  for both, because they are infrastructure rather than workflow.
- `DescribeRefusal` produces the message the refused hotkey reports, so a dead hotkey always says
  *why* it is dead instead of silently doing nothing.
- `TrySwitch` refuses to change the active feature while either side holds an unresolved order or
  a live workflow. This is the load-bearing rule: switching away from a workflow that still owns a
  placed order would strand it under a feature whose actions are all refused.

Wiring: a `Settings.ActiveFeature` dropdown (`ListNode`, "Arbitrage" / "Sell Sweep"), observed
each `Tick` by `ObserveFeatureSelection`, which snaps the selector back to the committed value on
a refused switch and reports the reason. `Initialise` adopts the persisted value *without* gating,
since the saved mode is the one that produced whatever unresolved state is about to load. Every
arbitrage hotkey now begins with `RefusesFeatureScope(...)`; shared hotkeys do not.

Acceptance: with Sell Sweep active, every arbitrage hotkey refuses with a stated reason; the
calibration and diagnostic hotkeys still work; a switch attempt with a tracked order refuses and
the dropdown snaps back.

### Phase 3 - the sweep state machine (landed, single live order)

`Domain/SellSweep.cs` is pure and holds no game handles, so the whole ordering and safety story is
testable without the client. It is deliberately *not* an input controller - it decides, and Phase 4
dispatches. This mirrors `WorkflowCoordinator`/`WorkflowDirectiveKind`, which is the shape the
arbitrage side already proved out.

`SellSweepPlanner.Build` turns per-holding evaluations into an ordered plan. Accepted candidates
sort by realizable proceeds descending, ties broken by metadata ordinal, so the same stash always
produces the same plan and an interruption costs the least remaining value. Rejected holdings are
not silently dropped - `DescribeSkipped` reports each one with its rejection reason, so the operator
can tell "skipped, below your minimum" from "missed".

`SellSweepCoordinator.Decide(sweep, tracked)` emits one directive per tick:

| Phase | Tracked order | Directive |
| --- | --- | --- |
| `ReadyForCandidate` | none/resolved, no prepared quote | `RescanAndPlanCurrentCandidate` |
| `ReadyForCandidate` | none/resolved, prepared quote | `PlaceCurrentCandidate` |
| `ReadyForCandidate` | **unresolved** | `ManualReconciliationRequired` |
| `OrderLive` | matching attempt | mapped from `TrackedOrderStatus` |
| `OrderLive` | missing/foreign attempt | `ManualReconciliationRequired` |

Three properties are load-bearing, and each is enforced structurally rather than by a flag:

1. **One order at a time.** `PlaceCurrentCandidate` is reachable only from `ReadyForCandidate`,
   which is reachable only once the previous order has left the tracked state entirely. There is no
   code path that authorizes a placement while an order is outstanding, and `MarkPlaced` throws if
   called from `OrderLive`. An unresolved order that the sweep is *not* positioned on stops the
   sweep rather than placing alongside it.
2. **Advance on `Stashed`, not `Collected`.** Proceeds must be provably in the stash before the
   next candidate starts. Advancing at `Collected` would leave currency in the inventory while the
   next candidate stages against it.
3. **Re-plan before every placement.** `Advance` clears `PreparedSignature`, so a candidate can
   never inherit the previous candidate's quote; rates move between orders.

Ambiguity stops the whole sweep (`MarkAmbiguous`) rather than skipping one candidate, for the same
reason the arbitrage workflow does: an unprovable custody boundary means the plugin no longer knows
what it owns, so no further input is safe.

### Phase 4 - settings and wiring

- Sell-kind selector, execution-strategy selector, and `MinimumSaleChaos` in the Strategy category.
- `AllowSellSweep` threaded through all four `PermissionSnapshot` members plus
  `AnyLiteInputPermissionEnabled` and `DisableLiteInputPermissions`.
- A hotkey created by `CreateUnboundHotkey()`, dispatched in `Tick()`, and registered in the
  conflict table so it cannot silently escape detection.
- A `MaxExchangeOrders` constant of 10 refusing placement at capacity.

### Phase 5 - just-in-time probing and the sweep driver (landed, pricing only)

Phases 1-4 produced a sweep that plans nothing and then does nothing, for two independent
reasons found in the `-- Sell queue` diagnostic (101 holdings scanned, 101 skipped, 0 accepted):

- **99 x `MissingEdge`.** `TryBuildSellSweepPlan` prices the whole tab up front from
  `_rateStore.Captures`, but that book only ever holds pairs an operator manually probed. It held
  52 edges covering 2 scarab types out of 101 held.
- **2 x `SessionMismatch`.** The two priced types were still refused, because
  `SellCandidate.cs:221` gates every edge on `edge.SessionId == request.SessionId` and the sweep
  passes `_manualProbeSessionId`, which is regenerated on area change, target change and forced
  reset, and is overwritten by each probe. Stored rates are structurally unusable across sessions.

Together those mean the pre-Phase-5 sweep could only ever price the single type probed in the
current session, and only until the next area change. Planning was never the bug; **the sweep
never probes.** There is also no driver: nothing in the plugin consumes
`SellSweepCoordinator.Decide`, so even a non-empty plan would sit idle.

Phase 5 makes the sweep drive its own probing, one candidate at a time.

**Planning stops pricing.** `SellSweepPlanner.BuildQueue` replaces the up-front
`Build(evaluations)` call for sweeps. It takes the recognised holdings and emits an *unpriced*
queue: `PlannedProceedsChaos = 0` and `PlannedSignature = ""`. The empty signature is what makes
`Decide` return `RescanAndPlanCurrentCandidate` (`SellSweep.cs:198`) - the existing state machine
already models "this candidate is not priced yet", so no new phase is needed.

**Ordering is by stack quantity descending, then metadata ordinal.** Proceeds are unknown before
probing, so true value ranking is not available at plan time. Quantity is a deterministic,
testable proxy; the tie-break on metadata keeps two equal stacks from reordering between runs.
This is a deliberate accuracy trade: a large stack of junk is probed before a small stack of gold.

**The per-candidate cycle**, driven from `ReadyForCandidate`:

1. `RescanAndPlanCurrentCandidate` starts an automated probe for *that candidate's* target.
   `StartAutomatedProbe` is currently hard-wired to `Settings.TargetCurrency`, so it gains a
   parameterized entry point taking an explicit `CurrencyTargetDescriptor`.
2. The probe publishes its captures and sets `_manualProbeSessionId` to its own session id, so the
   session gate that refused everything in Phase 1-4 now passes by construction.
3. The candidate alone is re-evaluated through `FaustusSellPlanner.Evaluate` against the fresh
   edges, applying `MinimumSaleChaos` and the whole-lot rule.
4. Accepted -> `MarkPrepared(signature)`; the next tick yields `PlaceCurrentCandidate`.
   Rejected -> `Advance(Skipped, 0, reason)` and the sweep moves to the next candidate without
   ever placing.

**What Phase 5 must not relax.** The probe is reachable only from `ReadyForCandidate`, which is
reachable only once the previous order has left tracked state entirely, so probing can never race
a live order. An area change mid-sweep still rotates `_manualProbeSessionId` and invalidates
custody assumptions, so it stops the sweep rather than silently re-probing into a new instance.

### Phase 6 - sweep placement and collection

Phase 5 left a sweep that prices a candidate and then stops: `Decide` returns
`PlaceCurrentCandidate` and `TickSellSweep` answers with
`"next step '{directive}' is not automated yet."`. Phase 6 makes the directives act. Three
findings shape it, all verified against the current source rather than assumed.

**Finding 1 - the permission records are ready, the call sites are not.** Every
`*InputPermissions` record already carries `SellSweep` / `SweepAuthorized` and defers to
`CoordinatorOwnership`, which refuses two authorized coordinators at once
(`SingleLegPlacementController.cs:24-47`, `SingleLegStagingController.cs:36-67`). But all
`*InputPermissions.From(...)` call sites in `FaustusControllerLite.cs` pass only
`_fullWorkflowAuthorized`. `Owner.Authorized` is therefore false for a sweep, and while
`AllowSellSweep` is on `Owner.None` is false too - so *every* sweep-driven controller refuses to
start. Phase 6 adds a single `_sweepAuthorized` field and threads it through those call sites as
the third argument.

**Finding 2 - the arbitrage placement chain cannot be reused as-is.**
`StartPreparedPlacement` gates on `ValidatePlacementPreparation`, which is bound to
`_placementToken`, `Settings.TargetCurrencyMetadata`, `Settings.MinimumProfitChaos` and
`GetCurrentPlacementLeg()` (itself `_workflowPreparedLeg` or `_selectedCandidate.Legs[0]`). A
sweep leg is none of those things. Per the separate-features rule the sweep gets its own
preparation token and its own validator, and reuses only the *controllers* -
`SingleLegStagingController`, `SingleLegPlacementController`, `TrackedOrderCollectionController`,
`CanceledReturnCollectionController`, `TrackedOrderCancellationController`,
`InventoryStashTransferController`. The sweep validator re-checks the candidate's own
quote (edge, rate, input, output) against a same-tick re-read, not the arbitrage plan.

**Finding 3 (blocker) - the bankroll cannot reserve a swept stack.** Arming goes
`PersistTrackedOrder(Armed)` -> `TryMoveAvailableToReserved` -> `BankrollAccounting.TryReserve`
-> `TryReadAvailable`, which returns **false** for any metadata that is neither chaos, divine,
nor already present in `NonCoreBalances` (`BankrollAccounting.cs:118-130`). The bankroll seeds
chaos and divine only (`BankrollState.cs:36-44`); non-core entries appear solely as arbitrage
workflow output. So arming a scarab sell order rejects with *"Arming requires no unresolved
order and N available <metadata> to reserve"*, and the failure compounds downstream:
`TrySettleTerminal` demands `reserved == originalOfferedAmount`, and `TryCreditCollected`
demands a matching completed bucket. Without a fix the sweep can never place, and if it somehow
placed it could never settle.

**Decision - sweep custody credit.** In the same cloned-bankroll transaction that persists
`Armed`, the sweep credits exactly the offered quantity proven by a final visible-stash read and
then reserves it normally. The alternative - bypassing reservation - was rejected: it forks the
durability model, leaves the tracked order unbacked, and breaks terminal settlement outright.

The credit is a ledger statement about currency that provably exists, so it is fenced:

- sourced only from a verified same-frame stash scan of the candidate's own metadata, never
  from the plan queue (which may be stale);
- exactly the amount about to be offered, never the whole holding;
- written at most once per candidate attempt; any failed validation, credit, reservation, or save
  discards the clone, so a rejected arm cannot leave phantom available balance behind;
- refused entirely if the tab is not visible, custody is ambiguous, or the scan disagrees with
  the planned quantity - those become `Skipped` with a recorded reason, not a credit.

**Directive dispatch.** `TickSellSweep`'s `default:` branch is replaced by a switch that maps
each directive onto the existing verified handler, with the sweep's own token and permissions:

| Directive | Action |
| --- | --- |
| `PlaceCurrentCandidate` | credit custody, stage the leg, then one verified Place Order click |
| `ObserveCurrentOrder` | poll tracked state only; no input |
| `AuthorizeCancellation` | `TrackedOrderCancellationController`, only while idle |
| `RecoverCancellationWithoutRetry` | lifecycle observation only; never repeat the cancel click |
| `AuthorizeSettlementCollection` / `RecoverSettlementCollectionWithoutRetry` | collect proceeds |
| `AuthorizeStashReturn` / `RecoverStashReturnWithoutRetry` | ctrl-right-click leftovers back |
| `AdvanceToNextCandidate` | `Advance(Sold, proceeds, reason)`, next candidate |
| `ManualReconciliationRequired` / `Ambiguous` | stop the sweep, no further input |

**Still single live order.** Phase 6 does not add slot awareness; `Decide` continues to refuse a
second placement while a tracked order is unresolved. Multi-slot is Phase 7.

### Phase 6 - work order (landed)

- `Domain/SellSweepPlacement.cs` is the sweep-only placement contract. Its token binds the sweep,
  candidate, probe session, league/area, metadata and hashes, selected-intent edge, exact rate, source
  book, input/output/remainder, proceeds identity, valuation rate, signature, and expiry. It has no
  arbitrage target or minimum-profit setting.
- `SellMarketQuote` retains the exact proceeds-to-Chaos rate. Terminal advancement values the
  actual `TerminalReceivedWantedAmount`, including partial and zero fills, and still advances only
  from the matching canonical `Stashed` attempt.
- Sell Sweep now holds one fixed probe session for its entire run. It captures both intents for the
  Divine/Chaos benchmark once before the first candidate, then captures Chaos/target and
  Divine/target for each candidate. Each two-market result is atomically combined with the retained
  benchmark before pricing; arbitrage retains its unchanged three-market probe.
- `SingleLegPlacementController` passes the exact final `MarketCapture` read at the click boundary
  to a caller validator. Arbitrage keeps its readable competing-head drift behavior; sell sweep
  additionally requires the prepared intent and rate to remain exact. Aggressive Immediate limits
  require positive live depth but intentionally do not require depth to cover the full staged input.
- Sweep authorization is established before controller ticks from the complete permission snapshot,
  exclusive owner, active feature, live sweep, league, area, and probe session. Revocation cancels
  pre-click work or marks post-arm custody manual/ambiguous. Area change, unload, hot reload, and
  resets clear sweep-only transients without deleting canonical tracked-order evidence.
- The `Armed` callback re-reads the visible home stash tab, requires zero unreadable target slots
  and the exact scanned holding, credits only `OfferedAmount` on the cloned bankroll, reserves it,
  attaches the tracked order, and performs one canonical save. Any refusal discards the clone, so
  no reverse mutation or phantom live credit exists. The successful callback binds the controller's
  generated attempt ID to the sweep before returning and audits
  `SweepCustodyCreditedAndOrderPlacementArmed` with attempt, probe session, and candidate signature.
- Every directive is explicit. Observation and cancellation recovery send no placement input;
  toggle-like cancellation, collection, and stash handlers are dispatched only while idle; terminal
  assets remain batch-by-batch collect-then-stash; unknown state or mismatched attempts stop input.
- Placement remains one live order. Queue persistence and automatic continuation after reload remain
  Phase 7/non-goals; canonical tracked state is the manual recovery authority after reload.

### Phase 7 - competing-head liquidity gate (landed)

The sweep priced against whatever led the competing ladder without asking whether that head was
reachable. On the 2026-08-15 book the `Divine Orb / Expedition Scarab` pair carried exactly one
competing level - 1 Divine per Scarab, three units listed - against an immediate side of 450 Scarabs
per Divine. Sixty Scarabs worth 47 Chaos on their own healthy Chaos book were priced at 60 Divine
(11,880 Chaos), beat the Chaos market by 253x, and were posted against nobody.
`Expedition Scarab of Infusion` was the same shape: queue 1, spread 200x, 11,880 against 60 Chaos.

Measured across all 141 captures in that `latest-rates.json`, the two signals separate cleanly:

| Signal | Trolled rows | Legit target->Divine | Legit target->Chaos |
|---|---|---|---|
| competing / immediate, same direction | 450x, 200x | 3.0x - 11.4x | ~1.001x |
| competing levels in the book | 1 | 1 - 5 | 5 |
| `CompetingQueueAhead` at the head | 3, 1 | 80 - 1313 | 84 - 8689 |

- `Domain/CompetingLiquidityGate.cs` is the whole rule, shared by the planner and the click boundary
  so the two cannot drift. Two independent halves, because credibility and price are independent
  failures. The queue floor is `MarketSweepScore.DefaultMinCycleQueue` reused rather than picked
  again - the market sweep already answers this question about these books, and its rationale is the
  same one. The spread cap anchors the competing rate to the same-direction immediate rate, which is
  the only rate in that direction with a counterparty already resting on it; the default of 25x sits
  an order of magnitude above the widest legitimate row and an order of magnitude below the narrowest
  troll. A missing immediate edge fails the spread half: no anchor is worse evidence than a wide one.
- The spread is cross-multiplied in `BigInteger`, matching `Rational.CompareTo`. This is a
  place / do-not-place decision, so no float enters it. `MarketSweepCycleLeg.Spread` stays `double`
  because it is a ranking input, not a gate.
- `FaustusSellPlanner.EvaluateMarket` runs the gate after the edge is selected and before the holding
  is sized, and returns `SellRejectionReason.UnbackedCompetingHead` for that market alone. `Evaluate`
  already scores Chaos and Divine independently and takes the higher `ProceedsChaos`, so the healthy
  market wins with no fallback machinery. Both markets failing falls through the existing
  `SellSweepCoordinator.Advance(..., Skipped, ...)` path and the sweep moves to the next candidate.
- `SellSweepPlacement.TryValidateLiveMarket` re-runs the same gate on the final capture. The rate and
  amounts can match exactly while the queue behind the head drains or is cancelled, so plan-time
  belief is not click-time belief.
- Thresholds are read live from settings at pricing and at the click. They are not snapshotted into
  `SellSweepState`: that record is persisted with a schema version, and a safety threshold does not
  earn a schema bump.
- Not done, deliberately: repricing down the ladder. The trolled capture has exactly one competing
  level, so there is nothing to walk down to, and `QuoteMatrixBuilder` asserts exactly four edges per
  capture. Chaos fallback is the real remedy. Fastest Fill's uncapped sizing is also left alone; it
  is a documented decision above and a separate change.
- Not gated: the Divine->Chaos benchmark edge. It values proceeds rather than being sold into, and
  in the observed failure it was a healthy 198/1 rate. If a trolled benchmark is ever seen, it
  belongs in this gate too.

### Phase 8 - concurrent resting orders (landed)

The sweep sold one holding, then blocked until that order filled or timed out, was cancelled,
collected, and stashed, and only then priced the next candidate. A competing sell is priced to
*rest*, so the wait is the point of the order - and with nine of the exchange's ten slots empty the
whole time, it was also the entire cost of the design. A sweep of twenty holdings serialised twenty
fill-or-timeout waits back to back.

The single-order rule was not incidental. It was enforced in four places: `BankrollState.TrackedOrder`
is one nullable record, `SellSweepState` carried a singular `CurrentIndex` / `CurrentAttemptId` /
`PreparedSignature`, `SellSweepCoordinator.MarkPlaced` threw *"one order is live at a time."*, and
sweep placement refuses unless the visible order list is completely empty. `_trackedOrderState` has
185 references in `FaustusControllerLite.cs`; turning all of them into a collection is the wrong
shape of change.

So the split is between what "an order exists" means, not between one order and many:

- **Active slot** - the one order an input controller currently owns: arming, clicking, cancelling,
  collecting, stashing. Stays `BankrollState.TrackedOrder` and stays `_trackedOrderState`. Exactly
  one may exist, ever. All 185 call sites keep their current meaning.
- **Resting slots** - orders that are placed and simply waiting, with no armed input intent.
  `BankrollState.RestingOrders`, observation-only. Resting is the only thing that happens in
  parallel; settlement stays strictly serial, one collect / stash / cancel at a time on the same
  evidence it demands today.

| Class | Statuses | Rule |
|---|---|---|
| Restable | `Pending`, `TimedOut`, `CompletedUncollected`, `CanceledUncollected`, `Ambiguous` | any number may rest |
| Active-only | `Armed`, `CancelArmed`, `CancelClicked`, `CollectionArmed`, `Collected`, `StashTransferArmed` | at most one, and it is the active slot |

`Ambiguous` rests as well as blocking. It has to: a resting order that turns ambiguous while the
active slot is mid-settlement would otherwise have nowhere to be recorded, and canonical state that
cannot be written is the exact wedge shape of the Milestone 10 history. It still blocks all trading
and is still never retried.

Concurrency is a setting, `MaxConcurrentSweepOrders`, 1-10, default 3. `1` reproduces the old
behaviour exactly and is the rollback lever. One live order per offered metadata: two competing
sells of the same item share a queue and the second is priced against the first, so the sweep would
compete with itself. The arbitrage full workflow stays single-order - route legs are sequential by
construction, leg 2 spends what leg 1 produced - and canonical state refuses to hold a workflow and
a resting set at the same time.

**8.1 - canonical state (landed).** `BankrollState` gains `RestingOrders`, `AllOrders`, and
`ComputeUnresolved()`; bankroll schema 6 -> 7, migration lossless and additive (an existing file
loads with an empty resting set and its tracked order still the active slot).
`Orders/TrackedOrderRestPolicy` is the single answer to which statuses may rest, which need
settlement, and which block trading. `BankrollStore` applies every existing per-order validation to
each slot via `AllOrders` instead of to `TrackedOrder` alone, and adds three rules of the resting
set's own: restable status, unique `AttemptId`, distinct `OfferedMetadata`. Reservation validation
sums across slots and, because two slots may never offer the same asset, stays an exact identity per
metadata rather than becoming a bound.

The one safety property that changes, and it changes narrowly: `HasUnresolvedOrder` is now "the
active slot is unresolved, **or** any resting slot is ambiguous". A resting `Pending` order no longer
blocks trading - which is the entire point - and `Armed` and `Ambiguous` still block globally.

**8.2 - fingerprint split (landed).** `OrderSetFingerprint` hashes every unrelated order's fill
amounts, so a sibling filling mid-settlement aborted a collection. It splits into an identity half
(`OrderIdentityFingerprint`, strict for every order: nothing may appear, vanish, or change what it
is) and a volatile half (the existing hash, kept verbatim, now strict only for orders the sweep does
not own). `GoldCost` is deliberately excluded from the identity half - a completed order can report
it as zero, which `TerminalIdentityMatches` already accounts for. Safe because a fill sits inside the
order until collected and never touches inventory; the proof that authorizes crediting is the
phase-bound exact ownership increase, and that is untouched.

The fingerprints were never the only whole-set check. `TrackedOrderCollectionController.SnapshotsEqual`
and `SnapshotsEqualIgnoringIds`, and `TrackedOrderCancellationController.SnapshotsUnchanged`, compare
every visible order field by field, so they had to learn the same rule: a listed sibling is compared
on its immutable half, everything else exactly. In `SnapshotsEqualIgnoringIds` the strict matches are
consumed first, so a lenient sibling match can never absorb the row an unrelated order was supposed
to prove.

The sibling set travels as `List<int> SiblingOrderIds` on `CollectionAssetIntentState` and
`CancelIntentState` alongside `UnrelatedIdentityFingerprint`, so an interrupted settlement recovers
with the same leniency it armed with; tracked-order schema 6 -> 7. Siblings are named by
`PlayerOrderId`, so a renumber makes the check fail closed - an abort, never a silent pass. An intent
armed before this change has an empty identity half: it verifies on the volatile half alone, exactly
as it did, and refuses any sibling leniency it never recorded. All three `Start` overloads now take
the sibling set; `FaustusControllerLite.RestingOrderIds()` derives it from `BankrollState.RestingOrders`,
which stays empty until 8.3 - so this phase changes no behaviour at all.

**8.3 - sweep slots (landed).** `SellSweepState.CurrentAttemptId` becomes
`List<SellSweepSlot> Slots` - a slot is `{ CandidateIndex, AttemptId, PreparedSignature,
OfferedMetadata, PlacedAtUtc }`, the durable link between a holding and the attempt selling it, so
several candidates can be out at once without the sweep having to guess which tracked order belongs
to which holding. Sweep schema 1 -> 2; nothing persists it, so there is no migration.

`CurrentIndex` narrows to *the next candidate to price*: placement consumes a candidate and moves
the cursor on, so a slotted candidate is always behind it and can never be re-priced or re-placed.
Every read that reaches a candidate through the cursor had to stop doing that -
`TryCalculateRealizedProceedsChaos` and the sold branch of `Advance` now reach it through the slot
the attempt closed, because by the time an order stashes the cursor is several holdings past it.
`PreparedSignature` stays singular: placement is still serial, and only one placement is ever in
flight. `Phase` is maintained as `Slots.Count > 0 ? OrderLive : ReadyForCandidate`, so every
external `Phase == OrderLive` read still means "the sweep has an order out" and needed no change.

`Decide` gains a `(sweep, active, resting, maxConcurrentSweepOrders)` overload and one directive
per tick, in priority order: anything ambiguous - or a slot with no order behind it, which means a
row the sweep placed is gone - requires an operator; the active slot runs today's per-status switch
untouched, and anything but a plain observation owns the tick outright because settlement is
serial; a resting order that has reached a terminal state is promoted and settled *before* any new
order is placed, which keeps reserved principal and uncollected proceeds low; only then, with a
free slot and a candidate whose item is not already resting, is a placement authorized; otherwise
the sweep observes. The two-argument form delegates with an empty resting set and a limit of 1,
which is the old machine step for step - so the driver, which still calls that form until 8.4, is
byte-for-byte unchanged in behaviour.

The limit lives in one place, `HasFreeSlot`, which `MarkPrepared`, `ClearPreparationForRetry`, and
`MarkPlaced` all pass through, each defaulting to 1. A slot therefore cannot be opened past the
limit whichever path asks. `MarkPlaced` also refuses an attempt id that already stands for a slot
and any item already resting. `MarkAmbiguous` fails every candidate the sweep still owns, resting
ones included: a resting order's custody is exactly as unprovable as the active one's.

**8.4 - driver and settings (landed).** `MaxConcurrentSweepOrders` is a Strategy setting,
`RangeNode<int>(3, 1, 10)`, clamped by `SweepSlotLimit()` to what the exchange can hold. `1`
reproduces the single-order behaviour exactly and is the rollback lever.

The driver moves one order at a time between the active slot and the resting set, and that is the
whole of the new plumbing:

- **Demotion.** `TickSellSweep` calls `TryDemoteActiveOrderToRest` before deciding anything. An
  order the sweep owns that has reached `Pending` is placed, stable, and holds no armed intent, so
  holding the one active slot open for it is exactly what serialised the sweep. It moves out to a
  resting slot in one durable write and the next candidate can be priced while it waits.
- **Promotion.** `PollSweepSlotMoves` runs beside `PollTrackedOrderLifecycle` and watches the
  orders nobody is holding. It sends no input and writes nothing while an order is still plainly
  pending: `TrackedOrderLifecycle.Evaluate` is pure, so observing a resting order costs nothing. The
  moment one observes as timed out, terminal, or ambiguous it is promoted into the active slot -
  status untouched - and the existing lifecycle poll records the transition and settles the ledger
  exactly as it always has. **No settlement, crediting, or terminal proof is duplicated for resting
  orders.** That is the reason the whole design is one active slot rather than a set of them.
- **Orphans.** A sweep that stopped or went ambiguous cannot settle what it left out, so when no
  sweep is running the poll promotes the oldest resting order anyway. That keeps the operator's
  ordinary cancel/collect/stash hotkeys able to reach it, one at a time, instead of stranding orders
  nothing in the plugin can name.

Two `Decide` rules had to widen for the driver's lazy clearing. A stashed order stays in the active
slot after `Advance` has already closed its slot, so for a tick or two the sweep sees a tracked
order it cannot name while other orders are still out. An **unresolved** one is still lost custody
and still requires an operator; a **resolved** one is harmless leftover, and settlement promotion
now overwrites it rather than waiting for an empty active slot.

The placement gate no longer requires an empty order list. It requires every row on the panel -
live or terminal - to be one the sweep can prove it placed by attempt id, and live orders strictly
below `min(MaxExchangeOrders, MaxConcurrentSweepOrders)`. An unrecognised row still stops the sweep;
that is the sweep detecting another actor on the panel, and it must not be lost. At a limit of 1
with no slots open the owned set is empty, so any row at all refuses - which is the old
`orders.Count != 0` check exactly.

`StopSweepBeforePlacement` marks ambiguous instead of stopping when slots are open, for the same
reason authorization revocation does: stopping says "nothing is outstanding", and with orders
resting that is a lie.

**Both directions are decided from one observation, and that is load-bearing.** The first build
demoted on the stored status (`Pending`) and promoted on the live observation. Promotion
deliberately leaves an order's status alone - the existing lifecycle poll is what records the
transition - so a resting order that observed as timed out was promoted into the active slot still
reading `Pending`, and the demotion rule moved it straight back out the same frame. That ping-ponged
**two canonical bankroll writes per frame** until Windows refused one
(`SweepRestingDemotionRefused: Access to the path is denied`), and the collection that eventually
ran on top of the storm went ambiguous on a torn inventory read.

Demotion therefore moved into `PollSweepSlotMoves` alongside promotion, where both share one panel
read: an order rests only while it observes as plainly `Pending`
(`TrackedOrderRestPolicy.ObservationAllowsRest`), and takes the active slot only on a conclusive
non-pending observation (`ObservationRequiresSettlement`). `NotVisible` and `Transitioning` fall in
the gap between them and move nothing at all. `resting and settling never claim the same order`
asserts the two are disjoint over every observation kind there is. Independently of that logic,
`SlotMoveIntervalMilliseconds` caps slot moves at two per second and a failed move backs off five
seconds, so no future mistake above it can rewrite canonical state at frame rate again.

**An unloaded book is not a moved price.** A full Immediate-mode sweep produced 42 staging aborts
and zero placements - every candidate refused on *"The live quote no longer exactly matches the
candidate leg"*, three times each, then skipped. A 100% refusal rate across books 62,000 listings
deep is not a market moving; it is a check that cannot pass.

The refusal named neither the rate nor the sample, so it was instrumented before it was touched -
and the instrumented line settled it in one run: `[SamplingInitialQuote] … live no edges at all`.
The *initial* sample, before any amount is typed, against a correctly-read capture of **nothing**.
The exchange does not populate its stock ladder the instant a pair is selected, so the first sample
after selection can legitimately see an empty book.

`ShouldRetryMissingCompetingBook` already waited exactly that out - and was hard-gated to competing
legs, so an immediate leg abandoned instantly on a panel that had merely not finished loading. It is
now `ShouldRetryMissingBook`: a competing ladder still settles as before, and *any* leg waits while
the capture holds no edge in its own direction. A book that loaded and reads a different price is a
moved market and still fails at once, so a stale plan is re-planned rather than sat on. Both are
bounded by the sampling step deadline.

This is the same shape as the concurrency bugs above and as the `Ambiguous` rest policy: a tolerance
that was written for one path and never generalised to the other. Worth checking first whenever
Fastest Fill and Most Currency behave differently.

**A moved quote is re-planned, wherever it moves.** The placement click already re-probed when the
quote moved out from under it; staging did not, and stopped the whole sweep instead - even though
`SingleLegStagingController` had already set `FreshProbeRetryRecommended` to say the abort was
routine, and even though the arbitrage workflow'''s branch of the same method reads that flag. Only
the sweep'''s branch ignored it. Fastest Fill made it constant, because an immediate head is repriced
by every fill.

Both aborts now share `TryReProbeSweepCandidate`; they differ only in *when* the quote moved, never
in what should happen next. Re-planning without a bound is its own failure, though - a market
volatile enough to move on every attempt would hold the queue forever - so after
`MaximumSweepReProbes` consecutive re-plans of one candidate the holding is skipped, which is what
the sweep already does with a market it cannot use. The counter is keyed to the cursor, so a
successful placement resets it by moving on.

**A skip reports every market, not the loudest one.** A holding is skipped only when *both*
proceeds markets refuse it, and they usually refuse for different reasons. `ReasonPriority` picks
one for the summary line, which is fine for an overlay and actively misleading in a log: fifteen
Divination Scarab of Plenty were reported as *"cannot fill one lot of 65 for CurrencyModValues"* -
true, and irrelevant, because Divine was never the market that mattered. `NoWholeLot` outranks
`UnbackedCompetingHead`, so the Chaos market'''s actual refusal never reached the log at all.
`FaustusSellPlanner.DescribeRejections` now appends every market'''s verdict to
`SweepCandidateSkipped`; the overlay keeps the one-line summary.

**A preparation has two halves and they move together.** `PreparedSignature` is the durable half;
the staged leg and placement token in the driver are the other. `AdvanceSweepCandidate` clears the
driver'''s half on every advancement - but `Advance` only cleared the durable half when the cursor'''s
own candidate was retired, not when a slot closed. So a sweep that planned its next holding and then
finished settling a resting order kept a `PreparedSignature` whose plan no longer existed anywhere:
the next tick read it as *place now*, found no leg or token, and aborted with *"Sweep placement
preparation was unavailable"*. A partially filled order made it easy to hit, because settling both
sides takes long enough for the sweep to have planned something else in the meantime.

Any retirement now clears the preparation, whichever candidate was retired; only the cursor still
moves for a candidate that was never placed. And because the two halves being out of step is a plan
problem rather than a custody problem, the driver re-probes instead of aborting if it ever sees it
again - a plan is always recoverable, and the orders an abort strands are not.

**The proceeds need time to arrive, and how much depends on how many.** A 368-Chaos batch is
nineteen inventory stacks. The order row disappears the instant the server acknowledges the collect,
and `SynchronizeTrackedCollection` settled on that same frame - reading the inventory exactly once
and calling the batch ambiguous if all nineteen stacks had not yet materialised. They had not; they
arrived moments later, and 368 Chaos then sat in the inventory against a canonical state that had
given up on them.

Every other stage of this flow already waits: three seconds for the row to disappear, three for
canceled-return evidence, two stable reads for ownership. The inventory check was the only stage
with no window at all, and the only one waiting on a *count* of items rather than on one thing
changing - so it is the stage most sensitive to batch size, and the one that had the least
tolerance. `CollectedInventorySettleTimeout` gives it six seconds, retried per frame from
`CollectionFlowState.SettlingCollectedInventory`, and ownership has already proved the exact rise
before that wait ever begins, so nothing is being taken on trust.

**Waiting is not failing.** `Decide` deliberately authorizes a placement alongside a pending order -
that is the whole of the concurrency - because it reasons about slots, not about who is holding
input. `PlaceCurrentSweepCandidate` still carried the single-order world'''s refusal of *any*
unresolved active order, so the moment a resting order was promoted mid-pricing the driver refused
the placement `Decide` had just authorized, and `StopSweepBeforePlacement` marked the whole sweep
ambiguous with every order it had out.

Placement is serial, so the driver is where that authority meets the fact that the active slot must
be free first - but the answer is to **wait**, not to abort. `SweepActiveSlotIsBusy` now separates
the ordinary races of a multi-order sweep (an order not yet demoted, one just promoted and not yet
reclassified, a controller mid-operation) from the faults that do not resolve on their own, and each
of the seven durable blockers names itself instead of sharing *"preconditions were unavailable"*.
`AdvanceSweepCandidate` had the same shape and was worse - it marked the sweep *ambiguous* because
something else was mid-operation on the frame a stashed order came up - and now waits too.

**A row is evidence of being terminal, never of which terminal.** A partly filled order that is
then cancelled has two collectible assets, and collecting the first one broke the second every
time. The SDK reported the row as `completed=True canceled=False` while the row itself still read
*"Order Cancelled"*, so `expectedStatus = IsCanceled ? "Order Cancelled" : "Order Completed"`
looked for the wrong string and refused with *"lost exact visible status evidence"*. The game does
not keep the flag and the text in agreement, and nothing ever required it to.

`OrderRowStatusText.IsTerminal` replaces all four sites that derived an expected string that way.
The safety property is unchanged and is the only one the text ever carried: a row that is still
trading reads `Order Listed` and is still refused. *Which* terminal a row reached, and for how
much, is proved by the SDK amounts and the durable intent - which is where it was always proved.

This was never sweep-specific. `CanceledReturnCollectionController` is shared with the arbitrage
workflow and the manual hotkeys, so any partly filled order that gets cancelled hit it; the sweep
simply meets that shape constantly, because a competing order that times out mid-fill is its normal
outcome rather than an unusual one.

**A retired candidate leaves a record.** A skip is the sweep declining to sell a stack - a decision
about the operator's holdings - and it used to exist only in the on-screen status line, which
scrolls away. Worse, the branch an operator actually *sees* (the cursor turning back from Place
Order when the final live-market re-check rejects) deliberately clears `_lastFailure`, so the one
visible moment left no trace at all. `SweepCandidateSkipped`, `SweepCandidateFailed`, and
`SweepPlacementAbortedForReProbe` now carry the reason into `workflow-runtime.log`. Skips are the
sweep working as designed, which is exactly why they have to be legible afterwards - otherwise a
correct competing-liquidity refusal is indistinguishable from a bug.

**Settlement failures now report themselves.** `VerifyInventoryPostState`, and both halves of the
interrupted-collection classifier in `CanceledReturnCollectionController`, used to answer eleven
different faults with three sentences - so a live refusal said only that settlement had been
refused, never which evidence moved. Each condition is now its own check with the actual numbers in
it, and `InventoryTransferEvidence.DescribeNonTargetChange` names the first non-target difference
rather than reporting that a hash moved: an item appearing, a stack changing size, an item being
repositioned, or - the one worth telling apart - `clearOfExchange` flipping, which means the
exchange panel resized underneath items that never moved at all.

**The limit is carried in four places, and all four must agree.** `Decide`, `MarkPrepared`,
`MarkPlaced`, and `ClearPreparationForRetry` each take `maxConcurrentSweepOrders` and each defaults
it to 1. The first live run threw *"A sweep can prepare only its current unplaced candidate."* on the
second placement because only `Decide` had been told the real limit: the driver authorized a
placement the coordinator then refused. All four now pass `SweepSlotLimit()`, and
`sell sweep honours one limit end to end` walks limits 1 through 3 asserting they agree slot for slot.

Two more driver reads of `Phase` had to go for the same reason - `Phase` now says whether *some*
order is resting, not whether *this* placement is out. Arming a placement no longer requires
`ReadyForCandidate` (it requires only that the sweep is running and the preparation still
validates), and a cancelled click decides between re-probe and ambiguity on whether it consumed the
prepared signature into a slot, which is the click's own outcome rather than the panel's.

The status line reports occupancy as `2/3 resting (Sacrifice Scarab 41s, Gilded Scarab 12s)` -
idle slots are sweep throughput left on the table, so the operator sees the number that matters.

## Invariants To Preserve

- Every `Allow*` setting and hotkey defaults off/unbound.
- The sweep coordinator composes verified controllers and sends no input itself.
- Every input effect still requires foreground Path of Exile, the same league and area, a closed
  picker, no popup, no held modifiers, an unchanged commanded cursor, and an unexpired deadline.
- Currency identity remains exact metadata; names stay display-only.
- All economics stay exact-integer; no floating point enters a decision.
- One order under input at a time: canonical state keeps a single active tracked order, and any
  other order it knows about is resting - placed, observation-only, with no armed intent.

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

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)` and `198/198 tests passed`. Any warning is a
regression - the project has been kept warning-clean, so a new one means something was silently
reinterpreted.

### Phase 1 - read-only scan and economics

Bench, no game required:

1. Build and test as above. Confirm the full count (92 as of Phase 2).
2. Confirm these eight names appear and pass: `sell planner selects best proceeds`,
   `requires whole lot`, `enforces minimum sale`, `needs divine benchmark`, `rejects unusable
   quotes`, `ignores immediate books`, `prefers freshest competing capture`, `validates request`.
   (`scarab target routes through Divine` is a buy-side `RoutePlanner` test and is not part of this
   set, despite the name.)

In game. Preconditions: exchange panel open, scarab tab visible, inventory open, and **every
`Allow*` permission off**. Phase 1 sends no input, so any cursor movement or click during these
steps is itself the bug.

3. Bind `DumpSdkReadsHotkey` in settings. Leave every `Allow*` unchecked.
4. Capture the three markets first, via `CaptureCurrentPairHotkey` per pair or the automated probe.
   The sell queue reads *persisted* captures, not live panel state. Skipping this makes every row
   reject with `MissingEdge` or `MissingDivineBenchmark`, which is correct behaviour and not a bug.
5. Press the dump hotkey. The status line should read `SDK diagnostic: <summary>; wrote <path>`, and
   the file should be at `%ConfigDirectory%\FaustusControllerLite\sdk-diagnostic.txt`.
6. Confirm no `sdk-diagnostic.txt.tmp` is left behind. The write is temp-then-`File.Move`; a
   surviving temp file means the move failed and the report you are reading may be stale.

Read the `Sell queue` section and check by hand:

7. `scan readable=True`, `tabType` matches the open tab's `InvType`, `unreadable=0`. A non-zero
   unreadable count means slots were skipped and the queue is under-reporting.
8. `holdings` count equals the number of distinct scarab metadata in the tab, and each row's
   `amount` equals what you count in the stash. Per-metadata totals, not per-slot.
9. Each accepted row names a market, a chosen rate, and `proceedsChaos`. Recompute one row by hand
   against the rate shown and confirm it matches exactly.
10. Every rejected row carries a reason you can explain. The full set is `ZeroHolding`,
    `MissingEdge`, `MissingDivineBenchmark`, `InvalidQuote`, `StaleQuote`, `SessionMismatch`,
    `AreaMismatch`, `NoWholeLot`, `ArithmeticOverflow`, `ProceedsBelowMinimum`. A reason you cannot
    account for is the finding - that is the whole point of this phase.
11. `NumeraireCurrency` and `NotInCatalogue` skips are expected for Chaos, Divine, and anything the
    catalogue does not know. They are skips, not rejections, and appear before evaluation.

Deliberate negatives - each should degrade cleanly, never crash and never emit a queue row:

12. Close the stash entirely and dump again. Expect `scan readable=False` with a failure reason and
    no holdings.
13. Open the currency tab instead of the scarab tab and dump. The holdings should follow the visible
    tab, and Chaos and Divine rows should skip as `NumeraireCurrency`.
14. Wait past `MaximumQuoteAgeSeconds` (default 60) without recapturing, then dump. Rows should flip
    to `StaleQuote`. This proves the freshness gate is live rather than nominal.
15. Re-probe so a new session id is issued, then dump. Rows quoting the prior session should reject
    `SessionMismatch` rather than silently mixing sessions.

Known limitation to expect, not to file: `minimumSaleChaos` is hard-coded to `10` at the
`DumpSdkReads` call site until Phase 4 wires the setting. `ProceedsBelowMinimum` therefore always
tests against 10 chaos in Phase 1 regardless of what you intend to use later.

### Phase 2 - custody generalization

Bench, no game required:

1. Build and test. Confirm 92/92 and that `custody resolves against visible tab` passes. That test
   pins the whole table: home tab exact for both families, off-home aggregate in both directions,
   the legacy overload still currency-exact / scarab-affinity, and unreadable tabs plus unsupported
   metadata resolving to nothing.
2. Confirm `three-capture batch commits atomically`, `tracked order persistence round trip`, and the
   schema-migration tests still pass. Store validation moved from an exact-mode comparison to
   `IsResolvableCustody`, so a persisted intent that used to load must still load.

In game, currency-only first - this is the no-behavioural-change proof. Preconditions: exchange
panel open, **currency** tab visible, inventory open.

3. Run one full buy workflow end to end exactly as before Phase 2: place, wait, collect, stash.
4. Watch the status line at the transfer step. With the currency tab visible it must still read
   `moving exact collected amount to the visible home stash tab` - the exact wording changed, the
   mode behind it must not have. Any mention of affinity here is the regression.
5. Confirm the visible currency stash count rises by exactly the collected amount, and that the
   tracked order settles rather than going ambiguous.
6. Repeat once with the **scarab** tab visible while collecting currency proceeds. The status line
   should now read `through configured stash affinity`, the visible scarab tab must not change, and
   aggregate ownership must be unchanged. This is the inverted case that used to be unreachable.

Recovery, the part most likely to bite:

7. Arm a transfer with the currency tab visible, then kill the plugin mid-transfer (before the
   post-transfer read). Reopen with the **currency** tab visible and let it recover. Expect a clean
   `PreTransfer` or `PostTransfer` classification and a settled order.
8. Repeat step 7 but reopen with the **scarab** tab visible. The live mode no longer matches the
   armed mode, so expect `Ambiguous` and a refusal to settle - not a silent settlement. A settlement
   here means custody was credited without proof, which is the worst failure this phase can produce.
9. Open a non-custody tab (a map tab) and try to collect. Expect a clean refusal naming the stash
   custody policy, no cursor movement, and no click.

### Phase 2.5 - feature mode gate

Bench, no game required:

1. Build and test. Confirm 95/95 and that `feature mode parses exactly`, `feature mode gates by
   scope`, and `feature mode switch refuses unresolved state` pass. Between them they pin the
   label round-trip (including the corrupt-value fallback to Arbitrage), the per-action scope
   table, and the refusal-while-unresolved rule in both directions.

In game. Preconditions: plugin loaded, no tracked order, no workflow running.

2. Open settings. Confirm the **Active feature** dropdown shows exactly `Arbitrage` and `Sell
   Sweep`, and that the overlay status line reads `Active feature: Arbitrage`.
3. With Arbitrage active, run any arbitrage hotkey (place, collect, cancel, stash). Unchanged
   behaviour is the requirement here - this phase must be invisible while the selector is left
   alone.
4. Switch the dropdown to **Sell Sweep**. The status line must follow. Now press each arbitrage
   hotkey in turn: place order, collect tracked order, cancel timed-out order, stash collected
   currency, execute single leg, adopt pending order, full workflow. Every one must refuse with a
   message naming the active feature. No cursor movement, no click, no state change.
5. Still in Sell Sweep, press the **shared** hotkeys: probe markets, dump SDK readahead, and each
   of the calibration hotkeys. These must still work - they are infrastructure, not workflow. A
   refusal here is the regression.
6. Switch back to Arbitrage and confirm the same hotkeys from step 4 work again.

The refusal-to-switch path, which is the one that protects money:

7. Place a real order under Arbitrage so a tracked order is outstanding. Now try to switch the
   dropdown to Sell Sweep. Expect: refusal, a status message naming the unresolved order, and the
   dropdown **snapping back to Arbitrage** on the next frame. If the dropdown stays on Sell Sweep,
   stop - the placed order is now stranded behind refused hotkeys.
8. Collect and settle that order, then switch again. It must now succeed.
9. Start a full workflow, and while it is running try to switch. Expect the same refusal naming the
   running workflow, and the same snap-back.
10. With a tracked order outstanding under Arbitrage, restart the plugin. It must come back up in
    **Arbitrage** (the persisted mode is adopted ungated) with the order still recoverable. Coming
    back up in the wrong mode would refuse the recovery hotkeys.

### Phase 3 - sweep state machine

Bench only. Phase 3 is pure decision logic with no wiring yet, so there is deliberately nothing to
do in game until Phase 4 - the state machine cannot move a mouse.

1. Build and test. Confirm **108/108** and that all thirteen `sell sweep *` tests pass.
2. The three tests that carry the money risk, if you only re-read three:
   - `sell sweep never places while an order is unresolved` - walks all 11 unresolved
     `TrackedOrderStatus` values and requires `ManualReconciliationRequired` for each. It asserts
     `covered == 11` so it cannot silently become vacuous if the enum or `IsUnresolved` changes.
   - `sell sweep placement is rejected while live` - walks all 13 statuses while an order is live
     and requires that none of them yields a place or re-plan directive; also requires `MarkPlaced`
     to throw from `OrderLive`.
   - `sell sweep advances only after proceeds are stashed` - `Collected` must still ask for the
     stash return, never advance.
3. Deliberately break the invariant and confirm the tests catch it, since a safety test that cannot
   fail is worthless. In `SellSweepCoordinator.Decide`, delete the
   `if (tracked?.IsUnresolved == true)` guard in the `ReadyForCandidate` branch and re-run: `sell
   sweep never places while an order is unresolved` must fail. Restore it.
4. Same drill for ordering: change `OrderByDescending` to `OrderBy` in `SellSweepPlanner.Build` and
   confirm `sell sweep plan orders by proceeds descending` fails. Restore it.

   Both mutations are confirmed to fail the intended test and only that test. If you restore by
   copying a backup file over the original, touch it afterwards - a restored file can carry an
   older timestamp than the last build, and MSBuild will then silently re-run the *mutated*
   binary and show you a failure you have already fixed.
5. Confirm the arbitrage suite is untouched - Phase 3 adds files and registers tests but edits no
   existing production code, so any pre-existing test failing here is a real regression.

### Phase 4 - settings, permission and hotkey wiring

Phase 4 is the first block with in-game steps for the sweep itself, but it still places nothing:
the hotkey only *plans* and *stops*. Placement lands in a later phase. That is the point of this
block - it proves the plan, the gates and the refusals are right while the cost of being wrong is
still zero.

Bench first:

1. Build and test. Confirm **108/108** and 0 warnings.
2. Confirm the four wiring points exist and are off by default, since every one of them is a way
   for the feature to act without being asked:
   - `AllowSellSweep` defaults `new(false)` in `FaustusControllerLiteSettings.cs`.
   - `SellSweepHotkey` is `CreateUnboundHotkey()` and appears in the `Binding(...)` conflict table.
   - `PermissionSnapshot` carries `SellSweep` in `AnyLiteInputPermissionEnabled`,
     `DisableLiteInputPermissions` and `ReadyForSellSweep`.
   - `ExchangeOrderCapacity.MaxExchangeOrders == 10`.
3. Mutation, conflict table: comment out the `SellSweepHotkey` line in the binding table and press
   the plugin's duplicate-hotkey check with the sweep hotkey bound to something already in use. The
   conflict must be reported; if it is not, the table lost its only entry for this key.

In game, with **Active Feature = Sell Sweep** and the exchange panel, scarab tab and inventory open:

4. Leave `Allow sell sweep` unchecked and press the hotkey. Nothing may happen beyond a refusal;
   this is the permission gate, and it is the last line between a mis-set hotkey and real orders.
5. Check it, bind the hotkey, press once. The `Sell sweep:` overlay line must move off
   `Idle; no sell sweep planned.` to a planned sweep naming the candidate count, and `Last failure`
   must read `None`. Cross-check the plan against the tab by hand: the first candidate should be
   your highest realizable-proceeds holding, not merely your largest stack.
6. Press again with no order live. The sweep must stop and the status line must say so. Nothing was
   placed either way, so this is safe to repeat.
7. Set `Minimum sale (chaos)` above your best holding's proceeds and re-plan. The sweep must plan
   nothing and say so, rather than planning a candidate it would refuse later. Restore the minimum.
   This is the setting the user asked for and it is only honoured if planning reads it live - it is
   read from `Settings.MinimumSaleChaos.Value` at plan time, not baked in.
8. Place one order by hand on the exchange, then press the hotkey. Planning must refuse with the
   live-order count, because a sweep plans against an empty book. Cancel the order, collect it, and
   confirm planning then succeeds.
9. Switch the visible stash tab to a currency tab and press the hotkey. Planning reads the visible
   tab, so it must either plan currency holdings or refuse - it must never plan scarabs it can no
   longer see.
10. Feature gate, both directions: with a sweep planned and active, try to switch Active Feature to
    Arbitrage. It must refuse and snap the selector back, because an active sweep is unresolved
    state. Stop the sweep, then confirm the switch is accepted.
11. Forced fresh-state reset while a sweep is planned: arm and apply it, then confirm the
    `Sell sweep:` line returns to idle and reports that the reset discarded the sweep. A sweep
    surviving a reset would point at a tracked order that no longer exists.
12. Re-run the Phase 2 and Phase 2.5 in-game blocks unchanged. Phase 4 touches custody and the
    feature gate only through settings, but that is exactly the kind of change that regresses them.

### Phase 5 - just-in-time probing and the sweep driver

Phase 5 is the first block where the sweep spends real time in game (one sweep-wide benchmark,
then two probes per candidate)
but it still places nothing: the driver prices a candidate, marks it prepared, and then reports
that placement is not automated yet. That is deliberate - it lets the probe loop, the session
gate and the skip path all be proven while a wrong answer still costs nothing.

Bench first:

1. Build and test. Confirm **108/108** and 0 warnings.
2. `BuildQueue` must emit unpriced candidates: confirm `PlannedProceedsChaos == 0` and
   `PlannedSignature == ""` for every entry. A non-empty signature here would make `Decide`
   return `PlaceCurrentCandidate` and the sweep would place against a stale, possibly
   cross-session quote.
3. Confirm ordering is quantity descending then metadata ordinal, and that it is stable across two
   builds of the same holdings.

In game, **Active Feature = Sell Sweep**, `Allow sell sweep` checked, exchange panel + scarab tab
+ inventory open:

4. Press the hotkey. The status line must first show
   `Probing Divine>Chaos once for the whole sell sweep.` For the first candidate it must then show
   `Using the retained Divine/Chaos benchmark; probing Chaos><name> and Divine><name>.` then either a priced
   line naming proceeds, edge and lots, or a skip naming the rejection reason. If it goes straight
   to a priced line, the probe never ran and the quote came from the stale store.
5. Watch three consecutive candidates. Divine/Chaos must not be selected again. Each candidate must
   still select and probe its own Chaos/target and Divine/target books; pricing without those two
   fresh captures is reading another candidate's markets.
6. Set `Minimum sale (chaos)` above the value of a mid-sized stack. That stack must be skipped with
   `MinimumSaleChaos`-flavoured detail and the sweep must continue to the next candidate, not stop.
   This is the "not enough to be worth selling, move on" requirement.
7. Confirm a stack that cannot fill one whole lot (e.g. 15 scarabs against a 1:50 Divine edge) is
   either priced against Chaos or skipped - never priced against Divine. This is the whole-lot rule
   and it is the one the user called out by example.
8. Change area mid-sweep. The sweep must stop and say so rather than continue probing; the probe
   session id has rotated and custody assumptions no longer hold.
9. Start an unrelated manual probe, then press the sweep hotkey. The sweep must wait
   (`Waiting for an unrelated probe to finish...`) rather than start a second probe.
10. Place an order by hand while the sweep sits on a prepared candidate. The next tick must report
    manual reconciliation required, not place. The single-live-order rule is enforced by
    `Decide`, and this is the only in-game way to prove a foreign order trips it.
11. Re-run the Phase 4 in-game block unchanged.

### Phase 6 - sweep placement and collection

Phase 6 is the first block where the sweep sends **order-placing** input, so it is also the
first where a wrong answer costs currency. Test with a low-value scarab type and a small stack.

Bench first:

1. Build and test. Confirm the full count (**126/126** when this phase landed; **172/172** as of
   the sweep mid-cycle abandonment fixes), 0 warnings, and these new names pass:
   `sweep probe plans partition benchmark once`,
   `sell planner preserves proceeds valuation rate`,
   `sell sweep placement token validates exact preparation`,
   `sell sweep strict live quote rejects drift`,
   `sell sweep quote drift returns to reprobe`,
   `sell sweep placement requires preparation`,
   `sell sweep values actual terminal proceeds`,
   `sell sweep custody scan is exact`,
   `sell sweep permission requires exclusive owner`, and
   `tracked audit identifies sweep attempt`.
2. Confirm `sweep custody credit is fenced` also passes. The arm transaction applies credit and
   reservation only to a cloned bankroll; a failed reserve leaves the complete live non-core map
   unchanged. Canonical unresolved-order exclusion prevents a second arm for the same attempt.
3. The strict token test independently mutates sweep/candidate/session/area/signature, rate,
   input, output, valuation, and expiry. The contract accepts no settings object, proving
   `TargetCurrencyMetadata` and `MinimumProfitChaos` cannot affect sweep validation.
4. Confirm the existing `competing placement tolerates readable head drift` test remains green
   beside `sell sweep strict live quote rejects drift`. This pins the deliberate behavior split.
5. Confirm `sell sweep maps live order statuses` maps matching `Armed` to observation, not manual
   ambiguity. That is the same-frame durable-arm regression: the sweep must recognize its own
   attempt before the outer driver runs.

In game, **Active Feature = Sell Sweep**, exchange panel + scarab tab + inventory open,
`Allow sell sweep` + placement/click/movement permissions on:

6. Run a sweep against one small stack. Expected sequence, all visible in the status line:
   probe -> priced -> staged -> one Place Order click -> order live -> collected ->
   leftovers returned to the tab -> `Sold` -> next candidate. Any step that repeats a click is
   a bug; the click is verified and one-shot by design.
7. Confirm exactly one order goes live at a time. A second placement while the first is
   unresolved is the failure this phase most needs to not have.
8. Let an order time out. The sweep must cancel it, collect the returned stack, put it back in
   the tab, and advance - not stall and not re-place.
9. Interrupt mid-collection (close the panel). The sweep must report recovery/manual
   reconciliation and stop sending input, and the tracked order must survive a plugin reload.
10. After a completed sale, confirm the ledger: reserved returns to zero for that metadata, the
   proceeds are credited, and no phantom available balance remains from the custody credit.
11. Confirm leftovers land back in the correct stash tab via the existing ctrl-right-click move,
    and that inventory is empty of the swept type before the next candidate is probed.
12. Confirm the final sequence exactly: probe, strict price, stage, one placement click, one live
    order, terminal observation, cancellation if timed out, exact batch collection, proceeds and
    leftover stash custody, zero reserved offered balance, no phantom credit, `Sold`, then a fresh
    probe for the next candidate.
13. Re-run the Phase 5 in-game block unchanged.

### Execution strategy selector

1. Confirm `sell sweep execution mode parses exactly`, `sell planner fastest fill is immediate only`,
   `aggressive immediate validation is depth positive`, `sell sweep captures execution mode`,
   `sell sweep fastest live quote uses full holding`, and `fastest fill retains normal pending timeout`
   all pass alongside the existing competing and arbitrage regressions.
2. With **Most Currency**, confirm immediate books are ignored and the sweep places the same
   competing limits as before this selector existed.
3. With **Fastest Fill (Market Rate)**, confirm a candidate with no positive immediate head is
   skipped rather than repriced from a competing edge.
4. Confirm a shallow positive immediate head stages every whole lot in the holding at the exact
   prepared rate. Only the ordinary lot-size remainder remains unsold; displayed depth does not
   resize the order.
5. Change the selector while a sweep is active. Status and subsequent candidates must retain the
   mode captured at planning; only the next sweep may use the changed setting.
6. Let an aggressive order partially fill. The remaining order must stay pending until the normal
   configured wait deadline, then use the existing cancellation and settlement flow.

### Phase 7 - competing-head liquidity gate

1. Confirm `competing liquidity gate reads queue and spread`, `sell planner skips unbacked competing
   heads`, and `sell sweep live quote rejects unbacked competing head` pass, and that the whole suite
   is green - the pre-existing sell-planner fixtures were given believable books (a queue and an
   immediate anchor) rather than the gate being loosened to accommodate them.
2. With `AllowSellSweep` off, probe and scan a tab holding the trolled scarabs. Expedition Scarab and
   Expedition Scarab of Infusion must plan at their **Chaos** proceeds (47 and 60 Chaos), never at
   11,880. Any candidate whose only market was trolled must appear as skipped, and its detail must
   name the measured queue or spread.
3. Confirm every healthy holding plans at the same proceeds and into the same market as before the
   gate existed. A gate that rejects real business is worse than the bug.
4. Set `MinCompetingQueue` to 0 and `MaxCompetingSpread` to 1000, rescan, and confirm the old
   11,880-Chaos plan returns. This is the proof that the gate is the only thing changing the outcome.
5. Restore the defaults, enable `AllowSellSweep`, and place one real order on a healthy candidate.
   The click-boundary re-check must not reject a good book.
6. Confirm **Fastest Fill (Market Rate)** is unchanged: re-run the execution strategy selector block
   above and confirm every step behaves exactly as it did before this phase.

### Phase 8.1 - canonical resting order set

Back up `config/FaustusControllerLite/FaustusControllerLite/bankroll-<league>.json` before step 2.
This phase changes the canonical file's schema; nothing else in it sends input, so every step here
is a load/save proof rather than a trading one.

1. Confirm `resting orders round trip and do not block trading`, `resting ambiguity still blocks
   trading`, `resting set rejects armed, duplicate, and shared asset`, `resting reservations are
   exact per order`, `schema six bankroll gains empty resting set`, and `workflow refuses alongside
   resting orders` pass, and that the whole suite is green.
2. **Migration.** Start the plugin on an existing schema-6 bankroll file. It must load without
   complaint, and the rewritten file must read `"SchemaVersion": 7` with an empty `RestingOrders`
   and the same balances, the same tracked order, and the same `HasUnresolvedOrder` as the backup.
   Diff the two files: schema version and the new empty array are the only permitted differences.
3. **Nothing behaves differently yet.** Nothing writes a resting order until 8.3, so run one
   complete single-order sweep and one arbitrage workflow leg end to end. Both must be
   indistinguishable from before this phase - place, wait, settle, stash, next.
4. **Unresolved still blocks.** With an order live (`Pending` or `Armed` in the active slot), confirm
   the plugin still refuses to start anything else, exactly as before. `HasUnresolvedOrder` is
   computed now rather than assigned, so this is the check that the computation agrees with the old
   assignment on every single-order state.
5. **Corrupt-state refusal is intact.** Hand-edit a copy of the file to add a `RestingOrders` entry
   whose status is `CollectionArmed`, and another copy with two entries sharing one
   `OfferedMetadata`. Both must refuse to load and must not be overwritten - the safe reset still
   refuses corrupt evidence, and the forced reset is still the only exit.
6. **Workflow exclusion.** Hand-edit a copy with both a `Workflow` and one resting entry. It must
   refuse to load with the workflow-alongside-resting message rather than silently running a
   workflow on top of sweep orders.

### Phase 8.2 - fingerprint split

Nothing lists a sibling yet, so every check below is a proof that settlement is exactly as strict as
it was. Back up `bankroll-<league>.json` and `tracked-order-<league>.json` before step 2.

1. Confirm `identity fingerprint ignores fills but not identity` and `sibling fills survive
   settlement verification` pass, and that the whole suite is green.
2. **Migration.** Start the plugin on an existing tracked-order file. It must load without
   complaint and rewrite as `"SchemaVersion": 7`. Diff it against the backup: the schema number, an
   empty `SiblingOrderIds`, and an empty `UnrelatedIdentityFingerprint` on any armed intent are the
   only permitted differences.
3. **Settlement is unchanged.** Run one sweep order all the way through - place, time out, cancel,
   collect the return, stash - and one that completes and is collected. Both must be
   indistinguishable from before this phase. `RestingOrderIds()` returns empty, so both halves are
   computed over the same set and the volatile half alone is the old check.
4. **An unrelated fill still aborts.** With one sweep order settling, place a second order by hand
   on an unrelated item and let it take a fill while the collection is armed. The collection must
   abort exactly as it does today and leave the order recoverable - the sweep does not own that row,
   so it gets no leniency.
5. **Interrupted recovery.** Arm a cancellation, alt-tab out mid-confirmation, and let the recovery
   path run. It must reach the same verdict as before: unchanged orders recover, a changed unrelated
   order marks ambiguous. This is the check that the intent now carries both halves and reads both.
6. **Pre-upgrade intent.** Hand-edit a copy of an armed tracked-order file to blank
   `UnrelatedIdentityFingerprint` and empty `SiblingOrderIds`. Recovery must still verify on the
   volatile half alone rather than refusing or passing everything.

### Phase 8.4 - driver and settings

Back up `bankroll-<league>.json` first. Steps 2 and 3 are the ones that decide whether to go
further; do not raise the setting above 2 until step 5 has passed once.

1. Confirm `sell sweep works around a settled leftover`, `sell sweep honours one limit end to end`,
   and `resting and settling never claim the same order` pass, and the whole suite is green.
2. **Rollback proof.** `MaxConcurrentSweepOrders = 1`. Run a full sweep of two or three holdings.
   Behaviour must be indistinguishable from before this phase: place, wait, settle, stash, next,
   one at a time. The status line reads `0/1 resting` or `1/1 resting` throughout. Any difference
   here is a bug in the demotion path, not in the concurrency.
3. **No slot churn.** Before anything else, with one order resting, watch
   `execution-audit-<league>.jsonl`. `SweepOrderMovedToRestingSlot` must appear **once** per placed
   order and `RestingOrderPromotedToActiveSlot` **once** per settled one. Alternating pairs on one
   attempt id are the ping-pong regressing, and it writes the bankroll file twice a frame.
4. **Two concurrent.** Set 2. The second order must go up while the first is still resting, on a
   different item, and a third must be refused until one settles. Watch the status line count the
   slots and the ages climb. Confirm `bankroll-<league>.json` holds the resting order under
   `RestingOrders` with `"Status": "Pending"`, and that `HasUnresolvedOrder` is `false` while both
   are merely pending.
5. **Settling wins over placing.** With two resting, let one time out. The sweep must promote and
   settle that one before opening another slot, even though a slot is free. Reserved principal and
   uncollected proceeds staying low is the whole reason for that ordering.
6. **A large batch.** Let one order fill for enough proceeds to need ten or more inventory stacks
   (200+ Chaos). The collection must wait for every stack to arrive rather than settling on the
   frame the order row disappears. This is the stage with the least tolerance for batch size, and
   the one that failed at nineteen stacks.
7. **The fill-during-settlement case** - the one this design exists for. With two resting, settle
   one while the other takes a partial fill. The collection must not abort: the filling order is a
   listed sibling and is compared on its immutable half only. Confirm the collected amount is
   credited exactly once and the audit records one credit.
8. **Foreign-order refusal.** Place an order by hand outside the sweep, then let a sweep try to
   place. It must refuse on the row it cannot account for and mark ambiguous, naming the count.
9. **Interruption.** With two resting, alt-tab out. Both orders must persist, both candidates must
   read `Failed`, nothing may be retried, and a reload must recover both. Then confirm the orphan
   path: with the sweep no longer running, one resting order is promoted into the active slot so the
   manual cancel/collect/stash hotkeys can reach it, and the next is promoted after that one stashes.
10. Raise to 3, run a full sweep, and only after a clean run raise toward 10.

### Phase 8.3 - sweep slots

The driver still calls the two-argument `Decide`, so a live sweep cannot reach a second slot yet.
Everything below is therefore a proof that the single-slot path is unchanged, plus the unit
coverage for the concurrent path that 8.4 switches on.

1. Confirm `sell sweep fills every slot it is given` and `sell sweep single slot reproduces the old
   machine` pass, and that the whole suite is green. The second is the rollback proof: it walks the
   old directive sequence tick for tick through the new state machine.
2. **A whole sweep is unchanged.** Run a sweep of two or three holdings end to end - price, place,
   wait, time out, cancel, collect, stash, next. Every status line, every directive, and the final
   realized total must be indistinguishable from before this phase.
3. **The cursor moved.** While one order is resting, the status line's candidate is now the *next*
   holding rather than the one that is out. That is the intended change and the only visible one;
   confirm the sold candidate is still credited to the right row when it stashes, not to the one
   the cursor is now on. `Advance` reaching the candidate through the slot is exactly what this
   checks.
4. **Revocation with an order out.** Alt-tab away while an order is resting. The sweep must mark
   ambiguous, not stop: `durable` is now "any slot exists", so an order that is out is never
   forgotten. Confirm the candidate that was out is `Failed` and nothing is retried.
5. **A skipped candidate does not disturb a slot.** Let one candidate fail its re-probe while
   another order is resting (a market that drains between plan and placement will do it). The
   failed candidate must be retired off the cursor and the resting slot must be untouched - same
   attempt id, same status, still resting afterwards.

## Open Risks

- The exchange-panel overlap is still live: `exchangeRight=1793`, with inventory columns at x=1696
  and x=1766 reported `clearOfExchange=False`. The return path needs at least one uncovered stack
  per type.
- Affinity behaviour for proceeds while a Fragment tab is visible is assumed symmetric with the
  known scarab-affinity case. The code now encodes that symmetry, but it is still an assumption
  until Phase 2 regression step 6 is run in game - that step is the only thing that confirms it.
- The 10-order cap is a supplied constant and is not verifiable from the SDK.
- Aborting a sweep with orders resting marks ambiguous and leaves those orders in `RestingOrders`.
  They are recoverable - the orphan promotion path hands them to the manual hotkeys one at a time -
  but nothing settles them automatically, and a sweep cannot be planned again until the order book
  is empty. A transient failure at placement time therefore costs more now than it did when only
  one order could ever be out.
- Concurrent resting orders relax `HasUnresolvedOrder` for the first time since it was written: a
  resting `Pending` order no longer blocks trading. Every other blocking state is unchanged, but
  this is the one place where the multi-order design trades a safety margin for the throughput it
  exists to buy, and it is the first thing to look at if the sweep ever acts while it should wait.
