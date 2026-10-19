# data-flow.md — How a bill moves through Angadi

## Voice billing (the core loop)
1. **Capture:** Android `SpeechRecognizer` (te-IN) → Telugu text. iOS: same interface, adapter pending.
2. **Parse** (`TeluguParser`): text → candidate (qty, unit, item-name fragments). Handles free word order, fractions (అర/పావు/సగం), fused tokens (2కేజీలు), code-mixed ("dolo 650"). Ambiguous input → Confirm, never a confident wrong number.
3. **Match** (`ItemMatcher`): candidate → catalog item. Score ≥0.90 auto-add; 0.70–0.90 Confirm with candidates; <0.70 NoMatch with feedback. Learned aliases feed back into matching.
4. **Draft** (`BillDraft`): lines accumulate; qty/unit defaults apply (M3); running total in paise.

## Finalize (money moves here — every step in one UoW transaction)
1. `FinalizeBillValidator`: cash ≥ total, no legacy GST slabs (H8), discount ≤100% (M9), quota <50 (H2), subscription state.
2. `BillCalculator`: paise math, GST-inclusive back-calculation per slab, half-up rounding, explicit `RoundingAdjustment` line. Idempotent.
3. Write `Bill` + lines → decrement stock via `StockLedger` → post udhaar dues if tender=Udhaar → append `BillFinalized` to outbox (`SyncState=0`) → commit. Commit happens **only** if the `Result` succeeds.

## Void
Fail closed first: if `DuesBalance < GrandTotal` → `BillVoidBlockedByPayments` (H1). Else reverse in one transaction: restore stock, reverse dues, mark VOID, append `BillVoided` to outbox.

## Tenders
Cash (change due computed, cash-only), UPI (optional `UpiRef` recorded, H9), Udhaar (dues posted to customer). Every tender path appends to the outbox.

## Outbox → Sync (v2 seam)
`Outbox` accumulates `SyncState=0` rows on every money/data mutation (M6: customers, shop profile, seed too). `ISyncService.DrainAsync` (declared, no v1 implementation) will drain them in v2.

## Backup / Restore
Backup: copy SQLite file + marker + version → share sheet. Restore: into `<db>.restore-tmp` → `integrity_check` → invariant sweep (stock ≥ 0, dues ≥ 0, lines join, totals recompute) → atomic `File.Replace`; failure auto-restores the safety copy (H3).

## Why this exists
New agents (10 just joined) get the money path in one page instead of reverse-engineering it from code. If a change breaks this flow, the change is wrong — not the flow.
