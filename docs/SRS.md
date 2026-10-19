# Angadi POS — Software Requirements Specification (v1)

> **STATUS: DRAFT — awaiting owner sign-off.**
> Sign-off: _______________________ (name / date). Replace this line with your sign-off comment once §9 is answered.
>
> Source documents: `docs/Angadi_HQ.html` — Product Spec (final scope, locked 2026-10-19) and Architecture Blueprint (canonical). Where the HQ sections disagree, this draft follows the **Product Spec** and lists the conflict in §9.

---

## 1. Purpose & scope

Angadi is a Telugu-first, Square-like point-of-sale app for small retail — **kirana shops and pharmacies** — running on Android tablets/phones the shop already owns. One retail vertical: the item library covers groceries and medicines alike. No card readers, no hardware purchase, no backend.

**Positioning:** "మీ అంగడి, మీ హిసాబు — మీ భాషలో." The wedge vs Vyapar/myBillBook/Khatabook is Telugu-first UI + Telugu voice billing.

## 2. Users

| Role | Description | v1 |
|---|---|---|
| Owner / cashier | Single role, no PIN, operates the counter and the back office | **In** (assumed — see Q1) |
| Staff with separate logins/roles | | Out (v2) |
| End customer | Receives receipt on WhatsApp; owes udhaar | Indirect only |

## 3. Functional requirements

Each FR is testable; every test in `Angadi.Tests` is tagged with its FR number (e.g. `FR07_VoiceBill_TwoKgRice`).

### Onboarding & settings
- **FR-01** First-run setup captures shop profile: shop name (Telugu/English), address, phone, GSTIN (optional), UPI ID (VPA). Editable later in Settings.
- **FR-02** First run seeds the item library with 80 kirana + 40 pharmacy items, each with Telugu/English names and Telugu aliases. Seeding is skippable and idempotent.
- **FR-03** UI language is Telugu-first with English secondary labels; all strings come from resource JSON.

### Item library & inventory
- **FR-04** Owner can create/edit/archive items: Telugu name, English name, aliases, category, unit (kg / g / L / ml / pcs / strip / box), GST-inclusive price, GST slab (0/5/12/18/28%), stock quantity, low-stock threshold, optional barcode.
- **FR-05** Item barcode is unique per shop and indexed; item form can fill it by camera scan or typing.
- **FR-06** Every stock change (sale, manual adjust, stock-in) writes a `StockMove` row; current stock always equals the sum of moves.
- **FR-07** Items at or below their low-stock threshold appear in a low-stock list and raise an in-app alert on the home screen.

### Billing
- **FR-08** Shopkeeper speaks items in Telugu (e.g. "బియ్యం రెండు కేజీలు, పప్పు అర కేజీ"); bill lines appear within **3 seconds** of end of speech.
- **FR-09** Voice match confidence ≥ 0.90 adds the line automatically; 0.70–0.90 shows a confirmation sheet with top candidates; < 0.70 shows a no-match state with manual search. Each confirmation/correction is saved as a shop-local alias.
- **FR-10** Voice mic is also available on item search.
- **FR-11** Camera barcode scan on the POS home adds the matching item to the bill (qty 1; repeat scan increments). Unknown barcode offers "create item with this barcode". Manual code entry fallback.
- **FR-12** Quick-sale grid of favourite items; one tap adds a line.
- **FR-13** Line editing: change quantity, remove line, per-line discount (% or flat).
- **FR-14** Bill-level discount (% or flat).
- **FR-15** Bill totals use GST-inclusive pricing: `Tax = Gross − Gross/(1+slab)` per slab; grand total rounded to the whole rupee with an explicit rounding-adjustment line. Tax is computed after discounts (see BILLING_RULES.md).
- **FR-16** Tenders: Cash (with change due), UPI (shop QR shown on screen), Udhaar (requires a customer). Split tender across these — see Q4.
- **FR-17** Finalizing a bill is atomic: bill, lines, stock moves, dues update and outbox row commit together or not at all.

### Receipts
- **FR-18** Finalized bill renders a PDF receipt: shop header, GSTIN, items, per-slab tax split, discounts, rounding line, tender, UPI QR, Telugu text rendered correctly.
- **FR-19** Receipt shares via the OS share sheet (WhatsApp and others); `wa.me` deep link when customer phone is known.

### Customers & udhaar
- **FR-20** Customer directory: name, phone, udhaar balance.
- **FR-21** Udhaar ledger: credit sales increase balance; recording a payment decreases it; history of entries per customer.
- **FR-22** One-tap WhatsApp due reminder with a prefilled Telugu message (wa.me deep link, no paid API).

### Reports
- **FR-23** Day-end report: sales total, bill count, tender split (cash/UPI/udhaar), discounts given.
- **FR-24** GST summary for a date range, per slab (taxable value + tax).
- **FR-25** Top items by quantity and value for a date range.
- **FR-26** Udhaar outstanding: all customers with a balance, total outstanding.

### AI (blueprint §11)
- **FR-27** Tier 1 (on-device, free, offline): reorder forecast per item, dead-stock radar (30/60/90 days), smart day summary card.
- **FR-28** Tier 2 (behind `AiFeatures` flag): ask-your-data assistant and supplier-bill photo scan → stock-in, via `IAiService`. Prompts carry aggregates and item names only — never customer names/phones.
- **FR-29** Tier 2 calls are capped per shop per month (default 50) with a visible meter in Settings; over cap falls back to Tier 1 with a Telugu notice.

### Subscription
- **FR-30** Google Play Billing subscription gate: 50 free bills per calendar month; monthly and yearly plans unlock unlimited (price — see Q2). Restore purchase supported.

### Support
- **FR-31** Settings → "Export logs" shares the rolling log file.

## 4. Non-functional requirements

- **NFR-01** Telugu voice requires internet (cloud-backed te-IN recognizer); app says so clearly. All other features work fully offline.
- **NFR-02** Bill finalize (tap Tender → receipt screen) < 2 s on a mid-range Android 9 tablet.
- **NFR-03** Runs on Android 9+ phones and 7"/10" tablets; tablet-first landscape layout, usable in portrait on phones.
- **NFR-04** Telugu + English UI; Telugu text gets +15% line height; large touch targets.
- **NFR-05** Zero backend in v1: all data on device in SQLite. Every mutation also appends an `Outbox` row (v2 sync seam).
- **NFR-06** App never hard-crashes at the counter: global handler logs with correlation ID and shows a friendly `errors.json` message.
- **NFR-07** Logging: Serilog file sink, 7-day rolling, 5 MB cap; no customer PII beyond phone in logs; AI calls log counts/tokens, never prompts.
- **NFR-08** Test coverage ≥ 70% of Domain + Application; ≥ 95% on billing math.
- **NFR-09** Voice accuracy: ≥ 27/30 on the unit battery; ≥ 35/40 phrases need no confirmation on the integration battery.
- **NFR-10** Per-shop AI cost ≤ ₹5/month at cap; no AI call in the per-bill path.

## 5. Scope OUT (v2 or later)

Expiry/batch tracking · delivery day-book · staff logins/roles · cloud sync/backup · iOS · multi-shop dashboard · supplier purchase orders · GST e-invoicing · detailed per-customer purchase history · card readers / thermal printer drivers (see Q6).

## 6. Assumptions

- Prices are GST-inclusive (Indian MRP norm); whole-rupee rounding.
- Google Play Billing is the only payment channel for the subscription.
- Android SpeechRecognizer `te-IN` is available on target devices.
- WhatsApp sharing via OS share sheet / `wa.me` only.

## 7. Cut order if week 2 slips

Discount engine depth → customer purchase-history detail → WhatsApp due reminders.
**Never cut:** voice billing, barcode scan, GST receipts, inventory + low-stock, day-end reports, on-device AI insights.

## 8. Traceability

| FR group | Owning agent | Doc |
|---|---|---|
| FR-01…07 | A (Data) + C (UI) | DB_SCHEMA.md, UI_MAP.md |
| FR-08…10 | B (Voice) | VOICE_PIPELINE.md |
| FR-11…17 | C (UI) + D (Money) | UI_MAP.md, BILLING_RULES.md |
| FR-18…19, 30 | D (Money) | BILLING_RULES.md |
| FR-20…26 | A + C + D | |
| FR-27…29 | AI lib (owner TBD — see Q7) | |
| FR-31, NFR-06/07 | E (Quality) | TEST_PLAN.md |

## 9. Open questions for sign-off

Conflicts found in the HQ doc, plus the Day 1 interview list. Answer inline and sign at the top.

1. **Roles** — one PIN-less owner/cashier role in v1? (Draft assumes yes.)
2. **Price** — Product Spec says **₹200/mo, ₹1,999/yr**, but Spec §2 and Agent D's brief say **₹99/mo** and the kickoff pack says ₹99/mo, ₹999/yr. Which is final? (Draft leaves price out of FR-30 until confirmed.)
3. **Seed data** — 80 kirana + 40 pharmacy (Spec/Blueprint) vs "the 80 seeded" in the user-guide brief. Draft assumes 120 total.
4. **Split tender** — can one bill be paid part cash / part UPI / part udhaar, or one tender per bill?
5. **Discount/tax order** — Blueprint §1 (Kahera) says "tax removed before discount"; with GST-inclusive prices, draft assumes discount reduces the inclusive price and tax is extracted from the discounted amount. Confirm.
6. **Printing** — share-only PDF in v1, or also Bluetooth thermal printers (common at counters)? Draft assumes share-only.
7. **AI ownership & key** — Tier 2 is "in v1 behind the flag", but none of agents A–E owns `Angadi.AI`, and an on-device app can't safely hold an LLM API key. Options: (a) ship Tier 1 only in v1, Tier 2 in v2 with a tiny proxy; (b) Tier 2 in v1 with a key in the app (extractable — not recommended). Draft recommends (a).
8. **GSTIN** — are target shops mostly unregistered (composition / below threshold)? If so, receipts must say "Bill of Supply" rather than "Tax Invoice" when no GSTIN is set.
9. **Scope note** — "No backend" and FR-28 Tier 2 conflict (see Q7); "Telugu voice billing" needs internet — confirmed acceptable per NFR-01.
