# Billing rules (FR-12, FR-13, FR-18)

Implemented in `Angadi.Domain/Billing/BillCalculator.cs`. Tests in `tests/Angadi.Tests/Domain/BillCalculatorTests.cs`. Every number below is a test.

## Units
- Money is **paise** (`long`). ₹1 = 100.
- Quantity is **thousandths** of the item's unit (`long`). 1 kg = 1000, ½ kg = 500, 3 pcs = 3000.
- Prices are **per one whole unit, GST-inclusive** (the MRP the shopkeeper already knows).
- Rounding is always **half-up** on non-negative integers: `(2n + d) / 2d`.

## Order of operations
1. **Gross** = round(UnitPrice × Qty ÷ 1000).
2. **Line discount**: % (basis points, capped at 100%) or flat (capped at gross). LineTotal = Gross − line discount.
3. **SubTotal** = Σ LineTotal.
4. **Bill discount**: % or flat, capped at SubTotal. It is split across lines **pro rata by LineTotal**. Leftover paise go to the lines with the largest remainders, ties to the earlier line, so the shares always sum exactly.
5. **Tax per slab** (only when the shop has a GSTIN). Net = LineTotal − bill-discount share, summed per slab. Taxable = round(Net × 10000 ÷ (10000 + slab bp)). Tax = Net − Taxable. 0% slab: Taxable = Net.
6. **GrandTotal** = (SubTotal − bill discount) rounded to the nearest whole rupee. The difference is printed as the **Rounding** line (−₹0.49 to +₹0.50).

Discount comes before tax extraction. With inclusive prices, a discount lowers what the customer pays, and the tax inside that lower amount is what's owed (SRS §7 Q4).

## Tax Invoice vs Bill of Supply
- Shop has a GSTIN → **Tax Invoice**: per-slab taxable value and tax are printed.
- No GSTIN → **Bill of Supply**: no tax split and TaxTotal = 0. Totals are identical.

## Worked examples

| Case | Lines | Result |
|---|---|---|
| A | Rice 2 kg @ ₹60 (5%), dal ½ kg @ ₹140 (5%) | ₹190.00; 5%: taxable ₹180.95, tax ₹9.05 |
| B | ₹45 (0%), 3 × ₹20 (18%), ₹118 (12%) | ₹223.00; tax ₹0 / ₹12.64 / ₹9.15 = ₹21.79 |
| C | ₹100 (5%) + ₹50 (18%), 10% bill discount | discount ₹15 split ₹10 / ₹5; ₹135.00 |
| D | 3 × ₹10 at 0/5/18%, flat ₹7 off | shares ₹2.34 / ₹2.33 / ₹2.33; ₹23.00 |
| E | 3 × ₹33.33 (12%), 5% line discount | ₹94.99 → **₹95.00**, rounding +₹0.01 |
| F | 250 g @ ₹280/kg (5%) | ₹70.00, tax ₹3.33 |
| G | ₹10.50 / ₹10.49 | ₹11.00 (+₹0.50) / ₹10.00 (−₹0.49) |

## Change due
Cash tender: ChangeDue = CashReceived − GrandTotal. Finalize is rejected with `BILL_CASH_SHORT` if it's negative.

## Split tender (v2)
Code: `FinalizeBillValidator.SplitError`, `FinalizeBillHandler`, `Domain/Billing/BillPayment.cs`.
1. A split bill has `Tender = Split` and 2–4 `BillPayment` parts, each Cash, UPI or Udhaar, each more than ₹0.
2. The parts add up to the GrandTotal **exactly** — not a paisa more or less (`BILL_SPLIT_SUM_MISMATCH`). The check runs
   after the total is computed, so a changed bill can never be paid with stale parts.
3. At most **one cash part** (the only one with "received" and change: change = received − cash part; received below
   the cash part is `BILL_CASH_SHORT`; nothing typed = exact) and at most **one udhaar part** (needs a customer,
   `BILL_UDHAAR_NEEDS_CUSTOMER`). Only the udhaar part is charged to dues; void reverses only that part.
4. Every bill finalized from schema 4 on stores its payment as parts (a single tender = one part for the whole bill).
   Bills from before have no parts and read as one part of their tender (`BillPayments.PartsOf`), so old reports stay
   identical. Reports, the customer report and receipts read tenders only through `PartsOf` / `AmountBy`.
5. A retry of an already-saved split bill is the same bill only if the parts are the same (tender + amount);
   otherwise `BILL_CHANGED_AFTER_SAVE`. Stock, dues and the free-bill quota move once.
6. Counter: **Split** → type the cash and/or UPI parts → "Rest goes to" Cash / UPI / Udhaar takes the remainder → the
   UPI QR is for the UPI part only → the udhaar part asks for the customer.

## Returns / partial refunds (v2)
Code: `Application/Billing/Returns.cs`, `Domain/Billing/SalesReturn.cs` (`ReturnCalculator`).
1. A finalized bill never changes. Goods brought back become a **SalesReturn** (credit note) with its own number
   (`R-000001`; `R<prefix>-…` when the bill prefix is not the default `A`), linked to the bill. Owner-only: the return
   screen asks for the owner PIN (the same gate as void).
2. Quantity per line ≤ sold − already returned (`RETURN_QTY_TOO_LARGE`); empty returns are refused (`RETURN_EMPTY`);
   only finalized bills (`RETURN_BILL_NOT_FINALIZED`).
3. Value: each returned quantity is worth its share of the line's **Net** (after the line and bill discounts),
   computed cumulatively so the parts can never exceed what was paid:
   `amount = round(Net × returnedSoFar / sold) − round(Net × returnedBefore / sold)`.
   Refund = `rupee(Σ all returns so far) − rupee(Σ earlier returns)` (the bill's own whole-rupee rounding).
   GST reversed per slab = `split(Σ slab amounts so far) − split(Σ before)`. Returning a whole bill in any number of
   parts therefore refunds exactly the GrandTotal and reverses exactly the bill's taxable value and tax per slab.
   A Bill of Supply's return carries no GST.
4. Refund method: **cash**, **UPI** (optional reference) or **cut udhaar** (bill's customer only,
   `RETURN_NEEDS_CUSTOMER`): dues go down at most to zero; any excess is handed back as cash.
5. Stock comes back through the ledger (`StockMove` reason `Return`, note = return number). Dues credit is a
   `Reversal` dues entry against the bill. Outbox: `ReturnRecorded`, `DuesCreditedByReturn`.
6. A bill with any return can no longer be voided (`BILL_HAS_RETURNS`) — the rest of it can still be returned.
7. Idempotent: the screen mints the return id once; the same id records once (a different bill → refused).
8. Reports: returns count on the day they happen (day-end: Returns, refunded in cash / by UPI / udhaar reduced, Net
   sales = Sales − Returns); the GST summary subtracts a return's GST in the period of the return, never the bill's
   (a filed month never moves). The customer account lists the customer's returns.
