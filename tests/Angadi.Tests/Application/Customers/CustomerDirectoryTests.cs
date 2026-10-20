using Angadi.Application.Billing;
using Angadi.Application.Catalog;
using Angadi.Application.Customers;
using Angadi.Domain.Billing;
using Angadi.Domain.Common;
using Angadi.Domain.Customers;

namespace Angadi.Tests.Application.Customers;

/// <summary>Finding a customer (Telugu, English, phone digits), the same phone twice, and edits that keep the account.</summary>
public class CustomerDirectoryTests : IDisposable
{
    private readonly AppHost _h = new();
    public void Dispose() => _h.Dispose();

    private async Task<Customer> AddAsync(string name, string? phone)
    {
        var c = new Customer { Name = name, Phone = phone };
        var r = await _h.Sender.Send(new SaveCustomerCommand(c));
        Assert.True(r.IsSuccess, r.Error.Code);
        return c;
    }

    private async Task<string[]> FindAsync(string text) =>
        (await _h.Sender.Send(new SearchCustomersQuery(text))).Select(c => c.Name).ToArray();

    private async Task SeedPeopleAsync()
    {
        await AddAsync("రవి", "98765 43210");
        await AddAsync("Ravi Kumar", "9123456789");
        await AddAsync("సీత", null);
        await AddAsync("Sita Medicals", "+91 70000 12345");
    }

    [Fact]
    public async Task Search_EnglishIsCaseInsensitive_TeluguMatchesTelugu()
    {
        await SeedPeopleAsync();
        Assert.Equal(["Ravi Kumar"], await FindAsync("ravi"));
        Assert.Equal(["Ravi Kumar"], await FindAsync("KUMAR"));
        Assert.Equal(["రవి"], await FindAsync("రవ"));
        Assert.Equal(["సీత"], await FindAsync(" సీత "));
        Assert.Equal(["Sita Medicals"], await FindAsync("medical"));
        Assert.Empty(await FindAsync("Lakshmi"));
        Assert.Equal(4, (await FindAsync("")).Length);   // empty box lists everyone
    }

    [Theory]
    [InlineData("43210", "రవి")]
    [InlineData("98765", "రవి")]
    [InlineData("9876543210", "రవి")]
    [InlineData("919876543210", "రవి")]
    [InlineData("00012345", "Sita Medicals")]
    public async Task Search_ByPhoneDigits(string typed, string expected)
    {
        await SeedPeopleAsync();
        Assert.Equal([expected], await FindAsync(typed));
    }

    [Theory(Skip = "BUG T3-2: phone search matches raw stored digits only; a number typed with a space, +91 or 0 finds nobody")]
    [InlineData("98765 43210")]
    [InlineData("+91 98765 43210")]
    [InlineData("09876543210")]
    [InlineData("98765-43210")]
    public async Task Search_ByPhone_AsTheShopkeeperTypesIt(string typed)
    {
        await SeedPeopleAsync();
        Assert.Equal(["రవి"], await FindAsync(typed));
    }

    [Fact]
    public async Task Search_WildcardsAreLiteral_NotSqlPatterns()
    {
        await SeedPeopleAsync();
        Assert.Empty(await FindAsync("R%i"));    // '%' stripped → "Ri": no customer has "Ri" in a row
        Assert.Empty(await FindAsync("Ra_i"));   // '_' stripped → "Rai"
    }

    [Fact]
    public async Task SamePhone_TwoCustomers_AreBothKept_AndBothFound()
    {
        // Current behaviour (see T3-4): a family sharing one phone is allowed; nothing warns the shopkeeper.
        var ravi = await AddAsync("Ravi", "9876543210");
        var wife = await AddAsync("Lakshmi (Ravi)", "+91 98765-43210");
        Assert.Equal((await _h.Sender.Send(new GetCustomerQuery(ravi.Id)))!.Phone, (await _h.Sender.Send(new GetCustomerQuery(wife.Id)))!.Phone);
        Assert.Equal(2, (await FindAsync("43210")).Length);
    }

    [Fact]
    public async Task Edit_NameAndPhone_KeepsBalanceLedgerBillsPromiseAndReminderTime()
    {
        await _h.SeedShopAsync();
        var rice = await _h.AddItemAsync("బియ్యం", 6000);
        var ravi = await AddAsync("రవి", "9876543210");
        var draft = AppHost.Draft(TenderType.Udhaar, (rice, 2000));
        draft.CustomerId = ravi.Id;
        Assert.True((await _h.Sender.Send(new FinalizeBillCommand(draft))).IsSuccess);
        var cash = AppHost.Draft(TenderType.Cash, (rice, 1000));
        cash.CustomerId = ravi.Id;
        Assert.True((await _h.Sender.Send(new FinalizeBillCommand(cash))).IsSuccess);
        await _h.Sender.Send(new RecordDuesPaymentCommand(ravi.Id, Paise.FromRupees(20), null));
        await _h.Sender.Send(new SetPromisedPayDateCommand(ravi.Id, new DateOnly(2026, 10, 30)));
        await _h.Sender.Send(new SendDuesReminderCommand(ravi.Id));
        var before = (await _h.Sender.Send(new GetCustomerReportQuery(ravi.Id)))!;
        var remindedAt = (await _h.Sender.Send(new GetCustomerQuery(ravi.Id)))!.LastRemindedAt;
        Assert.NotNull(remindedAt);

        _h.Clock.Advance(TimeSpan.FromHours(2));
        var edit = (await _h.Sender.Send(new GetCustomerQuery(ravi.Id)))!;   // what the detail page holds
        edit.Name = "రవి కుమార్";
        edit.Phone = "70000 12345";
        edit.Address = "గాంధీ నగర్";
        Assert.True((await _h.Sender.Send(new SaveCustomerCommand(edit))).IsSuccess);

        var after = (await _h.Sender.Send(new GetCustomerQuery(ravi.Id)))!;
        Assert.Equal("రవి కుమార్", after.Name);
        Assert.Equal("917000012345", after.Phone);
        Assert.Equal(10_000, after.DuesBalance.Value);
        Assert.Equal(new DateOnly(2026, 10, 30), after.PromisedPayDate);
        Assert.Equal(remindedAt, after.LastRemindedAt);
        Assert.Equal(2, (await _h.Sender.Send(new GetCustomerBillsQuery(ravi.Id))).Count);
        Assert.Equal(2, (await _h.Sender.Send(new GetCustomerLedgerQuery(ravi.Id))).Count);
        var report = (await _h.Sender.Send(new GetCustomerReportQuery(ravi.Id)))!;
        Assert.Equal(before.TotalSpent, report.TotalSpent);
        Assert.Equal(before.DuesBalance, report.DuesBalance);
        Assert.Equal(before.DuesPaidSoFar, report.DuesPaidSoFar);
        Assert.Equal(["రవి కుమార్"], await FindAsync("70000"));
        Assert.Empty(await FindAsync("9876543210"));
    }

    [Fact]
    public async Task Edit_FromAStaleCopy_CanNeverResetTheBalance()
    {
        await _h.SeedShopAsync();
        var rice = await _h.AddItemAsync("బియ్యం", 6000);
        var ravi = await AddAsync("రవి", "9876543210");
        var stale = (await _h.Sender.Send(new GetCustomerQuery(ravi.Id)))!;   // balance 0 when read
        var draft = AppHost.Draft(TenderType.Udhaar, (rice, 1000));
        draft.CustomerId = ravi.Id;
        Assert.True((await _h.Sender.Send(new FinalizeBillCommand(draft))).IsSuccess);

        stale.Notes = "pays on the 5th";
        Assert.True((await _h.Sender.Send(new SaveCustomerCommand(stale))).IsSuccess);
        Assert.Equal(6000, stale.DuesBalance.Value);   // the saved object is corrected too
        Assert.Equal(6000, (await _h.Sender.Send(new GetCustomerQuery(ravi.Id)))!.DuesBalance.Value);
    }

    [Fact]
    public async Task TooLongNotesOrAddress_AreRejected()
    {
        var notes = await _h.Sender.Send(new SaveCustomerCommand(new Customer { Name = "Ravi", Notes = new string('x', Angadi.Domain.Notes.Note.MaxLength + 1) }));
        Assert.Equal(Angadi.Application.Common.ErrorCodes.NoteTooLong, notes.Error.Code);
        var address = await _h.Sender.Send(new SaveCustomerCommand(new Customer { Name = "Ravi", Address = new string('x', 301) }));
        Assert.Equal(Angadi.Application.Common.ErrorCodes.NoteTooLong, address.Error.Code);
    }
}
