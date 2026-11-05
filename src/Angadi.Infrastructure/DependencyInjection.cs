using Angadi.Application.Persistence;
using Angadi.Application.Platform;
using Angadi.Application.Printing;
using Angadi.Infrastructure.Billing;
using Angadi.Infrastructure.Logging;
using Angadi.Infrastructure.Persistence;
using Angadi.Infrastructure.Printing;
using Angadi.Infrastructure.Receipts;
using Angadi.Infrastructure.Seed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Angadi.Infrastructure;

public sealed class InfrastructureOptions
{
    /// <summary>App data folder (MAUI: FileSystem.AppDataDirectory).</summary>
    public required string DataDirectory { get; init; }
    /// <summary>Shareable cache folder for receipts, backups and log exports (MAUI: FileSystem.CacheDirectory).</summary>
    public required string CacheDirectory { get; init; }
    /// <summary>Noto Sans Telugu + Noto Sans (Latin, ₹) font streams for receipt PDFs (FR-17).</summary>
    public required ReceiptFonts ReceiptFonts { get; init; }
}

public static class DependencyInjection
{
    public static IServiceCollection AddAngadiInfrastructure(this IServiceCollection services, InfrastructureOptions options)
    {
        services.AddSingleton(options);
        services.AddSingleton<IClock, SystemClock>();
        // v2 several shops: the database opened at start is the current shop's (the first shop keeps files/angadi.db,
        // safety copies in files/snapshots — exactly the v1 paths, so existing installs are untouched).
        services.AddSingleton(_ => new Angadi.Infrastructure.Shops.ShopRegistry(options.DataDirectory));
        services.AddSingleton(sp =>
        {
            var shops = sp.GetRequiredService<Angadi.Infrastructure.Shops.ShopRegistry>();
            var current = shops.CurrentId;
            return new AngadiDb(shops.DatabasePath(current), shops.SnapshotDirectory(current));
        });
        services.AddSingleton<Angadi.Application.Shops.IShopDirectory, Angadi.Infrastructure.Shops.ShopDirectory>();
        services.AddSingleton<IUnitOfWork>(sp => sp.GetRequiredService<AngadiDb>());
        services.AddSingleton<IDataSafety, DataSafety>();
        services.AddSingleton<IBillDraftStore, BillDraftStore>();

        services.AddSingleton<IItemRepository, ItemRepository>();
        services.AddSingleton<IBillRepository, BillRepository>();
        services.AddSingleton<IReturnRepository, ReturnRepository>();   // v2 returns
        services.AddSingleton<ICustomerRepository, CustomerRepository>();
        // v2-stock: one ledger object serves both the item and the batch side.
        services.AddSingleton<StockLedger>();
        services.AddSingleton<IStockLedger>(sp => sp.GetRequiredService<StockLedger>());
        services.AddSingleton<IBatchLedger>(sp => sp.GetRequiredService<StockLedger>());
        services.AddSingleton<IPurchasingRepository, PurchasingRepository>();
        services.AddSingleton<IOutbox, Outbox>();
        services.AddSingleton<IShopProfileRepository, ShopProfileRepository>();
        services.AddSingleton<ISeedService, SeedService>();
        services.AddSingleton<IQuotaStore, QuotaStore>();
        services.AddSingleton<INoteRepository, NoteRepository>();
        services.AddSingleton<Angadi.Application.Catalog.IBackupStatusStore, BackupStatusStore>();

        services.AddSingleton<IBackupService>(sp => new BackupService(
            sp.GetRequiredService<AngadiDb>(), sp.GetRequiredService<IClock>(),
            Path.Combine(options.CacheDirectory, "backups"), sp.GetRequiredService<ILogger<BackupService>>()));
        // FIX(SEC-6, 2026-10-20, FIX-3): the restore screen reads a staged backup's counts/date before the
        // swap (see BackupInspector). Logged in docs/AGENT_FIX_LOG.md.
        services.AddSingleton<IBackupInspector, BackupInspector>();
        // FIX(SEC-4, 2026-10-20, FIX-3): owner PIN service (salted PBKDF2 hash + lockout in the Meta table;
        // the gate itself lives in the MAUI head). Logged in docs/AGENT_FIX_LOG.md.
        services.AddSingleton<IPinService, PinService>();
        services.AddSingleton<Angadi.Application.Security.IAccessControl, AccessControl>();   // RBAC-1

        services.AddSingleton(sp => new ReceiptRenderer(
            options.ReceiptFonts, Path.Combine(options.CacheDirectory, "receipts"),
            sp.GetRequiredService<ILocalizer>(), sp.GetRequiredService<ILogger<ReceiptRenderer>>()));
        services.AddSingleton<IReceiptRenderer>(sp => sp.GetRequiredService<ReceiptRenderer>());
        // v2 printing: the same renderer (same rows, fonts, gate) draws the thermal raster. The printer choice is
        // hardware, kept in its own file outside the database; the Android head replaces the transport.
        services.AddSingleton<IThermalReceiptRenderer>(sp => sp.GetRequiredService<ReceiptRenderer>());
        // CHAIN-1: this phone's chain code + received branch reports (per phone, not in the shop database or backups).
        services.AddSingleton<Angadi.Application.Chain.IChainStore>(sp => new ChainStore(
            Path.Combine(options.DataDirectory, "chain.json"), sp.GetRequiredService<AngadiDb>(), sp.GetRequiredService<ILogger<ChainStore>>()));
        services.AddSingleton(new Angadi.Application.Chain.ChainPackFolder(Path.Combine(options.CacheDirectory, "chain")));
        services.AddSingleton<IPrinterSettingsStore>(_ => new PrinterSettingsStore(Path.Combine(options.DataDirectory, "printer.json")));
        services.AddSingleton<IBluetoothPrinterTransport, NoBluetoothPrinterTransport>();

        // C2: never-Pro stub until the Play Billing adapter lands; the platform layer may replace it.
        services.AddSingleton<ISubscriptionService, StubSubscriptionService>();

        services.AddSingleton(new LogFiles(Path.Combine(options.DataDirectory, "logs")));
        services.AddSingleton<ILogExporter>(sp => new LogExporter(
            sp.GetRequiredService<LogFiles>(), Path.Combine(options.CacheDirectory, "logs"),
            sp.GetRequiredService<IClock>(), sp.GetRequiredService<ILogger<LogExporter>>()));
        return services;
    }
}

internal sealed class SystemClock : IClock
{
    public DateTimeOffset Now => DateTimeOffset.Now;
    public DateOnly Today => DateOnly.FromDateTime(DateTimeOffset.Now.DateTime);
}
