using System.Diagnostics;
using Angadi.Infrastructure.Persistence;

namespace Angadi.Tests.Infrastructure;

/// <summary>
/// PERF-1: big reads (reports over a large inventory) and writes (saving a bill) must not wait for each other, and a
/// read always sees a consistent, committed picture.
/// </summary>
public sealed class ConcurrentAccessTests : IDisposable
{
    private readonly TempDb _t = new();
    public void Dispose() => _t.Dispose();

    private Task Write(string key) =>
        _t.Db.RunAsync(c => c.Execute("INSERT OR REPLACE INTO Meta (Key, Value) VALUES (?, '1')", key));

    [Fact]
    public async Task ASlowReport_DoesNotHoldUpASave()
    {
        using var reading = new ManualResetEventSlim();
        var report = _t.Db.ReadAsync(c =>
        {
            reading.Set();
            Thread.Sleep(6000);   // a heavy report (long, so a loaded CI machine's stalls can't look like blocking)
            return c.ExecuteScalar<int>("SELECT COUNT(*) FROM Meta");
        });
        Assert.True(reading.Wait(TimeSpan.FromSeconds(10)));

        var sw = Stopwatch.StartNew();
        await Write("perf.save");   // the bill save, while the report is still running
        sw.Stop();

        Assert.False(report.IsCompleted, "the report should still be running");
        Assert.True(sw.ElapsedMilliseconds < 3000, $"the save waited {sw.ElapsedMilliseconds} ms for the report");
        await report;
    }

    [Fact]
    public async Task ASlowSave_DoesNotHoldUpAReport()
    {
        using var writing = new ManualResetEventSlim();
        var save = _t.Db.ExecuteAsync(async () =>
        {
            await _t.Db.RunAsync(c => c.Execute("INSERT OR REPLACE INTO Meta (Key, Value) VALUES ('perf.tx', '1')"));
            writing.Set();
            await Task.Delay(6000);   // a long transaction
            return true;
        }, ok => ok);
        Assert.True(writing.Wait(TimeSpan.FromSeconds(10)));

        var sw = Stopwatch.StartNew();
        var seenDuring = await _t.Db.ReadAsync(c => c.ExecuteScalar<int>("SELECT COUNT(*) FROM Meta WHERE Key = 'perf.tx'"));
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 3000, $"the report waited {sw.ElapsedMilliseconds} ms for the save");
        Assert.Equal(0, seenDuring);   // uncommitted work is never visible to a report
        await save;
        Assert.Equal(1, await _t.Db.ReadAsync(c => c.ExecuteScalar<int>("SELECT COUNT(*) FROM Meta WHERE Key = 'perf.tx'")));
    }

    [Fact]
    public async Task ReadsInsideACommand_SeeTheCommandsOwnChanges()
    {
        var seen = await _t.Db.ExecuteAsync(async () =>
        {
            await _t.Db.RunAsync(c => c.Execute("INSERT OR REPLACE INTO Meta (Key, Value) VALUES ('perf.own', '1')"));
            return await _t.Db.ReadAsync(c => c.ExecuteScalar<int>("SELECT COUNT(*) FROM Meta WHERE Key = 'perf.own'"));
        }, _ => true);

        Assert.Equal(1, seen);
    }

    [Fact]
    public async Task ManyParallelReadsAndWrites_StayConsistent()
    {
        var writes = Enumerable.Range(0, 200).Select(i => Write("perf.k" + i)).ToList();
        var reads = Enumerable.Range(0, 200).Select(_ =>
            _t.Db.ReadAsync(c => c.ExecuteScalar<int>("SELECT COUNT(*) FROM Meta WHERE Key LIKE 'perf.k%'"))).ToList();
        await Task.WhenAll(writes);
        var counts = await Task.WhenAll(reads);

        Assert.All(counts, n => Assert.InRange(n, 0, 200));
        Assert.Equal(200, await _t.Db.ReadAsync(c => c.ExecuteScalar<int>("SELECT COUNT(*) FROM Meta WHERE Key LIKE 'perf.k%'")));
    }
}
