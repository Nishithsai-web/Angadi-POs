using Angadi.Application.Persistence;
using Angadi.Application.Platform;
using Angadi.Application.Printing;
using Angadi.Application.Shops;
using Angadi.Infrastructure.Billing;
using Angadi.Infrastructure.Printing;
using Angadi.Infrastructure.Shops;
using Angadi.Infrastructure.Logging;
using Angadi.Infrastructure.Persistence;
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
        // v2 several shops: the database opened at start is the current shop's (the first shop keeps files/angadi.db).
        services.AddSingleton(_ => new ShopRegistry(options.DataDirectory));
        services.AddSingleton(sp =>
        {
            var shops = sp.GetRequiredService<ShopRegistry>();
            var current = shops.CurrentId;
            return new AngadiDb(shops.DatabasePath(current), shops.SnapshotDirectory(current));
        });
        services.AddSingleton<IShopDirectory, ShopDirectory>();
        services.AddSingleton<IUnitOfWork>(sp => sp.GetRequiredService<AngadiDb>());
        services.AddSingleton<IDataSafety, DataSafety>();
        services.AddSingleton<IBillDraftStore, BillDraftStore>();

        services.AddSingleton<IItemRepository, ItemRepository>();
        services.AddSingleton<IBillRepository, BillRepository>();
        services.AddSingleton<ICustomerRepository, CustomerRepository>();
        services.AddSingleton<IStockLedger, StockLedger>();
        services.AddSingleton<IOutbox, Outbox>();
        services.AddSingleton<IShopProfileRepository, ShopProfileRepository>();
        services.AddSingleton<ISeedService, SeedService>();
        services.AddSingleton<IQuotaStore, QuotaStore>();
        services.AddSingleton<INoteRepository, NoteRepository>();

        services.AddSingleton<IBackupService>(sp => new BackupService(
            sp.GetRequiredService<AngadiDb>(), sp.GetRequiredService<IClock>(),
            Path.Combine(options.CacheDirectory, "backups"), sp.GetRequiredService<ILogger<BackupService>>()));
        // FIX(SEC-6, 2026-10-20, FIX-3): the restore screen reads a staged backup's counts/date before the
        // swap (see BackupInspector). Logged in docs/AGENT_FIX_LOG.md.
        services.AddSingleton<IBackupInspector, BackupInspector>();
        // FIX(SEC-4, 2026-10-20, FIX-3): owner PIN service (salted PBKDF2 hash + lockout in the Meta table;
        // the gate itself lives in the MAUI head). Logged in docs/AGENT_FIX_LOG.md.
        services.AddSingleton<IPinService, PinService>();

        services.AddSingleton(sp => new ReceiptRenderer(
            options.ReceiptFonts, Path.Combine(options.CacheDirectory, "receipts"),
            sp.GetRequiredService<ILocalizer>(), sp.GetRequiredService<ILogger<ReceiptRenderer>>()));
        services.AddSingleton<IReceiptRenderer>(sp => sp.GetRequiredService<ReceiptRenderer>());
        // v2 printing: the thermal raster comes from the SAME renderer (layout, fonts, HarfBuzz shaping) as the PDF.
        services.AddSingleton<IReceiptRasterizer>(sp => sp.GetRequiredService<ReceiptRenderer>());
        services.AddSingleton<IPrinterSettingsStore>(_ => new PrinterSettingsStore(Path.Combine(options.DataDirectory, "printer.json")));
        // No Bluetooth outside the Android head; the platform layer replaces this (last registration wins).
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
