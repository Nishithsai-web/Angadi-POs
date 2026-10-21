using Angadi.Application.Billing;
using Angadi.Application.Catalog;
using Angadi.Application.Common;
using Angadi.Application.Persistence;
using Angadi.Application.Platform;
using Angadi.Domain.Billing;
using Angadi.Domain.Common;
using Angadi.Domain.Customers;
using Angadi.Domain.Items;
using Microsoft.Extensions.DependencyInjection;

namespace Angadi.Tests.Application;

/// <summary>
/// The counter must survive what real shops do to it: double taps, a slow save the watchdog gives up on,
/// the app being closed mid-bill, and a full phone.
/// </summary>
public class CounterResilienceTests : IDisposable
{
    private readonly AppHost _h = new();
    public void Dispose() => _h.Dispose();

    private async Task<(Customer ravi, Item rice)> SetupAsync()
    {
        await _h.SeedShopAsync();
        var ravi = new Customer { Name = "రవి", Phone = "98765 43210" };
        Assert.True((await _h.Sender.Send(new SaveCustomerCommand(ravi))).IsSuccess);
        var rice = await _h.AddItemAsync("బియ్యం", 6000, stockMilli: 100_000);
        return (ravi, rice);
    }

    private async Task<long> DuesAsync(Guid customerId) => (await _h.Sender.Send(new GetCustomerQuery(customerId)))!.DuesBalance.Value;

    [Fact]
    public async Task Finalize_Retried_SameObject_ReturnsSameBill_NothingTakenTwice()
    {
        var (ravi, rice) = await SetupAsync();
        var draft = AppHost.Draft(TenderType.Udhaar, (rice, 2_000));
        draft.CustomerId = ravi.Id;

        var first = await _h.Sender.Send(new FinalizeBillCommand(draft));
        Assert.True(first.IsSuccess, first.Error.Code);
        // The watchdog fired, the shopkeeper taps Udhaar again with the same bill on screen.
        var again = await _h.Sender.Send(new FinalizeBillCommand(draft));

        Assert.True(again.IsSuccess, again.Error.Code);
        Assert.Equal(first.Value.BillId, again.Value.BillId);
        Assert.Equal(first.Value.Number, again.Value.Number);
        Assert.Equal(98_000, await _h.StockAsync(rice.Id));     // stock taken once
        Assert.Equal(12_000, await DuesAsync(ravi.Id));         // dues charged once
        Assert.Equal(1, (await _h.Sender.Send(new GetBillQuotaQuery())).UsedThisMonth);
    }

    [Fact]
    public async Task Finalize_Retried_FromRestoredDraft_SameId_ReturnsSameBill_WithChange()
    {
        var (_, rice) = await SetupAsync();
        var draft = AppHost.Draft(TenderType.Cash, (rice, 1_000));
        draft.CashReceived = new Paise(10_000);
        var first = await _h.Sender.Send(new FinalizeBillCommand(draft));
        Assert.True(first.IsSuccess, first.Error.Code);

        // A different object carrying the same bill id (the draft came back after the app was closed).
        var copy = AppHost.Draft(TenderType.Cash, (rice, 1_000));
        copy.Id = draft.Id;
        copy.CashReceived = new Paise(10_000);
        var again = await _h.Sender.Send(new FinalizeBillCommand(copy));

        Assert.True(again.IsSuccess, again.Error.Code);
        Assert.Equal(first.Value.Number, again.Value.Number);
        Assert.Equal(first.Value.ChangeDue, again.Value.ChangeDue);
        Assert.Equal(99_000, await _h.StockAsync(rice.Id));
    }

    [Fact]
    public async Task Draft_SavedAndLoaded_RebuildsTheSameBill()
    {
        var (ravi, rice) = await SetupAsync();
        var milk = await _h.AddItemAsync("పాలు", 3000, unit: Unit.Pack);
        var draft = new BillDraft(shopHasGstin: false);
        draft.Add(rice, new Qty(2_500), LineSource.Voice);
        var line = draft.Add(milk);
        draft.SetPrice(line.Id, new Paise(2_800));
        draft.SetBillDiscount(Discount.Percent(500));
        draft.SetCustomer(ravi.Id);

        Assert.True((await _h.Sender.Send(new SaveBillDraftCommand(draft.ToSnapshot()))).IsSuccess);
        var loaded = await _h.Sender.Send(new GetBillDraftQuery());

        Assert.NotNull(loaded);
        var back = BillDraft.Restore(loaded, shopHasGstin: false);
        Assert.Equal(draft.Bill.Id, back.Bill.Id);
        Assert.Equal(ravi.Id, back.Bill.CustomerId);
        Assert.Equal(2, back.Lines.Count);
        Assert.Equal(draft.Bill.GrandTotal, back.Bill.GrandTotal);
        Assert.Equal(new Paise(2_800), back.Lines.Single(l => l.ItemId == milk.Id).UnitPrice);
        Assert.Equal(LineSource.Voice, back.Lines.Single(l => l.ItemId == rice.Id).Source);
    }

    [Fact]
    public async Task Draft_IsClearedByFinalize_InTheSameTransaction()
    {
        var (_, rice) = await SetupAsync();
        var draft = new BillDraft(shopHasGstin: false);
        draft.Add(rice);
        await _h.Sender.Send(new SaveBillDraftCommand(draft.ToSnapshot()));
        draft.SetTender(TenderType.Cash);

        Assert.True((await _h.Sender.Send(new FinalizeBillCommand(draft.Bill))).IsSuccess);

        Assert.Null(await _h.Sender.Send(new GetBillDraftQuery()));
    }

    [Fact]
    public async Task Draft_LateSaveAfterFinalize_NeverBringsAPaidBillBack()
    {
        var (_, rice) = await SetupAsync();
        var draft = new BillDraft(shopHasGstin: false);
        draft.Add(rice);
        var stale = draft.ToSnapshot();
        draft.SetTender(TenderType.Upi);
        Assert.True((await _h.Sender.Send(new FinalizeBillCommand(draft.Bill))).IsSuccess);

        await _h.Sender.Send(new SaveBillDraftCommand(stale));   // a save queued before the payment lands after it

        Assert.Null(await _h.Sender.Send(new GetBillDraftQuery()));
    }

    [Fact]
    public async Task Draft_EmptySave_Clears_AndUnreadableJson_IsDiscardedWithoutError()
    {
        var (_, rice) = await SetupAsync();
        var draft = new BillDraft(shopHasGstin: false);
        draft.Add(rice);
        await _h.Sender.Send(new SaveBillDraftCommand(draft.ToSnapshot()));
        draft.Clear();
        await _h.Sender.Send(new SaveBillDraftCommand(draft.ToSnapshot()));
        Assert.Null(await _h.Sender.Send(new GetBillDraftQuery()));

        await _h.Services.GetRequiredService<IBillDraftStore>().SaveAsync("{not json");
        Assert.Null(await _h.Sender.Send(new GetBillDraftQuery()));
        Assert.Null(await _h.Services.GetRequiredService<IBillDraftStore>().LoadAsync());   // and it is cleaned up
    }

    [Fact]
    public void Draft_Restore_DropsNonsenseLines_AndNeverThrows()
    {
        var good = new BillDraftLine(Guid.NewGuid(), Guid.NewGuid(), "బియ్యం", "Rice", Unit.Kilogram, 6_000, GstSlab.Five, 1_000, DiscountKind.None, 0, LineSource.Manual);
        var snapshot = new BillDraftSnapshot(Guid.NewGuid(),
            [good, good with { LineId = Guid.NewGuid(), QtyMilli = 0 }, good with { LineId = Guid.NewGuid(), UnitPricePaise = -1 }],
            DiscountKind.None, 0, null);

        var back = BillDraft.Restore(snapshot, shopHasGstin: false);

        Assert.Single(back.Lines);
        Assert.Equal(new Paise(6_000), back.Bill.GrandTotal);

        var huge = snapshot with { Lines = [good with { UnitPricePaise = long.MaxValue / 2, QtyMilli = long.MaxValue / 2 }] };
        Assert.True(BillDraft.Restore(huge, shopHasGstin: false).IsEmpty);   // overflow: an empty bill, not a crash
    }

    [Fact]
    public async Task FullPhone_ShowsStorageFull_NotUnexpected()
    {
        using var h = new AppHost(configure: s => s.AddSingleton<IBillDraftStore, FullDiskDraftStore>());
        var draft = new BillDraft(shopHasGstin: false);
        draft.Add(new Item { TeluguName = "ఉప్పు", EnglishName = "Salt", Price = new Paise(2_800), Unit = Unit.Pack });

        var r = await h.Sender.Send(new SaveBillDraftCommand(draft.ToSnapshot()));

        Assert.True(r.IsFailure);
        Assert.Equal(ErrorCodes.StorageFull, r.Error.Code);
    }

    [Theory]
    [InlineData(28, ErrorCodes.StorageFull)]                       // ENOSPC (Android/Linux)
    [InlineData(unchecked((int)0x80070070), ErrorCodes.StorageFull)] // ERROR_DISK_FULL (Windows)
    [InlineData(5, null)]                                           // some other I/O error
    public void StorageFailures_ClassifyIoErrors(int hresult, string? expected)
    {
        Assert.Equal(expected, StorageFailures.Classify(new IOException("x", hresult)));
    }

    [Fact]
    public void StorageFailures_ClassifyBusyAndWrapped()
    {
        Assert.Equal(ErrorCodes.DatabaseBusy, StorageFailures.Classify(new DatabaseBusyException("busy")));
        Assert.Equal(ErrorCodes.StorageFull, StorageFailures.Classify(new InvalidOperationException("outer", new StorageFullException("full"))));
        Assert.Null(StorageFailures.Classify(new InvalidOperationException("other")));
    }

    [Fact]
    public void EveryNewMessage_HasTeluguAndEnglish()
    {
        var text = _h.Services.GetRequiredService<ILocalizer>();
        foreach (var language in new[] { Angadi.Domain.Shop.UiLanguage.Telugu, Angadi.Domain.Shop.UiLanguage.English })
        {
            text.Language = language;
            foreach (var code in new[] { ErrorCodes.BillSaveSlow, ErrorCodes.StorageFull, ErrorCodes.DatabaseBusy })
                Assert.NotEqual(code, text.Error(new Error(code)));
            foreach (var key in new[] { "pos.draft.restored", "data.recovered.title", "data.recovered", "data.recovered.none" })
                Assert.NotEqual(key, text[key]);
        }
    }

    // ---------- MONEY-2 / LOG-3a: the subscription service can hang or throw; billing must not ----------

    [Fact]
    public async Task Subscription_Hanging_TimesOutToFreeTier_AndTheSaleStillWorks()
    {
        using var h = new AppHost(configure: s => s.AddSingleton<ISubscriptionService, HangingSubscription>());
        await h.SeedShopAsync();
        var item = await h.AddItemAsync("అగ్గిపెట్టె", 200, GstSlab.Five, stockMilli: 100_000, unit: Unit.Piece);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = await h.Sender.Send(new FinalizeBillCommand(AppHost.Draft(TenderType.Cash, (item, 1000))));

        Assert.True(r.IsSuccess, r.Error.Code);   // free tier (0 of 50 used): never UNEXPECTED, never a hang
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), $"finalize took {sw.Elapsed}");
        Assert.Equal(1, (await h.Sender.Send(new GetBillQuotaQuery())).UsedThisMonth);
    }

    [Fact]
    public async Task Subscription_Throwing_FallsBackToFreeTier_AndTheSaleStillWorks()
    {
        using var h = new AppHost(configure: s => s.AddSingleton<ISubscriptionService, ThrowingSubscription>());
        await h.SeedShopAsync();
        var item = await h.AddItemAsync("అగ్గిపెట్టె", 200, GstSlab.Five, stockMilli: 100_000, unit: Unit.Piece);

        var r = await h.Sender.Send(new FinalizeBillCommand(AppHost.Draft(TenderType.Cash, (item, 1000))));

        Assert.True(r.IsSuccess, r.Error.Code);   // before MONEY-2 this surfaced as UNEXPECTED on every sale
        Assert.Equal(1, (await h.Sender.Send(new GetBillQuotaQuery())).UsedThisMonth);
    }

    /// <summary>Play services missing/wedged: the status call never answers at all.</summary>
    private sealed class HangingSubscription : ISubscriptionService
    {
        public Task<SubscriptionStatus> GetStatusAsync(CancellationToken ct = default) =>
            new TaskCompletionSource<SubscriptionStatus>().Task;   // never completes; the guard's 2 s bound must fire
        public Task<Result<SubscriptionStatus>> PurchaseAsync(SubscriptionPlan plan, CancellationToken ct = default) =>
            Task.FromResult(Result.Failure<SubscriptionStatus>(ErrorCodes.PurchaseFailed));
        public Task<Result<SubscriptionStatus>> RestoreAsync(CancellationToken ct = default) =>
            Task.FromResult(Result.Failure<SubscriptionStatus>(ErrorCodes.PurchaseFailed));
    }

    /// <summary>A future/other implementation that throws instead of answering (MONEY-2's scenario).</summary>
    private sealed class ThrowingSubscription : ISubscriptionService
    {
        public Task<SubscriptionStatus> GetStatusAsync(CancellationToken ct = default) =>
            Task.FromException<SubscriptionStatus>(new InvalidOperationException("play store gone"));
        public Task<Result<SubscriptionStatus>> PurchaseAsync(SubscriptionPlan plan, CancellationToken ct = default) =>
            Task.FromResult(Result.Failure<SubscriptionStatus>(ErrorCodes.PurchaseFailed));
        public Task<Result<SubscriptionStatus>> RestoreAsync(CancellationToken ct = default) =>
            Task.FromResult(Result.Failure<SubscriptionStatus>(ErrorCodes.PurchaseFailed));
    }

    private sealed class FullDiskDraftStore : IBillDraftStore
    {
        public Task<string?> LoadAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task SaveAsync(string json, CancellationToken ct = default) => throw new StorageFullException("database or disk is full");
        public Task ClearAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> BillExistsAsync(Guid billId, CancellationToken ct = default) => Task.FromResult(false);
    }
}
