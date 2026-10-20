using System.Numerics;
using Angadi.Domain.Billing;
using Angadi.Domain.Common;
using Angadi.Domain.Items;

namespace Angadi.Tests.Domain;

/// <summary>
/// T1: the money math, checked against an independent arbitrary-precision oracle written from docs/BILLING_RULES.md
/// (BigInteger, so it can never overflow or wrap), plus first-principles properties that do not depend on the
/// algorithm at all (tax is the slab rate of the taxable value to within a paisa, discount shares are within a
/// paisa of the exact pro-rata amount, totals are whole rupees, …). Seeded Random: every run checks the same bills.
/// </summary>
public class BillMathPropertyTests
{
    private static readonly GstSlab[] CurrentSlabs = [GstSlab.Zero, GstSlab.Five, GstSlab.Eighteen, GstSlab.Forty];

    // ---------------------------------------------------------------- oracle

    private sealed record Expected(
        long[] Gross, long[] LineTotal, long[] Share, long SubTotal, long BillDiscount,
        (GstSlab Slab, long Taxable, long Tax)[] Splits, long Net, long Grand)
    {
        public bool FitsInLong { get; init; } = true;
    }

    private static BigInteger HalfUp(BigInteger n, BigInteger d) => (2 * n + d) / (2 * d);

    private static BigInteger DiscountOf(BigInteger baseAmount, Discount d) => d.Kind switch
    {
        DiscountKind.PercentBasisPoints => HalfUp(baseAmount * Math.Clamp(d.Amount, 0, 10_000), 10_000),
        DiscountKind.FlatPaise => BigInteger.Min(BigInteger.Max(d.Amount, 0), baseAmount),
        _ => BigInteger.Zero,
    };

    /// <summary>The rules of docs/BILLING_RULES.md, re-derived in BigInteger. Also reports whether any intermediate exceeds long.</summary>
    private static Expected Oracle(Bill bill, bool gstin)
    {
        var max = new BigInteger(long.MaxValue);
        var fits = true;
        void Track(BigInteger v) { if (v > max) fits = false; }

        var n = bill.Lines.Count;
        var gross = new BigInteger[n];
        var lt = new BigInteger[n];
        for (var i = 0; i < n; i++)
        {
            var l = bill.Lines[i];
            var product = (BigInteger)l.UnitPrice.Value * l.Qty.Milli;
            Track(product); Track(2 * product + 1000);
            gross[i] = HalfUp(product, 1000);
            var disc = DiscountOf(gross[i], l.LineDiscount);
            if (l.LineDiscount.Kind == DiscountKind.PercentBasisPoints) Track(2 * gross[i] * Math.Clamp(l.LineDiscount.Amount, 0, 10_000) + 10_000);
            lt[i] = gross[i] - disc;
        }
        var sub = lt.Aggregate(BigInteger.Zero, (a, b) => a + b);
        Track(sub);
        if (bill.BillDiscount.Kind == DiscountKind.PercentBasisPoints) Track(2 * sub * Math.Clamp(bill.BillDiscount.Amount, 0, 10_000) + 10_000);
        var bd = DiscountOf(sub, bill.BillDiscount);

        // Largest remainder: floor of the exact pro-rata share, leftover paise to the biggest remainders, ties to the earlier line.
        var share = new BigInteger[n];
        if (bd > 0 && sub > 0)
        {
            var rem = new BigInteger[n];
            for (var i = 0; i < n; i++)
            {
                Track(lt[i] * bd);
                share[i] = BigInteger.DivRem(lt[i] * bd, sub, out rem[i]);
            }
            var left = (int)(bd - share.Aggregate(BigInteger.Zero, (a, b) => a + b));
            foreach (var i in Enumerable.Range(0, n).OrderByDescending(i => rem[i]).ThenBy(i => i).Take(left)) share[i] += 1;
        }

        var splits = Array.Empty<(GstSlab Slab, BigInteger Taxable, BigInteger Tax)>();
        if (gstin)
        {
            splits = Enumerable.Range(0, n)
                .GroupBy(i => bill.Lines[i].GstSlab)
                .OrderBy(g => (int)g.Key)
                .Select(g =>
                {
                    var net = g.Aggregate(BigInteger.Zero, (a, i) => a + lt[i] - share[i]);
                    var bp = (int)g.Key;
                    if (bp != 0) Track(2 * net * 10_000 + 10_000 + bp);
                    var taxable = bp == 0 ? net : HalfUp(net * 10_000, 10_000 + bp);
                    return (g.Key, taxable, net - taxable);
                })
                .ToArray();
        }

        var billNet = sub - bd;
        Track(2 * billNet + 100);
        var grand = HalfUp(billNet, 100) * 100;
        if (!fits) return new Expected([], [], [], 0, 0, [], 0, 0) { FitsInLong = false };
        return new Expected(
            gross.Select(g => (long)g).ToArray(), lt.Select(x => (long)x).ToArray(), share.Select(s => (long)s).ToArray(),
            (long)sub, (long)bd, splits.Select(x => (x.Slab, (long)x.Taxable, (long)x.Tax)).ToArray(), (long)billNet, (long)grand);
    }

    // ---------------------------------------------------------------- generators

    private static BillLine Line(long price, long qtyMilli, GstSlab slab, Discount? discount = null, Unit unit = Unit.Piece) => new()
    {
        ItemId = Guid.NewGuid(), UnitPrice = new Paise(price), Qty = new Qty(qtyMilli), GstSlab = slab,
        LineDiscount = discount ?? Discount.None, Unit = unit,
    };

    /// <summary>Counter-shaped bills: loose goods by kg/L (fractional), per-gram/per-ml items, packs, strips; all current slabs.</summary>
    private static Bill RandomCounterBill(Random rng)
    {
        var lines = new List<BillLine>();
        var count = rng.Next(1, 16);
        for (var i = 0; i < count; i++)
        {
            var slab = CurrentSlabs[rng.Next(CurrentSlabs.Length)];
            var (price, qty, unit) = rng.Next(5) switch
            {
                0 => ((long)rng.Next(1_000, 120_000), rng.Next(1, 40) * 250L, Unit.Kilogram),  // ¼ kg steps of ₹10–₹1,200/kg
                1 => ((long)rng.Next(2_000, 30_000), rng.Next(1, 10) * 500L, Unit.Litre),     // ½ L steps
                2 => ((long)rng.Next(1, 300), rng.Next(1, 2_000) * 1_000L, Unit.Gram),        // priced per gram, e.g. 28 paise/g
                3 => ((long)rng.Next(1, 50), rng.Next(1, 1_000) * 1_000L, Unit.Millilitre),   // priced per ml
                _ => ((long)rng.Next(0, 250_000), rng.Next(1, 25) * 1_000L, Unit.Piece),      // packs/strips incl. free (₹0) items
            };
            if (rng.Next(20) == 0) qty = rng.Next(1, 3_000);   // odd weights straight off a scale: 0.001–2.999
            var discount = rng.Next(5) switch
            {
                0 => Discount.Percent(rng.Next(0, 10_001)),
                1 => Discount.Flat(new Paise(rng.Next(0, 100_000))),
                _ => Discount.None,
            };
            lines.Add(Line(price, qty, slab, discount, unit));
        }
        return new Bill
        {
            Lines = lines,
            BillDiscount = rng.Next(3) switch
            {
                0 => Discount.Percent(rng.Next(0, 10_001)),
                1 => Discount.Flat(new Paise(rng.Next(0, 500_000))),
                _ => Discount.None,
            },
        };
    }

    // ---------------------------------------------------------------- checks

    private static void AssertMatchesOracle(Bill bill, bool gstin, string ctx)
    {
        var e = Oracle(bill, gstin);
        Assert.True(e.FitsInLong, ctx);
        for (var i = 0; i < bill.Lines.Count; i++)
        {
            var l = bill.Lines[i];
            Assert.True(e.Gross[i] == l.Gross.Value, $"{ctx} line {i} gross {l.Gross.Value} != {e.Gross[i]}");
            Assert.True(e.LineTotal[i] == l.LineTotal.Value, $"{ctx} line {i} total {l.LineTotal.Value} != {e.LineTotal[i]}");
            Assert.True(e.Share[i] == l.BillDiscountShare.Value, $"{ctx} line {i} share {l.BillDiscountShare.Value} != {e.Share[i]}");
        }
        Assert.Equal(e.SubTotal, bill.SubTotal.Value);
        Assert.Equal(e.BillDiscount, bill.BillDiscountAmount.Value);
        Assert.Equal(e.Splits, bill.TaxSplits.Select(s => (s.Slab, s.Taxable.Value, s.Tax.Value)).ToArray());
        Assert.Equal(e.Grand, bill.GrandTotal.Value);
        Assert.Equal(e.Grand - e.Net, bill.RoundingAdjustment.Value);
    }

    /// <summary>Properties that hold whatever the algorithm: the shopkeeper-visible promises of the receipt.</summary>
    private static void AssertInvariants(Bill bill, bool gstin, string ctx)
    {
        // Lines: nothing negative, discount never more than the line.
        foreach (var l in bill.Lines)
        {
            Assert.True(l.Gross.Value >= 0, ctx);
            Assert.True(l.LineTotal.Value >= 0 && l.LineTotal <= l.Gross, $"{ctx}: line total {l.LineTotal.Value} outside 0..{l.Gross.Value}");
            Assert.True(l.BillDiscountShare.Value >= 0 && l.BillDiscountShare <= l.LineTotal, $"{ctx}: share {l.BillDiscountShare.Value} outside 0..{l.LineTotal.Value}");
            Assert.True(l.Net.Value >= 0, ctx);
        }

        // Sub-total and bill discount.
        Assert.Equal(bill.Lines.Sum(l => l.LineTotal.Value), bill.SubTotal.Value);
        Assert.InRange(bill.BillDiscountAmount.Value, 0, bill.SubTotal.Value);
        Assert.Equal(bill.BillDiscountAmount.Value, bill.Lines.Sum(l => l.BillDiscountShare.Value));
        // Each share is within one paisa of its exact pro-rata part: |share·S − LT·D| < S.
        if (bill.SubTotal.Value > 0)
            foreach (var l in bill.Lines)
            {
                var diff = BigInteger.Abs((BigInteger)l.BillDiscountShare.Value * bill.SubTotal.Value - (BigInteger)l.LineTotal.Value * bill.BillDiscountAmount.Value);
                Assert.True(diff < bill.SubTotal.Value, $"{ctx}: share {l.BillDiscountShare.Value} not within a paisa of pro-rata");
            }

        // Rounding to the whole rupee.
        var net = bill.SubTotal.Value - bill.BillDiscountAmount.Value;
        Assert.Equal(0, bill.GrandTotal.Value % 100);
        Assert.InRange(bill.RoundingAdjustment.Value, -49, 50);
        Assert.Equal(net + bill.RoundingAdjustment.Value, bill.GrandTotal.Value);
        Assert.True(bill.GrandTotal.Value >= 0, ctx);

        // Tax.
        Assert.Equal(gstin, bill.IsTaxInvoice);
        Assert.Equal(bill.TaxSplits.Sum(s => s.Tax.Value), bill.TaxTotal.Value);
        if (!gstin)
        {
            Assert.Empty(bill.TaxSplits);
            Assert.Equal(0, bill.TaxTotal.Value);
            return;
        }
        Assert.Equal(bill.Lines.Select(l => l.GstSlab).Distinct().OrderBy(s => (int)s), bill.TaxSplits.Select(s => s.Slab));
        foreach (var s in bill.TaxSplits)
        {
            var slabNet = bill.Lines.Where(l => l.GstSlab == s.Slab).Sum(l => l.Net.Value);
            Assert.True(slabNet == s.Taxable.Value + s.Tax.Value, $"{ctx}: slab {s.Slab} taxable+tax != net {slabNet}");
            Assert.True(s.Taxable.Value >= 0 && s.Tax.Value >= 0, ctx);
            var bp = (long)s.Slab;
            if (bp == 0) { Assert.Equal(0, s.Tax.Value); continue; }
            // Tax is the slab rate of the taxable value to within a paisa: |tax·10000 − taxable·bp| ≤ (10000 + bp) / 2.
            var err = BigInteger.Abs((BigInteger)s.Tax.Value * 10_000 - (BigInteger)s.Taxable.Value * bp);
            Assert.True(err * 2 <= 10_000 + bp, $"{ctx}: slab {s.Slab} tax {s.Tax.Value} is not {bp}bp of {s.Taxable.Value}");
        }
    }

    // ---------------------------------------------------------------- property tests

    [Fact]
    public void T1_CounterBills_MatchTheOracle_AndHoldEveryInvariant_TaxInvoiceAndBillOfSupply()
    {
        var rng = new Random(1_19_10_2026);
        for (var n = 0; n < 3_000; n++)
        {
            var bill = RandomCounterBill(rng);
            var gstin = n % 2 == 0;
            BillCalculator.Compute(bill, gstin);
            var ctx = $"bill #{n}";
            AssertInvariants(bill, gstin, ctx);
            AssertMatchesOracle(bill, gstin, ctx);
        }
    }

    [Fact]
    public void T1_BillOfSupply_AndTaxInvoice_ChargeTheSame_OnlyTheTaxLinesDiffer()
    {
        var rng = new Random(18);
        for (var n = 0; n < 1_000; n++)
        {
            var a = RandomCounterBill(rng);
            var b = Clone(a);
            BillCalculator.Compute(a, shopHasGstin: true);
            BillCalculator.Compute(b, shopHasGstin: false);

            Assert.True(a.IsTaxInvoice);
            Assert.False(b.IsTaxInvoice);
            Assert.Empty(b.TaxSplits);
            Assert.Equal(0, b.TaxTotal.Value);
            Assert.Equal((a.SubTotal, a.BillDiscountAmount, a.RoundingAdjustment, a.GrandTotal), (b.SubTotal, b.BillDiscountAmount, b.RoundingAdjustment, b.GrandTotal));
            Assert.Equal(a.Lines.Select(l => (l.Gross, l.LineTotal, l.BillDiscountShare)), b.Lines.Select(l => (l.Gross, l.LineTotal, l.BillDiscountShare)));
        }
    }

    [Fact]
    public void T1_LineOrder_NeverChangesWhatTheCustomerPays()
    {
        var rng = new Random(7);
        for (var n = 0; n < 1_000; n++)
        {
            var a = RandomCounterBill(rng);
            var b = Clone(a);
            b.Lines = [.. b.Lines.OrderBy(_ => rng.Next())];
            BillCalculator.Compute(a, true);
            BillCalculator.Compute(b, true);
            Assert.Equal((a.SubTotal, a.BillDiscountAmount, a.GrandTotal), (b.SubTotal, b.BillDiscountAmount, b.GrandTotal));
        }
    }

    [Fact]
    public void T1_RecomputeIsStable_AndComputeNeverTouchesStatusOrTender()
    {
        var rng = new Random(99);
        for (var n = 0; n < 300; n++)
        {
            var bill = RandomCounterBill(rng);
            bill.Tender = TenderType.Upi;
            BillCalculator.Compute(bill, true);
            var first = Snapshot(bill);
            BillCalculator.Compute(bill, true);
            Assert.Equal(first, Snapshot(bill));
            Assert.Equal(BillStatus.Draft, bill.Status);
            Assert.Equal(TenderType.Upi, bill.Tender);
        }
    }

    /// <summary>Near the M7 caps (₹1 crore price, 1,00,000 units a line): either the exact oracle answer or a loud OverflowException.</summary>
    [Fact]
    public void T1_HugeBills_AreExact_OrOverflowLoudly_NeverWrapSilently()
    {
        var rng = new Random(7_000_000);
        int exact = 0, overflowed = 0;
        for (var n = 0; n < 1_500; n++)
        {
            // Log-uniform up to the caps (₹1 crore a unit, 1,00,000 units a line), so every order of magnitude is hit.
            var lines = Enumerable.Range(0, rng.Next(1, 14)).Select(_ => Line(
                (long)Math.Pow(10, rng.NextDouble() * 9),
                Math.Max(1, (long)Math.Pow(10, rng.NextDouble() * 8)),
                CurrentSlabs[rng.Next(CurrentSlabs.Length)],
                rng.Next(3) == 0 ? Discount.Percent(rng.Next(0, 10_001)) : Discount.None)).ToList();
            var bill = new Bill
            {
                Lines = lines,
                BillDiscount = rng.Next(3) switch
                {
                    0 => Discount.Percent(rng.Next(0, 10_001)),
                    1 => Discount.Flat(new Paise(rng.NextInt64(0, long.MaxValue / 4))),
                    _ => Discount.None,
                },
            };
            var gstin = rng.Next(2) == 0;
            var oracle = Oracle(bill, gstin);
            try
            {
                BillCalculator.Compute(bill, gstin);
            }
            catch (OverflowException)
            {
                Assert.False(oracle.FitsInLong, $"bill #{n}: overflowed although every intermediate fits in a long");
                overflowed++;
                continue;
            }
            Assert.True(oracle.FitsInLong, $"bill #{n}: computed although an intermediate exceeds long — the result may have wrapped");
            AssertInvariants(bill, gstin, $"huge #{n}");
            AssertMatchesOracle(bill, gstin, $"huge #{n}");
            exact++;
        }
        Assert.True(exact > 100, $"only {exact} huge bills computed ({overflowed} overflowed) — generator too aggressive");
    }

    [Fact]
    public void T1_SingleLineAtTheM7Caps_FitsInLong_WithEveryCurrentSlab()
    {
        foreach (var slab in CurrentSlabs)
        {
            var bill = new Bill { Lines = [Line(1_00_00_000_00, 100_000_000, slab)] };   // ₹1 crore × 1,00,000 = ₹1,00,000 crore
            BillCalculator.Compute(bill, true);
            Assert.Equal(1_00_00_000_00L * 100_000, bill.GrandTotal.Value);
            AssertInvariants(bill, true, slab.ToString());
            AssertMatchesOracle(bill, true, slab.ToString());
        }
    }

    [Fact]
    public void T1_Overflow_IsAnOverflowException_NotAWrappedTotal()
    {
        // price × qty overflows.
        Assert.Throws<OverflowException>(() => BillCalculator.Compute(new Bill { Lines = [Line(long.MaxValue / 1000 + 1, 1000, GstSlab.Zero)] }, false));
        // Ten max-cap lines in one slab: the per-slab tax extraction (net × 10000) overflows on a Tax Invoice…
        var lines = Enumerable.Range(0, 10).Select(_ => Line(1_00_00_000_00, 100_000_000, GstSlab.Five)).ToList();
        Assert.Throws<OverflowException>(() => BillCalculator.Compute(new Bill { Lines = lines }, shopHasGstin: true));
        // …a Bill of Supply does no tax extraction, so the same goods total exactly.
        var bos = new Bill { Lines = Enumerable.Range(0, 10).Select(_ => Line(1_00_00_000_00, 100_000_000, GstSlab.Five)).ToList() };
        BillCalculator.Compute(bos, shopHasGstin: false);
        Assert.Equal(10L * 1_00_00_000_00 * 100_000, bos.GrandTotal.Value);
        // A percent bill discount on a near-max subtotal overflows in subtotal × bp.
        var discounted = new Bill { Lines = [Line(long.MaxValue / 2000, 1000, GstSlab.Zero)], BillDiscount = Discount.Percent(5_000) };
        Assert.Throws<OverflowException>(() => BillCalculator.Compute(discounted, false));
    }

    // ---------------------------------------------------------------- worked edge cases (hand-computed)

    [Theory]
    // price paise, qty milli, unit, slab, expected gross, expected grand, expected rounding
    [InlineData(6_400, 1_500, Unit.Litre, GstSlab.Five, 9_600, 9_600, 0)]           // 1.5 L @ ₹64/L
    [InlineData(28_000, 250, Unit.Kilogram, GstSlab.Five, 7_000, 7_000, 0)]          // 0.25 kg @ ₹280/kg
    [InlineData(9_999, 333, Unit.Kilogram, GstSlab.Zero, 3_330, 3_300, -30)]         // 0.333 kg @ ₹99.99 = 3329.667 → 3330
    [InlineData(50, 125_000, Unit.Gram, GstSlab.Eighteen, 6_250, 6_300, 50)]         // 125 g @ 50 paise/g, half rupee rounds up
    [InlineData(12, 750_000, Unit.Millilitre, GstSlab.Forty, 9_000, 9_000, 0)]       // 750 ml @ 12 paise/ml
    [InlineData(1, 500, Unit.Gram, GstSlab.Zero, 1, 0, -1)]                          // half a paisa rounds up to 1 paisa, bill rounds to ₹0
    [InlineData(1, 499, Unit.Gram, GstSlab.Zero, 0, 0, 0)]                           // under half a paisa is free
    [InlineData(1, 1, Unit.Gram, GstSlab.Five, 0, 0, 0)]                             // 1 milli of 1 paisa
    [InlineData(0, 5_000, Unit.Piece, GstSlab.Eighteen, 0, 0, 0)]                    // free item
    public void T1_FractionalAndPerUnitQuantities(long price, long qty, Unit unit, GstSlab slab, long gross, long grand, long rounding)
    {
        var bill = new Bill { Lines = [Line(price, qty, slab, unit: unit)] };
        BillCalculator.Compute(bill, true);
        Assert.Equal(gross, bill.Lines[0].Gross.Value);
        Assert.Equal(grand, bill.GrandTotal.Value);
        Assert.Equal(rounding, bill.RoundingAdjustment.Value);
        AssertInvariants(bill, true, "case");
    }

    [Fact]
    public void T1_AllFourCurrentSlabs_OnOneBill_WithBillDiscount_SplitIndependently()
    {
        // ₹105 (0%), ₹105 (5%), ₹118 (18%), ₹140 (40%) = ₹468; 10% off = ₹46.80 → shares 10.50 / 10.50 / 11.80 / 14.00.
        var bill = new Bill
        {
            Lines = [Line(10_500, 1000, GstSlab.Zero), Line(10_500, 1000, GstSlab.Five), Line(11_800, 1000, GstSlab.Eighteen), Line(14_000, 1000, GstSlab.Forty)],
            BillDiscount = Discount.Percent(1000),
        };
        BillCalculator.Compute(bill, true);

        Assert.Equal(4_680, bill.BillDiscountAmount.Value);
        Assert.Equal(new long[] { 1_050, 1_050, 1_180, 1_400 }, bill.Lines.Select(l => l.BillDiscountShare.Value));
        Assert.Equal(new[] { (GstSlab.Zero, 9_450L, 0L), (GstSlab.Five, 9_000L, 450L), (GstSlab.Eighteen, 9_000L, 1_620L), (GstSlab.Forty, 9_000L, 3_600L) },
            bill.TaxSplits.Select(s => (s.Slab, s.Taxable.Value, s.Tax.Value)));
        Assert.Equal(5_670, bill.TaxTotal.Value);
        Assert.Equal(42_100, bill.GrandTotal.Value);       // 421.20 → ₹421
        Assert.Equal(-20, bill.RoundingAdjustment.Value);
    }

    [Fact]
    public void T1_LineAndBillDiscountTogether_NeverGoNegative_EvenWhenBothAreTotal()
    {
        var bill = new Bill
        {
            Lines = [Line(1_000, 1000, GstSlab.Five, Discount.Flat(new Paise(5_000))), Line(2_000, 1000, GstSlab.Eighteen, Discount.Percent(10_000)), Line(500, 2000, GstSlab.Zero)],
            BillDiscount = Discount.Flat(new Paise(1_000_000)),
        };
        BillCalculator.Compute(bill, true);
        Assert.Equal(new long[] { 0, 0, 1_000 }, bill.Lines.Select(l => l.LineTotal.Value));
        Assert.Equal(1_000, bill.BillDiscountAmount.Value);
        Assert.Equal(new long[] { 0, 0, 1_000 }, bill.Lines.Select(l => l.BillDiscountShare.Value));
        Assert.Equal(0, bill.GrandTotal.Value);
        Assert.Equal(4_000, BillCalculator.TotalDiscount(bill).Value);
        AssertInvariants(bill, true, "total discount");
    }

    [Fact]
    public void T1_NegativeDiscounts_AreTreatedAsNoDiscount_NeverASurcharge()
    {
        var bill = new Bill
        {
            Lines = [Line(1_000, 1000, GstSlab.Zero, Discount.Flat(new Paise(-500))), Line(1_000, 1000, GstSlab.Zero, Discount.Percent(-2_000))],
            BillDiscount = Discount.Flat(new Paise(-700)),
        };
        BillCalculator.Compute(bill, false);
        Assert.Equal(2_000, bill.GrandTotal.Value);
        Assert.Equal(0, BillCalculator.TotalDiscount(bill).Value);
    }

    [Fact]
    public void T1_LargestRemainder_TiesGoToTheEarlierLine_AndZeroLinesGetNothing()
    {
        // ₹1 off across three equal ₹1 lines and a ₹0 line: 34 / 33 / 33 / 0.
        var bill = new Bill
        {
            Lines = [Line(100, 1000, GstSlab.Zero), Line(0, 1000, GstSlab.Zero), Line(100, 1000, GstSlab.Zero), Line(100, 1000, GstSlab.Zero)],
            BillDiscount = Discount.Flat(new Paise(100)),
        };
        BillCalculator.Compute(bill, false);
        Assert.Equal(new long[] { 34, 0, 33, 33 }, bill.Lines.Select(l => l.BillDiscountShare.Value));
    }

    [Fact]
    public void T1_ChangeDue_OnlyCash_NeverNegative()
    {
        var bill = new Bill { Lines = [Line(17_550, 1000, GstSlab.Zero)] };
        BillCalculator.Compute(bill, false);   // ₹175.50 → ₹176
        bill.Tender = TenderType.Cash;
        bill.CashReceived = Paise.FromRupees(100);
        Assert.Equal(0, BillCalculator.ChangeDue(bill).Value);          // short: no negative change
        bill.CashReceived = Paise.FromRupees(176);
        Assert.Equal(0, BillCalculator.ChangeDue(bill).Value);
        bill.CashReceived = Paise.FromRupees(500);
        Assert.Equal(32_400, BillCalculator.ChangeDue(bill).Value);
    }

    // ---------------------------------------------------------------- helpers

    private static Bill Clone(Bill b) => new()
    {
        BillDiscount = b.BillDiscount,
        Lines = b.Lines.Select(l => new BillLine
        {
            Id = l.Id, ItemId = l.ItemId, UnitPrice = l.UnitPrice, Qty = l.Qty, GstSlab = l.GstSlab, LineDiscount = l.LineDiscount, Unit = l.Unit,
        }).ToList(),
    };

    private static string Snapshot(Bill b) => string.Join("|",
        b.SubTotal.Value, b.BillDiscountAmount.Value, b.TaxTotal.Value, b.RoundingAdjustment.Value, b.GrandTotal.Value,
        string.Join(",", b.TaxSplits.Select(s => $"{(int)s.Slab}:{s.Taxable.Value}:{s.Tax.Value}")),
        string.Join(",", b.Lines.Select(l => $"{l.Gross.Value}:{l.LineTotal.Value}:{l.BillDiscountShare.Value}")));
}
