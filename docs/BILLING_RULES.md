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
