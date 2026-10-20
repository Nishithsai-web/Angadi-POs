using Angadi.Application.Persistence;
using SQLite;

namespace Angadi.Infrastructure.Persistence;

/// <summary>
/// The one SQLite connection. All access is serialised through a single gate (single-user,
/// on-device app). Inside <see cref="ExecuteAsync{T}"/> the current async flow owns the gate, and
/// repository calls run inline in the open transaction.
/// </summary>
public sealed class AngadiDb : IUnitOfWork, IDisposable
{
    public const int SchemaVersion = 1;
    public const string AppMarker = "angadi";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly AsyncLocal<bool> _inTransaction = new();
    private SQLiteConnection _conn;

    public AngadiDb(string databasePath)
    {
        DatabasePath = databasePath;
        _conn = Open(databasePath);
    }

    public string DatabasePath { get; }

    private static SQLiteConnection Open(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var conn = new SQLiteConnection(path,
            SQLiteOpenFlags.ReadWrite | SQLiteOpenFlags.Create | SQLiteOpenFlags.FullMutex);
        conn.ExecuteScalar<string>("PRAGMA journal_mode=WAL");
        conn.Execute("PRAGMA foreign_keys=ON");
        Migrate(conn);
        return conn;
    }

    /// <summary>sqlite-net CreateTable adds missing columns, so additive migrations are automatic.</summary>
    private static void Migrate(SQLiteConnection c)
    {
        c.CreateTable<ItemRow>();
        c.CreateTable<BillRow>();
        c.CreateTable<BillLineRow>();
        c.CreateTable<CustomerRow>();
        c.CreateTable<DuesEntryRow>();
        c.CreateTable<StockMoveRow>();
        c.CreateTable<OutboxRow>();
        c.CreateTable<ShopProfileRow>();
        c.CreateTable<MetaRow>();

        c.InsertOrReplace(new MetaRow { Key = "app", Value = AppMarker });
        var current = c.Find<MetaRow>("schema_version");
        if (current is null || int.Parse(current.Value, System.Globalization.CultureInfo.InvariantCulture) < SchemaVersion)
            c.InsertOrReplace(new MetaRow { Key = "schema_version", Value = SchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) });
    }

    /// <summary>Runs a read or write; joins the open transaction when called inside ExecuteAsync.</summary>
    public async Task<T> RunAsync<T>(Func<SQLiteConnection, T> action, CancellationToken ct = default)
    {
        if (_inTransaction.Value) return action(_conn);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return action(_conn); }
        finally { _gate.Release(); }
    }

    public Task RunAsync(Action<SQLiteConnection> action, CancellationToken ct = default)
        => RunAsync(c => { action(c); return true; }, ct);

    public async Task<T> ExecuteAsync<T>(Func<Task<T>> work, Func<T, bool> shouldCommit, CancellationToken ct = default)
    {
        if (_inTransaction.Value) return await work().ConfigureAwait(false);   // nested command: join outer

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        _inTransaction.Value = true;   // set before awaiting work, so it flows into it
        try
        {
            _conn.BeginTransaction();
            T result;
            try
            {
                result = await work().ConfigureAwait(false);
            }
            catch
            {
                _conn.Rollback();
                throw;
            }

            if (shouldCommit(result)) _conn.Commit();
            else _conn.Rollback();
            return result;
        }
        finally
        {
            _inTransaction.Value = false;
            _gate.Release();
        }
    }

    /// <summary>Flushes WAL into the main file so a plain file copy is a complete backup (FR-37).</summary>
    public Task CheckpointAsync(CancellationToken ct = default)
        => RunAsync(c => c.ExecuteScalar<int>("PRAGMA wal_checkpoint(TRUNCATE)"), ct);

    /// <summary>FR-37 restore: closes, swaps the file, reopens and migrates. Caller validated the file.</summary>
    /// <summary>
    /// H3: atomic swap. <paramref name="validatedPath"/> must be a validated copy on the same volume; it is moved
    /// into place with File.Replace (the live file is kept as ".pre-restore"). Any failure puts the live file back.
    /// </summary>
    public async Task ReplaceWithAsync(string validatedPath, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        var safety = DatabasePath + ".pre-restore";
        try
        {
            _conn.ExecuteScalar<int>("PRAGMA wal_checkpoint(TRUNCATE)");
            _conn.Close();   // closing the last connection checkpoints the WAL
            foreach (var suffix in new[] { "-wal", "-shm" })
                if (File.Exists(DatabasePath + suffix)) File.Delete(DatabasePath + suffix);
            try
            {
                File.Replace(validatedPath, DatabasePath, safety, ignoreMetadataErrors: true);
            }
            catch (Exception) when (File.Exists(safety) && !File.Exists(DatabasePath))
            {
                File.Copy(safety, DatabasePath);   // replace failed mid-way: put the live data back
                throw;
            }
        }
        finally
        {
            _conn = Open(DatabasePath);
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _conn.Close();
        _gate.Dispose();
    }
}
