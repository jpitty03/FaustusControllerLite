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
3. Select Divine offered and Chaos wanted. Compare `MarketRateGet`, `MarketRateGive`, and the first five rows of both stock books with the report.
4. Reverse the pair, capture again, and confirm the latest-rate record count remains one for this league/pair.
5. Open wanted and offered pickers separately. Confirm side, metadata, owned count, option rectangle, and catalogue match.
6. Open stash and inventory. Confirm both visibility reads are plausible.
7. Inspect a disposable pending/completed order. Record the matching `PlayerOrderId` and order-element tree; do not infer click indices.

## Three-Market Matrix

1. In one area, manually capture Divine/Chaos, target/Chaos, and target/Divine.
2. Open picker options containing Chaos and Divine so their live owned counts are observed.
3. Confirm the status reports either an exact candidate or explicit rejection reasons.
4. Repeat one pair and confirm it replaces the prior canonical record.
5. Change area and confirm prior captures cannot form a coherent calculation session.

Input automation remains blocked until all reads above are plausible in game.
