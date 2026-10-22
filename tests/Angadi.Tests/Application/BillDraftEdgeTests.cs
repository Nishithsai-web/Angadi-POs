using System.Text.Json;
using Angadi.Application.Billing;
using Angadi.Domain.Billing;
using Angadi.Domain.Common;
using Angadi.Domain.Items;

namespace Angadi.Tests.Application;

/// <summary>T1: the counter's working bill — merge rules, per-bill price overrides, removal, and snapshot round-trips.</summary>
public class BillDraftEdgeTests
{
    private static Item Item(string name, long price, GstSlab slab = GstSlab.Five, Unit unit = Unit.Kilogram) =>
        new() { TeluguName = name, EnglishName = name, Price = new Paise(price), GstSlab = slab, Unit = unit };

    // ---------------------------------------------------------------- merge rules

    [Fact]
    public void Merge_SameItemSamePrice_AddsQuantities_IncludingFractions()
    {
        var d = new BillDraft(shopHasGstin: true);
        var rice = Item("బియ్యం", 6_000);
        var first = d.Add(rice, new Qty(250));
        var again = d.Add(rice, new Qty(250), LineSource.Voice);
        d.Add(rice);

        Assert.Same(first, again);
        var line = Assert.Single(d.Lines);
        Assert.Equal(1_500, line.Qty.Milli);
        Assert.Equal(LineSource.Manual, line.Source);   // the line keeps how it was first added
        Assert.Equal(9_000, d.Bill.GrandTotal.Value);
    }

    [Fact]
    public void Merge_NeverIntoAnOverriddenPrice_OrADiscountedLine_OrAnotherItem()
    {
        var d = new BillDraft(false);
        var rice = Item("బియ్యం", 6_000);
        var dal = Item("పప్పు", 14_000);

        var bargained = d.Add(rice);
        d.SetPrice(bargained.Id, new Paise(5_500));            // this customer gets rice at ₹55
        var full = d.Add(rice);                                  // the next scan is at the shelf price: a new line
        d.SetLineDiscount(full.Id, Discount.Percent(1_000));
        var third = d.Add(rice);                                 // not merged into the discounted line either
        var other = d.Add(dal);

        Assert.Equal(4, d.Lines.Count);
        Assert.Equal(new long[] { 5_500, 6_000, 6_000, 14_000 }, d.Lines.Select(l => l.UnitPrice.Value));
        Assert.Equal(Discount.None, third.LineDiscount);
        Assert.NotEqual(third.Id, other.Id);
        Assert.Equal(5_500 + 5_400 + 6_000 + 14_000, d.Bill.GrandTotal.Value);
    }

    [Fact]
    public void Merge_AfterTheCatalogPriceChanged_StartsANewLine_OldLineKeepsItsPrice()
    {
        var d = new BillDraft(false);
        var oil = Item("నూనె", 15_000, unit: Unit.Litre);
        d.Add(oil);
        oil.Price = new Paise(16_000);   // owner edits the price mid-bill
        d.Add(oil);
        Assert.Equal(new long[] { 15_000, 16_000 }, d.Lines.Select(l => l.UnitPrice.Value));
    }

    [Fact]
    public void PriceOverride_IsForThisBillOnly()
    {
        var rice = Item("బియ్యం", 6_000);
        var d = new BillDraft(false);
        var line = d.Add(rice, Qty.Whole(2));
        d.SetPrice(line.Id, new Paise(5_000));

        Assert.Equal(6_000, rice.Price.Value);                  // the item is untouched
        Assert.Equal(10_000, d.Bill.GrandTotal.Value);

        var next = new BillDraft(false);
        next.Add(rice, Qty.Whole(2));
        Assert.Equal(12_000, next.Bill.GrandTotal.Value);       // the next customer pays the shelf price

        d.SetPrice(line.Id, Paise.Zero);                        // giving it away is allowed
        Assert.Equal(0, d.Bill.GrandTotal.Value);
    }

    // ---------------------------------------------------------------- quantity edits

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-1_000)]
    public void SetQty_ZeroOrBelow_RemovesOnlyThatLine_AndTotalsFollow(long qty)
    {
        var d = new BillDraft(true);
        var rice = d.Add(Item("బియ్యం", 6_000));
        d.Add(Item("పప్పు", 14_000));
        d.SetQty(rice.Id, new Qty(qty));

        var left = Assert.Single(d.Lines);
        Assert.Equal("పప్పు", left.TeluguName);
        Assert.Equal(14_000, d.Bill.GrandTotal.Value);
        Assert.Equal(667, d.Bill.TaxTotal.Value);               // 14000 − round(14000 / 1.05) = 14000 − 13333
    }

    [Fact]
    public void DecrementingToZero_ThenRemovingTheLastLine_LeavesAZeroBill()
    {
        var d = new BillDraft(true);
        var line = d.Add(Item("బియ్యం", 6_000), new Qty(500));
        d.SetQty(line.Id, line.Qty - Qty.Whole(1));             // the "−" button on ½ kg
        Assert.True(d.IsEmpty);
        Assert.Equal((0L, 0L, 0L, 0L), (d.Bill.SubTotal.Value, d.Bill.TaxTotal.Value, d.Bill.GrandTotal.Value, d.Bill.RoundingAdjustment.Value));
        Assert.Empty(d.Bill.TaxSplits);
    }

    [Fact]
    public void Add_NonPositiveQuantity_Throws_AndChangesNothing()
    {
        var d = new BillDraft(false);
        Assert.Throws<ArgumentOutOfRangeException>(() => d.Add(Item("బియ్యం", 6_000), Qty.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => d.Add(Item("బియ్యం", 6_000), new Qty(-250)));
        Assert.True(d.IsEmpty);
    }

    [Fact]
    public void EveryEdit_RaisesChanged_SoTheScreenAndTheSavedDraftFollow()
    {
        var d = new BillDraft(false);
        var count = 0;
        d.Changed += (_, _) => count++;
        var line = d.Add(Item("బియ్యం", 6_000));
        d.SetQty(line.Id, Qty.Whole(2));
        d.SetPrice(line.Id, new Paise(5_000));
        d.SetLineDiscount(line.Id, Discount.Flat(new Paise(100)));
        d.SetBillDiscount(Discount.Percent(500));
        d.SetCustomer(Guid.NewGuid());
        d.SetTender(TenderType.Cash);
        d.Remove(line.Id);
        d.Clear();
        Assert.Equal(9, count);
    }

    /// <summary>
    /// A typo in the quantity prompt (up to 1,00,00,000 accepted) on an expensive item overflows the checked maths. The
    /// draft must stay as it was — today the line keeps the impossible quantity, every later edit throws again, and
    /// the screen (which was not refreshed) no longer matches the bill that will be sent to finalize.
    /// </summary>
    [Fact]
    public void Edit_ThatOverflows_LeavesTheDraftExactlyAsItWas()
    {
        var d = new BillDraft(shopHasGstin: true);
        var gold = d.Add(Item("వెండి నాణెం", 1_00_00_000_00, GstSlab.Five, Unit.Piece));   // ₹1 crore, the catalog maximum
        var before = d.Bill.GrandTotal;

        try { d.SetQty(gold.Id, new Qty(10_000_000_000)); }   // "10000000" typed into the qty prompt
        catch (OverflowException) { /* rejecting the edit loudly is fine */ }

        Assert.Equal(1_000, gold.Qty.Milli);
        Assert.Equal(before, d.Bill.GrandTotal);
        d.Add(Item("ఉప్పు", 2_800, GstSlab.Zero, Unit.Pack));   // the counter carries on
        Assert.Equal(2, d.Lines.Count);
    }

    // ---------------------------------------------------------------- snapshot / restore

    [Fact]
    public void Snapshot_RoundTripsThroughJson_WithIdenticalTotals_ForRandomDrafts()
    {
        var rng = new Random(4242);
        GstSlab[] slabs = [GstSlab.Zero, GstSlab.Five, GstSlab.Eighteen, GstSlab.Forty];
        var catalog = Enumerable.Range(0, 12).Select(i => Item("item" + i, rng.Next(0, 100_000), slabs[rng.Next(slabs.Length)],
            (Unit)rng.Next(0, 7))).ToArray();

        for (var n = 0; n < 400; n++)
        {
            var gstin = rng.Next(2) == 0;
            var d = new BillDraft(gstin);
            for (var k = rng.Next(1, 15); k > 0; k--)
            {
                var line = d.Add(catalog[rng.Next(catalog.Length)], new Qty(rng.Next(1, 5_000)), (LineSource)rng.Next(4));
                switch (rng.Next(6))
                {
                    case 0: d.SetPrice(line.Id, new Paise(rng.Next(0, 50_000))); break;
                    case 1: d.SetLineDiscount(line.Id, Discount.Percent(rng.Next(0, 10_001))); break;
                    case 2: d.SetLineDiscount(line.Id, Discount.Flat(new Paise(rng.Next(0, 20_000)))); break;
                    case 3: d.SetQty(line.Id, new Qty(rng.Next(-500, 3_000))); break;
                }
            }
            if (d.IsEmpty) continue;
            d.SetBillDiscount(rng.Next(3) switch
            {
                0 => Discount.Percent(rng.Next(0, 10_001)),
                1 => Discount.Flat(new Paise(rng.Next(0, 30_000))),
                _ => Discount.None,
            });
            if (rng.Next(2) == 0) d.SetCustomer(Guid.NewGuid());

            var json = JsonSerializer.Serialize(d.ToSnapshot(), DraftJson.Default.BillDraftSnapshot);
            var back = BillDraft.Restore(JsonSerializer.Deserialize(json, DraftJson.Default.BillDraftSnapshot)!, gstin);

            Assert.Equal(d.Bill.Id, back.Bill.Id);
            Assert.Equal(d.Bill.CustomerId, back.Bill.CustomerId);
            Assert.Equal(d.Bill.BillDiscount, back.Bill.BillDiscount);
            Assert.Equal(Fingerprint(d.Bill), Fingerprint(back.Bill));
        }
    }

    [Fact]
    public void Restore_DoesNotBringBackTheTender_TheShopkeeperChoosesItAgain()
    {
        var d = new BillDraft(false);
        d.Add(Item("బియ్యం", 6_000));
        d.SetTender(TenderType.Upi, upiRef: "9999");
        var back = BillDraft.Restore(d.ToSnapshot(), false);
        Assert.Null(back.Bill.Tender);
        Assert.Null(back.Bill.UpiRef);
        Assert.Equal(0, back.Bill.CashReceived.Value);
        Assert.Equal(BillStatus.Draft, back.Bill.Status);
    }

    [Fact]
    public void Restore_UnderTheOtherGstinSetting_ChangesOnlyTheTaxLines()
    {
        var d = new BillDraft(shopHasGstin: true);
        d.Add(Item("బియ్యం", 6_000), new Qty(1_250));
        d.Add(Item("కూల్ డ్రింక్", 4_000, GstSlab.Forty, Unit.Piece), Qty.Whole(3));
        var back = BillDraft.Restore(d.ToSnapshot(), shopHasGstin: false);   // GSTIN removed in settings meanwhile
        Assert.Equal(d.Bill.GrandTotal, back.Bill.GrandTotal);
        Assert.Equal(2, d.Bill.TaxSplits.Count);
        Assert.Empty(back.Bill.TaxSplits);
    }

    private static string Fingerprint(Bill b) => string.Join("|",
        b.SubTotal.Value, b.BillDiscountAmount.Value, b.TaxTotal.Value, b.RoundingAdjustment.Value, b.GrandTotal.Value, b.IsTaxInvoice,
        string.Join(",", b.TaxSplits.Select(s => $"{(int)s.Slab}:{s.Taxable.Value}:{s.Tax.Value}")),
        string.Join(",", b.Lines.Select(l =>
            $"{l.Id}:{l.ItemId}:{l.TeluguName}:{l.Unit}:{l.UnitPrice.Value}:{(int)l.GstSlab}:{l.Qty.Milli}:{l.LineDiscount}:{l.Source}:{l.Gross.Value}:{l.LineTotal.Value}:{l.BillDiscountShare.Value}")));
}
