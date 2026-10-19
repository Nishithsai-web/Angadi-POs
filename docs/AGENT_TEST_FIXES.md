# AGENT_TEST_FIXES.md — 5-agent QA sweep findings, ordered by severity
**Date:** 2026-10-19 · **Audited code state:** `src/` as of the Day-1–5 build (8 commits; docs-only commits on top do not affect findings)
**How to use this file:** Work top-down. Every item states the **Flaw** (what's wrong, concretely), the **Fix** (exact change), and the **Rationale** (why this way). Items marked "extends AUDIT_FIXES #N" build on that file — do not duplicate. Commit each fix separately, referencing the fix ID, with its tests green. Never force-push; `git pull --rebase` before every push.

**PROCESS NOTE — C3 WITHDRAWN:** An earlier draft of this report claimed pushed docs were erased from `main` (C3). **Verified false:** `docs/AUDIT_FIXES.md` and `docs/AGENT_WORKFLOW.md` (§9 included) are intact on `main`. The "missing" commits were a stale commit-listing cache in the review tooling, not a force-push. Do NOT re-push those files. The real process rule stands: never force-push, always rebase onto the current head.

---

## CRITICAL

### C1. Build is broken — two missing pieces
**Flaw:** `Infrastructure/DependencyInjection.cs` references `Angadi.Infrastructure.Receipts.ReceiptRenderer`, but no `Receipts/` folder exists. `UnitConversion` (used in `BillDraft.cs:71,74`) has no definition anywhere. The solution does not compile.
**Fix:** Implement `Receipts/ReceiptRenderer.cs` (FR-17 receipt fields + Telugu text shaping) and the `UnitConversion` type. Then add a DI container self-test that resolves every registered `IRequestHandler<>` so this class of break is caught forever.
**Rationale:** Nothing else matters until the code compiles — every agent is currently building on a broken base. The self-test converts a recurring human-error category into a compile-time guarantee.

### C2. Play Billing unwired — every sale fails
**Flaw:** `FinalizeBillHandler` and `GetBillQuotaHandler` require `ISubscriptionService`, which has no implementation and no DI registration. The first bill finalize throws; the shopkeeper sees "Something went wrong" on every bill.
**Fix:** Register a stub `ISubscriptionService` returning `IsPro=false` immediately so the app runs; implement the real Android Play Billing client next. Add handler tests for both Pro and non-Pro paths.
**Rationale:** A stub unblocks all other development and testing today; the real billing client is a platform task that must not gate the billing engine, quota logic, or UI work.

---

## HIGH

### H1. Void of a partially-paid udhaar bill bricks the customer account (extends AUDIT_FIXES #9)
**Flaw:** A ₹200 udhaar bill with ₹50 paid, when voided, posts a full ₹200 reversal → dues balance goes −₹50 → the overpayment validator then rejects every future payment on that account forever, and the ₹50 cash is unrecorded anywhere.
**Fix:** In `VoidBillHandler`, fail closed *before* mutating: if `DuesBalance < GrandTotal`, return a new `BillVoidBlockedByPayments` failure ("settle or refund first"). Add te/en strings and a test.
**Rationale:** Money must never silently disappear, and an account must never enter a state no valid action can leave. Failing closed with a clear message is the only safe behavior.

### H2. Free tier bypassed by clock rollback; reinstall wipes the counter
**Flaw:** Quota is `COUNT(*) WHERE Day=yyyymmdd` from the device clock. Roll the clock back a month → 50 more free bills, forever. Reinstall wipes the rows entirely.
**Fix:** Store `quota.high_water_ms` / `last_seen_ms` in the Meta table; quota month = month of `max(now, last_seen)`, monotonic. Test with a fake `IClock` (rollback → count unchanged). Document the reinstall path; v2 moves to a server entitlement.
**Rationale:** The free tier is the revenue gate — a trivially bypassable gate is the same as no gate. Monotonic time is the standard client-side defense.

### H3. Restore accepts crafted backups and can brick the live DB
**Flaw:** `IsAngadiBackup` checks only a file marker + version — a hand-built SQLite file with negative stock or inflated dues imports cleanly. Worse, `ReplaceWithAsync` overwrites the live DB with no rollback: a mid-copy failure (disk full) leaves a truncated database and a dead app.
**Fix:** Restore into `<db>.restore-tmp`; run `PRAGMA quick_check` / `integrity_check`; pre-swap invariant sweep (`StockMilli>=0`, dues ≥ 0, bill lines join live Bill+Item, recompute totals); then atomic `File.Replace`. On failure, auto-restore the safety copy. Test with a crafted DB per violation type.
**Rationale:** Backup/restore is the shopkeeper's disaster recovery. A restore path that can destroy the live database is worse than no restore at all.

### H4. Decimal quantities silently become wrong quantities
**Flaw:** "2.5 కేజీల బియ్యం" → `SplitPhrases` splits on `.` *before* tokenizing → 5 kg gets auto-added. The `Decimal()` regex exists but is dead code — it never sees the input.
**Fix:** Protect the `[0-9]\.[0-9]` pattern before phrase splitting. Add test `Parse_DecimalQuantity`: "2.5 కేజీల బియ్యం" → 2500 milli.
**Rationale:** Silent wrong quantities are the worst voice failure mode — the shopkeeper trusts the bill and overcharges. A misparse must never become a confident wrong number.

### H5. Generic alias auto-adds the wrong item; transliterated English never matches
**Flaw:** "నూనె" exact-matches sunflower oil's alias at 1.0 and auto-adds — but the shopkeeper usually means groundnut oil. Meanwhile "రైస్"/"షుగర్" (how shopkeepers actually speak) score ~0 → NoMatch.
**Fix (seed/JSON data, per ADR-5):** demote generic aliases (`నూనె`, `సబ్బు`, `oil`) so they route to Confirm instead of auto-add; harvest transliterated aliases (`రైస్`, `షుగర్`, `ఆయిల్`); add `సగం`/`అర్ధ` to fractions and dative forms (`రూపాయలకు`/`రూపాయలకి`) to currency tokens. One test per phrase.
**Rationale:** Voice billing lives or dies on matching how shopkeepers *actually speak*, not dictionary Telugu. A wrong auto-add bills the wrong item — a Confirm is always cheaper than a wrong bill.

### H6. Ranges summed into false quantities; dative currency misread as counts
**Flaw:** "రెండు మూడు కేజీల బియ్యం" → `number += value` → 5 kg auto-added. "పది రూపాయలకు ఉల్లిపాయలు" (onions for ten rupees) → parsed as "10 onions".
**Fix:** Never sum adjacent bare number words (keep the `రెండు వందల` multiplier path for genuine compounds); treat dative-marked currency as an amount, not a quantity. Tests for both phrases.
**Rationale:** Same principle as H4 — ambiguous input must degrade to Confirm, never to a confident wrong quantity.

### H7. Options pattern declared, never wired
**Flaw:** `VoiceOptions`/`BillingOptions`/`InsightsOptions` exist, but there are zero `Configure<T>` calls and no `appsettings.json` — thresholds, caps, and windows are compile-time constants.
**Fix:** Add `appsettings.json` to the MAUI head and bind the options in `MauiProgram`. Add test `VoiceOptions_Defaults_MatchSRS` pinning the 0.90/0.70 thresholds.
**Rationale:** Unwired options are dead documentation. The price change (₹200) must not require a code edit — this is the same class of issue as AUDIT_FIXES #12.

### H8. Legacy 12%/28% GST slabs flow into new bills
**Flaw:** GST 2.0 abolished the 12% and 28% slabs (22 Sep 2025). A ₹1120@12% item finalized today computes the wrong tax, and nothing stops it.
**Fix:** `FinalizeBillValidator` rejects legacy slabs with a new `BillLegacySlab` error; one-time seed migration moves existing items to current slabs.
**Rationale:** Wrong tax on a GST receipt is a compliance defect, not a cosmetic one. The validator is the last line of defense — it must know the law changed.

### H9. UPI tender captures no reference
**Flaw:** `Bill` has no `UpiRef` field. A ₹500 "UPI paid" bill records zero evidence of the payment — day-end UPI totals can't reconcile against UTRs.
**Fix:** Add `string? UpiRef`; accept it as an optional tender parameter; persist it, print it on the receipt, and add a UPI column to the day-end report.
**Rationale:** UPI is the primary digital tender. Without a reference, every UPI dispute becomes unresolvable — the shopkeeper's word against the customer's.

### H10. Handler layer implemented but untested
**Flaw:** Finalize/void/quota (FR-14/16/34), dues reminders (FR-23), stock adjust (FR-24), reports (FR-27–30), insights (FR-31–33), BillDraft (FR-11) — implemented, zero tests.
**Fix:** Add the named tests: cash-short rejection, void-restores-stock, 51st-bill block, reminder happy/no-phone paths, report math, forecast buckets, draft merge rules.
**Rationale:** Untested handlers are where money bugs hide (see H1). The billing engine has 2,000 fuzz bills; the handlers that *move* the money have none.

### H11. FR-36 export-logs not wired; platform UIs pending (expected WIP, not defects)
**Flaw:** `LogFiles.ExportZip` exists but no `ExportLogsCommand`, no Settings-screen wiring, no test. Speech/scanner/share/subscription Android implementations and MAUI pages are absent.
**Fix:** Wire the export command + Settings entry + test. Track platform bodies and pages as the remaining v1 bulk.
**Rationale:** Export-logs is the support lifeline for a field app — without it, every shopkeeper bug report is undebuggable. Platform gaps are scheduled work, listed here so they aren't forgotten.

---

## MEDIUM

### M1. Leading bulk quantities mishandled (extends AUDIT_FIXES #1)
**Flaw:** "200 గుడ్లు" → the digits are swallowed into the product name → Confirm with the quantity lost or defaulted.
**Fix:** In `DigitsThatBelongToName`, add `if (i == 0) continue;` — a bare leading number is a quantity, never a product code. Test `Parse_BulkCountNoUnit`: "200 గుడ్లు" → Qty 200.
**Rationale:** Bulk counts ("200 eggs", "50 packets") are everyday counter speech. Codes like "dolo 650" trail the name; leading numbers are quantities.

### M2. AliasLearner evicts seeds; poisoning has no undo (extends AUDIT_FIXES #2, #11)
**Flaw:** One wrong voice correction becomes a permanent 1.0 alias (poisoning, no undo); overflow `RemoveAt(0)` deletes curated seed aliases from the shared list.
**Fix:** Split `Aliases`/`LearnedAliases` in `Item.cs` — the learner writes only to learned, FIFO-capped at 30. Add an `Unlearn`/correction-count threshold so repeated corrections can demote a bad alias. Tests: conflicting alias forces Confirm; 35 learns keep all seed aliases.
**Rationale:** A learning system without undo learns mistakes permanently. Seeds are curated data; learned data must never be able to destroy them.

### M3. No quantity/unit defaults (extends AUDIT_FIXES #5)
**Flaw:** "కేజీ బియ్యం" and "ఉప్పు" yield `Qty=null` — the draft can't bill them.
**Fix:** Default qty=1 when a name is present; default 1×unit when a unit is present. Quantity-only utterances ("అర కేజీ") → `VoiceNoMatch` with spoken feedback.
**Rationale:** "Salt" means one salt. Forcing the shopkeeper to always say a quantity makes voice slower than typing — defaults are what make voice billing feel magical.

### M4. ISyncService seam still undeclared (extends AUDIT_FIXES #3)
**Flaw:** Only a comment in `Rows.cs:122` — no interface declares the v2 sync contract.
**Fix:** Add the interface with the v2 doc comment (3 lines, per AUDIT_FIXES #3).
**Rationale:** The outbox accumulates rows for a consumer that doesn't exist yet. The interface is the contract that consumer will implement — without it the seam is a hope, not a design.

### M5. Sign-off boxes unticked (extends AUDIT_FIXES #4)
**Flaw:** SRS §0 and ARCHITECTURE.md §4 approval boxes unticked while Days 7+ build on the contracts.
**Fix:** Owner ticks/approves before further building.
**Rationale:** The approval gate is the only thing that keeps 22 agents building against the same contracts. Unticked, it's theater.

### M6. Outbox misses customer/shop/seed mutations
**Flaw:** `SaveCustomerHandler`, `SaveShopProfileCommand`, `SeedItemsCommand` never append to the outbox — v2 sync will never see these changes.
**Fix:** Add `CustomerChanged` / `ShopProfileChanged` / `CatalogSeeded` events; append in the same UoW transaction; tests.
**Rationale:** A sync that misses customer and catalog changes syncs the money but loses the master data — the worst kind of partial sync.

### M7. Unchecked arithmetic (extends AUDIT_FIXES #16)
**Flaw:** No `CheckForOverflowUnderflow`; `SplitFor` wraps past ~₹9.2L-crore per slab; `FromRupees`/`Qty.Whole` unchecked.
**Fix:** Set `<CheckForOverflowUnderflow>true</CheckForOverflowUnderflow>`; map `OverflowException` → new `BillAmountTooLarge` error; validator caps (qty ≤ 100,000 units, price ≤ ₹1 Cr/unit).
**Rationale:** Silent integer wraparound on money is a catastrophic failure mode. Absurd inputs must fail loud, not wrap to small numbers.

### M8. Corrupt resources brick startup
**Flaw:** `JsonLocalizer` parses at DI composition — a truncated JSON or duplicate key across area files crashes the app at startup, and the dup-key check throws at *runtime*.
**Fix:** try/catch → fall back to English/last-good resources; add a `resVersion` check; move the dup-key check into a CI test.
**Rationale:** A bad string file must never be able to prevent the app from opening. Resource loading is the most hostile-input-adjacent code in the app.

### M9. 100%+ discounts silently clamp
**Flaw:** A 150% bill discount finalizes at 100% off with no warning to the shopkeeper.
**Fix:** Validator rejects `Percent > 10000bp`, or return a `BillDiscountClamped` info code for UI display.
**Rationale:** Silently giving everything away free is a money bug wearing a validation mask. The shopkeeper must see what happened.

### M10. Price/quota hardcoded in user strings (extends AUDIT_FIXES #12)
**Flaw:** `errors.json` `FREE_LIMIT_REACHED` ("₹200/నెల") and `sub.monthly` ("₹200 / నెల") hardcode the price.
**Fix:** Render from `BillingOptions` / the Play Billing product at runtime (same pattern as AUDIT_FIXES #12).
**Rationale:** A price change must not require a string hunt across two languages.

### M11. Generic validation error hides six actionable cases
**Flaw:** Dues-overpayment, no-phone, no-dues, missing void reason, and negative price all render the same "check the details" message.
**Fix:** Specific error codes + concrete Telugu copy (e.g. the dues-exceeds error names the actual balance via `Localizer.Format`).
**Rationale:** A shopkeeper can't act on "check the details". Error messages are UI — each must say what happened and what to do.

### M12. `VoidedAt` uses the wrong UTC offset
**Flaw:** `Mapping.cs:86` reuses the finalize offset for the void timestamp.
**Fix:** Persist `VoidedOffsetMinutes` on `BillRow` and use it for voids.
**Rationale:** Wrong timestamps on void records corrupt the audit trail — and voids are exactly the records auditors ask about.

### M13. Fused tokens lose quantities; dialect and stutter pollute names
**Flaw:** "అరకేజీబియ్యం" → qty null; "అరడజను గుడ్లు" → the 6 is lost; dialect "బియ్యము" → Confirm (`MatchKey` lacks the `ము`→`ం` rule); stutter/filler ("బి బియ్యం", "మరి …") pollute item names.
**Fix:** Multiplier-suffix splitting in `Explode`; add the `ము`→`ం` normalization rule (or seed aliases); drop 1-char unknown fragments; extend the filler-word list. One test per phrase.
**Rationale:** Real counter speech is fast, fused, and dialectal. The parser must meet shopkeepers where they are, not where the dictionary is.

### M14. Top-items ranks by value only
**Flaw:** FR-29 wants quantity ranking too; the query only sorts by value.
**Fix:** Add `TopItemsSort { ByValue, ByQuantity }` to the query.
**Rationale:** "What sells most" (quantity) and "what earns most" (value) are different business questions — the report must answer both.

### M15. FR-21/22 untested; NFR-12 integration battery missing
**Flaw:** No overpayment-rejection or bad-phone tests; no 40-phrase pipeline battery.
**Fix:** Add both handler tests; add the 40-phrase battery asserting ≥35 auto-add.
**Rationale:** The voice pipeline's headline claim (29/30 auto) needs a permanent regression battery, or it will rot silently.

---

## LOW (condensed)

- extends AUDIT_FIXES #13: rename `VoicePipeline.cs` → `VoiceContracts.cs`; #14: `MatchKey("పాలు")` regression test; #15: `// PERF` prefilter note past ~2k items.
- Naming honesty: `FinalizeBillCommand(Bill Draft)` parameter is typed `Bill` but a `BillDraft` class exists → rename the parameter; `Application/Persistence/Repositories.cs` vs Infrastructure same-name → rename the Application one; `LogFiles` → `LoggingSetup`.
- Magic numbers → options/constants: customer search `LIMIT 50`, `MaxAliasesPerItem=30` → `VoiceOptions`, morning-summary `-7`/`/7` → `MorningSummaryDays`, query default limits as named constants.
- `BILL_CASH_SHORT` never states the shortfall → template it (`ఇంకా {0} కావాలి`); `BILL_QTY_INVALID` should name the offending line.
- Move correlation-scope creation above validation in `Messaging.cs`; log learned alias text at Debug in `AliasLearner`.
- `ChangeDue` must be cash-only (udhaar showing −₹200 is nonsense); reject undefined `TenderType` casts; `AddFromVoice` must not coerce qty ≤ 0 → 1 — surface the validation error instead (also guards divide-by-zero in `FromAmount`).
- Defense in depth: `PRAGMA busy_timeout=5000`; map UNIQUE-violation on barcode → `BARCODE_DUPLICATE`.
- `MaxCandidates=3` hides 2 of 5 dals on generic "పప్పు" — raise the limit or paginate.
- Add the missing NFR-07 (correlation-id) and NFR-08 (no-phone-in-logs) tests.

## COULD NOT VERIFY

CI green (no Actions API access); Android adapters (speech/scanner/Play Billing — interfaces only at the audited state); fine Telugu naturalness of error copy; NFR-02/NFR-11 device-lab metrics (finalize <2s, PDF <3s, APK size, 7"/10" tablet layouts); seed-data slab values (covered regardless by the H8 validator guard).

## Net for the implementer

The money core is genuinely sound — slabs, rounding, apportioning, and discount ordering survived adversarial hand-computed attacks. The two things that can hurt a shopkeeper are **C1/C2 (nothing works until the build compiles and billing is registered)** and **H1 (a voided udhaar bill permanently bricks an account)**. The voice pipeline needs the seed/JSON harvest (H5) and the decimal/range fixes (H4/H6) before real counter use. Fix in severity order; do not cherry-pick easy mediums while criticals are open.
