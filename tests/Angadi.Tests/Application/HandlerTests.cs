using Angadi.Application.Billing;
using Angadi.Application.Catalog;
using Angadi.Application.Common;
using Angadi.Application.Persistence;
using Angadi.Application.Platform;
using Angadi.Application.Reports;
using Angadi.Domain.Billing;
using Angadi.Domain.Common;
using Angadi.Domain.Customers;
using Angadi.Domain.Items;
using Angadi.Infrastructure.Billing;
using Microsoft.Extensions.DependencyInjection;

namespace Angadi.Tests.Application;

/// <summary>H10 / C1 / C2: the handlers that move money, through the real pipeline and real SQLite.</summary>
public class HandlerTests : IDisposable
{
    private readonly AppHost _h = new();
    public void Dispose() => _h.Dispose();

    // ---------- C1-correction: registration errors are caught in CI, not on a shopkeeper's tablet ----------

    [Fact]
    public void C1_EveryRequestHandler_ResolvesFromTheRealGraph()
    {
        using var host = new AppHost(fakePlatform: false);
        var requestTypes = typeof(Result).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .SelectMany(t => t.GetInterfaces()
                .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IRequest<>))
                .Select(i => typeof(IRequestHandler<,>).MakeGenericType(t, i.GetGenericArguments()[0])))
            .ToList();

        Assert.NotEmpty(requestTypes);
        foreach (var handlerType in requestTypes)
            Assert.NotNull(host.Services.GetRequiredService(handlerType));

        Assert.IsType<StubSubscriptionService>(host.Services.GetRequiredService<ISubscriptionService>());
        Assert.NotNull(host.Services.GetRequiredService<IReceiptRenderer>());
    }

    [Fact]
    public async Task C2_StubSubscription_NeverGrantsPro_AndPurchaseFailsClosed()
    {
        var stub = new StubSubscriptionService();
        Assert.False((await stub.GetStatusAsync()).IsPro);
        Assert.Equal(ErrorCodes.PurchaseFailed, (await stub.PurchaseAsync(SubscriptionPlan.Monthly)).Error.Code);
    }

    // ---------- FinalizeBill ----------

    [Fact]
    public async Task FR14_Cash_ExactWhenNothingTyped_DecrementsStock_NumbersSequentially()
    {
        await _h.SeedShopAsync();
        var rice = await _h.AddItemAsync("బియ్యం", 6000);

        var first = await _h.Sender.Send(new FinalizeBillCommand(AppHost.Draft(TenderType.Cash, (rice, 2000))));
        var second = await _h.Sender.Send(new FinalizeBillCommand(AppHost.Draft(TenderType.Cash, (rice, 1000))));

        Assert.True(first.IsSuccess, first.Error.Code);
        Assert.Equal("A-000001", first.Value.Number);
        Assert.Equal("A-000002", second.Value.Number);
        Assert.Equal(12000, first.Value.GrandTotal.Value);
        Assert.Equal(0, first.Value.ChangeDue.Value);
        Assert.Equal(10_000 - 3000, await _h.StockAsync(rice.Id));
    }

    [Fact]
    public async Task FR14_CashShort_IsRejected_AndNothingIsSaved()
    {
        await _h.SeedShopAsync();
        var rice = await _h.AddItemAsync("బియ్యం", 6000);
        var draft = AppHost.Draft(TenderType.Cash, (rice, 2000));
        draft.CashReceived = Paise.FromRupees(100);

        var r = await _h.Sender.Send(new FinalizeBillCommand(draft));

        Assert.Equal(ErrorCodes.BillCashShort, r.Error.Code);
        Assert.Equal(10_000, await _h.StockAsync(rice.Id));
        Assert.Equal(BillStatus.Draft, draft.Status);
    }

    [Fact]
    public async Task FR14_CashChange_IsReturned()
    {
        await _h.SeedShopAsync();
        var rice = await _h.AddItemAsync("బియ్యం", 6000);
        var draft = AppHost.Draft(TenderType.Cash, (rice, 2000));
        draft.CashReceived = Paise.FromRupees(200);

        var r = await _h.Sender.Send(new FinalizeBillCommand(draft));
        Assert.Equal(8000, r.Value.ChangeDue.Value);
    }

    [Fact]
    public async Task FR14_Udhaar_WithoutCustomer_IsRejectedByValidator()
    {
        await _h.SeedShopAsync();
        var rice = await _h.AddItemAsync("బియ్యం", 6000);
        var r = await _h.Sender.Send(new FinalizeBillCommand(AppHost.Draft(TenderType.Udhaar, (rice, 1000))));
        Assert.Equal(ErrorCodes.BillUdhaarNeedsCustomer, r.Error.Code);
    }

    [Fact]
    public async Task FR22_Udhaar_ChargesDues_AndShowsNoChange()
    {
        await _h.SeedShopAsync();
        var rice = await _h.AddItemAsync("బియ్యం", 6000);
        var customer = new Customer { Name = "లక్ష్మి", Phone = "9876543210" };
        await _h.Sender.Send(new SaveCustomerCommand(customer));

        var draft = AppHost.Draft(TenderType.Udhaar, (rice, 2000));
        draft.CustomerId = customer.Id;
        var r = await _h.Sender.Send(new FinalizeBillCommand(draft));

        Assert.True(r.IsSuccess, r.Error.Code);
        Assert.Equal(0, r.Value.ChangeDue.Value);   // B1
        Assert.Equal(12000, (await _h.Sender.Send(new GetCustomerQuery(customer.Id)))!.DuesBalance.Value);
    }

    [Fact]
    public async Task FR34_FreeTier_Blocks51stBill_ProIsUnlimited()
    {
        await _h.SeedShopAsync();
        var item = await _h.AddItemAsync("అగ్గిపెట్టె", 200, GstSlab.Five, stockMilli: 1_000_000, unit: Unit.Piece);
        for (var i = 0; i < 50; i++)
            Assert.True((await _h.Sender.Send(new FinalizeBillCommand(AppHost.Draft(TenderType.Cash, (item, 1000))))).IsSuccess);

        var blocked = await _h.Sender.Send(new FinalizeBillCommand(AppHost.Draft(TenderType.Cash, (item, 1000))));
        Assert.Equal(ErrorCodes.FreeLimitReached, blocked.Error.Code);

        _h.Subscription.IsPro = true;
        Assert.True((await _h.Sender.Send(new FinalizeBillCommand(AppHost.Draft(TenderType.Cash, (item, 1000))))).IsSuccess);
    }

    [Fact]
    public async Task FR18_TaxInvoice_FollowsShopGstin()
    {
        await _h.SeedShopAsync(gstin: "36ABCDE1234F1Z5");
        var rice = await _h.AddItemAsync("బియ్యం", 6000);
        var r = await _h.Sender.Send(new FinalizeBillCommand(AppHost.Draft(TenderType.Upi, (rice, 2000))));
        var bill = await _h.Sender.Send(new GetBillQuery(r.Value.BillId));
        Assert.True(bill!.IsTaxInvoice);
        Assert.Single(bill.TaxSplits);
    }

    [Fact]
    public async Task H8_LegacySlab_FailsClosedAtFinalize()
    {
        await _h.SeedShopAsync();
        var old = await _h.AddItemAsync("పాత సరుకు", 11200, GstSlab.Twelve);
        var r = await _h.Sender.Send(new FinalizeBillCommand(AppHost.Draft(TenderType.Cash, (old, 1000))));
        Assert.Equal(ErrorCodes.BillLegacySlab, r.Error.Code);
        Assert.Equal(10_000, await _h.StockAsync(old.Id));
    }

    [Fact]
    public async Task T1_04_Finalize_UndefinedGstSlab_FailsClosed()
    {
        await _h.SeedShopAsync();
        var rice = await _h.AddItemAsync("బియ్యం", 6000);
        var draft = AppHost.Draft(TenderType.Cash, (rice, 1000));
        draft.Lines[0].GstSlab = (GstSlab)9999;   // no item save could produce this; a restored/tampered draft can

        var r = await _h.Sender.Send(new FinalizeBillCommand(draft));

        Assert.True(r.IsFailure);
        Assert.Equal(ErrorCodes.ValidationFailed, r.Error.Code);
        Assert.Equal(10_000, await _h.StockAsync(rice.Id));
        Assert.Null(await _h.Sender.Send(new GetBillQuery(draft.Id)));
    }

    [Fact]
    public async Task M9_DiscountOver100Percent_IsRejected()
    {
        await _h.SeedShopAsync();
        var rice = await _h.AddItemAsync("బియ్యం", 6000);
        var draft = AppHost.Draft(TenderType.Cash, (rice, 1000));
        draft.BillDiscount = Discount.Percent(15_000);
        Assert.Equal(ErrorCodes.BillDiscountTooLarge, (await _h.Sender.Send(new FinalizeBillCommand(draft))).Error.Code);
    }

    [Fact]
    public async Task MONEY3_NegativeCashReceived_IsRejected()
    {
        await _h.SeedShopAsync();
        var rice = await _h.AddItemAsync("బియ్యం", 6000);
        var draft = AppHost.Draft(TenderType.Cash, (rice, 2000));   // ₹120 bill
        draft.CashReceived = new Paise(-50_000);                    // −₹500: was reported as "short ₹620"

        var r = await _h.Sender.Send(new FinalizeBillCommand(draft));

        Assert.Equal(ErrorCodes.ValidationFailed, r.Error.Code);
        Assert.Equal(10_000, await _h.StockAsync(rice.Id));
    }

    // ---------- TESTS-1 (T1 money suite, production audit 2026-10-20) ----------

    [Fact]
    public async Task T1_01_FinalizeOverflow_FailsAmountTooLarge_AndSavesNothing()
    {
        await _h.SeedShopAsync();
        var rice = await _h.AddItemAsync("బియ్యం", 6000);
        var draft = AppHost.Draft(TenderType.Cash, (rice, 1000));
        draft.Lines[0].UnitPrice = new Paise(long.MaxValue / 2);   // checked maths must overflow, never wrap

        var r = await _h.Sender.Send(new FinalizeBillCommand(draft));

        Assert.Equal(ErrorCodes.BillAmountTooLarge, r.Error.Code);
        Assert.Equal(10_000, await _h.StockAsync(rice.Id));
        Assert.Equal(0, (await _h.Sender.Send(new GetBillQuotaQuery())).UsedThisMonth);
        Assert.Null(await _h.Sender.Send(new GetBillQuery(draft.Id)));
    }

    [Fact]
    public async Task T1_02_LineDiscountOver100Percent_IsRejectedAtFinalize()
    {
        await _h.SeedShopAsync();
        var rice = await _h.AddItemAsync("బియ్యం", 6000);
        var draft = AppHost.Draft(TenderType.Cash, (rice, 1000));
        draft.Lines[0].LineDiscount = Discount.Percent(15_000);   // 150% on one line

        var r = await _h.Sender.Send(new FinalizeBillCommand(draft));

        Assert.Equal(ErrorCodes.BillDiscountTooLarge, r.Error.Code);
        Assert.Equal(10_000, await _h.StockAsync(rice.Id));
    }

    [Fact]
    public async Task T1_03_Legacy28Slab_FailsClosedAtFinalize()
    {
        await _h.SeedShopAsync();
        var old = await _h.AddItemAsync("పాత సరుకు", 12800, GstSlab.TwentyEight);
        var r = await _h.Sender.Send(new FinalizeBillCommand(AppHost.Draft(TenderType.Cash, (old, 1000))));
        Assert.Equal(ErrorCodes.BillLegacySlab, r.Error.Code);
        Assert.Equal(10_000, await _h.StockAsync(old.Id));
    }

    private static async Task T1_05_Check(Func<AppHost, Task<Result<FinalizedBill>>> failingAttempt, string expectedCode)
    {
        using var h = new AppHost();
        await h.SeedShopAsync();

        var bad = await failingAttempt(h);
        Assert.Equal(expectedCode, bad.Error.Code);
        Assert.Equal(0, (await h.Sender.Send(new GetBillQuotaQuery())).UsedThisMonth);   // no quota consumed

        var rice = await h.AddItemAsync("బియ్యం", 6000);
        var good = await h.Sender.Send(new FinalizeBillCommand(AppHost.Draft(TenderType.Cash, (rice, 1000))));
        Assert.True(good.IsSuccess, good.Error.Code);
        Assert.Equal("A-000001", good.Value.Number);   // and no bill number burned
    }

    [Fact]
    public Task T1_05_CashShort_BurnsNoQuotaAndNoNumber() => T1_05_Check(async h =>
    {
        var rice = await h.AddItemAsync("బియ్యం", 6000);
        var draft = AppHost.Draft(TenderType.Cash, (rice, 2000));
        draft.CashReceived = Paise.FromRupees(100);
        return await h.Sender.Send(new FinalizeBillCommand(draft));
    }, ErrorCodes.BillCashShort);

    [Fact]
    public Task T1_05_LegacySlab_BurnsNoQuotaAndNoNumber() => T1_05_Check(async h =>
    {
        var old = await h.AddItemAsync("పాత సరుకు", 11200, GstSlab.Twelve);
        return await h.Sender.Send(new FinalizeBillCommand(AppHost.Draft(TenderType.Cash, (old, 1000))));
    }, ErrorCodes.BillLegacySlab);

    [Fact]
    public Task T1_05_BillDiscountOver100_BurnsNoQuotaAndNoNumber() => T1_05_Check(async h =>
    {
        var rice = await h.AddItemAsync("బియ్యం", 6000);
        var draft = AppHost.Draft(TenderType.Cash, (rice, 1000));
        draft.BillDiscount = Discount.Percent(15_000);
        return await h.Sender.Send(new FinalizeBillCommand(draft));
    }, ErrorCodes.BillDiscountTooLarge);

    [Fact]
    public async Task T1_06_QuotaBoundary_TwoConcurrentFinalizes_ExactlyOneWins()
    {
        await _h.SeedShopAsync();
        var item = await _h.AddItemAsync("అగ్గిపెట్టె", 200, GstSlab.Five, stockMilli: 1_000_000, unit: Unit.Piece);
        for (var i = 0; i < 49; i++)
            Assert.True((await _h.Sender.Send(new FinalizeBillCommand(AppHost.Draft(TenderType.Cash, (item, 1000))))).IsSuccess);

        var results = await Task.WhenAll(
            _h.Sender.Send(new FinalizeBillCommand(AppHost.Draft(TenderType.Cash, (item, 1000)))),
            _h.Sender.Send(new FinalizeBillCommand(AppHost.Draft(TenderType.Cash, (item, 1000)))));

        Assert.Single(results, r => r.IsSuccess);
        Assert.Single(results, r => r.Error.Code == ErrorCodes.FreeLimitReached);
        Assert.Equal(50, (await _h.Sender.Send(new GetBillQuotaQuery())).UsedThisMonth);
    }

    [Fact]
    public async Task T1_08_ExactMaxLineQty_IsAccepted()
    {
        await _h.SeedShopAsync();
        _h.Subscription.IsPro = true;
        var item = await _h.AddItemAsync("చక్కెర", 200, GstSlab.Five, stockMilli: 200_000_000, unit: Unit.Piece);

        var r = await _h.Sender.Send(new FinalizeBillCommand(AppHost.Draft(TenderType.Cash, (item, 100_000_000))));   // MaxLineQtyMilli

        Assert.True(r.IsSuccess, r.Error.Code);
        Assert.Equal(20_000_000, r.Value.GrandTotal.Value);   // 100,000 pieces × ₹2
        Assert.Equal(100_000_000, await _h.StockAsync(item.Id));
    }

    [Fact]
    public async Task T1_09_NegativeDuesPayment_IsRejected_DuesUnchanged()
    {
        await _h.SeedShopAsync();
        var rice = await _h.AddItemAsync("బియ్యం", 10000);
        var customer = new Customer { Name = "రవి", Phone = "9876543210" };
        await _h.Sender.Send(new SaveCustomerCommand(customer));
        var draft = AppHost.Draft(TenderType.Udhaar, (rice, 1000));
        draft.CustomerId = customer.Id;
        Assert.True((await _h.Sender.Send(new FinalizeBillCommand(draft))).IsSuccess);

        var r = await _h.Sender.Send(new RecordDuesPaymentCommand(customer.Id, new Paise(-5000), null));

        Assert.Equal(ErrorCodes.AmountInvalid, r.Error.Code);
        Assert.Equal(10000, (await _h.Sender.Send(new GetCustomerQuery(customer.Id)))!.DuesBalance.Value);
    }

    [Fact]
    public async Task T1_10_Reports_GstSummaryAndDayEnd_ExcludeVoids()
    {
        await _h.SeedShopAsync(gstin: "36ABCDE1234F1Z5");
        var five = await _h.AddItemAsync("ఐదు", 19000, GstSlab.Five, stockMilli: 1_000_000, unit: Unit.Piece);
        var eighteen = await _h.AddItemAsync("పద్దెనిమిది", 11800, GstSlab.Eighteen, stockMilli: 1_000_000, unit: Unit.Piece);
        var voidItem = await _h.AddItemAsync("రద్దు", 10000, GstSlab.Five, stockMilli: 1_000_000, unit: Unit.Piece);

        Assert.True((await _h.Sender.Send(new FinalizeBillCommand(AppHost.Draft(TenderType.Cash, (five, 1000))))).IsSuccess);       // ₹190 @5%
        Assert.True((await _h.Sender.Send(new FinalizeBillCommand(AppHost.Draft(TenderType.Cash, (eighteen, 1000))))).IsSuccess);   // ₹118 @18%
        var c = await _h.Sender.Send(new FinalizeBillCommand(AppHost.Draft(TenderType.Cash, (voidItem, 1000))));                     // ₹100 @5%
        Assert.True((await _h.Sender.Send(new VoidBillCommand(c.Value.BillId, "తప్పు బిల్లు"))).IsSuccess);

        var day = _h.Clock.Today;
        var splits = await _h.Sender.Send(new GetGstSummaryQuery(day, day));
        Assert.Equal(2, splits.Count);
        Assert.Equal((GstSlab.Five, 18095L, 905L), (splits[0].Slab, splits[0].Taxable.Value, splits[0].Tax.Value));
        Assert.Equal((GstSlab.Eighteen, 10000L, 1800L), (splits[1].Slab, splits[1].Taxable.Value, splits[1].Tax.Value));

        var dayEnd = await _h.Sender.Send(new GetDayEndReportQuery(day));
        Assert.Equal(2705, dayEnd.Tax.Value);
        Assert.Equal(2, dayEnd.BillCount);
        Assert.Equal(1, dayEnd.VoidCount);
    }

    [Fact]
    public async Task B1_CashReceivedOnUpi_IsRejected()
    {
        await _h.SeedShopAsync();
        var rice = await _h.AddItemAsync("బియ్యం", 6000);
        var draft = AppHost.Draft(TenderType.Upi, (rice, 1000));
        draft.CashReceived = Paise.FromRupees(100);
        Assert.Equal(ErrorCodes.ValidationFailed, (await _h.Sender.Send(new FinalizeBillCommand(draft))).Error.Code);
    }

    // ---------- VoidBill ----------

    [Fact]
    public async Task FR16_Void_RestoresStock_AndSecondVoidIsRejected()
    {
        await _h.SeedShopAsync();
        var rice = await _h.AddItemAsync("బియ్యం", 6000);
        var r = await _h.Sender.Send(new FinalizeBillCommand(AppHost.Draft(TenderType.Cash, (rice, 2000))));

        Assert.True((await _h.Sender.Send(new VoidBillCommand(r.Value.BillId, "తప్పు బిల్లు"))).IsSuccess);
        Assert.Equal(10_000, await _h.StockAsync(rice.Id));
        Assert.Equal(ErrorCodes.BillAlreadyVoid, (await _h.Sender.Send(new VoidBillCommand(r.Value.BillId, "again"))).Error.Code);
    }

    [Fact]
    public async Task FR16_Void_RequiresReason()
    {
        Assert.Equal(ErrorCodes.VoidReasonRequired, (await _h.Sender.Send(new VoidBillCommand(Guid.NewGuid(), "  "))).Error.Code);
    }

    [Fact]
    public async Task H1_VoidOfPartlyPaidUdhaarBill_FailsClosed_FullyUnpaidVoidReverses()
    {
        await _h.SeedShopAsync();
        var rice = await _h.AddItemAsync("బియ్యం", 10000);
        var customer = new Customer { Name = "రవి", Phone = "9876543210" };
        await _h.Sender.Send(new SaveCustomerCommand(customer));

        var d1 = AppHost.Draft(TenderType.Udhaar, (rice, 2000)); d1.CustomerId = customer.Id;   // ₹200
        var bill = await _h.Sender.Send(new FinalizeBillCommand(d1));
        Assert.True((await _h.Sender.Send(new RecordDuesPaymentCommand(customer.Id, Paise.FromRupees(50), null))).IsSuccess);

        var blocked = await _h.Sender.Send(new VoidBillCommand(bill.Value.BillId, "తప్పు"));
        Assert.Equal(ErrorCodes.BillVoidBlockedByPayments, blocked.Error.Code);
        Assert.Equal(15000, (await _h.Sender.Send(new GetCustomerQuery(customer.Id)))!.DuesBalance.Value);   // untouched
        Assert.Equal(8000, await _h.StockAsync(rice.Id));                                                 // untouched

        var d2 = AppHost.Draft(TenderType.Udhaar, (rice, 1000)); d2.CustomerId = customer.Id;   // ₹100, unpaid
        var second = await _h.Sender.Send(new FinalizeBillCommand(d2));
        Assert.True((await _h.Sender.Send(new VoidBillCommand(second.Value.BillId, "తప్పు"))).IsSuccess);
        Assert.Equal(15000, (await _h.Sender.Send(new GetCustomerQuery(customer.Id)))!.DuesBalance.Value);
    }

    // ---------- Dues ----------

    [Fact]
    public async Task FR22_Overpayment_IsRejected_NonPositiveRejected()
    {
        await _h.SeedShopAsync();
        var customer = new Customer { Name = "రవి" };
        await _h.Sender.Send(new SaveCustomerCommand(customer));
        var over = await _h.Sender.Send(new RecordDuesPaymentCommand(customer.Id, Paise.FromRupees(1), null));
        Assert.Equal(ErrorCodes.DuesExceedsBalance, over.Error.Code);
        Assert.NotNull(over.Error.Args);   // the balance is shown to the shopkeeper
        Assert.Equal(ErrorCodes.AmountInvalid, (await _h.Sender.Send(new RecordDuesPaymentCommand(customer.Id, Paise.Zero, null))).Error.Code);
    }

    [Fact]
    public async Task L7_FreeBill_WithoutConfirmation_IsRejected()
    {
        await _h.SeedShopAsync();
        var free = await _h.AddItemAsync("ఉచితం", 0, GstSlab.Zero, stockMilli: 10_000, unit: Unit.Piece);
        var r = await _h.Sender.Send(new FinalizeBillCommand(AppHost.Draft(TenderType.Cash, (free, 1000))));
        Assert.Equal(ErrorCodes.ValidationFailed, r.Error.Code);
        Assert.Equal(10_000, await _h.StockAsync(free.Id));
    }

    [Fact]
    public async Task L7_FreeBill_WithConfirmation_Finalizes()
    {
        await _h.SeedShopAsync();
        var free = await _h.AddItemAsync("ఉచితం", 0, GstSlab.Zero, stockMilli: 10_000, unit: Unit.Piece);
        var command = new FinalizeBillCommand(AppHost.Draft(TenderType.Cash, (free, 1000))) { FreeBillConfirmed = true };
        var r = await _h.Sender.Send(command);
        Assert.True(r.IsSuccess, r.Error.Code);
        Assert.Equal(0, r.Value.GrandTotal.Value);
        Assert.Equal("A-000001", r.Value.Number);
    }

    [Fact]
    public async Task L7_HundredPercentDiscount_WithoutConfirmation_IsRejected()
    {
        await _h.SeedShopAsync();
        var rice = await _h.AddItemAsync("బియ్యం", 6000);
        var draft = AppHost.Draft(TenderType.Cash, (rice, 1000));
        draft.BillDiscount = Discount.Percent(10_000);   // exactly 100% — a legal discount that zeroes the bill
        var r = await _h.Sender.Send(new FinalizeBillCommand(draft));
        Assert.Equal(ErrorCodes.ValidationFailed, r.Error.Code);
    }

    [Fact]
    public async Task L7_EmptyDraft_StillBillEmpty_NotFreeBillError()
    {
        await _h.SeedShopAsync();
        var r = await _h.Sender.Send(new FinalizeBillCommand(AppHost.Draft(TenderType.Cash)));
        Assert.Equal(ErrorCodes.BillEmpty, r.Error.Code);
    }

    [Fact]
    public async Task SEND1_RawStoredPhone_IsNormalizedBeforeSending()
    {
        await _h.SeedShopAsync();
        var rice = await _h.AddItemAsync("బియ్యం", 6000);
        // Written straight through the repository (which stores raw), as an old backup restore would —
        // the save-command path normalizes, restored rows never passed through it.
        var customers = _h.Services.GetRequiredService<ICustomerRepository>();
        var customer = new Customer { Name = "లక్ష్మి", Phone = "098765 43210" };
        await customers.SaveAsync(customer);

        var draft = AppHost.Draft(TenderType.Udhaar, (rice, 1000));
        draft.CustomerId = customer.Id;
        var bill = await _h.Sender.Send(new FinalizeBillCommand(draft));
        Assert.True(bill.IsSuccess, bill.Error.Code);

        Assert.True((await _h.Sender.Send(new SendBillToCustomerCommand(bill.Value.BillId))).IsSuccess);
        Assert.True((await _h.Sender.Send(new SendDuesReminderCommand(customer.Id))).IsSuccess);
        Assert.Equal(2, _h.Share.WhatsApp.Count);
        Assert.All(_h.Share.WhatsApp, w => Assert.Equal("919876543210", w.Phone));
    }

    [Fact]
    public async Task FR23_Reminder_NeedsPhoneAndDues()
    {
        await _h.SeedShopAsync();
        var noPhone = new Customer { Name = "రవి" };
        await _h.Sender.Send(new SaveCustomerCommand(noPhone));
        Assert.Equal(ErrorCodes.CustomerNoPhone, (await _h.Sender.Send(new SendDuesReminderCommand(noPhone.Id))).Error.Code);
        var noDues = new Customer { Name = "సీత", Phone = "9876543210" };
        await _h.Sender.Send(new SaveCustomerCommand(noDues));
        Assert.Equal(ErrorCodes.CustomerNoDues, (await _h.Sender.Send(new SendDuesReminderCommand(noDues.Id))).Error.Code);
        Assert.Empty(_h.Share.WhatsApp);
    }

    // ---------- Items ----------

    [Fact]
    public async Task FR08_DuplicateBarcode_IsRejected()
    {
        var a = new Item { TeluguName = "ఎ", Price = new Paise(100), Barcode = "8901" };
        var b = new Item { TeluguName = "బి", Price = new Paise(100), Barcode = "8901" };
        Assert.True((await _h.Sender.Send(new SaveItemCommand(a))).IsSuccess);
        Assert.Equal(ErrorCodes.BarcodeDuplicate, (await _h.Sender.Send(new SaveItemCommand(b))).Error.Code);
    }

    [Fact]
    public async Task FR03_ItemWithoutName_OrNegativePrice_IsRejected()
    {
        Assert.Equal(ErrorCodes.ItemNameRequired, (await _h.Sender.Send(new SaveItemCommand(new Item { Price = new Paise(100) }))).Error.Code);
        Assert.Equal(ErrorCodes.ItemPriceInvalid, (await _h.Sender.Send(new SaveItemCommand(new Item { TeluguName = "x", Price = new Paise(-1) }))).Error.Code);
        Assert.Equal(ErrorCodes.ItemPriceInvalid, (await _h.Sender.Send(new SaveItemCommand(new Item { TeluguName = "x", Price = Paise.FromRupees(10_000_001) }))).Error.Code);
    }

    [Fact]
    public async Task FR01_ShopProfile_RejectsBadUpiAndGstin()
    {
        var badUpi = await _h.Sender.Send(new SaveShopProfileCommand(new Angadi.Domain.Shop.ShopProfile { Name = "x", UpiId = "not-a-vpa" }));
        var badGstin = await _h.Sender.Send(new SaveShopProfileCommand(new Angadi.Domain.Shop.ShopProfile { Name = "x", UpiId = "ramu@okaxis", Gstin = "123" }));
        var noName = await _h.Sender.Send(new SaveShopProfileCommand(new Angadi.Domain.Shop.ShopProfile { Name = " ", UpiId = "ramu@okaxis" }));
        Assert.Equal(ErrorCodes.UpiIdInvalid, badUpi.Error.Code);
        Assert.Equal(ErrorCodes.GstinInvalid, badGstin.Error.Code);
        Assert.Equal(ErrorCodes.ShopNameRequired, noName.Error.Code);
    }
}
