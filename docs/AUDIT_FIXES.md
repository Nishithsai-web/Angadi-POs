# AUDIT_FIXES.md — Code audit findings, ordered by severity
**Date:** 2026-10-19 · **Scope:** WIP snapshot (8 commits, Day 1–5 + CI) · **Auditor:** external review against `Angadi_Architecture_Blueprint.md`
**Verdict:** Foundations are strong — billing math, architecture tests, error catalog, and voice battery are real, not theater. No critical flaws. Work through the items below in order. Commit each fix separately with its tests green.

**How to use this file:** Each item names the file, the flaw, and the exact change. After all fixes, run the full suite + coverage gates. Then delete this file (or move to `docs/archive/`).

---

## MEDIUM

### 1. Parser eats leading bulk quantities
**File:** `src/Angadi.Application/Voice/TeluguParser.cs` → `DigitsThatBelongToName`
**Flaw:** Any bare digit ≥100 is treated as a product-name fragment, so "200 గుడ్లు" (200 eggs) misparses instead of becoming quantity 200.
**Fix:** Right after the `unitFollows || currencyBefore` early-continue, add:
```csharp
if (i == 0) continue; // a bare leading number is a quantity ("200 గుడ్లు"), never a product code; codes ("dolo 650") trail the name.
```
Trailing-digit behavior ("dolo 650") is unchanged.

### 2. AliasLearner evicts curated seed aliases
**Files:** `src/Angadi.Application/Voice/AliasLearner.cs`, `src/Angadi.Domain/Items/Item.cs`
**Flaw:** After ~30 learns on one item, `RemoveAt(0)` deletes seed aliases (e.g. "biyyam") because seed and learned aliases share one list.
**Fix:** In `Item.cs`, split into `Aliases` (seed, never evicted) and `LearnedAliases` (capped FIFO at 30). The learner writes only to `LearnedAliases`. The matcher and the seed-collision test must consider both lists.

### 3. Declare the sync seam (v2 contract)
**File:** `src/Angadi.Application/Persistence/Repositories.cs` (or new `Sync.cs`)
**Flaw:** `Outbox` accumulates `SyncState=0` rows, but no `ISyncService` interface declares the v2 contract — the seam is implied, not written. (Blueprint §1: FloBiz's 6-month sync-build warning is why this must exist before the code does.)
**Fix:** Add:
```csharp
/// <summary>v2 seam — no implementation in v1; Outbox accumulates SyncState=0 rows for it.</summary>
public interface ISyncService { Task<Result<int>> DrainAsync(CancellationToken ct = default); }
```

### 4. Retroactive sign-off on contracts
**Files:** `docs/SRS.md` (§0), `docs/ARCHITECTURE.md` (§4)
**Flaw:** The approval boxes are unticked, yet 8 commits were built on these contracts — the "no code before sign-off" gate was bypassed on Day 1.
**Fix:** Owner: tick both approval boxes (or reply "approved") before agents build Days 7+ on these contracts. Otherwise the gate is theater and contract drift goes unnoticed.

---

## TEST GAPS — add these named cases

5. `VoicePipeline_QuantityOnlyUtterance_ReturnsVoiceNoMatch` — `ProcessUtteranceAsync("అర కేజీ")` must return Failure `VOICE_NO_MATCH` (parser drops it; pipeline must surface feedback — currently untested).
6. `Parser_TeluguScriptDigits` — "౨ కేజీలు బియ్యం" → qty 2000 milli (`Normalize` maps ౦–౯ but no test proves it).
7. `Parser_LeadingLargeDigit_IsQuantity` — "200 గుడ్లు" → Qty 200000 milli, name "గుడ్లు" (goes with fix #1).
8. `FinalizeBill_CashLessThanTotal_Rejected` — CashReceived ₹100 on a ₹175 bill → `BILL_CASH_SHORT` (validator exists, no test).
9. `VoidBill_RestoresStock_AndReversesUdhaar` — finalize udhaar bill → void with reason → assert stock restored, dues zero, status VOID, outbox contains `BillVoided` (FR-16 untested end-to-end).
10. `FreeTier_FiftyFirstBill_Blocked` — 50 finalized bills in-month → 51st finalize returns `FREE_LIMIT_REACHED`; viewing/reports/sharing still allowed (FR-34 untested).
11. `AliasLearner_NeverEvictsSeedAliases` — item with seed aliases + 35 learns → all seed aliases intact (goes with fix #2).

---

## NITS (5-year codebase hygiene)

12. `errors.json` → `FREE_LIMIT_REACHED` hardcodes "₹200/నెల". Render it from `BillingOptions` (e.g. "{Price}/నెల") so a price change doesn't require a string hunt; add a test.
13. `VoicePipeline.cs` contains five interfaces and zero pipeline logic — rename to `VoiceContracts.cs` (implementation already lives in `VoiceBillingPipeline.cs`).
14. `TeluguText.MatchKey`: "పాలు" (milk) survives plural-stripping only via the `Length > 4` guard — add regression test `MatchKey("పాలు") == "పాలు"` so a future threshold tweak can't silently break milk.
15. `ItemMatcher.Match` scores every item per utterance — fine under ~1k items; leave a `// PERF:` note to add a prefix prefilter past ~2k items.
16. `BillCalculator.DivHalfUp`: wrap `2 * numerator` in `checked` so absurd inputs fail loud instead of wrapping silently.

---

## Could not verify (needs a green CI run / later commits)
- CI green and local compilation (agents' sandbox had no .NET SDK; treat "builds + tests green" as unproven until the first green Actions run is observed).
- Platform adapters (`ISpeechToText`, `IBarcodeScanner`, Play Billing) — interfaces exist, Android implementations pending (expected; 12 agents still on UI/receipts/platform). The te-IN speech path is unproven; verify before UAT.
