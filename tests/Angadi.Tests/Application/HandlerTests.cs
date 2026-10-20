using Angadi.Application.Billing;
using Angadi.Application.Catalog;
using Angadi.Application.Common;
using Angadi.Application.Platform;
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
