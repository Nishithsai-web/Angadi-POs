# CLAUDE_STATUS.md — Claude Code's answer to the Muse review queue
**Updated:** 2026-10-19 20:45 ET · **Branch:** `claude/new-session-g6f55q`
**Evidence:** CI run 39345220912 — the first fully green run on a real .NET 10 SDK.

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
| **H8** | ✅ | The validator rejects legacy slabs (`BILL_LEGACY_SLAB`). The item editor offers only current slabs. Tested. The one-time catalog migration report is still open. |
| **H10** | 🟡 | 20 handler tests: finalize, quota, void, H1, dues, items, shop. Reports, insights and BillDraft tests are still to add. |
| **B1** | ✅ | `ChangeDue` is cash-only. Cash received on UPI or udhaar is rejected. Tested. |
| **M9** | ✅ | A discount over 100% is a hard failure (`BILL_DISCOUNT_TOO_LARGE`). Tested. |
| **M10** | ✅ | Prices come from `BillingOptions` (subscription page). The hardcoded "₹200" left in `errors.json` copy is still open. |
| **V1** | ✅ | `వెయ్యి` is a number now. Regression test: "వెయ్యి రూపాయల బియ్యం" → ₹1,000. |
| **H11** | 🟡 | Settings → Export logs is wired to `LogFiles.ExportZip` + share. The `ExportLogsCommand` + test are open. |
| — | ✅ | **Found by CI, not by review:** MSBuild treated `strings.te.json` / `numbers.te.json` as Telugu-culture resources and moved them to a satellite assembly, so 91 tests failed. Fixed with `WithCulture="false"`. |
| **H2** | ⏳ next | Per-month quota counters in Meta. |
| **H3** | ⏳ next | Restore to a temp file, then integrity_check, the invariant sweep (no stock ≥ 0 rule) and `File.Replace`. |
| **H5, H6, M1, M3, M13** | ⏳ | One serialized voice pass. Generic-alias demotion and transliterations; descending compounds + `IsAmbiguous`; dative currency. |
| **H9** | ⏳ | `UpiRef` on Bill and DuesEntry. |
| **B2** | ⏳ | Quota check inside the finalize transaction. The handler already runs inside the UoW; it still needs the `QuotaTracker` from H2. |
| M2, M4, M6, M7, M8, M11–M15, AUDIT_FIXES | ⏳ | In severity order after the highs. |

## v1 scope decisions taken (per the ARCHITECT_CORRECTIONS cut list)
- **Camera barcode:** manual or USB-scanner entry instead. A USB/Bluetooth barcode gun types into the box. The camera returns in v1.1.
- **Play Billing:** the stub stands. The real client is v1.1, before the production track.
- **UI:** built in C# instead of XAML for v1. Every value is compile-checked, and no XAML toolchain errors can block a green build. Secondary pages keep their logic in the page; the POS keeps a ViewModel.
- **Expiry tracking:** not built (owner cut).
