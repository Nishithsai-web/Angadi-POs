using Angadi.Application.Billing;
using Angadi.Application.Catalog;
using Angadi.Application.Common;
using Angadi.Application.Reports;
using Angadi.Domain.Billing;
using Angadi.Domain.Common;
using Angadi.Domain.Customers;
using Angadi.Domain.Items;
using Angadi.Domain.Shop;
using Angadi.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Angadi.Tests.Application;

/// <summary>Error args (message quality), M6 outbox coverage, M7 caps, M14 sort, H8 legacy slabs, H11 log export.</summary>
public class MediumsTests : IDisposable
{
    private readonly AppHost _h = new();
    public void Dispose() => _h.Dispose();

    private Task<List<string>> OutboxEventsAsync() =>
        _h.Services.GetRequiredService<AngadiDb>().RunAsync(c => c.QueryScalars<string>("SELECT EventType FROM Outbox ORDER BY CreatedAtMs"));

    // ---------- Localizer args ----------

    [Theory]
    [InlineData(UiLanguage.English, "₹20.00 short")]
    [InlineData(UiLanguage.Telugu, "ఇంకా ₹20.00 కావాలి")]
    public void ErrorArgs_AreFormattedIntoTheMessage(UiLanguage lang, string expected)
    {
        var text = new JsonLocalizer { Language = lang };
        var message = text.Error(Error.With(ErrorCodes.BillCashShort, "₹20.00"));
        Assert.Contains(expected, message, StringComparison.Ordinal);
    }

    [Fact]
    public void TemplatedError_WithoutArgs_NeverShowsRawPlaceholder()
    {
        var text = new JsonLocalizer { Language = UiLanguage.English };
        var message = text.Error(new Error(ErrorCodes.FreeLimitReached));
        Assert.DoesNotContain("{0}", message, StringComparison.Ordinal);
        Assert.DoesNotContain("{1}", message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownCode_FallsBackToUnexpected()
    {
        var text = new JsonLocalizer { Language = UiLanguage.English };
        Assert.Equal(text.Error(new Error(ErrorCodes.Unexpected)), text.Error(new Error("NO_SUCH_CODE")));
    }

    [Fact]
    public void M8_StrictLoad_StillCatchesDuplicatesInCi()
    {
        // Runtime is lenient; this strict load is the release gate for duplicate keys.
        Assert.NotEmpty(JsonLocalizer.LoadAll("te", strict: true));
        Assert.NotEmpty(JsonLocalizer.LoadAll("en", strict: true));
    }

    [Fact]
    public async Task CashShort_CarriesTheShortfall_FreeLimit_CarriesLimitAndPrice()
    {
        await _h.SeedShopAsync();
        var rice = await _h.AddItemAsync("బియ్యం", 6000);
        var draft = AppHost.Draft(TenderType.Cash, (rice, 2000));   // ₹120
        draft.CashReceived = Paise.FromRupees(100);
        var shortR = await _h.Sender.Send(new FinalizeBillCommand(draft));
        Assert.Equal(ErrorCodes.BillCashShort, shortR.Error.Code);
        Assert.Contains("20", (string)shortR.Error.Args![0], StringComparison.Ordinal);

        var item = await _h.AddItemAsync("అగ్గిపెట్టె", 200, GstSlab.Five, stockMilli: 1_000_000, unit: Unit.Piece);
        for (var i = 0; i < 50; i++) await _h.Sender.Send(new FinalizeBillCommand(AppHost.Draft(TenderType.Cash, (item, 1000))));
        var blocked = await _h.Sender.Send(new FinalizeBillCommand(AppHost.Draft(TenderType.Cash, (item, 1000))));
        Assert.Equal(ErrorCodes.FreeLimitReached, blocked.Error.Code);
        Assert.Equal(new object[] { 50, 200 }, blocked.Error.Args);
    }

    [Fact]
    public async Task M15_CustomerWithBadPhone_IsRejected()
    {
        var r = await _h.Sender.Send(new SaveCustomerCommand(new Customer { Name = "రవి", Phone = "12345" }));
        Assert.Equal(ErrorCodes.PhoneInvalid, r.Error.Code);
    }

    // ---------- M7 caps ----------

    [Fact]
    public async Task M7_AbsurdQuantity_IsRejectedBeforeAnyMaths()
    {
        await _h.SeedShopAsync();
        var rice = await _h.AddItemAsync("బియ్యం", 6000);
        var r = await _h.Sender.Send(new FinalizeBillCommand(AppHost.Draft(TenderType.Cash, (rice, 100_000_001))));
        // A1-08: the cap now has its own message ("quantity too large"), not "must be more than zero".
        Assert.Equal(ErrorCodes.BillQtyTooLarge, r.Error.Code);
    }

    // ---------- M6 outbox ----------

    [Fact]
    public async Task M6_EveryStateChange_LeavesAnOutboxRow()
    {
        await _h.SeedShopAsync();
        var customer = new Customer { Name = "రవి" };
        await _h.Sender.Send(new SaveCustomerCommand(customer));
        var rice = await _h.AddItemAsync("బియ్యం", 6000);
        var draft = AppHost.Draft(TenderType.Udhaar, (rice, 1000));
        draft.CustomerId = customer.Id;
        Assert.True((await _h.Sender.Send(new FinalizeBillCommand(draft))).IsSuccess);

        var events = await OutboxEventsAsync();
        Assert.Contains(nameof(Angadi.Domain.Events.ShopProfileChanged), events);
        Assert.Contains(nameof(Angadi.Domain.Events.CustomerChanged), events);
        Assert.Contains(nameof(Angadi.Domain.Events.DuesCharged), events);
        Assert.Contains(nameof(Angadi.Domain.Events.BillFinalized), events);
    }

    [Fact]
    public async Task M6_Seeding_WritesOneSummaryRow_NotOnePerItem()
    {
        var seeded = await _h.Sender.Send(new SeedItemsCommand(Kirana: true, Pharmacy: true));
        Assert.Equal(120, seeded.Value);
        var events = await OutboxEventsAsync();
        Assert.Single(events, e => e == nameof(Angadi.Domain.Events.ItemsSeeded));
        Assert.DoesNotContain(nameof(Angadi.Domain.Events.ItemChanged), events);
    }

    // ---------- M14 top items ----------

    [Fact]
    public async Task M14_TopItems_ByValueOrByHowOftenSold()
    {
        await _h.SeedShopAsync();
        var oil = await _h.AddItemAsync("నూనె", 20_000, stockMilli: 100_000);         // dear, sold once
        var match = await _h.AddItemAsync("అగ్గిపెట్టె", 200, stockMilli: 100_000);     // cheap, sold often
        await _h.Sender.Send(new FinalizeBillCommand(AppHost.Draft(TenderType.Cash, (oil, 1000))));
        for (var i = 0; i < 3; i++)
            await _h.Sender.Send(new FinalizeBillCommand(AppHost.Draft(TenderType.Cash, (match, 1000))));

        var day = _h.Clock.Today;
        var byValue = await _h.Sender.Send(new GetTopItemsQuery(day, day));
        var byBills = await _h.Sender.Send(new GetTopItemsQuery(day, day, Sort: TopItemsSort.BillCount));
        Assert.Equal(oil.Id, byValue[0].ItemId);
        Assert.Equal(match.Id, byBills[0].ItemId);
        Assert.Equal(3, byBills[0].BillCount);
    }

    // ---------- H8 legacy slabs ----------

    [Fact]
    public async Task H8_LegacySlabItems_AreListedForReslabbing()
    {
        await _h.AddItemAsync("పాత", 1000, GstSlab.Twelve);
        await _h.AddItemAsync("కొత్త", 1000, GstSlab.Five);
        var legacy = await _h.Sender.Send(new GetLegacySlabItemsQuery());
        Assert.Equal("పాత", Assert.Single(legacy).TeluguName);
    }

    // ---------- H11 log export ----------

    [Fact]
    public async Task H11_ExportLogs_GoesThroughTheDispatcher_AndWritesAZip()
    {
        var r = await _h.Sender.Send(new ExportLogsCommand());
        Assert.True(r.IsSuccess, r.Error.Code);
        Assert.True(File.Exists(r.Value));
        Assert.EndsWith(".zip", r.Value, StringComparison.Ordinal);
    }
}
