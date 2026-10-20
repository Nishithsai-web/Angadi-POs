using Angadi.Application;
using Angadi.Application.Catalog;
using Angadi.Application.Common;
using Angadi.Application.Platform;
using Angadi.Domain.Billing;
using Angadi.Domain.Common;
using Angadi.Domain.Items;
using Angadi.Domain.Shop;
using Angadi.Infrastructure;
using Angadi.Infrastructure.Persistence;
using Angadi.Infrastructure.Receipts;
using Microsoft.Extensions.DependencyInjection;

namespace Angadi.Tests.Application;

/// <summary>
/// The REAL Application + Infrastructure graph (same registrations the app uses) on a temp SQLite file,
/// with only the platform edges faked: clock, subscription, receipt renderer, share sheet.
/// </summary>
public sealed class AppHost : IDisposable
{
    public string Folder { get; } = Path.Combine(Path.GetTempPath(), "angadi-apphost", Guid.NewGuid().ToString("N"));
    public FakeClock Clock { get; } = new();
    public FakeSubscription Subscription { get; } = new();
    public FakeShare Share { get; } = new();
    public ServiceProvider Services { get; }
    public ISender Sender => Services.GetRequiredService<ISender>();

    public AppHost(bool fakePlatform = true, Action<IServiceCollection>? configure = null)
    {
        Directory.CreateDirectory(Folder);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAngadiApplication();
        services.AddAngadiInfrastructure(new InfrastructureOptions
        {
            DataDirectory = Path.Combine(Folder, "data"),
            CacheDirectory = Path.Combine(Folder, "cache"),
            ReceiptFonts = new ReceiptFonts(NoFont, NoFont, NoFont, NoFont),
        });
        services.AddSingleton<IReceiptShare>(Share);   // platform-only service; the app registers the Android one
        if (fakePlatform)
        {
            services.AddSingleton<IClock>(Clock);
            services.AddSingleton<ISubscriptionService>(Subscription);
            services.AddSingleton<IReceiptRenderer, FakeRenderer>();
        }
        configure?.Invoke(services);
        Services = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    private static Stream NoFont() => throw new InvalidOperationException("fonts are not used in handler tests");

    public async Task SeedShopAsync(string? gstin = null)
    {
        var r = await Sender.Send(new SaveShopProfileCommand(new ShopProfile { Name = "రాము కిరాణా", UpiId = "ramu@okaxis", Gstin = gstin }));
        Assert.True(r.IsSuccess, r.Error.Code);
    }

    public async Task<Item> AddItemAsync(string name, long pricePaise, GstSlab slab = GstSlab.Five, long stockMilli = 10_000, Unit unit = Unit.Kilogram)
    {
        var item = new Item { TeluguName = name, EnglishName = name, Unit = unit, Price = new Paise(pricePaise), GstSlab = slab };
        var r = await Sender.Send(new SaveItemCommand(item, new Qty(stockMilli)));
        Assert.True(r.IsSuccess, r.Error.Code);
        return item;
    }

    public static Bill Draft(TenderType tender, params (Item item, long qtyMilli)[] lines) => new()
    {
        Tender = tender,
        Lines = lines.Select(l => new BillLine
        {
            ItemId = l.item.Id, TeluguName = l.item.TeluguName, EnglishName = l.item.EnglishName, Unit = l.item.Unit,
            UnitPrice = l.item.Price, GstSlab = l.item.GstSlab, Qty = new Qty(l.qtyMilli),
        }).ToList(),
    };

    public async Task<long> StockAsync(Guid itemId) => (await Sender.Send(new GetItemQuery(itemId)))!.Stock.Milli;

    public void Dispose()
    {
        Services.Dispose();
        try { Directory.Delete(Folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

public sealed class FakeSubscription : ISubscriptionService
{
    public bool IsPro { get; set; }
    public Task<SubscriptionStatus> GetStatusAsync(CancellationToken ct = default) =>
        Task.FromResult(new SubscriptionStatus(IsPro, IsPro ? SubscriptionPlan.Monthly : null, null));
    public Task<Result<SubscriptionStatus>> PurchaseAsync(SubscriptionPlan plan, CancellationToken ct = default)
    {
        IsPro = true;
        return Task.FromResult(Result.Success(new SubscriptionStatus(true, plan, null)));
    }
    public Task<Result<SubscriptionStatus>> RestoreAsync(CancellationToken ct = default) =>
        Task.FromResult(Result.Success(new SubscriptionStatus(IsPro, null, null)));
}

public sealed class FakeShare : IReceiptShare
{
    public List<string> SharedFiles { get; } = [];
    public List<(string Phone, string Message)> WhatsApp { get; } = [];
    public Task<Result> ShareFileAsync(string filePath, string title, CancellationToken ct = default)
    {
        SharedFiles.Add(filePath);
        return Task.FromResult(Result.Success());
    }
    public Task<Result> OpenWhatsAppAsync(string phoneE164, string message, CancellationToken ct = default)
    {
        WhatsApp.Add((phoneE164, message));
        return Task.FromResult(Result.Success());
    }
    public List<(string Phone, string Message)> Sms { get; } = [];
    public Task<Result> OpenSmsAsync(string phoneE164, string message, CancellationToken ct = default)
    {
        Sms.Add((phoneE164, message));
        return Task.FromResult(Result.Success());
    }
    public List<string> SharedText { get; } = [];
    public Task<Result> ShareTextAsync(string text, string title, CancellationToken ct = default)
    {
        SharedText.Add(text);
        return Task.FromResult(Result.Success());
    }
}

public sealed class FakeRenderer : IReceiptRenderer
{
    public Task<Result<string>> RenderAsync(Bill bill, ShopProfile shop, CancellationToken ct = default) =>
        Task.FromResult(Result.Success(Path.Combine(Path.GetTempPath(), bill.Number + ".pdf")));
}
