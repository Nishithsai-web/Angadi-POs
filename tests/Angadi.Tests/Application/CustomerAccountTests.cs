using Angadi.Application.Billing;
using Angadi.Application.Catalog;
using Angadi.Application.Common;
using Angadi.Application.Customers;
using Angadi.Domain.Billing;
using Angadi.Domain.Common;
using Angadi.Domain.Customers;
using Angadi.Domain.Items;

namespace Angadi.Tests.Application;

/// <summary>Customer accounts: details, every bill, the customer's own report, udhaar aging, promises and reminders.</summary>
public class CustomerAccountTests : IDisposable
{
    private readonly AppHost _h = new();
    public void Dispose() => _h.Dispose();

    private async Task<(Customer ravi, Item rice, Item milk)> SetupAsync()
    {
        await _h.SeedShopAsync();
        var ravi = new Customer { Name = "రవి", Phone = "98765 43210", Address = "గాంధీ నగర్", Notes = "ప్రతి నెల 5న జీతం" };
        Assert.True((await _h.Sender.Send(new SaveCustomerCommand(ravi))).IsSuccess);
        var rice = await _h.AddItemAsync("బియ్యం", 6000, stockMilli: 100_000);
        var milk = await _h.AddItemAsync("పాలు", 3000, stockMilli: 100_000, unit: Unit.Pack);
        return (ravi, rice, milk);
    }

    private async Task<Guid> SellAsync(TenderType tender, Guid? customerId, params (Item item, long qtyMilli)[] lines)
    {
        var draft = AppHost.Draft(tender, lines);
        draft.CustomerId = customerId;
        var r = await _h.Sender.Send(new FinalizeBillCommand(draft));
        Assert.True(r.IsSuccess, r.Error.Code);
        return r.Value.BillId;
    }

    [Fact]
    public async Task Account_KeepsDetails_AndEveryBill_AnyTender()
    {
        var (ravi, rice, milk) = await SetupAsync();
        await SellAsync(TenderType.Cash, ravi.Id, (rice, 2000));
        _h.Clock.Advance(TimeSpan.FromHours(1));
        await SellAsync(TenderType.Upi, ravi.Id, (milk, 2000));
        _h.Clock.Advance(TimeSpan.FromHours(1));
        await SellAsync(TenderType.Udhaar, ravi.Id, (rice, 1000), (milk, 1000));
        await SellAsync(TenderType.Cash, null, (rice, 1000));   // walk-in: not in Ravi's account

        var saved = (await _h.Sender.Send(new GetCustomerQuery(ravi.Id)))!;
        Assert.Equal("గాంధీ నగర్", saved.Address);
        Assert.Equal("ప్రతి నెల 5న జీతం", saved.Notes);

        var bills = await _h.Sender.Send(new GetCustomerBillsQuery(ravi.Id));
        Assert.Equal([TenderType.Udhaar, TenderType.Upi, TenderType.Cash], bills.Select(b => b.Tender));   // newest first
    }

    [Fact]
    public async Task Report_TotalsSplitTopItemsAndDues()
    {
        var (ravi, rice, milk) = await SetupAsync();
        await SellAsync(TenderType.Cash, ravi.Id, (rice, 2000));                 // ₹120
        await SellAsync(TenderType.Upi, ravi.Id, (milk, 2000));                  // ₹60
        await SellAsync(TenderType.Udhaar, ravi.Id, (rice, 1000), (milk, 1000)); // ₹90
        await _h.Sender.Send(new RecordDuesPaymentCommand(ravi.Id, Paise.FromRupees(40), null));

        var r = (await _h.Sender.Send(new GetCustomerReportQuery(ravi.Id)))!;
        Assert.Equal(3, r.BillCount);
        Assert.Equal(27_000, r.TotalSpent.Value);
        Assert.Equal(9_000, r.AverageBill.Value);
        Assert.Equal(12_000, r.PaidCash.Value);
        Assert.Equal(6_000, r.PaidUpi.Value);
        Assert.Equal(9_000, r.OnUdhaar.Value);
        Assert.Equal(5_000, r.DuesBalance.Value);
        Assert.Equal(4_000, r.DuesPaidSoFar.Value);
        Assert.Equal(2, r.TopItems.Count);
        Assert.All(r.TopItems, t => Assert.Equal(2, t.Times));
    }

    [Fact]
    public async Task VoidedBill_StaysInHistory_ButLeavesTheReport()
    {
        var (ravi, rice, _) = await SetupAsync();
        var bill = await SellAsync(TenderType.Cash, ravi.Id, (rice, 1000));
        await _h.Sender.Send(new VoidBillCommand(bill, "mistake"));

        Assert.Equal(BillStatus.Void, Assert.Single(await _h.Sender.Send(new GetCustomerBillsQuery(ravi.Id))).Status);
        Assert.Equal(0, (await _h.Sender.Send(new GetCustomerReportQuery(ravi.Id)))!.BillCount);
    }

    [Fact]
    public async Task UnknownCustomerOnACashBill_IsRejected()
    {
        var (_, rice, _) = await SetupAsync();
        var draft = AppHost.Draft(TenderType.Cash, (rice, 1000));
        draft.CustomerId = Guid.NewGuid();
        Assert.Equal(ErrorCodes.CustomerNotFound, (await _h.Sender.Send(new FinalizeBillCommand(draft))).Error.Code);
    }

    [Fact]
    public async Task Aging_PaymentsSettleOldestFirst()
    {
        var (ravi, rice, _) = await SetupAsync();
        await SellAsync(TenderType.Udhaar, ravi.Id, (rice, 1000));               // day 0: ₹60
        _h.Clock.Advance(TimeSpan.FromDays(5));
        await SellAsync(TenderType.Udhaar, ravi.Id, (rice, 1000));               // day 5: ₹60
        _h.Clock.Advance(TimeSpan.FromDays(5));                                  // day 10

        Assert.Equal(10, (await _h.Sender.Send(new GetCustomerReportQuery(ravi.Id)))!.OldestUnpaidDays);
        await _h.Sender.Send(new RecordDuesPaymentCommand(ravi.Id, Paise.FromRupees(60), null));   // clears day 0
        Assert.Equal(5, (await _h.Sender.Send(new GetCustomerReportQuery(ravi.Id)))!.OldestUnpaidDays);
        await _h.Sender.Send(new RecordDuesPaymentCommand(ravi.Id, Paise.FromRupees(60), null));
        Assert.Null((await _h.Sender.Send(new GetCustomerReportQuery(ravi.Id)))!.OldestUnpaidDays);
    }

    [Fact]
    public async Task DueReminders_PromiseAgeAndCooldown()
    {
        var (ravi, rice, _) = await SetupAsync();
        var sita = new Customer { Name = "సీత", Phone = "9000011223" };
        await _h.Sender.Send(new SaveCustomerCommand(sita));
        await SellAsync(TenderType.Udhaar, ravi.Id, (rice, 1000));
        await SellAsync(TenderType.Udhaar, sita.Id, (rice, 1000));

        Assert.Empty(await _h.Sender.Send(new GetDueRemindersQuery()));          // both too recent

        // Ravi promises in 2 days; Sita has no promise.
        await _h.Sender.Send(new SetPromisedPayDateCommand(ravi.Id, _h.Clock.Today.AddDays(2)));
        _h.Clock.Advance(TimeSpan.FromDays(3));
        var due = await _h.Sender.Send(new GetDueRemindersQuery());
        Assert.Equal(DueReason.PromiseDatePassed, Assert.Single(due).Reason);    // Ravi; Sita only 3 days old

        _h.Clock.Advance(TimeSpan.FromDays(5));                                  // Sita now 8 days
        Assert.Equal(2, (await _h.Sender.Send(new GetDueRemindersQuery())).Count);

        // Remind Ravi by SMS: message has the amount and the promise, and he leaves the list for 3 days.
        Assert.True((await _h.Sender.Send(new SendDuesReminderCommand(ravi.Id, ReminderChannel.Sms))).IsSuccess);
        var sms = Assert.Single(_h.Share.Sms);
        Assert.Equal("919876543210", sms.Phone);
        Assert.Contains("₹60.00", sms.Message, StringComparison.Ordinal);
        Assert.Equal(sita.Id, Assert.Single(await _h.Sender.Send(new GetDueRemindersQuery())).CustomerId);

        _h.Clock.Advance(TimeSpan.FromDays(3));
        Assert.Equal(2, (await _h.Sender.Send(new GetDueRemindersQuery())).Count);
    }

    [Fact]
    public async Task Reminder_ByWhatsApp_UsesTheCustomersNumber()
    {
        var (ravi, rice, _) = await SetupAsync();
        await SellAsync(TenderType.Udhaar, ravi.Id, (rice, 1000));
        Assert.True((await _h.Sender.Send(new SendDuesReminderCommand(ravi.Id))).IsSuccess);
        Assert.Equal("919876543210", Assert.Single(_h.Share.WhatsApp).Phone);
        Assert.NotNull((await _h.Sender.Send(new GetCustomerQuery(ravi.Id)))!.LastRemindedAt);
    }

    [Fact]
    public async Task Reminder_ListsTheUdhaarStillUnpaid_ToTheRegisteredNumber()
    {
        var (ravi, rice, milk) = await SetupAsync();
        await SellAsync(TenderType.Udhaar, ravi.Id, (rice, 1000));                // ₹60
        _h.Clock.Advance(TimeSpan.FromDays(2));
        await SellAsync(TenderType.Udhaar, ravi.Id, (milk, 1000));                // ₹30
        await _h.Sender.Send(new RecordDuesPaymentCommand(ravi.Id, Paise.FromRupees(20), null));   // ₹40 of the first left

        Assert.True((await _h.Sender.Send(new SendDuesReminderCommand(ravi.Id))).IsSuccess);
        var (phone, message) = Assert.Single(_h.Share.WhatsApp);
        Assert.Equal("919876543210", phone);
        Assert.Contains("₹70.00", message, StringComparison.Ordinal);   // total
        Assert.Contains("₹40.00", message, StringComparison.Ordinal);   // what's left of the first udhaar
        Assert.Contains("₹30.00", message, StringComparison.Ordinal);   // the second
    }

    [Fact]
    public async Task BillToCustomer_GoesToTheirWhatsApp_WithBalance()
    {
        var (ravi, rice, _) = await SetupAsync();
        var bill = await SellAsync(TenderType.Udhaar, ravi.Id, (rice, 2000));    // ₹120
        Assert.True((await _h.Sender.Send(new SendBillToCustomerCommand(bill))).IsSuccess);
        var (phone, message) = Assert.Single(_h.Share.WhatsApp);
        Assert.Equal("919876543210", phone);
        Assert.Contains("₹120.00", message, StringComparison.Ordinal);

        var walkIn = await SellAsync(TenderType.Cash, null, (rice, 1000));
        Assert.Equal(ErrorCodes.CustomerNotFound, (await _h.Sender.Send(new SendBillToCustomerCommand(walkIn))).Error.Code);
    }
}
