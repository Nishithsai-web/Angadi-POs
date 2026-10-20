using Angadi.Application.Persistence;
using Angadi.Application.Platform;
using SQLite;

namespace Angadi.Infrastructure.Persistence;

/// <summary>
/// The one SQLite connection. All access is serialised through a single gate (single-user,
/// on-device app). Inside <see cref="ExecuteAsync{T}"/> the current async flow owns the gate, and
/// repository calls run inline in the open transaction.
/// </summary>
public sealed class AngadiDb : IUnitOfWork, IDisposable
{
    /// <summary>2: customer account columns + Note table. 3: shop's own UPI QR. Bump on every schema change.</summary>
    public const int SchemaVersion = 3;
    public const string AppMarker = "angadi";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly AsyncLocal<bool> _inTransaction = new();
    private SQLiteConnection _conn;

    /// <param name="snapshotDirectory">Where the automatic safety copies live (default: "snapshots" beside the file).</param>
    public AngadiDb(string databasePath, string? snapshotDirectory = null)
    {
        DatabasePath = databasePath;
        SnapshotDirectory = snapshotDirectory ?? Path.Combine(Path.GetDirectoryName(databasePath) ?? ".", "snapshots");
        try
        {
            _conn = OpenVerified(databasePath);
        }
        catch (DamagedDatabaseException ex)
        {
            // A damaged file must never stop the counter: move it aside (kept for support), put back the newest good
            // copy, and tell the shopkeeper once (IDataSafety.TakeRecoveryReport).
            _recovery = DatabaseRecovery.Recover(databasePath, SnapshotDirectory, ex.Message, DateTimeOffset.Now);
            _conn = OpenVerified(databasePath);
        }
    }

    public string DatabasePath { get; }
    public string SnapshotDirectory { get; }

    private DataRecoveryReport? _recovery;

    /// <summary>What startup recovery did, handed out once.</summary>
    public DataRecoveryReport? TakeRecovery() => Interlocked.Exchange(ref _recovery, null);

    /// <summary>Opens, migrates and runs SQLite's quick_check. Corruption becomes <see cref="DamagedDatabaseException"/>.</summary>
    private static SQLiteConnection OpenVerified(string path)
    {
        SQLiteConnection? conn = null;
        try
        {
            conn = Open(path);
            var check = conn.ExecuteScalar<string>("PRAGMA quick_check");
            if (!string.Equals(check, "ok", StringComparison.OrdinalIgnoreCase))
                throw new DamagedDatabaseException("quick_check: " + check);
            return conn;
        }
        catch (SQLiteException ex) when (IsDamage(ex))
        {
            conn?.Close();
            throw new DamagedDatabaseException(ex.Result + ": " + ex.Message, ex);
        }
        catch (DamagedDatabaseException)
        {
            conn?.Close();
            throw;
        }
    }

    /// <summary>Only real damage moves the file aside — never a full disk, a permission problem or a lock.</summary>
    internal static bool IsDamage(SQLiteException ex) => ex.Result is SQLite3.Result.Corrupt or SQLite3.Result.NonDBFile;

    private static SQLiteConnection Open(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        BackupBeforeMigration(path);

        var conn = new SQLiteConnection(path,
            SQLiteOpenFlags.ReadWrite | SQLiteOpenFlags.Create | SQLiteOpenFlags.FullMutex);
        try
        {
            conn.ExecuteScalar<string>("PRAGMA journal_mode=WAL");
            conn.Execute("PRAGMA foreign_keys=ON");
            conn.BusyTimeout = TimeSpan.FromSeconds(5);   // LOW: a backup checkpoint racing a bill waits instead of failing
            Migrate(conn);
            return conn;
        }
        catch
        {
            conn.Close();
            throw;
        }
    }

    /// <summary>
    /// Release safety (deployment-and-rollback.md): before an app update migrates an existing database to a newer
    /// schema, keep a full copy as "&lt;db&gt;.pre-update". A failed or bad migration can then be rolled back by hand.
    /// </summary>
    private static void BackupBeforeMigration(string path)
    {
        if (!File.Exists(path)) return;
        using (var probe = new SQLiteConnection(path, SQLiteOpenFlags.ReadWrite | SQLiteOpenFlags.FullMutex))
        {
            int stored;
            try { stored = probe.ExecuteScalar<int>("SELECT CAST(Value AS INTEGER) FROM Meta WHERE Key = 'schema_version'"); }
            catch (SQLiteException) { return; }   // not an Angadi database yet
            if (stored >= SchemaVersion) return;
            probe.ExecuteScalar<int>("PRAGMA wal_checkpoint(TRUNCATE)");   // everything into the main file
        }
        File.Copy(path, path + ".pre-update", overwrite: true);
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
        c.CreateTable<NoteRow>();

        c.InsertOrReplace(new MetaRow { Key = "app", Value = AppMarker });
        var current = c.Find<MetaRow>("schema_version");
        if (current is null || int.Parse(current.Value, System.Globalization.CultureInfo.InvariantCulture) < SchemaVersion)
            c.InsertOrReplace(new MetaRow { Key = "schema_version", Value = SchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) });
    }

    /// <summary>Runs a read or write; joins the open transaction when called inside ExecuteAsync.</summary>
    public async Task<T> RunAsync<T>(Func<SQLiteConnection, T> action, CancellationToken ct = default)
    {
        if (_inTransaction.Value) return Guarded(action);
        await EnterAsync(ct).ConfigureAwait(false);
        try { return Guarded(action); }
        finally { _gate.Release(); }
    }

    /// <summary>A full disk surfaces as <see cref="StorageFullException"/> ("phone storage is full"), not a driver error.</summary>
    private T Guarded<T>(Func<SQLiteConnection, T> action)
    {
        try { return action(_conn); }
        catch (SQLiteException ex) when (ex.Result is SQLite3.Result.Full) { throw new StorageFullException("database or disk is full", ex); }
    }

    public Task RunAsync(Action<SQLiteConnection> action, CancellationToken ct = default)
        => RunAsync(c => { action(c); return true; }, ct);

    public async Task<T> ExecuteAsync<T>(Func<Task<T>> work, Func<T, bool> shouldCommit, CancellationToken ct = default)
    {
        if (_inTransaction.Value) return await work().ConfigureAwait(false);   // nested command: join outer

        await EnterAsync(ct).ConfigureAwait(false);
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

            if (shouldCommit(result))
            {
                try { _conn.Commit(); }
                catch (SQLiteException ex) when (ex.Result is SQLite3.Result.Full)
                {
                    _conn.Rollback();
                    throw new StorageFullException("database or disk is full", ex);
                }
            }
            else _conn.Rollback();
            return result;
        }
        finally
        {
            _inTransaction.Value = false;
            _gate.Release();
        }
    }

    /// <summary>
    /// The single connection's lock. Bounded: if something ever holds it this long, fail loudly (logged, a message
    /// on screen) instead of freezing every screen of the app — the failure the owner hit on the emulator.
    /// </summary>
    public static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(30);

    private async Task EnterAsync(CancellationToken ct)
    {
        if (!await _gate.WaitAsync(LockTimeout, ct).ConfigureAwait(false))
            throw new DatabaseBusyException("Database busy: lock not released within " + LockTimeout.TotalSeconds + " s");
    }

    /// <summary>Flushes WAL into the main file so a plain file copy is a complete backup (FR-37).</summary>
    public Task CheckpointAsync(CancellationToken ct = default)
        => RunAsync(c => c.ExecuteScalar<int>("PRAGMA wal_checkpoint(TRUNCATE)"), ct);

    /// <summary>
    /// H3: atomic swap. <paramref name="validatedPath"/> must be a validated copy on the same volume; it is moved
    /// into place with File.Replace (the live file is kept as ".pre-restore"). Any failure puts the live file back.
    /// </summary>
    public async Task ReplaceWithAsync(string validatedPath, CancellationToken ct = default)
    {
        await EnterAsync(ct).ConfigureAwait(false);
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
            try
            {
                // FIX(F6, 2026-10-20, FIX-2): Flaw: the swapped-in file was reopened with a plain Open() —
                // a torn copy would limp on as a damaged live DB until something crashed far from the cause.
                // Fix: reopen verified (PRAGMA quick_check); on damage, run the constructor's recovery,
                // which adopts the .pre-restore file (the pre-swap live DB) when usable. Why: the swap is
                // the one moment damage can be caught exactly where it happened. Logged in docs/AGENT_FIX_LOG.md.
                try { _conn = OpenVerified(DatabasePath); }
                catch (DamagedDatabaseException)
                {
                    RecoverIfNeeded(DatabasePath);
                    _conn = OpenVerified(DatabasePath);
                }
            }
            finally { _gate.Release(); }
        }
    }

    public void Dispose()
    {
        _conn.Close();
        _gate.Dispose();
    }
}
