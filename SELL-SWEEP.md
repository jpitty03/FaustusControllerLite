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

## Invariants To Preserve

- Every `Allow*` setting and hotkey defaults off/unbound.
- The sweep coordinator composes verified controllers and sends no input itself.
- Every input effect still requires foreground Path of Exile, the same league and area, a closed
  picker, no popup, no held modifiers, an unchanged commanded cursor, and an unexpired deadline.
- Currency identity remains exact metadata; names stay display-only.
- All economics stay exact-integer; no floating point enters a decision.
- One order in flight; canonical state keeps a single tracked order.

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

## Open Risks

- The exchange-panel overlap is still live: `exchangeRight=1793`, with inventory columns at x=1696
  and x=1766 reported `clearOfExchange=False`. The return path needs at least one uncovered stack
  per type.
- Affinity behaviour for proceeds while a Fragment tab is visible is assumed symmetric with the
  known scarab-affinity case. The code now encodes that symmetry, but it is still an assumption
  until Phase 2 regression step 6 is run in game - that step is the only thing that confirms it.
- The 10-order cap is a supplied constant and is not verifiable from the SDK.
