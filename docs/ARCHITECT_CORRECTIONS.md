# ARCHITECT_CORRECTIONS.md — Senior architect review: design corrections + new findings
**Date:** 2026-10-19 · **By:** 5-architect strike team (billing, voice, data, platform, chief integrator)
**Why this file exists:** The QA sweep (`docs/AGENT_TEST_FIXES.md`) found the right problems, but several prescribed fixes were wrong or incomplete at the design level, and two bigger criticals were missed entirely. This file corrects the fixes and adds the new findings. Where this file and `AGENT_TEST_FIXES.md` disagree, **this file wins** — it is the later, deeper review.
**How to use:** Same Flaw → Fix → Rationale pattern. Items tagged "CORRECTS Hn/Mn" replace that item's fix design (the flaw description still stands).

---

## NEW CRITICALS (bigger than C1 as originally stated)

### P1. `MauiProgram.cs` doesn't exist — the app cannot compile, period
**Flaw:** `MainApplication` calls into `MauiProgram.cs`, which is absent. This is not a missing feature — the MAUI head has no entry point.
**Fix:** Create `MauiProgram.cs` with the standard MAUI builder: `CreateMauiApp()`, fonts, handlers, DI registrations for all platform adapters (with stubs where the real adapter is pending). This is Wave-0 work, serialized, one agent.
**Rationale:** C1 (missing `ReceiptRenderer`) was reported as "the build is broken." The architects found the break is deeper: without `MauiProgram.cs` there is no app at all. This is the true first compile gate.

### P2. `src/Angadi.Maui/Resources/` is missing — declared assets break the build
**Flaw:** The csproj's explicit `MauiIcon` include points at resources that don't exist (icon, splash, fonts). Missing explicit includes = build break.
**Fix:** Create `Resources/` with app icon, splash screen, and **Noto Sans Telugu as a `MauiAsset`**. Telugu text needs a Telugu font bundled — do not rely on the device having one.
**Rationale:** Same class as P1: the MAUI head was scaffolded in name only. Wave-0, same serialized agent as P1.

### C1-CORRECTION. The DI self-test is the wrong guard; shaping will break Telugu receipts
**Flaw (in the original C1 fix):** A startup self-test resolving handlers guards against missing *registrations*, but C1 was a missing *type* — a compile error the compiler already catches. A startup test also burns launch time on a shopkeeper's tablet.
**Fix:** Instead of a startup self-test: an **xunit test** in `tests/Angadi.Tests` that builds the real Application+Infrastructure graph with stub platform adapters and resolves every `IRequestHandler<>` — CI-time, not launch-time. Extend it to resolve `ISubscriptionService` and `IReceiptRenderer` explicitly. Note: `UnitConversion` may already exist in `Domain/Items` — verify before writing a duplicate.
**Rationale:** Compile errors belong to the compiler; registration errors belong to CI. Each guard at its proper layer.
**Additional (receipts):** SkiaSharp `DrawText` does **not** shape Indic text — conjuncts and vowel signs render broken. The csproj already references `SkiaSharp.HarfBuzz`: the renderer MUST shape via `SKShaper` with the Telugu TTF, plus a shaping smoke test (render "క్ష" and assert it doesn't come out as broken glyphs).
**Rationale:** An unshaped Telugu receipt is unreadable — the core deliverable of a billing app.

---

## FIX-DESIGN CORRECTIONS (this file wins over AGENT_TEST_FIXES.md)

### H2-CORRECTION. The monotonic-clock design has a hole — use per-month counters
**Flaw (in the original H2 fix):** Running the app once with the clock 2 months *forward* advances `last_seen`; setting it back then pins the quota month to a *future* month with count 0 → free bills until real time catches up.
**Fix:** Per-month counters in `Meta` (`quota.count.YYYYMM`) + `quota.last_seen_ms`. Effective month = local month of `max(now, last_seen)`; the future bucket keeps its own count. Increment inside the finalize transaction; one shared `QuotaTracker`. Document the unwinnable remainder: reinstall/factory-reset wipes Meta — v2 moves to a server entitlement.
**Rationale:** The original fix closed the rollback hole but opened a roll-forward hole. Per-month buckets close both.

### H3-CORRECTION. Three corrections to the safe-restore design
**Flaw (in the original H3 fix):** (1) `StockMilli>=0` in the invariant sweep would reject *valid* backups — negative stock is legal by design ("sold before stock-in recorded"). (2) WAL handling unspecified. (3) Content-URI staging unspecified.
**Fix:** (1) Drop the non-negativity check on stock from the sweep. (2) WAL is safe: closing the last connection auto-checkpoints; validate `restore-tmp` over a read-only connection disposed before the swap. (3) Stage the content-URI source to a temp file (scoped storage), then `File.Replace(tmp, live, live+".pre-restore")` — atomic rename plus safety copy in one.
**Rationale:** A restore validator that rejects legal data is as bad as one that accepts illegal data. The atomic rename is the only safe swap.

### H4-CORRECTION. Worse than reported — fix at the right layer
**Flaw (in the original H4 fix):** A `[0-9]\.[0-9]` pre-pass guards the splitter at the wrong layer. And actual behavior is worse than the QA report said: "2.5 కేజీల బియ్యం" → **7 kg**, not 5 kg.
**Fix:** Make `SplitPhrases` decimal-aware by construction: whitespace-split first, treat `.` as a separator only at token boundaries.
**Rationale:** Patching around a splitter with a regex pre-pass is fragile; the splitter itself must understand decimals.

### H6-CORRECTION. "Never sum" is underspecified — report uncertainty instead
**Flaw (in the original H6 fix):** "Never sum adjacent bare number words" leaves the fallback undefined — and every fallback (first number? 1?) is another confident-wrong quantity.
**Fix:** Allow `+=` only when place-value-descending (600+50 is a genuine compound; "రెండు మూడు" — 3≮2 — is ambiguous). Add `IsAmbiguous` to `ParsedLine`; the pipeline downgrades ambiguous lines to Confirm.
**Rationale:** The parser's job is to report what was said *including its uncertainty*. The pipeline, not the parser, decides what to do with uncertainty.

### M1-CORRECTION. Keep the parser fix, add a pharmacy guard in the matcher
**Flaw (in the original M1 fix):** "650 పారాసెటమాల్ రెండు స్ట్రిప్" under the M1 rule becomes qty **650 strips** (today: NoMatch). In a pharmacy that's a dangerous misparse.
**Fix:** Keep M1 in the parser; add a catalog-aware fallback in `ItemMatcher`: score below Confirm *and* a leading digit present → retry with the digit fused to the name front (పారాసెటమాల్650) → auto-add on match.
**Rationale:** Dosage numbers ("650") look like quantities but are part of the product identity. The matcher knows the catalog; the parser doesn't — so the fallback belongs in the matcher.

### M2-CORRECTION. Confidence-graded aliases beat FIFO + manual unlearn
**Flaw (in the original M2 fix):** FIFO-30 with a manual Unlearn is clunky and still lets one bad correction sit at 1.0 confidence until someone notices.
**Fix:** Learned aliases enter at **low confidence** (Confirm-only); graduate to auto-add after 2 consecutive confirmations; corrections decrement confidence, removal at 0 (Unlearn becomes emergent, not a separate feature). Add a **seed-collision guard**: never learn a key that matches another item's key or alias. LRU eviction for the learned list.
**Rationale:** The system should get *safer* with use, not just bigger. Confidence grading makes poisoning self-healing.

### M7-CORRECTION. `checked` blocks, not the assembly-wide flag
**Flaw (in the original M7 fix):** `<CheckForOverflowUnderflow>true</CheckForOverflowUnderflow>` would mislabel *any* `OverflowException` anywhere as a bill-amount error.
**Fix:** `checked` blocks in `BillCalculator` and the `Paise`/`Qty` operators (precise catch sites). Catch around `Compute` in the handler → `BillAmountTooLarge`. Keep the validator caps (qty ≤ 100,000 units, price ≤ ₹1 Cr/unit).
**Rationale:** Overflow handling must be precise about *whose* overflow it is. A global flag turns every overflow into a wrong diagnosis.

### M9-CORRECTION. Hard reject, not an info code
**Flaw (in the original M9 fix):** Returning a `BillDiscountClamped` info code still lets a "150%" typo become free goods if the UI doesn't display it.
**Fix:** `Percent > 10000bp` → hard validation failure naming the line. No silent clamping path.
**Rationale:** A typo'd discount must stop the bill, not quietly give the shop away.

### H8-CORRECTION. Never silently remap tax slabs
**Flaw (in the original H8 fix):** Silently remapping 12→5 / 28→40 is not 1:1 — it creates wrong-tax liability on existing items.
**Fix:** Fail closed at finalize with `BillLegacySlab` + a one-time catalog report listing affected items for shopkeeper confirmation. Migration matches seeds by `EnglishName` (seed GUIDs are random), updates only rows still on legacy slabs, records `seed.slab_migration_v1` in Meta. Never rewrite finalized history — bill lines already snapshot `GstBasisPoints`.
**Rationale:** Tax remapping without the shopkeeper's eyes is a compliance landmine. Fail closed, show the list, let the human confirm.

### H9-EXTENSION. Dues payments need `UpiRef` too
**Flaw:** The original H9 covers bills only. UPI dues *collection* is routine and equally unreconcilable today.
**Fix:** `RecordDuesPaymentCommand` / `DuesEntry` get `UpiRef` as well.
**Rationale:** Same money, same evidence requirement.

### H1-APPROVED (do not "improve")
The architects explicitly approve H1 as specified. Do NOT extend it with partial-payment reversal logic — the handler must never invent cash movements; real refunds are v2. Fail closed, name the amounts.

### M3-RELOCATED, M8-APPROVED, H7-APPROVED
- **M3:** defaults belong in `VoiceBillingPipeline` (from `VoiceOptions`); the parser reports what was said, nothing more.
- **M8:** approved — plus make `LoadErrors` lenient (missing te/en props throw today; add a hardcoded final fallback).
- **H7:** approved — `appsettings.json` must be a `MauiAsset` under `Resources/Raw`; fold M10 (prices into `BillingOptions`) here.
- **H11-SPLIT:** `ExportLogsCommand` + its test now; the Settings-screen entry is blocked (no pages/VMs exist yet) — track it, don't fake it.
- **M4/M6:** approved — seam goes in `Angadi.Application.Platform`; seeds append one `ItemsSeeded(count)` outbox row, not 120.

---

## NEW FINDINGS

### B1. `ChangeDue` goes negative on udhaar/UPI bills — HIGH
**Flaw:** `BillingHandlers.cs:124` computes change as if every tender were cash: a udhaar bill shows change due of −₹200.
**Fix:** Return `Paise.Zero` unless tender is cash; reject non-zero `CashReceived` on UPI/Udhaar tenders.
**Rationale:** Negative change-due on a credit bill is nonsense the shopkeeper will "fix" by handing over cash.

### B2. Quota check races the finalize — HIGH
**Flaw:** The quota check isn't atomic with the finalize: two rapid finalizes at 49/50 bills → 51 bills get through.
**Fix:** Run the quota check inside the save transaction; unify with `CanFinalize`.
**Rationale:** A revenue gate with a race condition is a gate that fails exactly when someone pushes it.

### P3. `IReceiptShare` is orphaned — HIGH
**Flaw:** `ShareReceiptHandler` depends on two services with no implementations and no DI registration (same disease as C2).
**Fix:** Android share-sheet adapter + registration, same pattern as C2.
**Rationale:** WhatsApp receipt sharing is a headline feature; the handler is dead code without its adapter.

### P4. No speech fallback chain — HIGH
**Flaw:** te-IN `SpeechRecognizer` with no fallback — dead on non-GMS / cheap tablets with no Telugu pack.
**Fix:** Fallback chain: te-IN → device default locale → manual entry. Never a dead mic button.
**Rationale:** The mic button is the product's face. A dead mic on a cheap tablet is a dead product.

### V1. `వెయ్యి` (thousand) is in the filler list — HIGH, money bug
**Flaw:** "వెయ్యి రూపాయల బియ్యం" (rice for a thousand rupees) silently drops the amount — the word sits in the filler list.
**Fix:** Move `వెయ్యి` to the number words immediately. Add a regression test with the exact phrase.
**Rationale:** Silently dropping "thousand" from a price is a money bug wearing a parser's clothes.

### Mediums (condensed)
- **Voice:** no repair/negation handling ("అది కాదు" should truncate to the last clause); missing measure words (కట్ట/గెల/పిడికెడు → Piece); English number words absent; short-name threshold cliff (len≤4 + sim≥0.66 → Confirm, never NoMatch — needs a floor); spoken "పాయింట్" decimals; approximatives/politeness missing from fillers; fused-hundreds orthography gaps.
- **Data:** backups are **plaintext** — customer phones and udhaar balances leave the device unencrypted (AES-GCM + PBKDF2); udhaar dues moves lack a customer-aggregate outbox event (v2 can't reconstruct the ledger); outbox needs `PayloadVersion` + stable event names now (cheap today, painful later).
- **Billing:** voided bills count toward quota — acceptable only once B2 (atomic quota) is fixed.
- **Deferred to v1.1:** all LOW renames — renaming mid-sprint breaks 20 agents' references. Defer M12 similarly to dodge the `Mapping.cs`/`BillRow` conflict with H9.

---

## 2-DAY SEQUENCING (replaces the Day-1 table in BATTLE_PLAN_2DAY.md)

**Wave 0 — first hours, SERIALIZED, 3 agents (nothing else starts):**
1. **C1 agent:** `ReceiptRenderer` (HarfBuzz shaping via `SKShaper` + Telugu TTF + smoke test), `UnitConversion` (verify existence first), **`MauiProgram.cs` (P1)**, **`Resources/` (P2)** — icon, splash, Noto Sans Telugu — DI registrations, xunit DI-resolution test.
2. **C2 agent:** `StubSubscriptionService` in Infrastructure (stateless, `stub_*` keys, never grants Pro) + registration — after the C1 agent's DI edits (same file; rebase).
3. **Voice agent:** serialized `TeluguParser.cs` pass — H4 (decimal-aware splitter), H6 (descending-compound + `IsAmbiguous`→Confirm), M1 (+matcher dosage fallback), M3 (pipeline defaults), M13. Re-target the battery for H5 (it asserts the old auto-add behavior).

**Wave 1 — C1 green, parallel lanes:** Billing (H1, H8, B1, B2, M9 hard-reject, H10 tests) · Data (H2 per-month counters, H3 corrected restore, H9+dues, M4/M6, backup encryption) · Platform (H7, P3 share adapter, P4 fallback chain, `ExportLogsCommand`, golden-path pages) · Strings (single agent, one te/en pass: H1/H8/M10/M11).

**Day 2:** M15 40-phrase battery (the voice exit gate), H5 seed harvest, M2 confidence-graded aliases, demo-path UAT, bug-bash. Fillers: M7 checked blocks, M8 lenient resources, V1 వెయ్యి fix.

**Critical path:** C1 (compile + MauiProgram + Resources) → H7 → M10 ∥ C1 → receipt page → share ∥ C1 → parser pass → M15. **Every chain starts at C1.**

**File-ownership rules (avoiding the 20-agent pileup):** `DependencyInjection.cs` and `MauiProgram.cs` — serialized, one agent each, rebase after. `TeluguParser.cs` — one serialized voice pass, never co-edited. Seed JSON — same data-lane agent, schema agreed first. `errors.json`/Telugu strings — single strings agent. Handler code vs handler tests may parallelize (different files).

**CUT for v1 (v1.1 hardening):** real Play Billing client (stub stands), speech/scanner adapters (manual entry covers the demo), pages beyond the POS golden path, H2's clock-rollback remainder (documented leak), LOW renames, M12/M14.

**Top convergence risks:** (1) Uncompilable base + 20 parallel agents — C1 lands first via a single agent, one merge captain builds hourly, never force-push. (2) Adapters are interfaces-only — stub + mocks; adapters never gate UAT. (3) `TeluguParser.cs` is a five-fix pileup — single serialized pass, M15 battery as the hard exit gate.

## v2-retrofit notes (cheap now, painful later — do during Wave 1 if time allows)
Outbox `PayloadVersion` + stable event names; `NextBillSequence` needs a device prefix for multi-device v2; normalize `DateTimeOffset` to UTC on server ingest. Already v2-friendly: GUID PKs, Unix-ms UTC timestamps, paise money.
