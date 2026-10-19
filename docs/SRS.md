# Angadi — Software Requirements Specification (v1)

> **SIGN-OFF:** ☐ Approved by Sri — ____ (date). Until this box is ticked, no code is written.
> Rule: every test traces to an FR/NFR number below. Not in this SRS → not built in v1.

| | |
|---|---|
| Product | Angadi — Telugu-first retail POS for kirana & pharmacy shops |
| Version | SRS v1.0 draft · 2026-10-19 |
| Platform | Android 9+ (API 28), .NET MAUI, on-device SQLite, no backend |
| Canonical design | `Angadi_Architecture_Blueprint.md` |

## 0. Decisions locked on Day 1 (supersede older docs)

| # | Decision | Why |
|---|---|---|
| D1 | **Price: ₹200/mo or ₹1,999/yr.** Free tier 50 bills/month. | Resolves ₹99 vs ₹200 conflict. Agent D brief and spec §2 must be updated. |
| D2 | **Tier 2 LLM features move to v2.** v1 ships Tier 1 on-device AI only. | Zero backend means the API key would ship inside the APK and could be stolen. v2 adds a small proxy that holds the key and enforces caps. |
| D3 | **One user role, no PIN.** | Staff roles are v2. |
| D4 | **Tablet-first landscape, phone portrait supported.** | As planned. Every screen must also work on a 6" phone. |

## 1. Users

- **Shopkeeper** (owner or helper, same role): bills customers, manages items, stock, dues and reports.
- **Customer** (indirect): receives a receipt on WhatsApp and may hold an udhaar balance.

## 2. Functional requirements

### 2.1 Shop setup
- **FR-01** First run asks for shop name, address, phone, UPI ID and optional GSTIN, and offers seed items. Telugu is the default UI language.
- **FR-02** The shopkeeper can switch the UI between Telugu and English at any time. All strings come from resource JSON.

### 2.2 Item library
- **FR-03** Create, edit and deactivate items. Fields: Telugu name, English name, aliases, category, unit (kg/g/l/ml/pcs/pack/strip), GST-inclusive price, GST slab (0/5/12/18/28%), optional HSN, stock quantity, low-stock threshold, optional barcode.
- **FR-04** First run can seed 80 kirana and 40 pharmacy items, each with Telugu aliases. Seeding can be skipped and is idempotent: running it twice adds nothing.
- **FR-05** Search items by Telugu name, English name, alias or barcode. Voice search is available on the search box.

### 2.3 Billing
- **FR-06** **Voice billing.** The shopkeeper speaks items in Telugu (e.g. "బియ్యం రెండు కేజీలు, పప్పు అర కేజీ"). Bill lines appear within 3 s of speech ending.
- **FR-07** Match confidence ≥ 0.90 adds the line automatically. 0.70–0.90 asks the shopkeeper to confirm. Below 0.70 shows "not found" with manual search. A correction is saved as an alias for that shop.
- **FR-08** **Barcode scan-to-add** uses the camera on the billing screen. A known barcode adds the item, or +1 qty if already on the bill. An unknown barcode offers "create item with this barcode".
- **FR-09** The barcode can be typed by hand when the camera fails or the barcode is damaged.
- **FR-10** **Quick-sale grid.** The shopkeeper pins favourite items, and one tap adds that item to the bill.
- **FR-11** Edit a line's quantity or price, or remove it, before finalizing.
- **FR-12** Per-line discount and bill-level discount, each as % or flat ₹.
- **FR-13** **GST math.** Prices are GST-inclusive. Tax is split per slab. The total is rounded to the whole rupee with an explicit rounding line. Rules are in `docs/BILLING_RULES.md`.
- **FR-14** **Tenders.** Each bill takes exactly one of:
  - **Cash**, showing change due.
  - **UPI**, showing the shop's UPI QR with the amount.
  - **Udhaar**, which requires a customer to be selected.
- **FR-15** Finalizing is atomic. The bill, the stock decrement with a `StockMove`, the udhaar update and an outbox row are all saved, or none are.
- **FR-16** Void a finalized bill with a reason. Stock is restored, udhaar is reversed, and the bill is kept and marked VOID.

### 2.4 Receipts
- **FR-17** Generate a PDF receipt with:
  - shop details,
  - bill number and date,
  - lines,
  - discounts,
  - per-slab tax split,
  - the rounding line,
  - tender,
  - the UPI QR.
  Telugu text must render in the PDF.
- **FR-18** If no GSTIN is set, the receipt prints as a **"Bill of Supply"** with no tax split. If a GSTIN is set, it prints as a **"Tax Invoice"**.
- **FR-19** Share the receipt to WhatsApp or any app via the OS share sheet. A customer phone, if present, prefills a `wa.me` link.
- **FR-20** View any past bill and share it again.

### 2.5 Customers & udhaar
- **FR-21** Customer directory with name, phone and udhaar balance.
- **FR-22** Record an udhaar payment (full or partial) and view the ledger per customer.
- **FR-23** One-tap WhatsApp dues reminder as a prefilled Telugu message. *(First to cut if the schedule slips.)*

### 2.6 Inventory
- **FR-24** Stock-in and adjustment with a reason (purchase, damage, count correction). Every change writes a `StockMove`.
- **FR-25** Low-stock alerts: a list screen plus a home badge for items at or below threshold.
- **FR-26** Stock history per item, from `StockMove`.

### 2.7 Reports
- **FR-27** Day-end report: sales total, bill count, tender split (cash/UPI/udhaar), discounts, tax, voids.
- **FR-28** GST summary by slab for a date range.
- **FR-29** Top items by quantity and value for a date range.
- **FR-30** Udhaar outstanding by customer.

### 2.8 On-device AI (Tier 1 — free, offline)
- **FR-31** Reorder forecast per item: "runs out in ~N days", from a moving average of sales.
- **FR-32** Dead-stock radar: items with no sales in 30, 60 or 90 days, and the stock value locked up.
- **FR-33** Morning summary card: yesterday vs 7-day average, top mover, dues to collect.

### 2.9 Subscription
- **FR-34** Free tier allows 50 finalized bills per calendar month. Over the limit, new bills are blocked. Viewing, sharing, reports and udhaar payments still work.
- **FR-35** Google Play Billing subscriptions at ₹200/mo and ₹1,999/yr, with restore purchase.

### 2.10 Support & data safety
- **FR-36** Settings → **Export logs** shares the log file.
- **FR-37** Settings → **Backup / Restore** writes the full database to a file the shopkeeper can save to Drive or WhatsApp, and restores it. *(Added Day 1. Without it, a lost phone loses every udhaar record.)*

## 3. Non-functional requirements

| ID | Requirement |
|---|---|
| NFR-01 | Voice needs internet; the app says so plainly. Every other feature works fully offline. |
| NFR-02 | Bill finalize takes < 2 s; receipt PDF is ready in < 3 s on a 3 GB RAM device. |
| NFR-03 | Android 9+. Tested on a 7" and a 10" tablet (landscape) and a 6" phone (portrait). |
| NFR-04 | Telugu and English UI. No hardcoded user-facing strings. Telugu line height is +15%. |
| NFR-05 | No backend in v1. All data stays on the device. No paid APIs. Every mutation also appends an `Outbox` row (the v2 sync seam). |
| NFR-06 | Integrity: no half-saved bill. Stock reconciles to the unit after 100 random sales. |
| NFR-07 | The app never hard-crashes at the counter. A global handler shows a friendly message and logs with a correlation ID. |
| NFR-08 | Logs never contain customer names or full bills. Phone numbers are masked. |
| NFR-09 | Touch targets ≥ 48 dp; billing actions ≥ 64 dp. |
| NFR-10 | Test coverage ≥ 70% on Domain + Application, and ≥ 95% on billing math. |
| NFR-11 | Cold start < 3 s; APK < 60 MB. |
| NFR-12 | Voice accuracy: ≥ 27/30 on the unit-test phrase battery, and ≥ 35/40 phrases needing no confirmation on the integration battery. |

## 4. Scope

**In v1:**
- Voice billing
- Barcode scan
- Quick-sale grid
- Discounts
- Cash/UPI/udhaar
- GST receipts and WhatsApp share
- Void
- Inventory and low stock
- 4 reports
- Tier 1 AI
- Subscription
- Logs export
- Backup/restore

**Out (v2):**
- Tier 2 LLM features (ask-your-data, supplier-bill scan, voice rescue) and their proxy
- Staff roles / PINs
- Cloud sync
- iOS
- Expiry / batch tracking
- Delivery
- Purchase orders
- Split tender
- Returns / partial refunds (void only in v1)
- Bluetooth thermal printing
- GST e-invoicing
- Multi-shop

**Cut order if week 2 slips:**
1. FR-23 reminders
2. FR-29 top items
3. FR-12 discount depth (keep flat bill discount)

**Never cut:** FR-06/07 voice, FR-08 scan, FR-13/17 GST receipt, FR-24/25 inventory, FR-27 day-end, FR-31–33 Tier 1 AI.

## 5. Assumptions
- **A1** Prices are GST-inclusive (MRP norm), and rounding is to the whole rupee.
- **A2** Payments go through Google Play Billing only. UPI tender is display-only: the app does not confirm the payment, the shopkeeper does.
- **A3** Telugu speech uses Android's recognizer (`te-IN`), not a paid API.
- **A4** One device per shop. Backup/restore is the only way to move data between devices.

## 6. Traceability

| FRs | Owning agent | Doc |
|---|---|---|
| FR-01–05, FR-24–26 | A (Data) + C (UI) | DB_SCHEMA.md, UI_MAP.md |
| FR-06–07 | B (Voice) | VOICE_PIPELINE.md |
| FR-08–12 | C (UI) | UI_MAP.md |
| FR-13–20, FR-34–35 | D (Money) | BILLING_RULES.md |
| FR-21–23, FR-27–30 | A + C + D | — |
| FR-31–33 | D (Money + AI) | — |
| FR-36–37, NFR-07/08 | E (Quality) | TEST_PLAN.md |

## 7. Open questions for sign-off
1. **Thermal printers.** Many shops already own a 58 mm Bluetooth printer. Keeping it out of v1 is my recommendation, but confirm.
2. **GSTIN.** Is "Bill of Supply when no GSTIN" (FR-18) right for your pilot shops?
3. **UAT shops.** Which kirana and which chemist, and are they available Days 11–12?
4. **Discount vs tax order.** The blueprint's Kahera note says "tax removed before discount". With GST-inclusive prices, this draft lets the discount reduce the inclusive price and extracts tax from the discounted amount, which is how the customer actually pays. Confirm.
