# testing.md — Test strategy for Angadi POS v1

## What "tested" means
- **Unit:** pure logic — `BillCalculator`, `TeluguParser`, `ItemMatcher`, validators. Fast, no I/O. Target: every branch that touches money or voice parsing.
- **Handler:** `ISender` pipeline end-to-end against SQLite — finalize, void, dues, quota, stock. Each asserts DB state *and* outbox rows.
- **Architecture:** layer rules (Domain = BCL only, no float/double/decimal on entities), one handler per command, no Telugu literals in code. These are the guardrails that keep 20 agents from rotting the design.
- **Battery:** fixed adversarial input sets that must keep passing — 30-phrase voice battery (≥29 auto-add), 40-phrase pipeline battery (≥35 auto-add), 2,000-bill randomized invariant fuzz.
- **Coverage gates (CI):** ≥70% overall, ≥95% on `BillCalculator`. Gates are enforced by script, not by honor.

## Named tests required by the audits (implement in this order)
**Money:** `FinalizeBill_CashLessThanTotal_Rejected`, `VoidBill_RestoresStock_AndReversesUdhaar`, `Void_PartiallyPaidUdhaar_Blocked` (H1), `FreeTier_FiftyFirstBill_Blocked`, `FreeTier_ClockRollback_CountUnchanged`, `Bill_LegacySlab_Rejected` (H8), `Discount_Over100Percent_Rejected` (M9), `BillAmount_Overflow_FailsLoud` (M7).
**Voice:** `VoicePipeline_QuantityOnlyUtterance_ReturnsVoiceNoMatch`, `Parser_TeluguScriptDigits`, `Parser_LeadingLargeDigit_IsQuantity`, `Parse_DecimalQuantity` (H4), `Parse_RangeNotSummed` + `Parse_DativeCurrency` (H6), `Parser_BulkCountNoUnit` (M1), `AliasLearner_NeverEvictsSeedAliases` (M2), `MatchKey_Paalu_Regression` (milk), one test per H5 seed phrase (`నూనె`→Confirm, `రైస్`/`షుగర్`→match).
**Handlers/reports:** reminder happy + no-phone paths, report math (day-end/GST/top-items/udhaar), forecast buckets, draft merge rules, `ExportLogsCommand` wiring test, `VoiceOptions_Defaults_MatchSRS`.
**Robustness:** crafted-backup rejection per violation type (H3), corrupt-resource fallback to English, reinstall-quota documented behavior, NFR-07 correlation-id, NFR-08 no-phone-in-logs.

## Rules
- A test asserts independently computed hardcoded values — never tautological (don't assert the code's own output).
- Never weaken or delete a test to get green. A failing test is evidence, not an obstacle.
- If a test can't run (no device lab, no Play sandbox), mark it `UNVERIFIED` explicitly — don't pretend.
