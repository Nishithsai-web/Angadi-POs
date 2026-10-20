# CLAUDE_STATUS.md — Claude Code's answer to the Muse review queue
**Updated:** 2026-10-20 ET · **Branch:** `claude/new-session-g6f55q`
**Evidence:** CI run 39352632912 — core + Android green. (First green: 39345220912.)

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

## 2026-10-20 — owner-reported failures and production readiness

| Item | Status | Evidence |
|---|---|---|
| Counter froze after first payment (cash/UPI/udhaar/scan "nothing works") | ✅ | Root cause: Play subscription call inside the bill transaction. Cache-only status + background refresh + timeouts + bounded DB lock + finalize watchdog. |
| Never-fail UI | ✅ | 37 handlers through SafeUi; POS commands via SafeAsync; empty-bill message; navigation result leak fixed. |
| Bill lines invisible on phones | ✅ | Found by the emulator test; phone layout fixed; screenshot 07 shows lines. |
| Barcodes | ✅ | EAN/UPC normalization + check digits, two-frame confirm, torch, attach unknown code to an item, scan-to-find. Tests. |
| Udhaar reminders | ✅ | To the registered number; lists each unpaid udhaar; bill-to-customer WhatsApp; due banner. Tests. |
| Shop's own UPI QR | ✅ | Scan or upload; payments use the owner's payee + merchant code with the bill amount. Tests. |
| **Emulator end-to-end test** | ✅ | CI job `e2e`: 12 real-app steps green; screenshots on branch `ci-e2e`. |
| Release pipeline | ✅ | `release.yml` (signed AAB from secrets), `docs/RELEASE.md`, schema v3 with `.pre-update` copy, Release trimming roots, logcat error tag. |
| Double charge on a slow save | ✅ | Finalize idempotent on Bill.Id (validator status check moved after it). Tests: same object and restored copy. |
| Bill lost when the app is closed | ✅ | Draft snapshot in Meta after every change, restored on reopen, cleared inside the finalize transaction; late saves for a paid bill ignored. Tests + E2E `bill-survives-app-kill`. |
| Damaged database stops the app | ✅ | quick_check at open; corruption → file moved aside, newest validated safety copy restored, notice once. Daily rolling copies on start/background. Tests + E2E `damaged-data-recovers`. |
| Full phone / busy DB shown as UNEXPECTED | ✅ | `STORAGE_FULL`, `DATABASE_BUSY` from typed exceptions (SQLITE_FULL, ENOSPC, lock timeout). Tests. |
| Owner actions | ⏳ | Keystore + 4 secrets; Play Console setup; privacy-policy fields; support number; closed test; production go-ahead. |

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
| **M15** | ✅ | 40-phrase battery is the voice exit gate: ≥ 36 correct, ≥ 35 auto-add, or CI fails. Bad-phone test added. |
| **H10** | ✅ | Plus `BillDraftTests` (merge, edits, tender clearing, voice conversions). |
| LOW (part) | ✅ | Voice never silently coerces qty ≤ 0 (asks instead); undefined tender rejected; SQLite busy timeout 5 s; MaxCandidates 5; cash-short shows the shortfall. |
| Docs | ✅ | `PRIVACY_POLICY.md` (owner fills 2 fields, then hosts it), `RUNBOOK.md`, `USER_GUIDE_TE.md`. |
| LOW (rest) | ✅ | Correlation scope opens before validation; learned-alias cap, morning-summary days and customer search limit are named options/constants. |
| NFR-07 / NFR-08 | ✅ | Tests: every request log line carries `CorrelationId` (rejections too); a customer phone never reaches logs. |
| **FR-08 camera** | ✅ | `CameraBarcodeScanner` (on-device ML Kit). Falls back to the manual box on no camera, denied permission, error or "type it". |
| **FR-34/35 Play Billing** | ✅ | `PlaySubscriptionService` replaces the stub on devices: daily re-check, 7-day offline grace, acknowledged purchases. Needs the two Play Console products. |
| LOW renames | ➖ | Deliberately not done (v1 cut list): churn with no user-visible effect. |

## v1 scope decisions taken (per the ARCHITECT_CORRECTIONS cut list)
- **Camera barcode:** shipped (reversed the cut). Manual and USB-gun entry remain as the fallback.
- **Play Billing:** shipped (reversed the cut). Client-trusted in v1; server-side purchase verification is v2.
- **UI:** built in C# instead of XAML for v1. Every value is compile-checked, and no XAML toolchain errors can block a green build. Secondary pages keep their logic in the page; the POS keeps a ViewModel.
- **Expiry tracking:** not built (owner cut).
