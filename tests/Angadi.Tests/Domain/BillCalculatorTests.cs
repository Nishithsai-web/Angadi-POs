using Angadi.Domain.Billing;
using Angadi.Domain.Common;
using Angadi.Domain.Items;

namespace Angadi.Tests.Domain;

/// <summary>FR-13 billing math. Expected values were computed independently (docs/BILLING_RULES.md §Worked examples).</summary>
public class BillCalculatorTests
{
    internal static BillLine Line(long pricePaise, long qtyMilli, GstSlab slab, Discount? discount = null) => new()
    {
        ItemId = Guid.NewGuid(),
        UnitPrice = new Paise(pricePaise),
        Qty = new Qty(qtyMilli),
        GstSlab = slab,
        LineDiscount = discount ?? Discount.None,
    };

    internal static Bill BillOf(params BillLine[] lines) => new() { Lines = [.. lines] };

    [Fact]
    public void FR13_RiceAndDal_FivePercent_InclusiveTaxSplit()
    {
        var bill = BillOf(Line(6000, 2000, GstSlab.Five), Line(14000, 500, GstSlab.Five));
        BillCalculator.Compute(bill, shopHasGstin: true);

        Assert.Equal(12000, bill.Lines[0].LineTotal.Value);
        Assert.Equal(7000, bill.Lines[1].LineTotal.Value);
        Assert.Equal(19000, bill.SubTotal.Value);
        var split = Assert.Single(bill.TaxSplits);
        Assert.Equal(18095, split.Taxable.Value);
        Assert.Equal(905, split.Tax.Value);
        Assert.Equal(905, bill.TaxTotal.Value);
        Assert.Equal(19000, bill.GrandTotal.Value);
        Assert.Equal(0, bill.RoundingAdjustment.Value);
        Assert.True(bill.IsTaxInvoice);
    }

    [Fact]
    public void FR13_MixedSlabs_SplitPerSlabInOrder()
    {
        var bill = BillOf(Line(4500, 1000, GstSlab.Zero), Line(2000, 3000, GstSlab.Eighteen), Line(11800, 1000, GstSlab.Twelve));
        BillCalculator.Compute(bill, true);

        Assert.Equal(new[] { GstSlab.Zero, GstSlab.Twelve, GstSlab.Eighteen }, bill.TaxSplits.Select(s => s.Slab));
        Assert.Equal((4500L, 0L), (bill.TaxSplits[0].Taxable.Value, bill.TaxSplits[0].Tax.Value));
        Assert.Equal((10536L, 1264L), (bill.TaxSplits[1].Taxable.Value, bill.TaxSplits[1].Tax.Value));
        Assert.Equal((5085L, 915L), (bill.TaxSplits[2].Taxable.Value, bill.TaxSplits[2].Tax.Value));
        Assert.Equal(2179, bill.TaxTotal.Value);
        Assert.Equal(22300, bill.GrandTotal.Value);
    }

    [Fact]
    public void FR12_BillPercentDiscount_ReducesInclusivePrice_ThenTaxExtracted()
    {
        var bill = BillOf(Line(10000, 1000, GstSlab.Five), Line(5000, 1000, GstSlab.Eighteen));
        bill.BillDiscount = Discount.Percent(1000); // 10%
        BillCalculator.Compute(bill, true);

        Assert.Equal(1500, bill.BillDiscountAmount.Value);
        Assert.Equal(new[] { 1000L, 500L }, bill.Lines.Select(l => l.BillDiscountShare.Value));
        Assert.Equal((8571L, 429L), (bill.TaxSplits[0].Taxable.Value, bill.TaxSplits[0].Tax.Value));
        Assert.Equal((3814L, 686L), (bill.TaxSplits[1].Taxable.Value, bill.TaxSplits[1].Tax.Value));
        Assert.Equal(13500, bill.GrandTotal.Value);
        Assert.Equal(1500, BillCalculator.TotalDiscount(bill).Value);
    }

    [Fact]
    public void FR12_FlatBillDiscount_ApportionsByLargestRemainder_SumIsExact()
    {
        var bill = BillOf(Line(1000, 1000, GstSlab.Zero), Line(1000, 1000, GstSlab.Five), Line(1000, 1000, GstSlab.Eighteen));
        bill.BillDiscount = Discount.Flat(new Paise(700));
        BillCalculator.Compute(bill, true);

        Assert.Equal(new[] { 234L, 233L, 233L }, bill.Lines.Select(l => l.BillDiscountShare.Value));
        Assert.Equal(700, bill.Lines.Sum(l => l.BillDiscountShare.Value));
        Assert.Equal(154, bill.TaxTotal.Value);
        Assert.Equal(2300, bill.GrandTotal.Value);
    }

    [Fact]
    public void FR12_LinePercentDiscount_AndRoundingUpOnePaisa()
    {
        var bill = BillOf(Line(3333, 3000, GstSlab.Twelve, Discount.Percent(500)));
        BillCalculator.Compute(bill, true);

        Assert.Equal(9999, bill.Lines[0].Gross.Value);
        Assert.Equal(9499, bill.Lines[0].LineTotal.Value);
        Assert.Equal((8481L, 1018L), (bill.TaxSplits[0].Taxable.Value, bill.TaxSplits[0].Tax.Value));
        Assert.Equal(9500, bill.GrandTotal.Value);
        Assert.Equal(1, bill.RoundingAdjustment.Value);
        Assert.Equal(500, BillCalculator.TotalDiscount(bill).Value);
    }

    [Fact]
    public void FR13_QuarterKilo_PricePerKilo()
    {
        var bill = BillOf(Line(28000, 250, GstSlab.Five));
        BillCalculator.Compute(bill, true);
        Assert.Equal(7000, bill.GrandTotal.Value);
        Assert.Equal(333, bill.TaxTotal.Value);
    }

    [Theory]
    [InlineData(1050, 1100, 50)]   // exactly half rupee rounds up
    [InlineData(1049, 1000, -49)]  // just under rounds down
    [InlineData(1000, 1000, 0)]
    public void FR13_WholeRupeeRounding_HalfUp(long price, long grand, long rounding)
    {
        var bill = BillOf(Line(price, 1000, GstSlab.Zero));
        BillCalculator.Compute(bill, true);
        Assert.Equal(grand, bill.GrandTotal.Value);
        Assert.Equal(rounding, bill.RoundingAdjustment.Value);
    }

    [Fact]
    public void FR18_NoGstin_BillOfSupply_HasNoTaxSplit_SameTotal()
    {
        var bill = BillOf(Line(6000, 2000, GstSlab.Five));
        BillCalculator.Compute(bill, shopHasGstin: false);

        Assert.False(bill.IsTaxInvoice);
        Assert.Empty(bill.TaxSplits);
        Assert.Equal(0, bill.TaxTotal.Value);
        Assert.Equal(12000, bill.GrandTotal.Value);
    }

    [Fact]
    public void FR12_FlatDiscountLargerThanTotal_IsCappedAtTotal()
    {
        var bill = BillOf(Line(1000, 1000, GstSlab.Zero));
        bill.BillDiscount = Discount.Flat(new Paise(5000));
        BillCalculator.Compute(bill, true);
        Assert.Equal(1000, bill.BillDiscountAmount.Value);
        Assert.Equal(0, bill.GrandTotal.Value);
    }

    [Fact]
    public void FR12_PercentOver100_IsCapped()
    {
        var bill = BillOf(Line(1000, 1000, GstSlab.Zero, Discount.Percent(15000)));
        BillCalculator.Compute(bill, true);
        Assert.Equal(0, bill.Lines[0].LineTotal.Value);
    }

    [Fact]
    public void Compute_IsIdempotent()
    {
        var bill = BillOf(Line(1000, 1000, GstSlab.Five), Line(2500, 1500, GstSlab.Twelve));
        bill.BillDiscount = Discount.Flat(new Paise(333));
        BillCalculator.Compute(bill, true);
        var first = (bill.GrandTotal, bill.TaxTotal, Shares: string.Join(",", bill.Lines.Select(l => l.BillDiscountShare.Value)));
        BillCalculator.Compute(bill, true);
        Assert.Equal(first, (bill.GrandTotal, bill.TaxTotal, string.Join(",", bill.Lines.Select(l => l.BillDiscountShare.Value))));
    }

    [Fact]
    public void Compute_RejectsEmptyBill_AndZeroQty()
    {
        Assert.Throws<ArgumentException>(() => BillCalculator.Compute(new Bill(), true));
        Assert.Throws<ArgumentException>(() => BillCalculator.Compute(BillOf(Line(100, 0, GstSlab.Zero)), true));
    }

    [Fact]
    public void FR13_FortyPercentSlab_GstTwoPointZero()
    {
        var bill = BillOf(Line(10000, 1000, GstSlab.Forty));   // ₹100 soft drink
        BillCalculator.Compute(bill, true);
        Assert.Equal((7143L, 2857L), (bill.TaxSplits[0].Taxable.Value, bill.TaxSplits[0].Tax.Value));
    }

    [Fact]
    public void ChangeDue_IsCashMinusTotal()
    {
        var bill = BillOf(Line(17500, 1000, GstSlab.Zero));
        BillCalculator.Compute(bill, true);
        bill.Tender = TenderType.Cash;
        bill.CashReceived = Paise.FromRupees(200);
        Assert.Equal(2500, BillCalculator.ChangeDue(bill).Value);   // ₹200 − ₹175 = ₹25
    }

    [Theory]
    [InlineData(TenderType.Upi)]
    [InlineData(TenderType.Udhaar)]
    public void B1_ChangeDue_IsZero_ForNonCashTenders(TenderType tender)
    {
        var bill = BillOf(Line(20000, 1000, GstSlab.Zero));
        BillCalculator.Compute(bill, true);
        bill.Tender = tender;
        Assert.Equal(0, BillCalculator.ChangeDue(bill).Value);   // was −₹200 on udhaar
    }

    [Fact]
    public void B1_ChangeDue_IsZero_WhenCashNotYetEntered()
    {
        var bill = BillOf(Line(20000, 1000, GstSlab.Zero));
        BillCalculator.Compute(bill, true);
        bill.Tender = TenderType.Cash;
        Assert.Equal(0, BillCalculator.ChangeDue(bill).Value);
    }

    [Fact]
    public void FR13_Invariants_Hold_For2000RandomBills()
    {
        var rng = new Random(20261019);
        GstSlab[] slabs = [GstSlab.Zero, GstSlab.Five, GstSlab.Twelve, GstSlab.Eighteen, GstSlab.TwentyEight, GstSlab.Forty];

        for (var n = 0; n < 2000; n++)
        {
            var lines = Enumerable.Range(0, rng.Next(1, 12)).Select(_ => Line(
                rng.Next(0, 200_000), rng.Next(1, 25_000), slabs[rng.Next(slabs.Length)],
                rng.Next(4) switch
                {
                    0 => Discount.Percent(rng.Next(0, 3000)),
                    1 => Discount.Flat(new Paise(rng.Next(0, 5000))),
                    _ => Discount.None,
                })).ToArray();
            var bill = BillOf(lines);
            bill.BillDiscount = rng.Next(3) switch
            {
                0 => Discount.Percent(rng.Next(0, 2500)),
                1 => Discount.Flat(new Paise(rng.Next(0, 20_000))),
                _ => Discount.None,
            };

            BillCalculator.Compute(bill, shopHasGstin: true);

            Assert.Equal(bill.BillDiscountAmount.Value, bill.Lines.Sum(l => l.BillDiscountShare.Value));
            Assert.All(bill.Lines, l => Assert.True(l.Net.Value >= 0));
            var net = bill.SubTotal.Value - bill.BillDiscountAmount.Value;
            Assert.Equal(net, bill.TaxSplits.Sum(s => s.Taxable.Value + s.Tax.Value));
            Assert.All(bill.TaxSplits, s => Assert.True(s.Tax.Value >= 0 && s.Taxable.Value >= 0));
            Assert.Equal(0, bill.GrandTotal.Value % 100);
            Assert.InRange(bill.RoundingAdjustment.Value, -49, 50);
            Assert.Equal(net + bill.RoundingAdjustment.Value, bill.GrandTotal.Value);
        }
    }
}
