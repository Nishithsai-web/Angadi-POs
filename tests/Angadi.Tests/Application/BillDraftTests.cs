using Angadi.Application.Billing;
using Angadi.Application.Voice;
using Angadi.Domain.Billing;
using Angadi.Domain.Common;
using Angadi.Domain.Items;

namespace Angadi.Tests.Application;

/// <summary>H10: the counter's working bill — merging, edits, tender rules and voice quantities.</summary>
public class BillDraftTests
{
    private static Item Rice(long pricePaise = 6000) =>
        new() { TeluguName = "బియ్యం", EnglishName = "Rice", Unit = Unit.Kilogram, Price = new Paise(pricePaise), GstSlab = GstSlab.Five };

    private static ParsedLine Line(Qty? qty = null, Unit? unit = null, Paise? amount = null) => new("బియ్యం", qty, unit, amount, "raw");

    [Fact]
    public void SameItemTwice_MergesIntoOneLine()
    {
        var d = new BillDraft(shopHasGstin: false);
        var rice = Rice();
        d.Add(rice);
        d.Add(rice, Qty.Whole(2));
        Assert.Equal(3000, Assert.Single(d.Lines).Qty.Milli);
        Assert.Equal(18_000, d.Bill.GrandTotal.Value);
    }

    [Fact]
    public void SetQtyZero_RemovesTheLine_NegativePriceThrows()
    {
        var d = new BillDraft(false);
        var line = d.Add(Rice());
        Assert.Throws<ArgumentOutOfRangeException>(() => d.SetPrice(line.Id, new Paise(-1)));
        d.SetQty(line.Id, Qty.Zero);
        Assert.True(d.IsEmpty);
    }

    [Fact]
    public void Tender_ClearsFieldsThatDontBelongToIt()
    {
        var d = new BillDraft(false);
        d.Add(Rice());
        d.SetTender(TenderType.Upi, Guid.NewGuid(), Paise.FromRupees(100), " 123456 ");
        Assert.Null(d.Bill.CustomerId);
        Assert.Equal(0, d.Bill.CashReceived.Value);
        Assert.Equal("123456", d.Bill.UpiRef);
        Assert.Equal(0, d.ChangeDue.Value);   // B1: change is cash-only

        d.SetTender(TenderType.Cash, cashReceived: Paise.FromRupees(100));
        Assert.Null(d.Bill.UpiRef);
        Assert.Equal(4000, d.ChangeDue.Value);
    }

    [Fact]
    public void Voice_GramsOfAKgItem_Convert()
    {
        var d = new BillDraft(false);
        var line = d.AddFromVoice(Rice(), Line(new Qty(200_000), Unit.Gram), out var confirm);
        Assert.Equal(200, line.Qty.Milli);
        Assert.False(confirm);
    }

    [Fact]
    public void Voice_RupeeAmount_ToQuantity()
    {
        var d = new BillDraft(false);
        var line = d.AddFromVoice(Rice(), Line(amount: Paise.FromRupees(30)), out var confirm);
        Assert.Equal(500, line.Qty.Milli);
        Assert.False(confirm);
    }

    [Fact]
    public void Voice_AmountOnAZeroPricedItem_AsksInsteadOfGuessing()
    {
        var d = new BillDraft(false);
        d.AddFromVoice(Rice(0), Line(amount: Paise.FromRupees(10)), out var confirm);
        Assert.True(confirm);
    }

    [Fact]
    public void Clear_StartsAFreshBill()
    {
        var d = new BillDraft(false);
        var before = d.Bill.Id;
        d.Add(Rice());
        d.Clear();
        Assert.True(d.IsEmpty);
        Assert.NotEqual(before, d.Bill.Id);
    }
}
