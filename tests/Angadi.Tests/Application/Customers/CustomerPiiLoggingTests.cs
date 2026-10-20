using Angadi.Application.Billing;
using Angadi.Application.Catalog;
using Angadi.Application.Customers;
using Angadi.Application.Platform;
using Angadi.Domain.Billing;
using Angadi.Domain.Common;
using Angadi.Domain.Customers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Angadi.Tests.Application.Customers;

/// <summary>NFR-08 across every customer flow, including failures and crashes: a phone number never reaches a log line.</summary>
public class CustomerPiiLoggingTests
{
    private const string Mobile = "9123456780";
    private const string NewMobile = "8765012349";

    [Fact]
    public async Task NoCustomerPhone_InAnyLogLine_AcrossEveryCustomerFlow()
    {
        var sink = new LogCapture();
        var share = new ScriptedShare();
        using var h = new AppHost(configure: s =>
        {
            s.AddLogging(b => b.AddProvider(sink).SetMinimumLevel(LogLevel.Trace));
            s.AddSingleton<IReceiptShare>(share);
        });
        var send = h.Sender;
        await h.SeedShopAsync();
        var rice = await h.AddItemAsync("బియ్యం", 6000);

        var ravi = new Customer { Name = "రవి", Phone = "+91 91234-56780" };
        await send.Send(new SaveCustomerCommand(ravi));
        await send.Send(new SaveCustomerCommand(new Customer { Name = "bad", Phone = "91234 5678" }));       // rejected
        await send.Send(new SaveCustomerCommand(new Customer { Name = "bad2", Phone = Mobile + "1" }));     // rejected
        await send.Send(new SearchCustomersQuery(Mobile));
        await send.Send(new SearchCustomersQuery("91234 56780"));

        var udhaar = AppHost.Draft(TenderType.Udhaar, (rice, 1000));
        udhaar.CustomerId = ravi.Id;
        var bill = (await send.Send(new FinalizeBillCommand(udhaar))).Value;
        var cash = AppHost.Draft(TenderType.Cash, (rice, 500));
        cash.CustomerId = ravi.Id;
        var cashBill = (await send.Send(new FinalizeBillCommand(cash))).Value;

        await send.Send(new RecordDuesPaymentCommand(ravi.Id, Paise.FromRupees(10), "UPI", "412345678901"));
        await send.Send(new RecordDuesPaymentCommand(ravi.Id, Paise.FromRupees(1000), null));              // too much
        await send.Send(new SetPromisedPayDateCommand(ravi.Id, h.Clock.Today));
        await send.Send(new GetDueRemindersQuery());
        await send.Send(new GetCustomerReportQuery(ravi.Id));
        await send.Send(new GetCustomerLedgerQuery(ravi.Id));
        await send.Send(new GetCustomerBillsQuery(ravi.Id));

        foreach (var behaviour in new[] { ShareBehaviour.Works, ShareBehaviour.Fails, ShareBehaviour.Throws })
        {
            share.Behaviour = behaviour;
            await send.Send(new SendDuesReminderCommand(ravi.Id, ReminderChannel.WhatsApp));
            await send.Send(new SendDuesReminderCommand(ravi.Id, ReminderChannel.Sms));
            await send.Send(new SendBillToCustomerCommand(bill.BillId));
            await send.Send(new ShareReceiptCommand(cashBill.BillId));
        }
        share.Behaviour = ShareBehaviour.Works;

        var edit = (await send.Send(new GetCustomerQuery(ravi.Id)))!;
        edit.Phone = NewMobile;
        await send.Send(new SaveCustomerCommand(edit));
        await send.Send(new SendDuesReminderCommand(ravi.Id));
        await send.Send(new VoidBillCommand(cashBill.BillId, "customer returned"));

        Assert.NotEmpty(share.WhatsApp);   // the flows really ran with the numbers
        Assert.Contains(share.WhatsApp, w => w.Phone == "91" + NewMobile);
        var lines = sink.Lines;
        Assert.True(lines.Count > 20, "expected the request pipeline to log every flow");
        foreach (var needle in new[] { Mobile, NewMobile, "91234 56780", "91234-56780", "87650 12349" })
            Assert.DoesNotContain(lines, l => l.Contains(needle, StringComparison.Ordinal));
    }
}
