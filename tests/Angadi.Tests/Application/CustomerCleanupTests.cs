using Angadi.Application.Billing;
using Angadi.Application.Catalog;
using Angadi.Application.Common;
using Angadi.Application.Platform;
using Angadi.Domain.Billing;
using Angadi.Domain.Customers;
using Microsoft.Extensions.DependencyInjection;

namespace Angadi.Tests.Application;

/// <summary>CUST-1 (no duplicate phones, remove a customer) and SHARE-2 (receipt PDF straight to the customer's WhatsApp).</summary>
public sealed class CustomerCleanupTests : IDisposable
{
    private readonly DirectShare _share = new();
    private readonly AppHost _h;

    public CustomerCleanupTests() => _h = new AppHost(configure: s => s.AddSingleton<IReceiptShare>(_share));
    public void Dispose() => _h.Dispose();

    private async Task<Customer> AddCustomerAsync(string name, string? phone)
    {
        var c = new Customer { Name = name, Phone = phone };
        var r = await _h.Sender.Send(new SaveCustomerCommand(c));
        Assert.True(r.IsSuccess, r.Error.Code);
        return c;
    }

    [Fact]
    public async Task TheSamePhone_CannotBeSavedForTwoCustomers_InAnyWriting()
    {
        await _h.SeedShopAsync();
        await AddCustomerAsync("nishith", "89193 70399");
        var again = await _h.Sender.Send(new SaveCustomerCommand(new Customer { Name = "nishith", Phone = "+91 8919370399" }));
        Assert.Equal(ErrorCodes.CustomerPhoneDuplicate, again.Error.Code);
        Assert.Single(await _h.Sender.Send(new SearchCustomersQuery("nishith")));
    }

    [Fact]
    public async Task EditingACustomer_KeepsTheirOwnPhone()
    {
        await _h.SeedShopAsync();
        var c = await AddCustomerAsync("Ravi", "9876543210");
        c.Name = "Ravi Kumar";
        Assert.True((await _h.Sender.Send(new SaveCustomerCommand(c))).IsSuccess);
    }

    [Fact]
    public async Task ACustomerWithDues_CannotBeRemoved_OneWithout_IsHidden_ButTheirBillsStay()
    {
        await _h.SeedShopAsync();
        var rice = await _h.AddItemAsync("బియ్యం", 6000);
        var owes = await AddCustomerAsync("Lakshmi", "9000011223");
        var draft = AppHost.Draft(TenderType.Udhaar, (rice, 1000));
        draft.CustomerId = owes.Id;
        var bill = await _h.Sender.Send(new FinalizeBillCommand(draft));
        Assert.True(bill.IsSuccess, bill.Error.Code);
        Assert.Equal(ErrorCodes.CustomerHasDues, (await _h.Sender.Send(new RemoveCustomerCommand(owes.Id))).Error.Code);

        var paid = await AddCustomerAsync("nishith", "8919370399");
        Assert.True((await _h.Sender.Send(new RemoveCustomerCommand(paid.Id))).IsSuccess);
        Assert.Empty(await _h.Sender.Send(new SearchCustomersQuery("nishith")));
        // Removed people stay readable for old bills, and their number can be used again.
        Assert.NotNull(await _h.Sender.Send(new GetCustomerQuery(paid.Id)));
        await AddCustomerAsync("Nishith (new)", "8919370399");
    }

    [Fact]
    public async Task Receipt_ForACustomerWithAPhone_GoesStraightToTheirWhatsApp()
    {
        await _h.SeedShopAsync();
        var rice = await _h.AddItemAsync("బియ్యం", 6000);
        var c = await AddCustomerAsync("Ravi", "98765 43210");
        var draft = AppHost.Draft(TenderType.Cash, (rice, 1000));
        draft.CustomerId = c.Id;
        var bill = await _h.Sender.Send(new FinalizeBillCommand(draft));

        Assert.True((await _h.Sender.Send(new ShareReceiptCommand(bill.Value.BillId))).IsSuccess);
        Assert.Equal("919876543210", Assert.Single(_share.Direct).Phone);
        Assert.Empty(_share.Sheet);
    }

    [Fact]
    public async Task Receipt_FallsBackToTheShareSheet_WhenWhatsAppCannotTakeIt_OrThereIsNoCustomer()
    {
        await _h.SeedShopAsync();
        var rice = await _h.AddItemAsync("బియ్యం", 6000);
        var c = await AddCustomerAsync("Ravi", "9876543210");
        _share.DirectWorks = false;
        var draft = AppHost.Draft(TenderType.Cash, (rice, 1000));
        draft.CustomerId = c.Id;
        var withCustomer = await _h.Sender.Send(new FinalizeBillCommand(draft));
        var walkIn = await _h.Sender.Send(new FinalizeBillCommand(AppHost.Draft(TenderType.Cash, (rice, 500))));

        Assert.True((await _h.Sender.Send(new ShareReceiptCommand(withCustomer.Value.BillId))).IsSuccess);
        Assert.True((await _h.Sender.Send(new ShareReceiptCommand(walkIn.Value.BillId))).IsSuccess);
        Assert.Single(_share.Direct);          // tried once, for the customer only
        Assert.Equal(2, _share.Sheet.Count);   // both ended on the share sheet
    }

    private sealed class DirectShare : IReceiptShare
    {
        public bool DirectWorks { get; set; } = true;
        public List<(string Phone, string File)> Direct { get; } = [];
        public List<string> Sheet { get; } = [];

        public Task<Result> ShareFileToWhatsAppAsync(string phoneE164, string filePath, string caption, CancellationToken ct = default)
        {
            Direct.Add((phoneE164, filePath));
            return Task.FromResult(DirectWorks ? Result.Success() : Result.Failure(ErrorCodes.ShareFailed, "no whatsapp"));
        }

        public Task<Result> ShareFileAsync(string filePath, string title, CancellationToken ct = default)
        {
            Sheet.Add(filePath);
            return Task.FromResult(Result.Success());
        }

        public Task<Result> OpenWhatsAppAsync(string phoneE164, string message, CancellationToken ct = default) => Task.FromResult(Result.Success());
        public Task<Result> OpenSmsAsync(string phoneE164, string message, CancellationToken ct = default) => Task.FromResult(Result.Success());
        public Task<Result> ShareTextAsync(string text, string title, CancellationToken ct = default) => Task.FromResult(Result.Success());
    }
}
