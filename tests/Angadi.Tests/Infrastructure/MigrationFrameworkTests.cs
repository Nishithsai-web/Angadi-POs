using Angadi.Infrastructure.Persistence;
using SQLite;

namespace Angadi.Tests.Infrastructure;

/// <summary>DATA-1: ordered, once-only, all-or-nothing schema upgrades.</summary>
public sealed class MigrationFrameworkTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "angadi-migrate", Guid.NewGuid().ToString("N"));
    private string DbPath => Path.Combine(_folder, "angadi.db");

    public MigrationFrameworkTests() => Directory.CreateDirectory(_folder);
    public void Dispose() { try { Directory.Delete(_folder, recursive: true); } catch (IOException) { } }

    private SQLiteConnection OldDatabase(int version)
    {
        var c = new SQLiteConnection(DbPath);
        c.CreateTable<MetaRow>();
        c.InsertOrReplace(new MetaRow { Key = "schema_version", Value = version.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        return c;
    }

    private static string? Meta(SQLiteConnection c, string key) => c.Find<MetaRow>(key)?.Value;
    private static bool HasTable(SQLiteConnection c, string name) => c.ExecuteScalar<int>("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=?", name) > 0;

    [Fact]
    public void TheHistory_IsComplete_Ascending_AndEndsAtTheCurrentVersion()
    {
        var versions = Migrations.Steps.Select(s => s.Version).ToList();
        Assert.Equal(versions.OrderBy(v => v), versions);
        Assert.Equal(versions.Count, versions.Distinct().Count());
        Assert.Equal(AngadiDb.SchemaVersion, versions[^1]);
        Assert.Equal(Enumerable.Range(2, AngadiDb.SchemaVersion - 1), versions);   // no gaps from 2
    }

    [Fact]
    public void AFailingStep_RollsBackTheWholeUpgrade_AndTheNextStartRetries()
    {
        using (var c = OldDatabase(5))
        {
            MigrationStep[] broken =
            [
                new(6, "marks", x => x.InsertOrReplace(new MetaRow { Key = "test.step6", Value = "ran" })),
                new(7, "fails", _ => throw new InvalidOperationException("boom")),
            ];
            Assert.Throws<InvalidOperationException>(() => AngadiDb.Migrate(c, broken));
            Assert.Equal("5", Meta(c, "schema_version"));   // never stamped with a version it did not reach
            Assert.Null(Meta(c, "test.step6"));             // the earlier step's work was undone too
            Assert.False(HasTable(c, "Worker"));            // and so were the new tables
        }

        var runs = 0;
        using (var c = new SQLiteConnection(DbPath))
        {
            MigrationStep[] good = [new(6, "marks", _ => runs++), new(7, "ok", _ => runs++)];
            AngadiDb.Migrate(c, good);
            Assert.Equal(AngadiDb.SchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), Meta(c, "schema_version"));
            Assert.True(HasTable(c, "Worker"));
            AngadiDb.Migrate(c, good);                      // already up to date: nothing runs again
        }
        Assert.Equal(2, runs);
    }

    [Fact]
    public void OnlyStepsAboveTheStoredVersionRun()
    {
        var ran = new List<int>();
        using var c = OldDatabase(6);
        AngadiDb.Migrate(c, [new(5, "old", _ => ran.Add(5)), new(6, "old", _ => ran.Add(6)), new(7, "new", _ => ran.Add(7))]);
        Assert.Equal(new[] { 7 }, ran);
    }

    [Fact]
    public void ABrandNewFile_RunsNoSteps_AndIsStampedCurrent()
    {
        var ran = 0;
        using var c = new SQLiteConnection(DbPath);
        AngadiDb.Migrate(c, [new(7, "x", _ => ran++)]);
        Assert.Equal(0, ran);
        Assert.Equal(AngadiDb.SchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), Meta(c, "schema_version"));
    }
}
