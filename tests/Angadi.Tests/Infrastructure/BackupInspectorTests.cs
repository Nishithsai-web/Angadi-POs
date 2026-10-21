using Angadi.Application.Billing;
using Angadi.Application.Platform;
using Angadi.Domain.Billing;
using Angadi.Tests.Application;
using Microsoft.Extensions.DependencyInjection;

namespace Angadi.Tests.Infrastructure;

/// <summary>
/// SEC-6: before a restore swaps anything, the owner sees what the backup actually holds —
/// bill/customer/item counts and the newest bill's date. A file that is not an Angadi backup
/// inspects as null, so the restore screen can reject it before any swap is attempted.
/// </summary>
public class BackupInspectorTests
{
    [Fact]
    public async Task Inspect_ARealBackup_ReportsCountsAndNewestBill()
    {
        using var h = new AppHost();
        await h.SeedShopAsync();
        var rice = await h.AddItemAsync("బియ్యం", 6000);
        var done = await h.Sender.Send(new FinalizeBillCommand(AppHost.Draft(TenderType.Cash, (rice, 1000))));
        Assert.True(done.IsSuccess, done.Error.Code);

        var backup = await h.Services.GetRequiredService<IBackupService>().ExportAsync();
        Assert.True(backup.IsSuccess, backup.Error.Code);

        var info = h.Services.GetRequiredService<IBackupInspector>().Inspect(backup.Value);
        Assert.NotNull(info);
        Assert.Equal(1, info.BillCount);
        Assert.Equal(1, info.ItemCount);
        Assert.Equal(0, info.CustomerCount);
        Assert.NotNull(info.NewestBillAt);
    }

    [Fact]
    public async Task Inspect_ANonBackupFile_ReturnsNull()
    {
        using var h = new AppHost();
        var junk = Path.Combine(h.Folder, "not-a-backup.db");
        await File.WriteAllTextAsync(junk, "this is not a database");

        var info = h.Services.GetRequiredService<IBackupInspector>().Inspect(junk);
        Assert.Null(info);
    }
}
