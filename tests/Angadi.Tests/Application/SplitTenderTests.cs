using Angadi.Application.Billing;
using Angadi.Application.Catalog;
using Angadi.Application.Common;
using Angadi.Application.Reports;
using Angadi.Domain.Billing;
using Angadi.Domain.Common;
using Angadi.Domain.Customers;
using Angadi.Domain.Items;

namespace Angadi.Tests.Application;

/// <summary>v2 split tender (docs/BILLING_RULES.md §Split tender), through the real handler graph.</summary>
public sealed class SplitTenderTests : IDisposable
{
    private readonly AppHost _h = new();
    public void Dispose() => _h.Dispose();

    private static BillPayment Part(TenderType t, long paise, long received = 0, string? upiRef = null) =>
        new() { Tender = t, Amount = new Paise(paise), CashReceived = new Paise(received), UpiRef = upiRef };

    /// <summary>Rice ₹62.50/kg × 2 kg = ₹125.</summary>
    private async Task<(Item Rice, Bill Draft)> DraftAsync(params BillPayment[] parts)
    {
        await _h.SeedShopAsync();
        var rice = await _h.AddItemAsync("బియ్యం", 6_250);
        var draft = AppHost.Draft(TenderType.Split, (rice, 2_000));
        draft.Payments = parts.ToList();
        return (rice, draft);
    }

    private async Task<Guid> CustomerAsync() => (await _h.Sender.Send(new SaveCustomerCommand(new Customer { Name = "రవి" }))).Value;

    [Fact]
    public async Task CashPlusUpi_PartsAddUp_SavedAsParts_ReportedPerTender()
    {
        var (rice, draft) = await DraftAsync(Part(TenderType.Cash, 5_000), Part(TenderType.Upi, 7_500, upiRef: "123456"));
        var r = await _h.Sender.Send(new FinalizeBillCommand(draft));
        Assert.True(r.IsSuccess, r.IsFailure ? r.Error.Code : "");
        Assert.Equal(12_500, r.Value.GrandTotal.Value);
        Assert.Equal(0, r.Value.ChangeDue.Value);

        var saved = (await _h.Sender.Send(new GetBillQuery(r.Value.BillId)))!;
        Assert.Equal(TenderType.Split, saved.Tender);
        Assert.Equal(2, saved.Payments.Count);
        Assert.Equal(12_500, saved.Payments.Sum(p => p.Amount.Value));
        Assert.Equal("123456", saved.Payments.Single(p => p.Tender == TenderType.Upi).UpiRef);
        Assert.Equal(10_000 - 2_000, await _h.StockAsync(rice.Id));

        var day = await _h.Sender.Send(new GetDayEndReportQuery(_h.Clock.Today));
        Assert.Equal(12_500, day.SalesTotal.Value);
        Assert.Equal(5_000, day.Cash.Value);
        Assert.Equal(7_500, day.Upi.Value);
        Assert.Equal(0, day.Udhaar.Value);
        Assert.Equal(day.SalesTotal.Value, day.Cash.Value + day.Upi.Value + day.Udhaar.Value);
    }

    [Fact]
    public async Task SingleTender_StillWritesOnePart_AndReadsAsBefore()
    {
        await _h.SeedShopAsync();
        var rice = await _h.AddItemAsync("బియ్యం", 6_250);
        var r = await _h.Sender.Send(new FinalizeBillCommand(AppHost.Draft(TenderType.Cash, (rice, 2_000))));
        Assert.True(r.IsSuccess);
        var saved = (await _h.Sender.Send(new GetBillQuery(r.Value.BillId)))!;
        Assert.Equal(TenderType.Cash, saved.Tender);
        var part = Assert.Single(saved.Payments);
        Assert.Equal(TenderType.Cash, part.Tender);
        Assert.Equal(12_500, part.Amount.Value);
    }

    [Fact]
    public void OldBillWithoutParts_ReadsAsOnePartOfItsTender()
    {
        var bill = new Bill { Tender = TenderType.Upi, GrandTotal = new Paise(9_900), UpiRef = "77" };
        var part = Assert.Single(BillPayments.PartsOf(bill));
        Assert.Equal(TenderType.Upi, part.Tender);
        Assert.Equal(9_900, part.Amount.Value);
        Assert.Equal("77", part.UpiRef);
        Assert.Equal(0, BillPayments.AmountBy(bill, TenderType.Cash).Value);
        Assert.Empty(BillPayments.PartsOf(new Bill()));
    }

    [Fact]
    public async Task PartsNotAddingUp_AreRejected_NothingSaved()
    {
        var (rice, draft) = await DraftAsync(Part(TenderType.Cash, 5_000), Part(TenderType.Upi, 7_000));
        var r = await _h.Sender.Send(new FinalizeBillCommand(draft));
        Assert.Equal(ErrorCodes.BillSplitSumMismatch, r.Error.Code);
        Assert.Equal(10_000, await _h.StockAsync(rice.Id));

        draft.Payments = [Part(TenderType.Cash, 6_000), Part(TenderType.Upi, 7_500)];   // a rupee too many is wrong too
        Assert.Equal(ErrorCodes.BillSplitSumMismatch, (await _h.Sender.Send(new FinalizeBillCommand(draft))).Error.Code);
        Assert.Equal(0, (await _h.Sender.Send(new GetBillQuotaQuery())).UsedThisMonth);
    }

    [Theory]
    [InlineData("one-part")]
    [InlineData("two-cash")]
    [InlineData("two-udhaar")]
    [InlineData("zero-part")]
    [InlineData("negative-part")]
    [InlineData("split-in-split")]
    [InlineData("received-on-upi")]
    [InlineData("ref-on-cash")]
    [InlineData("five-parts")]
    public async Task InvalidSplits_AreRejected(string kind)
    {
        var customer = await CustomerAsyncAfterShop();
        var rice = await _h.AddItemAsync("బియ్యం", 6_250);
        var draft = AppHost.Draft(TenderType.Split, (rice, 2_000));
        draft.CustomerId = customer;
        draft.Payments = kind switch
        {
            "one-part" => [Part(TenderType.Cash, 12_500)],
            "two-cash" => [Part(TenderType.Cash, 5_000), Part(TenderType.Cash, 7_500)],
            "two-udhaar" => [Part(TenderType.Udhaar, 5_000), Part(TenderType.Udhaar, 7_500)],
            "zero-part" => [Part(TenderType.Cash, 0), Part(TenderType.Upi, 12_500)],
            "negative-part" => [Part(TenderType.Cash, -500), Part(TenderType.Upi, 13_000)],
            "split-in-split" => [Part(TenderType.Split, 5_000), Part(TenderType.Upi, 7_500)],
            "received-on-upi" => [Part(TenderType.Cash, 5_000), Part(TenderType.Upi, 7_500, received: 10_000)],
            "ref-on-cash" => [Part(TenderType.Cash, 5_000, upiRef: "9"), Part(TenderType.Upi, 7_500)],
            _ => [Part(TenderType.Cash, 2_500), Part(TenderType.Upi, 2_500), Part(TenderType.Udhaar, 2_500), Part(TenderType.Upi, 2_500), Part(TenderType.Upi, 2_500)],
        };
        var r = await _h.Sender.Send(new FinalizeBillCommand(draft));
        Assert.True(r.IsFailure);
        Assert.Contains(r.Error.Code, new[] { ErrorCodes.BillSplitInvalid, ErrorCodes.ValidationFailed });
        Assert.Equal(10_000, await _h.StockAsync(rice.Id));
    }

    private async Task<Guid> CustomerAsyncAfterShop()
    {
        await _h.SeedShopAsync();
        return await CustomerAsync();
    }

    [Fact]
    public async Task UdhaarPart_NeedsCustomer_AndOnlyThatPartGoesToDues()
    {
        var (_, draft) = await DraftAsync(Part(TenderType.Cash, 2_500), Part(TenderType.Udhaar, 10_000));
        Assert.Equal(ErrorCodes.BillUdhaarNeedsCustomer, (await _h.Sender.Send(new FinalizeBillCommand(draft))).Error.Code);

        var ravi = await CustomerAsync();
        draft.CustomerId = ravi;
        var r = await _h.Sender.Send(new FinalizeBillCommand(draft));
        Assert.True(r.IsSuccess, r.IsFailure ? r.Error.Code : "");
        var customer = (await _h.Sender.Send(new GetCustomerQuery(ravi)))!;
        Assert.Equal(10_000, customer.DuesBalance.Value);
        var charge = Assert.Single(await _h.Sender.Send(new GetCustomerLedgerQuery(ravi)));
        Assert.Equal(DuesEntryKind.Charge, charge.Kind);
        Assert.Equal(10_000, charge.Amount.Value);

        var day = await _h.Sender.Send(new GetDayEndReportQuery(_h.Clock.Today));
        Assert.Equal(2_500, day.Cash.Value);
        Assert.Equal(10_000, day.Udhaar.Value);
    }

    [Fact]
    public async Task ChangeIsOnlyOnTheCashPart()
    {
        var (_, draft) = await DraftAsync(Part(TenderType.Cash, 5_000, received: 10_000), Part(TenderType.Upi, 7_500));
        var r = await _h.Sender.Send(new FinalizeBillCommand(draft));
        Assert.True(r.IsSuccess);
        Assert.Equal(5_000, r.Value.ChangeDue.Value);
        var saved = (await _h.Sender.Send(new GetBillQuery(r.Value.BillId)))!;
        Assert.Equal(5_000, BillCalculator.ChangeDue(saved).Value);
    }

    [Fact]
    public async Task CashPartShort_IsRejected_ExactCashWhenNothingTyped()
    {
        var (_, draft) = await DraftAsync(Part(TenderType.Cash, 5_000, received: 2_000), Part(TenderType.Upi, 7_500));
        Assert.Equal(ErrorCodes.BillCashShort, (await _h.Sender.Send(new FinalizeBillCommand(draft))).Error.Code);
        Assert.Equal(2_000, draft.Payments[0].CashReceived.Value);   // what was typed survives into the retry

        draft.Payments[0].CashReceived = Paise.Zero;
        var r = await _h.Sender.Send(new FinalizeBillCommand(draft));
        Assert.True(r.IsSuccess);
        Assert.Equal(0, r.Value.ChangeDue.Value);
        var saved = (await _h.Sender.Send(new GetBillQuery(r.Value.BillId)))!;
        Assert.Equal(5_000, saved.Payments.Single(p => p.Tender == TenderType.Cash).CashReceived.Value);
    }

    [Fact]
    public async Task RetryOfSavedSplitBill_IsTheSameBill_StockAndQuotaOnce()
    {
        var (rice, draft) = await DraftAsync(Part(TenderType.Cash, 5_000), Part(TenderType.Upi, 7_500));
        var first = await _h.Sender.Send(new FinalizeBillCommand(draft));
        var again = await _h.Sender.Send(new FinalizeBillCommand(draft));
        Assert.True(first.IsSuccess);
        Assert.True(again.IsSuccess, again.IsFailure ? again.Error.Code : "");
        Assert.Equal(first.Value.BillId, again.Value.BillId);
        Assert.Equal(first.Value.Number, again.Value.Number);
        Assert.Equal(10_000 - 2_000, await _h.StockAsync(rice.Id));
        Assert.Equal(1, (await _h.Sender.Send(new GetBillQuotaQuery())).UsedThisMonth);
        var day = await _h.Sender.Send(new GetDayEndReportQuery(_h.Clock.Today));
        Assert.Equal(1, day.BillCount);
        Assert.Equal(5_000, day.Cash.Value);
    }

    [Fact]
    public async Task RetryThatMovedMoneyBetweenParts_IsRefused()
    {
        var (_, draft) = await DraftAsync(Part(TenderType.Cash, 5_000), Part(TenderType.Upi, 7_500));
        Assert.True((await _h.Sender.Send(new FinalizeBillCommand(draft))).IsSuccess);
        draft.Payments = [Part(TenderType.Cash, 7_500), Part(TenderType.Upi, 5_000)];
        Assert.Equal(ErrorCodes.BillChangedAfterSave, (await _h.Sender.Send(new FinalizeBillCommand(draft))).Error.Code);
    }

    [Fact]
    public async Task VoidOfSplitBill_ReversesOnlyTheUdhaarPart()
    {
        var (rice, draft) = await DraftAsync(Part(TenderType.Upi, 2_500), Part(TenderType.Udhaar, 10_000));
        var ravi = await CustomerAsync();
        draft.CustomerId = ravi;
        var r = await _h.Sender.Send(new FinalizeBillCommand(draft));
        Assert.True(r.IsSuccess);
        Assert.True((await _h.Sender.Send(new VoidBillCommand(r.Value.BillId, "wrong"))).IsSuccess);
        Assert.Equal(0, (await _h.Sender.Send(new GetCustomerQuery(ravi)))!.DuesBalance.Value);
        Assert.Equal(10_000, await _h.StockAsync(rice.Id));
    }

    [Fact]
    public void Draft_SetSplit_ThenSingleTender_DropsTheParts()
    {
        var draft = new BillDraft(shopHasGstin: false);
        draft.SetSplit([Part(TenderType.Cash, 100), Part(TenderType.Upi, 200, upiRef: " 55 ")]);
        Assert.Equal(TenderType.Split, draft.Bill.Tender);
        Assert.Equal("55", draft.Bill.Payments[1].UpiRef);
        draft.SetTender(TenderType.Cash);
        Assert.Empty(draft.Bill.Payments);
    }
}
