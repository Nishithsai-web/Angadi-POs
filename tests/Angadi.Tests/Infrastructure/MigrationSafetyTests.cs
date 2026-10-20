using Angadi.Infrastructure.Persistence;

namespace Angadi.Tests.Infrastructure;

/// <summary>Release safety: an app update that migrates an older database first keeps a full copy.</summary>
public class MigrationSafetyTests
{
    [Fact]
    public async Task OlderSchema_IsCopiedToPreUpdate_BeforeMigrating_CurrentSchemaIsNot()
    {
        var folder = Path.Combine(Path.GetTempPath(), "angadi-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "angadi.db");
        try
        {
            using (var db = new AngadiDb(path))
                await db.RunAsync(c => c.Execute("UPDATE Meta SET Value = '1' WHERE Key = 'schema_version'"));

            using (var reopened = new AngadiDb(path))
            {
                Assert.True(File.Exists(path + ".pre-update"));
                var version = await reopened.RunAsync(c => c.ExecuteScalar<string>("SELECT Value FROM Meta WHERE Key = 'schema_version'"));
                Assert.Equal(AngadiDb.SchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), version);
            }

            File.Delete(path + ".pre-update");
            using (new AngadiDb(path)) { }
            Assert.False(File.Exists(path + ".pre-update"));   // already current: nothing to protect
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task UnparseableSchemaVersion_RecoversInsteadOfCrashing()
    {
        var folder = Path.Combine(Path.GetTempPath(), "angadi-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "angadi.db");
        try
        {
            using (var db = new AngadiDb(path))
                await db.RunAsync(c => c.Execute("UPDATE Meta SET Value = 'abc' WHERE Key = 'schema_version'"));

            // DATA-1a: 'abc' used to throw FormatException out of the constructor — the app crashed on a
            // damaged database instead of recovering it. Now startup recovery handles it: no throw, a
            // recovery report, and a usable database at the current schema.
            using var reopened = new AngadiDb(path);
            Assert.NotNull(reopened.TakeRecovery());
            var version = await reopened.RunAsync(c => c.ExecuteScalar<string>("SELECT Value FROM Meta WHERE Key = 'schema_version'"));
            Assert.Equal(AngadiDb.SchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), version);
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }
    }
}
