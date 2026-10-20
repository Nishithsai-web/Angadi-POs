using Angadi.Infrastructure.Persistence;
using SQLite;

namespace Angadi.Tests.Infrastructure;

/// <summary>
/// Automatic safety copies and startup recovery: a damaged database file must never stop the app, and must never
/// silently lose more than the work since the last safety copy.
/// </summary>
public sealed class DataSafetyTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "angadi-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeClock _clock = new();
    private string DbPath => Path.Combine(_folder, "angadi.db");
    private string Snapshots => Path.Combine(_folder, "snapshots");

    public DataSafetyTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private DataSafety Safety(AngadiDb db) => new(db, _clock, TempDb.Log<DataSafety>());

    private static Task Mark(AngadiDb db, string key) =>
        db.RunAsync(c => c.Execute("INSERT OR REPLACE INTO Meta (Key, Value) VALUES (?, '1')", key));

    private static Task<bool> HasMark(AngadiDb db, string key) =>
        db.RunAsync(c => c.ExecuteScalar<int>("SELECT COUNT(*) FROM Meta WHERE Key = ?", key) == 1);

    private void Damage() => File.WriteAllBytes(DbPath, Enumerable.Repeat((byte)0x5A, 64 * 1024).ToArray());

    [Fact]
    public void HealthyDatabase_OpensWithoutRecovery()
    {
        using var db = new AngadiDb(DbPath, Snapshots);
        Assert.Null(Safety(db).TakeRecoveryReport());
    }

    [Fact]
    public async Task DamagedDatabase_IsMovedAside_AndNewestSafetyCopyPutBack()
    {
        using (var db = new AngadiDb(DbPath, Snapshots))
        {
            await Mark(db, "test.before-copy");
            Assert.True((await Safety(db).SnapshotAsync()).IsSuccess);
        }
        Damage();

        using var reopened = new AngadiDb(DbPath, Snapshots);
        var report = Safety(reopened).TakeRecoveryReport();

        Assert.NotNull(report);
        Assert.NotNull(report.RestoredFrom);
        Assert.True(await HasMark(reopened, "test.before-copy"));                         // the data came back
        Assert.Single(Directory.GetFiles(_folder, "angadi.db.damaged-*"));                // the damaged file is kept
        Assert.Null(Safety(reopened).TakeRecoveryReport());                               // told once
        await Mark(reopened, "test.after-recovery");                                      // and the app works
        Assert.True(await HasMark(reopened, "test.after-recovery"));
    }

    [Fact]
    public async Task DamagedDatabase_WithNoSafetyCopy_StartsEmpty_AndSaysSo()
    {
        using (var db = new AngadiDb(DbPath, Snapshots)) await Mark(db, "test.lost");
        Damage();

        using var reopened = new AngadiDb(DbPath, Snapshots);
        var report = Safety(reopened).TakeRecoveryReport();

        Assert.NotNull(report);
        Assert.Null(report.RestoredFrom);
        Assert.False(await HasMark(reopened, "test.lost"));
        Assert.Single(Directory.GetFiles(_folder, "angadi.db.damaged-*"));   // still there for a manual rescue
    }

    [Fact]
    public async Task Recovery_SkipsABadSafetyCopy_AndUsesTheNextGoodOne()
    {
        using (var db = new AngadiDb(DbPath, Snapshots))
        {
            await Mark(db, "test.good");
            Assert.True((await Safety(db).SnapshotAsync()).IsSuccess);
        }
        var bad = Path.Combine(Snapshots, "angadi-20991231.db");
        File.WriteAllText(bad, "garbage");
        File.SetLastWriteTimeUtc(bad, DateTime.UtcNow.AddHours(1));   // newest, and useless
        Damage();

        using var reopened = new AngadiDb(DbPath, Snapshots);

        Assert.NotNull(Safety(reopened).TakeRecoveryReport()?.RestoredFrom);
        Assert.True(await HasMark(reopened, "test.good"));
    }

    [Fact]
    public async Task Snapshot_IsSkippedWhenNothingChanged_AndRefreshedAfterAChange()
    {
        using var db = new AngadiDb(DbPath, Snapshots);
        var safety = Safety(db);
        Assert.True((await safety.SnapshotAsync()).IsSuccess);
        var file = Assert.Single(Directory.GetFiles(Snapshots, "angadi-*.db"));
        File.SetLastWriteTimeUtc(file, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.True((await safety.SnapshotAsync()).IsSuccess);
        Assert.Equal(2020, File.GetLastWriteTimeUtc(file).Year);   // untouched: no change since

        await Mark(db, "test.change");
        Assert.True((await safety.SnapshotAsync()).IsSuccess);
        Assert.NotEqual(2020, File.GetLastWriteTimeUtc(file).Year);
        Assert.Empty(Directory.GetFiles(Snapshots, "*.tmp"));
    }

    [Fact]
    public async Task Snapshots_OnePerDay_NewestThreeKept_EachAStandaloneValidDatabase()
    {
        using var db = new AngadiDb(DbPath, Snapshots);
        var safety = Safety(db);
        for (var day = 0; day < 5; day++)
        {
            await Mark(db, "test.day" + day);
            Assert.True((await safety.SnapshotAsync()).IsSuccess);
            _clock.Advance(TimeSpan.FromDays(1));
        }

        var files = Directory.GetFiles(Snapshots, "angadi-*.db").Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(DataSafety.KeepDays, files.Length);
        Assert.EndsWith("angadi-20261023.db", files[^1], StringComparison.Ordinal);
        foreach (var f in files)
        {
            Assert.False(File.Exists(f + "-wal"));
            Assert.True(DatabaseRecovery.IsGoodDatabase(f, out var why), why);
        }
    }

    [Fact]
    public async Task FullDisk_DuringAWrite_BecomesStorageFullException()
    {
        using var db = new AngadiDb(DbPath, Snapshots);
        // Cap the file at its current size: the next growth fails exactly like a full phone.
        var pages = await db.RunAsync(c => c.ExecuteScalar<long>("PRAGMA page_count"));
        await db.RunAsync(c => c.ExecuteScalar<long>("PRAGMA max_page_count = " + pages));

        await Assert.ThrowsAsync<Angadi.Application.Persistence.StorageFullException>(() =>
            db.RunAsync(c =>
            {
                for (var i = 0; i < 2_000; i++)
                    c.Execute("INSERT OR REPLACE INTO Meta (Key, Value) VALUES (?, ?)", "fill." + i, new string('x', 500));
            }));
    }

    [Fact]
    public void IsDamage_OnlyForCorruptionAndNonDatabaseFiles()
    {
        Assert.True(AngadiDb.IsDamage(SQLiteException.New(SQLite3.Result.Corrupt, "malformed")));
        Assert.True(AngadiDb.IsDamage(SQLiteException.New(SQLite3.Result.NonDBFile, "not a database")));
        Assert.False(AngadiDb.IsDamage(SQLiteException.New(SQLite3.Result.Full, "full")));        // never move a good file aside
        Assert.False(AngadiDb.IsDamage(SQLiteException.New(SQLite3.Result.Busy, "locked")));
        Assert.False(AngadiDb.IsDamage(SQLiteException.New(SQLite3.Result.CannotOpen, "permission")));
    }
}
