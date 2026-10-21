using Angadi.Application.Billing;
using Angadi.Application.Catalog;
using Angadi.Domain.Billing;
using Angadi.Domain.Events;
using Angadi.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Angadi.Tests.Application;

/// <summary>
/// M-11: v1 never drains the Outbox, so without a retention sweep it grows for the life of the install.
/// The sweep (startup, via PruneOutboxCommand) deletes rows older than 90 days and never touches newer ones.
/// </summary>
public class OutboxRetentionTests : IDisposable
{
    private readonly TempDb _t = new();
    public void Dispose() => _t.Dispose();

    private Task<int> CountAsync() => _t.Db.RunAsync(c => c.ExecuteScalar<int>("SELECT COUNT(*) FROM Outbox"));

    [Fact]
    public async Task Prune_RemovesOnlyRowsOlderThanTheCutoff()
    {
        _t.Clock.Now = new DateTimeOffset(2026, 7, 1, 10, 0, 0, TimeSpan.FromHours(5.5));   // 110 days before "now"
        await _t.Outbox.AppendAsync("Bill", new BillFinalized(Guid.NewGuid(), _t.Clock.Now, 100));
        _t.Clock.Now = new DateTimeOffset(2026, 10, 19, 10, 0, 0, TimeSpan.FromHours(5.5));
        await _t.Outbox.AppendAsync("Bill", new BillFinalized(Guid.NewGuid(), _t.Clock.Now, 200));
        Assert.Equal(2, await CountAsync());

        var removed = await _t.Outbox.PruneOlderThanAsync(_t.Clock.Now.AddDays(-90));

        Assert.Equal(1, removed);
        Assert.Equal(1, await CountAsync());
    }

    [Fact]
    public async Task Prune_KeepsARowExactlyAtTheCutoff()
    {
        var now = _t.Clock.Now;
        _t.Clock.Now = now.AddDays(-90);
        await _t.Outbox.AppendAsync("Bill", new BillFinalized(Guid.NewGuid(), _t.Clock.Now, 100));
        _t.Clock.Now = now;

        Assert.Equal(0, await _t.Outbox.PruneOlderThanAsync(now.AddDays(-90)));
        Assert.Equal(1, await CountAsync());
    }

    [Fact]
    public async Task StartupSweep_PrunesYesterdaysHistory_AndKeepsTodaysBill()
    {
        using var h = new AppHost();
        await h.SeedShopAsync();
        var rice = await h.AddItemAsync("బియ్యం", 6000, stockMilli: 100_000);

        var first = AppHost.Draft(TenderType.Cash, (rice, 1000));
        Assert.True((await h.Sender.Send(new FinalizeBillCommand(first))).IsSuccess);

        h.Clock.Advance(TimeSpan.FromDays(100));                       // the first bill's row is now 100 days old
        var second = AppHost.Draft(TenderType.Cash, (rice, 1000));
        Assert.True((await h.Sender.Send(new FinalizeBillCommand(second))).IsSuccess);

        Assert.True((await h.Sender.Send(new PruneOutboxCommand())).IsSuccess);

        var db = h.Services.GetRequiredService<AngadiDb>();
        var finalized = await db.RunAsync(c => c.ExecuteScalar<int>("SELECT COUNT(*) FROM Outbox WHERE EventType = 'BillFinalized'"));
        Assert.Equal(1, finalized);                                    // only today's bill's row survives
    }
}
