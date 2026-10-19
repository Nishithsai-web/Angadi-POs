# Angadi — Architecture (as-built)

> Day 1 PM. Contracts below are **drafted and awaiting Sri's approval**. Source design: `Angadi_HQ.html` → Architecture Blueprint. Where this file and the blueprint differ, this file wins and the difference is an ADR below.

## 1. Solution map

```
Angadi.sln
├── src/Angadi.Domain          entities, value types, BillCalculator, domain events — zero dependencies
├── src/Angadi.Application     Result<T>, interfaces, Mediator commands/queries + handlers, voice parser/matcher,
│                              insights, Telugu resources (strings, errors, voice rules — embedded JSON)
├── src/Angadi.Infrastructure  sqlite-net-pcl repositories + UoW, outbox, Serilog, receipt PDF (SkiaSharp + HarfBuzz),
│                              seed data, backup/restore — platform-neutral, runs in tests on Linux
├── src/Angadi.Maui            Shell UI, ViewModels (CommunityToolkit.Mvvm), AngadiTheme, Android adapters
│                              (speech te-IN, camera barcode, share sheet, Play Billing)
└── tests/Angadi.Tests         per-layer tests, tagged with FR numbers
```

Dependency rule: `Maui → Application → Domain ← Infrastructure` (Infrastructure → Application for interfaces).

## 2. Core rules

| Rule | Where enforced |
|---|---|
| Money is `Paise` (long), quantity is `Qty` (long, thousandths). No `decimal`/`double` in storage. | `Domain/Common/Primitives.cs` |
| All ids are `Guid` (v2 multi-device sync). Human bill numbers are separate (`A-000123`). | `Entity`, `IShopProfileRepository.NextBillNumberAsync` |
| One transaction per command; a bill never half-saves. | `IUnitOfWork` + TransactionBehavior |
| Stock changes only through `IStockLedger` (writes `StockMove`). | `Application/Persistence` |
| Expected failures return `Result`/`Result<T>` with an `ErrorCodes` key; every key exists in `errors.json` (test-enforced). | `Application/Common/Result.cs` |
| User-facing text only from `strings.te.json` / `strings.en.json` / `errors.json`. | `ILocalizer` |
| Voice parsing is rule-based from `Resources/voice/*.json`. No LLM anywhere in v1. | `ITeluguParser` |
| Finalized bills are immutable; correction = void. Lines snapshot item name/price/slab. | `Bill`, `BillLine` |
| Time comes from `IClock`, never `DateTime.Now`. | `Application/Platform` |

## 3. Golden path

```
Mic → ISpeechToText.ListenAsync("te-IN", partials)
    → IVoiceBillingPipeline.ProcessUtteranceAsync
        → ITeluguParser.Parse → ParsedLine[]
        → IItemMatcher.MatchAsync → AutoAdd (≥0.90) | Confirm (0.70–0.90) | NoMatch
    → PosViewModel adds lines / shows confirm sheet → IAliasLearner on correction
Scan → IBarcodeScanner.ScanAsync → IItemRepository.GetByBarcodeAsync → add/+1
Tender → IMediator.Send(FinalizeBillCommand)
    → Validation → Logging → Transaction
    → quota check → BillCalculator.Compute → number → save → IStockLedger (Sale)
      → DuesEntry (udhaar) → IOutbox.Append(BillFinalized) → commit
→ ShareReceiptCommand → IReceiptRenderer (PDF) → IReceiptShare (share sheet / wa.me)
```

## 4. Contract approval checklist (Sri)

Tick each, or comment on the line, then sign at the bottom.

- [ ] `Domain/Common/Primitives.cs` — `Paise`, `Qty`, `Discount`, Guid ids
- [ ] `Domain/Items/Item.cs` — fields, units, GST slab as basis points
- [ ] `Domain/Billing/Bill.cs` — single tender, Draft/Finalized/Void, snapshots, `IsTaxInvoice`
- [ ] `Domain/Billing/BillCalculator.cs` — the five billing rules in the doc comment
- [ ] `Domain/Customers/Customer.cs` — `DuesEntry` ledger (Charge / Payment / Reversal)
- [ ] `Domain/Inventory/StockMove.cs` — reasons
- [ ] `Application/Common/Result.cs` — `Result<T>`, `ErrorCodes`
- [ ] `Application/Persistence/Repositories.cs` — repositories, `IStockLedger`, `IOutbox`
- [ ] `Application/Platform/PlatformServices.cs` — speech, scanner, receipt, share, subscription, backup, localizer, clock
- [ ] `Application/Voice/VoicePipeline.cs` — parser, matcher, alias learner, pipeline
- [ ] `Application/Billing/BillingContracts.cs` — commands, quota, `UpiLink`
- [ ] `Application/Reports/ReportContracts.cs`, `Application/Insights/InsightsContracts.cs`

**Approved:** ☐ Sri — ____ (date)

## 5. Architecture decision records

| # | Decision | Status |
|---|---|---|
| ADR-1 | sqlite-net-pcl over EF Core (single user, on device). | Blueprint |
| ADR-2 | Mediator (source generator) over MediatR. Verify on first restore; fall back to MediatR if blocked on .NET 10. | Blueprint |
| ADR-3 | No backend in v1; `IOutbox` + future `ISyncService` seam. | Blueprint |
| ADR-4 | WhatsApp via OS share sheet and `wa.me` only. | Blueprint |
| ADR-5 | Telugu strings and voice rules in JSON, never code. | Blueprint |
| ADR-6 | Monorepo project references; NuGet packaging only when app #2 exists. | Blueprint |
| **ADR-7** | **5 projects, not 9.** The blueprint's `Core/Voice/Comms/AI` libraries are folders inside Application/Infrastructure for v1. Kirana and pharmacy are one app, so the Rule of Three isn't met yet, and the blueprint put GST math in both Core and Domain. Extract the folders into libraries when app #2 starts — the namespaces already line up. | Day 1 |
| **ADR-8** | Money as integer paise, quantity as integer thousandths. sqlite-net stores `decimal` as REAL. | Day 1 |
| **ADR-9** | Guid primary keys. Autoincrement ints collide when v2 sync merges devices. | Day 1 |
| **ADR-10** | Tier 2 LLM features deferred to v2 (SRS D2). No `IAiService`, no API key in the APK. | Day 1 |
| **ADR-11** | Receipt PDF with SkiaSharp + SkiaSharp.HarfBuzz, not QuestPDF. HarfBuzz shapes Telugu conjuncts correctly, SkiaSharp runs on Android, and there's no licence question. | Day 1 |
| **ADR-12** | Domain entities stay attribute-free; Infrastructure maps them to sqlite-net row classes. Keeps Domain at zero dependencies. | Day 1 |
| **ADR-13** | The draft bill lives in the POS ViewModel until finalize; only finalized bills are persisted. A crash mid-bill loses the draft, never a sale. | Day 1 |

## 6. Dependencies

Versions are pinned in `Directory.Packages.props`. They were written on Day 1 without NuGet access — on the first `dotnet restore`, bump any that fail to the latest stable in the same major and record it here.
