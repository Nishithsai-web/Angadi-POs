using Angadi.Application.Billing;
using Angadi.Application.Catalog;
using Angadi.Application.Common;
using Angadi.Application.Customers;
using Angadi.Domain.Billing;
using Angadi.Domain.Common;
using Angadi.Domain.Customers;
using Angadi.Domain.Items;

namespace Angadi.Tests.Application;

/// <summary>
/// SEC-5: bad debt is forgiven as a WriteOff ledger entry with a mandatory reason — never as a fake
/// payment — so "dues collected" keeps meaning money that actually arrived.
/// </summary>
public class DuesWriteOffTests : IDisposable
{
    private readonly AppHost _h = new();
    public void Dispose() => _h.Dispose();

    private async Task<(Customer ravi, Item rice, Item milk)> SetupAsync()
    {
        await _h.SeedShopAsync();
        var ravi = new Customer { Name = "రవి", Phone = "98765 43210" };
        Assert.True((await _h.Sender.Send(new SaveCustomerCommand(ravi))).IsSuccess);
        var rice = await _h.AddItemAsync("బియ్యం", 6000, stockMilli: 100_000);
        var milk = await _h.AddItemAsync("పాలు", 3000, stockMilli: 100_000, unit: Unit.Pack);
        return (ravi, rice, milk);
    }

    private async Task SellUdhaarAsync(Guid customerId, params (Item item, long qtyMilli)[] lines)
    {
        var draft = AppHost.Draft(TenderType.Udhaar, lines);
        draft.CustomerId = customerId;
        var r = await _h.Sender.Send(new FinalizeBillCommand(draft));
        Assert.True(r.IsSuccess, r.Error.Code);
    }

    [Fact]
    public async Task WriteOff_ReducesOutstanding_AndIsNotCountedAsCollected()
    {
        var (ravi, rice, milk) = await SetupAsync();
        await SellUdhaarAsync(ravi.Id, (rice, 1000));                 // ₹60
        await SellUdhaarAsync(ravi.Id, (milk, 1000));                 // ₹30 → owes ₹90
        await _h.Sender.Send(new RecordDuesPaymentCommand(ravi.Id, Paise.FromRupees(40), null));

        var r = await _h.Sender.Send(new RecordDuesWriteOffCommand(ravi.Id, Paise.FromRupees(50), "కుటుంబం ఊరు మారింది"));
        Assert.True(r.IsSuccess, r.Error.Code);

        var report = (await _h.Sender.Send(new GetCustomerReportQuery(ravi.Id)))!;
        Assert.Equal(0, report.DuesBalance.Value);                    // the debt is gone…
        Assert.Equal(4_000, report.DuesPaidSoFar.Value);              // …but only the real ₹40 counts as collected
        Assert.Equal(5_000, report.DuesWrittenOffSoFar.Value);      // the ₹50 shows as forgiven, on its own line
    }

    [Fact]
    public async Task WriteOff_WithoutAReason_IsRejected_AndChangesNothing()
    {
        var (ravi, rice, _) = await SetupAsync();
        await SellUdhaarAsync(ravi.Id, (rice, 1000));

        var r = await _h.Sender.Send(new RecordDuesWriteOffCommand(ravi.Id, Paise.FromRupees(60), "  "));
        Assert.True(r.IsFailure);
        Assert.Equal(ErrorCodes.ValidationFailed, r.Error.Code);
        Assert.Equal(6_000, (await _h.Sender.Send(new GetCustomerQuery(ravi.Id)))!.DuesBalance.Value);
    }

    [Fact]
    public async Task WriteOff_MoreThanTheBalance_IsRejected()
    {
        var (ravi, rice, _) = await SetupAsync();
        await SellUdhaarAsync(ravi.Id, (rice, 1000));                 // owes ₹60

        var r = await _h.Sender.Send(new RecordDuesWriteOffCommand(ravi.Id, Paise.FromRupees(61), "ఎక్కువ రాసేశాను"));
        Assert.True(r.IsFailure);
        Assert.Equal(ErrorCodes.DuesExceedsBalance, r.Error.Code);
    }

    [Fact]
    public async Task WriteOff_AppearsInTheLedger_AsItsOwnKind_WithTheReason()
    {
        var (ravi, rice, _) = await SetupAsync();
        await SellUdhaarAsync(ravi.Id, (rice, 1000));
        await _h.Sender.Send(new RecordDuesWriteOffCommand(ravi.Id, Paise.FromRupees(25), "వస్తువు పాడైంది"));

        var ledger = await _h.Sender.Send(new GetCustomerLedgerQuery(ravi.Id));
        var entry = Assert.Single(ledger, e => e.Kind == DuesEntryKind.WriteOff);
        Assert.Equal(2_500, entry.Amount.Value);
        Assert.Equal("వస్తువు పాడైంది", entry.Note);
        Assert.Equal(3_500, (await _h.Sender.Send(new GetCustomerQuery(ravi.Id)))!.DuesBalance.Value);
    }

    [Fact]
    public async Task WriteOff_SettlesTheOldestUdhaar_ForAgingAndReminders()
    {
        var (ravi, rice, _) = await SetupAsync();
        await SellUdhaarAsync(ravi.Id, (rice, 1000));                 // day 0: ₹60
        _h.Clock.Advance(TimeSpan.FromDays(5));
        await SellUdhaarAsync(ravi.Id, (rice, 1000));                 // day 5: ₹60
        _h.Clock.Advance(TimeSpan.FromDays(5));                       // day 10

        await _h.Sender.Send(new RecordDuesWriteOffCommand(ravi.Id, Paise.FromRupees(60), "మొదటి అప్పు మాఫీ"));
        // The forgiven ₹60 was the oldest: what remains is 5 days old, not 10 — and certainly not "paid".
        Assert.Equal(5, (await _h.Sender.Send(new GetCustomerReportQuery(ravi.Id)))!.OldestUnpaidDays);
    }
}
