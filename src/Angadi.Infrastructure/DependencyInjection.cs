using Angadi.Application.Persistence;
using Angadi.Application.Platform;
using Angadi.Infrastructure.Billing;
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
        services.AddSingleton(_ => new AngadiDb(Path.Combine(options.DataDirectory, "angadi.db")));
        services.AddSingleton<IUnitOfWork>(sp => sp.GetRequiredService<AngadiDb>());

        services.AddSingleton<IItemRepository, ItemRepository>();
        services.AddSingleton<IBillRepository, BillRepository>();
        services.AddSingleton<ICustomerRepository, CustomerRepository>();
        services.AddSingleton<IStockLedger, StockLedger>();
        services.AddSingleton<IOutbox, Outbox>();
        services.AddSingleton<IShopProfileRepository, ShopProfileRepository>();
        services.AddSingleton<ISeedService, SeedService>();

        services.AddSingleton<IBackupService>(sp => new BackupService(
            sp.GetRequiredService<AngadiDb>(), sp.GetRequiredService<IClock>(),
            Path.Combine(options.CacheDirectory, "backups"), sp.GetRequiredService<ILogger<BackupService>>()));

        services.AddSingleton<IReceiptRenderer>(sp => new ReceiptRenderer(
            options.ReceiptFonts, Path.Combine(options.CacheDirectory, "receipts"),
            sp.GetRequiredService<ILocalizer>(), sp.GetRequiredService<ILogger<ReceiptRenderer>>()));

        // C2: never-Pro stub until the Play Billing adapter lands; the platform layer may replace it.
        services.AddSingleton<ISubscriptionService, StubSubscriptionService>();

        services.AddSingleton(new LogFiles(Path.Combine(options.DataDirectory, "logs")));
        return services;
    }
}

internal sealed class SystemClock : IClock
{
    public DateTimeOffset Now => DateTimeOffset.Now;
    public DateOnly Today => DateOnly.FromDateTime(DateTimeOffset.Now.DateTime);
}
