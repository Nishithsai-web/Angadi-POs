using Angadi.Application.Platform;
using Angadi.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace Angadi.Tests;

/// <summary>Controllable clock in IST, starting Monday 19 Oct 2026 10:00.</summary>
public sealed class FakeClock : IClock
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 19, 10, 0, 0, TimeSpan.FromHours(5.5));
    public DateOnly Today => DateOnly.FromDateTime(Now.DateTime);
    public void Advance(TimeSpan by) => Now = Now.Add(by);
}

/// <summary>A real SQLite database in a temp folder, deleted on dispose.</summary>
public sealed class TempDb : IDisposable
{
    public string Folder { get; } = Path.Combine(Path.GetTempPath(), "angadi-tests", Guid.NewGuid().ToString("N"));
    public FakeClock Clock { get; } = new();
    public AngadiDb Db { get; }

    internal ItemRepository Items { get; }
    internal BillRepository Bills { get; }
    internal CustomerRepository Customers { get; }
    internal StockLedger Ledger { get; }
    internal Outbox Outbox { get; }
    internal ShopProfileRepository Shop { get; }

    public TempDb()
    {
        Directory.CreateDirectory(Folder);
        Db = new AngadiDb(Path.Combine(Folder, "angadi.db"));
        Items = new ItemRepository(Db, Clock);
        Bills = new BillRepository(Db, Clock);
        Customers = new CustomerRepository(Db, Clock);
        Ledger = new StockLedger(Db, Clock);
        Outbox = new Outbox(Db, Clock);
        Shop = new ShopProfileRepository(Db, Clock);
    }

    public static NullLogger<T> Log<T>() => NullLogger<T>.Instance;

    public void Dispose()
    {
        Db.Dispose();
        try { Directory.Delete(Folder, recursive: true); } catch (IOException) { }
    }
}
