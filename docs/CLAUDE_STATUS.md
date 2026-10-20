# CLAUDE_STATUS.md — Claude Code's answer to the Muse review queue
**Updated:** 2026-10-19 late ET · **Branch:** `claude/new-session-g6f55q`
**Evidence:** CI run 39350733740 — core + Android green, 178/178 tests. (First green: 39345220912.)

| Job | Result |
|---|---|
| Core build | ✅ warnings are errors |
| Tests | ✅ all pass |
| Coverage | ✅ NFR-10 gate passes (Domain + Application ≥ 70%, BillCalculator ≥ 95%) |
| Seed check | ✅ |
| Android | ✅ Debug APK built (artifact `angadi-debug-apk`, 27.6 MB) |

## How CI errors reach us
Raw Actions logs live on a host the sandbox can't reach, so CI posts its errors as annotations that `tools/ci_wait.sh` reads:
- `tools/ci_report.py` collects every build error.
- `tools/ci_tests.py` collects every failed test, plus the log tail when the test host crashes.

## Fix status

| ID | Status | Notes |
|---|---|---|
| **C1** | ✅ | ReceiptRenderer (HarfBuzz) wired. `UnitConversion` already existed in `Domain/Items`. CI-time DI test resolves every handler, `ISubscriptionService` and `IReceiptRenderer` from the real graph with `ValidateOnBuild`. |
| **C2** | ✅ | `StubSubscriptionService`: stateless, never Pro, purchases fail closed. Pro and non-Pro paths tested. |
| **P1** | ✅ | `MauiProgram`, `App`, `AppShell` (code-built: 6 tabs from DI, first-run shop setup). |
| **P2** | ✅ | Icon, foreground and splash SVGs. Noto Sans Telugu/Noto Sans come from `tools/fetch_fonts.sh`, which CI runs; the sandbox can't reach font hosts. |
| **P3** | ✅ | `ReceiptShare`: OS share sheet, plus `wa.me` links. |
| **P4** | ✅ | `FallbackSpeechToText`: te-IN → device language → "manual", which opens typed search. |
| **H1** | ✅ | Fails closed with `BILL_VOID_BLOCKED_BY_PAYMENTS`, before any mutation. No refund logic. Tested. |
| **H4** | ✅ | `MarkPunctuationSeparators`: a "." or "," between digits is never a phrase break. "2.5 కేజీల బియ్యం" → 2500. Tested. |
| **H7** | ✅ | `Resources/Raw/appsettings.json` is bound to Voice, Billing and Insights options. |
| **H8** | ✅ | The validator rejects legacy slabs (`BILL_LEGACY_SLAB`). The item editor offers only current slabs. `GetLegacySlabItemsQuery` drives a warning on the Items page. Tested. |
| **H10** | 🟡 | Handler tests cover finalize, quota, void, H1, dues, items, shop, top-items and outbox. Day-end, insights and BillDraft tests are still to add. |
| **B1** | ✅ | `ChangeDue` is cash-only. Cash received on UPI or udhaar is rejected. Tested. |
| **M9** | ✅ | A discount over 100% is a hard failure (`BILL_DISCOUNT_TOO_LARGE`). Tested. |
| **M10** | ✅ | Prices come from `BillingOptions`. `FREE_LIMIT_REACHED` is now a template filled from options, so no price is hardcoded in copy. |
| **V1** | ✅ | `వెయ్యి` is a number now. Regression test: "వెయ్యి రూపాయల బియ్యం" → ₹1,000. |
| **H11** | ✅ | `ExportLogsCommand` via `ISender` + `ILogExporter` adapter. An IO failure becomes `LOG_EXPORT_FAILED`. Tested. |
| — | ✅ | **Found by CI, not by review:** MSBuild treated `strings.te.json` / `numbers.te.json` as Telugu-culture resources and moved them to a satellite assembly, so 91 tests failed. Fixed with `WithCulture="false"`. |
| **H2, B2** | ✅ | Per-month quota buckets in Meta keyed on max(now, last seen); increment inside the finalize transaction. Clock-back and clock-forward tested. |
| **H3** | ✅ | Staged restore: integrity_check + invariant sweep, then atomic `File.Replace` keeping `.pre-restore`. Tampered backup rejected, live data untouched (tested). |
| **H9** | ✅ | `UpiRef` on Bill and DuesEntry, printed on the receipt. |
| **H5, H6, M1, M3, M13** | ✅ | Voice pass: generic aliases ask, ranges ask, dosage-first rotates, dative currency, ము→ం, fused number+unit. Battery tests. |
| **M2** | ✅ | Learned aliases are separate from curated ones: ask at confidence 1, auto at 2, contradictions demote, collision guard, LRU 30. Tested. |
| **M4** | ✅ | `ISyncService` seam declared; nothing registered in v1. |
| **M6** | ✅ | Outbox rows for CustomerChanged, ShopProfileChanged, DuesCharged, plus one ItemsSeeded summary. Tested. |
| **M7** | ✅ | Checked maths; qty ≤ 1,00,000 units per line, price ≤ ₹1 crore; overflow → `BILL_AMOUNT_TOO_LARGE`. |
| **M8** | ✅ | Runtime localizer is lenient (bad file costs only its keys; hardcoded error fallback). Strict duplicate check stays in CI. |
| **M12** | ✅ | `VoidedOffsetMinutes` stored, so void times keep their offset. |
| **M14** | ✅ | Top items sort by value or by bill count; mixed-unit quantity deliberately not offered. |
| — | ✅ | **Messages:** errors carry args (shortfall, balance, limit, price). Specific codes replace `VALIDATION_FAILED` across validators. |
| **AUDIT 5, 6, 13, 14** | ✅ | Quantity-only utterance never matches, Telugu-script digits parse, `MatchKey("పాలు")` tested; `VoicePipeline.cs` → `VoiceContracts.cs`. |
| M11, M15, other AUDIT nits, LOW | ⏳ | Next: the 40-phrase battery (M15), then nits and LOW items. |

## v1 scope decisions taken (per the ARCHITECT_CORRECTIONS cut list)
- **Camera barcode:** manual or USB-scanner entry instead. A USB/Bluetooth barcode gun types into the box. The camera returns in v1.1.
- **Play Billing:** the stub stands. The real client is v1.1, before the production track.
- **UI:** built in C# instead of XAML for v1. Every value is compile-checked, and no XAML toolchain errors can block a green build. Secondary pages keep their logic in the page; the POS keeps a ViewModel.
- **Expiry tracking:** not built (owner cut).
