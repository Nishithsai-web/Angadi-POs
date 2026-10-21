using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Angadi.Application.Billing;
using Angadi.Application.Catalog;
using Angadi.Application.Customers;
using Angadi.Application.Insights;
using Angadi.Application.Inventory;
using Angadi.Application.Persistence;
using Angadi.Application.Reports;
using Angadi.Domain.Billing;
using Angadi.Domain.Common;
using Angadi.Domain.Customers;
using Angadi.Domain.Events;
using Angadi.Domain.Inventory;
using Angadi.Domain.Items;
using Angadi.Infrastructure.Persistence;
using Angadi.Tests.Application;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;
using static Angadi.Infrastructure.Persistence.Mapping;

namespace Angadi.Tests.Infrastructure;

/// <summary>
/// A busy shop's first year: 150 bills a day × 365 days (~4 lines each), 3,000 items, 800 customers, udhaar and
/// payments, a few voids. Built straight into the real database (one transaction), then every screen the shopkeeper
/// waits on is timed through the real Application + Infrastructure graph — twice: BEFORE (the indexes and code paths
/// older builds used, S4-1..S4-4) and AFTER (today's). Cheap Android phones are ~5–10× slower than CI, so the budgets
/// are set for the phone: counter actions under 50 ms, the counter's dues banner under 250 ms, reports under 1 s.
/// The timing table and the database size are printed with a "[ci-report] " prefix: tools/ci_tests.py turns those
/// lines into a CI notice, so the numbers are visible without the TRX artifact.
/// </summary>
public sealed class YearOfDataTests(ITestOutputHelper output) : IDisposable
{
    internal const int Days = 365;
    internal const int BillsPerDay = 150;
    internal const int ItemCount = 3000;
    internal const int CustomerCount = 800;

    // FIX(S4-6, 2026-10-21, S4): Flaw: the D2 budgets put the dues banner (one load as the counter opens, ~90-120 ms
    // on CI) under the per-keystroke counter budget of 50 ms, so the suite failed on a screen that is not per-keystroke,
    // and a temporary Assert.Fail made the whole test red by design. Fix: three budget classes by what the shopkeeper
    // waits on — per keystroke/scan/sale 50 ms, counter open 250 ms, reports 1 s — each a 5x+ margin over today's CI
    // medians, plus a "not slower than before" check (after ≤ 2 × before + 5 ms) for every timed operation. Why: a
    // budget must fail on a real regression (an N+1, a lost index), never on CI noise. Logged in docs/AGENT_FIX_LOG.md.
    private const double CounterBudgetMs = 50;          // per keystroke / scan / sale
    private const double CounterOpenBudgetMs = 250;     // once each time the counter page appears
    private const double ReportBudgetMs = 1000;
    private const double ReportsPageBudgetMs = 2500;    // the Reports screen loads eight reports in a row
    private const double SnapshotBudgetMs = 10_000;     // background, but it holds the database lock while copying
    private const double NotSlowerFactor = 2, NotSlowerSlackMs = 5;
    /// <summary>Regression guard on storage: live bytes per finished bill (all tables and indexes, outbox at 90 days).</summary>
    private const long MaxBytesPerBill = 3_200;
    private const long AutoBackupCapBytes = 25L * 1024 * 1024;   // Android Auto Backup quota per app

    private static readonly string[] TeluguBase = ["బియ్యం", "కంది పప్పు", "నూనె", "చక్కెర", "ఉప్పు", "పాలు", "సబ్బు", "టీ పొడి", "కారం", "పసుపు"];
    private static readonly string[] EnglishBase = ["Rice", "Toor dal", "Oil", "Sugar", "Salt", "Milk", "Soap", "Tea powder", "Chilli", "Turmeric"];
    private static readonly string[] TeluguBrand = ["సోనా", "గోల్డ్", "ప్రీమియం", "లోకల్"];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The indexes older builds had (sqlite-net names), recreated to time the BEFORE side.</summary>
    private static readonly string[] OldIndexSql =
    [
        "CREATE INDEX IF NOT EXISTS \"BillLine_ItemId\" ON \"BillLine\"(\"ItemId\")",
        "CREATE INDEX IF NOT EXISTS \"StockMove_BillId\" ON \"StockMove\"(\"BillId\")",
        "CREATE INDEX IF NOT EXISTS \"StockMove_ItemId\" ON \"StockMove\"(\"ItemId\")",
    ];

    private readonly AppHost _h = new();
    private readonly List<string> _rows = [];

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task OlderDatabase_DropsRetiredIndexes_AndLedgerTotalsUseTheCoveringIndex()
    {
        var folder = Path.Combine(Path.GetTempPath(), "angadi-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(folder, "angadi.db");
        try
        {
            using (var old = new AngadiDb(path))   // a database as older builds left it
                await old.RunAsync(c =>
                {
                    c.Execute($"DROP INDEX IF EXISTS \"{StockMoveRow.StockMoveItemDeltaIndex}\"");
                    foreach (var sql in OldIndexSql) c.Execute(sql);
                });

            using var db = new AngadiDb(path);
            var names = await db.RunAsync(c => c.QueryScalars<string>("SELECT name FROM sqlite_master WHERE type = 'index'"));
            foreach (var retired in AngadiDb.RetiredIndexes) Assert.DoesNotContain(retired, names);
            Assert.Contains(StockMoveRow.StockMoveItemDeltaIndex, names);
            Assert.Contains("BillLine_BillId", names);   // bill loading still indexed
            Assert.Contains("StockMove_Day", names);     // day-range reports still indexed

            var plan = await db.RunAsync(c => string.Join(" | ", c.Query<PlanRow>(
                "EXPLAIN QUERY PLAN SELECT ItemId, SUM(DeltaMilli) FROM StockMove GROUP BY ItemId").Select(r => r.Detail)));
            Assert.Contains("COVERING INDEX", plan, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task AgingLedger_WithPaymentsFolded_GivesTheSameAgeAsTheFullLedger()
    {
        await _h.SeedShopAsync();
        var db = _h.Services.GetRequiredService<AngadiDb>();
        var repo = _h.Services.GetRequiredService<ICustomerRepository>();
        var now = _h.Clock.Now;
        Guid b1 = Guid.NewGuid(), b2 = Guid.NewGuid(), b3 = Guid.NewGuid(), b4 = Guid.NewGuid(), b5 = Guid.NewGuid(), b6 = Guid.NewGuid();
        var ledgers = new (string Name, (DuesEntryKind Kind, long Paise, Guid? Bill, int DaysAgo)[] Entries)[]
        {
            ("partly paid", [(DuesEntryKind.Charge, 10_000, b1, 40), (DuesEntryKind.Charge, 5_000, b2, 10), (DuesEntryKind.Payment, 12_000, null, 5)]),
            ("voided + written off", [(DuesEntryKind.Charge, 20_000, b3, 60), (DuesEntryKind.Reversal, 20_000, b3, 59), (DuesEntryKind.Charge, 8_000, b4, 20), (DuesEntryKind.WriteOff, 3_000, null, 3)]),
            ("reversal excess", [(DuesEntryKind.Charge, 10_000, b5, 30), (DuesEntryKind.Reversal, 15_000, b5, 29), (DuesEntryKind.Charge, 10_000, b6, 15), (DuesEntryKind.Payment, 1_000, null, 2)]),
            ("bill-less reversal", [(DuesEntryKind.Reversal, 1_000, null, 9), (DuesEntryKind.Charge, 4_000, Guid.NewGuid(), 7), (DuesEntryKind.Payment, 1_000, null, 1)]),
            ("charges only", [(DuesEntryKind.Charge, 4_000, Guid.NewGuid(), 12), (DuesEntryKind.Charge, 4_000, Guid.NewGuid(), 4)]),
            ("payments only, still owing", [(DuesEntryKind.Payment, 1_000, null, 3)]),
        };
        var ids = new List<Guid>();
        await db.RunAsync(c =>
        {
            foreach (var (name, entries) in ledgers)
            {
                var customer = new Customer { Name = name, DuesBalance = new Paise(1), CreatedAt = now, UpdatedAt = now };
                ids.Add(customer.Id);
                c.Insert(ToRow(customer));
                foreach (var (kind, paise, bill, daysAgo) in entries)
                    c.Insert(new DuesEntryRow { Id = Guid.NewGuid(), CustomerId = customer.Id, Kind = (int)kind, AmountPaise = paise, BillId = bill, CreatedAtMs = Ms(now.AddDays(-daysAgo)) });
            }
        });

        var aging = await repo.GetOwingAgingLedgersAsync();
        var today = _h.Clock.Today;
        foreach (var id in ids)
        {
            var full = DuesAging.OldestUnpaidDays(await repo.GetLedgerAsync(id), today);
            var folded = DuesAging.OldestUnpaidDays(aging.TryGetValue(id, out var l) ? l : [], today);
            Assert.Equal(full, folded);
        }
        Assert.Equal(10, DuesAging.OldestUnpaidDays(aging[ids[0]], today));   // b1 settled, b2 still open
        Assert.Equal(20, DuesAging.OldestUnpaidDays(aging[ids[1]], today));   // b3 reversed, b4 open after write-off
    }

    [Fact]
    public async Task BusyShopYear_CounterAndReports_StayWithinBudget_AndNothingGotSlower()
    {
        _h.Subscription.IsPro = true;   // the quota is not under test; a year of bills is far past 50 a month
        await _h.SeedShopAsync(gstin: "36AABCU9603R1ZM");
        var db = _h.Services.GetRequiredService<AngadiDb>();
        var s = _h.Sender;
        var sw = Stopwatch.StartNew();
        var data = await BuildYearAsync(db, _h.Clock.Today);
        var buildMs = sw.Elapsed.TotalMilliseconds;

        // App start sweeps outbox rows older than 90 days (M-11): what a shop's database holds in steady state.
        sw.Restart();
        Assert.True((await s.Send(new PruneOutboxCommand())).IsSuccess);
        var pruneMs = sw.Elapsed.TotalMilliseconds;
        await db.CheckpointAsync();
        var fileBytes = new FileInfo(db.DatabasePath).Length;
        var liveAfter = await LiveBytesAsync(db);
        await LogTableSizesAsync(db);
        await UseIndexesAsync(db, old: true);
        var liveBefore = await LiveBytesAsync(db);
        var perBill = liveAfter / data.Bills;
        var perBillBefore = liveBefore / data.Bills;
        Log($"Dataset: {data.Bills:N0} bills, {data.Lines:N0} lines, {data.Moves:N0} stock moves, {data.Outbox:N0} outbox rows " +
            $"(pruned to 90 days in {pruneMs:N0} ms), {ItemCount:N0} items, {CustomerCount:N0} customers; built in {buildMs:N0} ms");
        Log($"Database: file {fileBytes / 1e6:N1} MB; live data {liveAfter / 1e6:N1} MB = {perBill:N0} B/bill " +
            $"(before S4: {liveBefore / 1e6:N1} MB = {perBillBefore:N0} B/bill) -> {perBill * BillsPerDay * 365 / 1e6:N0} MB per busy year");
        Log($"Auto Backup cap 25 MB reached after ~{AutoBackupCapBytes / perBill:N0} bills = ~{AutoBackupCapBytes / perBill / BillsPerDay:N0} days at {BillsPerDay}/day");

        var today = _h.Clock.Today;
        var yesterday = today.AddDays(-1);
        var lastMonthStart = new DateOnly(today.Year, today.Month, 1).AddMonths(-1);
        var lastMonthEnd = lastMonthStart.AddMonths(1).AddDays(-1);
        var insights = _h.Services.GetRequiredService<IInsightsService>();
        var ledger = _h.Services.GetRequiredService<IStockLedger>();
        var customers = _h.Services.GetRequiredService<ICustomerRepository>();
        var itemRepo = _h.Services.GetRequiredService<IItemRepository>();
        var someBill = await db.RunAsync(c => Guid.Parse(c.ExecuteScalar<string>("SELECT Id FROM Bill ORDER BY Number LIMIT 1 OFFSET 30000")));

        // The optimised paths give the same answers as the old ones, on a whole year of data.
        var oldLastSold = await OldLastSoldAsync(ledger, today);
        var newLastSold = await ledger.LastSoldDaysAsync(today.AddDays(-365), today);
        Assert.Equal(oldLastSold.Count, newLastSold.Count);
        foreach (var (item, day) in oldLastSold) Assert.Equal(day, newLastSold[item]);
        var aging = await customers.GetOwingAgingLedgersAsync();
        var owing = (await customers.GetWithDuesAsync()).Where(c => c.DuesBalance.Value > 0).ToList();
        Assert.NotEmpty(owing);
        foreach (var c in owing)
            Assert.Equal(DuesAging.OldestUnpaidDays(await customers.GetLedgerAsync(c.Id), today),
                DuesAging.OldestUnpaidDays(aging.TryGetValue(c.Id, out var l) ? l : [], today));

        var sale = 0;
        async Task SellAsync()
        {
            var lines = Enumerable.Range(0, 4).Select(k => (data.Items[(sale * 7 + k * 13) % data.Items.Count], 1000L)).ToArray();
            sale++;
            var r = await s.Send(new FinalizeBillCommand(AppHost.Draft(TenderType.Cash, lines)));
            Assert.True(r.IsSuccess, r.IsSuccess ? "" : r.Error.Code);
        }
        async Task DeadStock(bool old)
        {
            if (old)
            {
                var last = await OldLastSoldAsync(ledger, today);
                Assert.NotEmpty((await itemRepo.GetActiveAsync()).Where(i => i.Stock.Milli > 0 && (!last.TryGetValue(i.Id, out var d) || d <= today.AddDays(-30))));
            }
            else Assert.NotEmpty(await insights.GetDeadStockAsync(30));
        }

        var ops = new List<Op>
        {
            // ---- Counter: the shopkeeper is standing there with a customer
            new("Search Telugu prefix (బియ్యం)", CounterBudgetMs, async _ => Assert.NotEmpty(await s.Send(new SearchItemsQuery("బియ్యం")))),
            new("Search Telugu middle (గోల్డ్)", CounterBudgetMs, async _ => Assert.NotEmpty(await s.Send(new SearchItemsQuery("గోల్డ్")))),
            new("Search English prefix (Rice)", CounterBudgetMs, async _ => Assert.NotEmpty(await s.Send(new SearchItemsQuery("Rice")))),
            new("Search English middle (dal 29)", CounterBudgetMs, async _ => Assert.NotEmpty(await s.Send(new SearchItemsQuery("dal 29")))),
            new("Search no match (xyzq)", CounterBudgetMs, async _ => Assert.Empty(await s.Send(new SearchItemsQuery("xyzq")))),
            new("Barcode lookup", CounterBudgetMs, async _ => Assert.NotNull(await s.Send(new GetItemByBarcodeQuery(data.SomeBarcode)))),
            new("Customer search (name)", CounterBudgetMs, async _ => Assert.NotEmpty(await s.Send(new SearchCustomersQuery("కస్టమర్ 12")))),
            new("Bill quota (free-bills meter)", CounterBudgetMs, async _ => await s.Send(new GetBillQuotaQuery())),
            new("Finalize one bill (4 lines, cash)", CounterBudgetMs, _ => SellAsync()),
            new("Dues reminders (counter banner)", CounterOpenBudgetMs, async old =>
            {
                if (!old) { Assert.NotEmpty(await s.Send(new GetDueRemindersQuery())); return; }
                var n = 0;   // the pre-S4-4 handler: one full-ledger query per owing customer
                foreach (var c in await customers.GetWithDuesAsync())
                    if (c.DuesBalance.Value > 0 && DuesAging.OldestUnpaidDays(await customers.GetLedgerAsync(c.Id), today) is not null) n++;
                Assert.True(n > 0);
            }),

            // ---- Reports and accounts
            new("Day-end report (yesterday)", ReportBudgetMs, async _ =>
            {
                var r = await s.Send(new GetDayEndReportQuery(yesterday));
                Assert.True(r.BillCount >= BillsPerDay - 5, "day-end bill count " + r.BillCount);
            }),
            new("GST summary (whole last month)", ReportBudgetMs, async _ => Assert.NotEmpty(await s.Send(new GetGstSummaryQuery(lastMonthStart, lastMonthEnd)))),
            new("Top items (7 days)", ReportBudgetMs, async _ => Assert.NotEmpty(await s.Send(new GetTopItemsQuery(today.AddDays(-6), today, 5)))),
            new("Bill list (one day)", ReportBudgetMs, async _ => Assert.NotEmpty(await s.Send(new ListBillsQuery(yesterday, yesterday)))),
            new("Open one bill (lines)", CounterBudgetMs, async _ => Assert.NotNull(await s.Send(new GetBillQuery(someBill)))),
            new("Inventory report (ledger totals)", ReportBudgetMs, async _ =>
            {
                var r = await s.Send(new GetInventoryReportQuery());
                Assert.Equal(ItemCount, r.ItemCount);
                Assert.True(r.LedgerMatches, "ledger mismatch after the year build");
            }),
            new("Stock moves (one day)", ReportBudgetMs, async _ => Assert.NotEmpty(await s.Send(new GetStockMovesQuery(yesterday, yesterday)))),
            new("Stock history (best-selling item)", ReportBudgetMs, async _ => Assert.NotEmpty(await s.Send(new GetStockHistoryQuery(data.Items[0].Id)))),
            new("Backup ledger check (restore/inspect)", ReportBudgetMs, async _ => Assert.Equal(0, await db.RunAsync(c => c.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM Item i WHERE i.StockMilli <> (SELECT COALESCE(SUM(m.DeltaMilli), 0) FROM StockMove m WHERE m.ItemId = i.Id)")))),
            new("Customer bills (busiest customer)", ReportBudgetMs, async _ => Assert.NotEmpty(await s.Send(new GetCustomerBillsQuery(data.BusiestCustomer)))),
            new("Customer report (busiest customer)", ReportBudgetMs, async _ => Assert.NotNull(await s.Send(new GetCustomerReportQuery(data.BusiestCustomer)))),
            new("Customer ledger (busiest customer)", ReportBudgetMs, async _ => Assert.NotEmpty(await s.Send(new GetCustomerLedgerQuery(data.BusiestCustomer)))),
            new("Dues outstanding", ReportBudgetMs, async _ => Assert.NotEmpty((await s.Send(new GetDuesOutstandingQuery())).Customers)),
            new("Insights: morning summary", ReportBudgetMs, async _ => Assert.True((await insights.GetMorningSummaryAsync()).YesterdaySales.Value > 0)),
            new("Insights: reorder forecast", ReportBudgetMs, async _ => await insights.GetReorderForecastAsync()),
            new("Insights: dead stock (30 days)", ReportBudgetMs, DeadStock),
            new("Reports screen (all 8 loads)", ReportsPageBudgetMs, async old =>
            {
                var month = new DateOnly(yesterday.Year, yesterday.Month, 1);
                await s.Send(new GetDayEndReportQuery(yesterday));
                await s.Send(new GetGstSummaryQuery(month, yesterday));
                await s.Send(new GetTopItemsQuery(yesterday.AddDays(-6), yesterday, 5));
                await insights.GetMorningSummaryAsync();
                await insights.GetReorderForecastAsync();
                await DeadStock(old);
                await s.Send(new GetInventoryReportQuery());
                await s.Send(new GetStockMovesQuery(yesterday, yesterday));
            }, Runs: 3),
        };

        // BEFORE: the old indexes (still in place from above) and the old code paths; AFTER: today's.
        var before = new List<double>();
        foreach (var op in ops) before.Add((await TimeAsync(() => op.Run(true), op.Runs)).Median);
        await UseIndexesAsync(db, old: false);
        var after = new List<(double First, double Median)>();
        foreach (var op in ops) after.Add(await TimeAsync(() => op.Run(false), op.Runs));
        var snapshot = await TimeAsync(async () => Assert.True((await s.Send(new TakeSafetySnapshotCommand())).IsSuccess), runs: 1);

        Log("");
        Log($"{"Operation",-40} {"before",8} {"after",8} {"1st run",8} {"budget",7}   (median ms; before = pre-S4 indexes + code)");
        var over = new List<string>();
        for (var i = 0; i < ops.Count; i++)
        {
            var (op, b, a) = (ops[i], before[i], after[i]);
            var slower = a.Median > NotSlowerFactor * b + NotSlowerSlackMs;
            var overBudget = a.Median > op.BudgetMs;
            Log(string.Create(CultureInfo.InvariantCulture,
                $"{op.Name,-40} {b,8:F1} {a.Median,8:F1} {a.First,8:F1} {op.BudgetMs,7:F0}{(overBudget ? "  OVER" : "")}{(slower ? "  SLOWER" : "")}"));
            if (overBudget) over.Add(string.Create(CultureInfo.InvariantCulture, $"{op.Name} {a.Median:F0} ms > {op.BudgetMs:F0} ms"));
            if (slower) over.Add(string.Create(CultureInfo.InvariantCulture, $"{op.Name} slower: {a.Median:F1} ms after vs {b:F1} ms before"));
        }
        Log(string.Create(CultureInfo.InvariantCulture, $"{"Safety snapshot (full copy + check)",-40} {"",8} {snapshot.Median,8:F1} {snapshot.First,8:F1} {SnapshotBudgetMs,7:F0}"));
        if (snapshot.Median > SnapshotBudgetMs) over.Add($"Safety snapshot {snapshot.Median:F0} ms > {SnapshotBudgetMs:F0} ms");

        var table = string.Join(Environment.NewLine, _rows);
        Assert.True(over.Count == 0, string.Join("; ", over) + Environment.NewLine + table);
        Assert.True(perBill <= MaxBytesPerBill, $"{perBill} bytes per bill > {MaxBytesPerBill}" + Environment.NewLine + table);
        Assert.True(liveAfter < liveBefore, $"retiring indexes did not shrink the database: {liveAfter} >= {liveBefore}");
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>One timed operation; Run(old) runs the pre-S4 code path when old is true (most have only one path).</summary>
    private sealed record Op(string Name, double BudgetMs, Func<bool, Task> Run, int Runs = 5);

    /// <summary>The pre-S4-2 dead-stock "last sold" step: a year of daily sales loaded and grouped in C#.</summary>
    private static async Task<Dictionary<Guid, DateOnly>> OldLastSoldAsync(IStockLedger ledger, DateOnly today)
        => (await ledger.GetDailySalesAsync(today.AddDays(-365), today))
            .Where(x => x.Sold.Milli > 0).GroupBy(x => x.ItemId).ToDictionary(g => g.Key, g => g.Max(x => x.Day));

    private static Task UseIndexesAsync(AngadiDb db, bool old) => db.RunAsync(c =>
    {
        if (old)
        {
            c.Execute($"DROP INDEX IF EXISTS \"{StockMoveRow.StockMoveItemDeltaIndex}\"");
            foreach (var sql in OldIndexSql) c.Execute(sql);
        }
        else
        {
            foreach (var name in AngadiDb.RetiredIndexes) c.Execute($"DROP INDEX IF EXISTS \"{name}\"");
            c.Execute(StockMoveRow.StockMoveItemDeltaIndexSql);
        }
    });

    /// <summary>Bytes in use (pages minus free pages): what the file settles at once freed pages are reused.</summary>
    private static Task<long> LiveBytesAsync(AngadiDb db) => db.RunAsync(c =>
        (c.ExecuteScalar<long>("PRAGMA page_count") - c.ExecuteScalar<long>("PRAGMA freelist_count")) * c.ExecuteScalar<long>("PRAGMA page_size"));

    private static async Task<(double First, double Median)> TimeAsync(Func<Task> op, int runs = 5)
    {
        var sw = Stopwatch.StartNew();
        await op();
        var times = new List<double> { sw.Elapsed.TotalMilliseconds };
        for (var i = 1; i < runs; i++)
        {
            sw.Restart();
            await op();
            times.Add(sw.Elapsed.TotalMilliseconds);
        }
        var warm = times.Count > 1 ? times.Skip(1).Order().ToList() : times;
        return (times[0], warm[warm.Count / 2]);
    }

    private void Log(string line)
    {
        _rows.Add(line);
        output.WriteLine("[ci-report] " + line);
    }

    private sealed class PlanRow
    {
        [SQLite.Column("detail")] public string Detail { get; set; } = "";
    }

    private sealed class SizeRow
    {
        public string Name { get; set; } = "";
        public long Bytes { get; set; }
    }

    /// <summary>Per table/index bytes, when this SQLite build has the dbstat table.</summary>
    private async Task LogTableSizesAsync(AngadiDb db)
    {
        try
        {
            var sizes = await db.RunAsync(c => c.Query<SizeRow>("SELECT name AS Name, SUM(pgsize) AS Bytes FROM dbstat GROUP BY name ORDER BY 2 DESC"));
            var sb = new StringBuilder("By table/index (MB):");
            foreach (var r in sizes.Where(r => r.Bytes > 100_000)) sb.Append(CultureInfo.InvariantCulture, $" {r.Name}={r.Bytes / 1e6:F1}");
            Log(sb.ToString());
        }
        catch (SQLite.SQLiteException ex)
        {
            Log("dbstat not available: " + ex.Message);
        }
    }

    // ------------------------------------------------------------------ the year's data

    internal sealed record YearData(int Bills, int Lines, int Moves, int Outbox, List<Item> Items, string SomeBarcode, Guid BusiestCustomer);

    /// <summary>
    /// Writes a year of finished bills (with their stock moves, udhaar ledger and outbox rows exactly as finalize and void
    /// would) straight into the database in one transaction. Item stock and customer balances end equal to their ledgers.
    /// </summary>
    internal static async Task<YearData> BuildYearAsync(AngadiDb db, DateOnly today)
    {
        var rnd = new Random(20261019);
        var ist = TimeSpan.FromHours(5.5);
        var start = new DateTimeOffset(today.AddDays(-Days).ToDateTime(new TimeOnly(7, 0)), ist);

        var items = new List<Item>(ItemCount);
        for (var i = 0; i < ItemCount; i++)
        {
            var b = i % TeluguBase.Length;
            items.Add(new Item
            {
                TeluguName = $"{TeluguBase[b]} {TeluguBrand[i / TeluguBase.Length % TeluguBrand.Length]} {i}",
                EnglishName = $"{EnglishBase[b]} {i}",
                Aliases = [EnglishBase[b].ToLowerInvariant()],
                Unit = b is 0 or 1 or 3 ? Unit.Kilogram : Unit.Piece,
                Price = new Paise(1000 + i % 50 * 500),
                GstSlab = b is 0 or 1 or 4 or 5 ? GstSlab.Zero : b is 2 or 3 or 7 ? GstSlab.Five : GstSlab.Eighteen,
                Barcode = Ean13(890_000_000_000L + i),
                LowStockThreshold = Qty.Whole(5),
                IsQuickSale = i < 12,
                CreatedAt = start, UpdatedAt = start,
            });
        }
        var stock = items.ToDictionary(i => i.Id, _ => 0L);

        var customers = Enumerable.Range(0, CustomerCount).Select(i => new Customer
        {
            Name = $"కస్టమర్ {i}", Phone = $"91987650{i:D4}", CreatedAt = start, UpdatedAt = start,
        }).ToList();
        var balance = customers.ToDictionary(c => c.Id, _ => 0L);

        var moves = new List<StockMoveRow>();
        var dues = new List<DuesEntryRow>();
        var outbox = new List<OutboxRow>();
        int billCount = 0, lineCount = 0, moveCount = 0, outboxCount = 0;
        var perCustomer = new Dictionary<Guid, int>();

        void Move(Guid itemId, long delta, StockMoveReason reason, Guid? billId, DateTimeOffset at)
        {
            stock[itemId] += delta;
            moves.Add(new StockMoveRow { Id = Guid.NewGuid(), ItemId = itemId, DeltaMilli = delta, Reason = (int)reason, BillId = billId, Day = Day(at), CreatedAtMs = Ms(at) });
        }

        void Event(string type, IDomainEvent e) => outbox.Add(new OutboxRow
        {
            Id = Guid.NewGuid(), AggregateType = type, AggregateId = e.AggregateId, EventType = e.GetType().Name,
            PayloadJson = JsonSerializer.Serialize(e, e.GetType(), Json), CreatedAtMs = Ms(e.OccurredAt), SyncState = 0,
        });

        await db.RunAsync(c => c.RunInTransaction(() =>
        {
            c.InsertAll(items.Select(i => ToRow(i)), runInTransaction: false);
            c.InsertAll(customers.Select(cu => ToRow(cu)), runInTransaction: false);
            foreach (var item in items) Move(item.Id, 400_000, StockMoveReason.Opening, null, start);

            for (var d = 0; d < Days; d++)
            {
                var dayStart = start.AddDays(d).AddHours(1);   // 08:00
                if (d % 30 == 15)
                    foreach (var item in items.Where((_, k) => k % 3 == d % 3)) Move(item.Id, 50_000, StockMoveReason.Purchase, null, dayStart);

                var bills = new List<BillRow>(BillsPerDay);
                var lines = new List<BillLineRow>(BillsPerDay * 4);
                for (var n = 0; n < BillsPerDay; n++)
                {
                    var at = dayStart.AddSeconds(n * 300 + rnd.Next(200));   // 08:00 – 20:30
                    var roll = rnd.Next(100);
                    var tender = roll < 60 ? TenderType.Cash : roll < 90 ? TenderType.Upi : TenderType.Udhaar;
                    Guid? customer = tender == TenderType.Udhaar || rnd.Next(100) < 20 ? customers[Popular(rnd, CustomerCount)].Id : null;
                    var bill = new Bill
                    {
                        Number = $"A-{billCount + 1:D6}", Tender = tender, CustomerId = customer, Status = BillStatus.Finalized,
                        FinalizedAt = at, CreatedAt = at, UpdatedAt = at,
                    };
                    var count = 2 + rnd.Next(5);   // 2–6 lines, ~4 on average
                    var picked = new HashSet<int>();
                    while (picked.Count < count) picked.Add(Popular(rnd, ItemCount));
                    foreach (var k in picked)
                    {
                        var item = items[k];
                        bill.Lines.Add(new BillLine
                        {
                            ItemId = item.Id, TeluguName = item.TeluguName, EnglishName = item.EnglishName, Unit = item.Unit,
                            UnitPrice = item.Price, GstSlab = item.GstSlab, Qty = new Qty(item.Unit == Unit.Kilogram ? 500 * (1 + rnd.Next(4)) : 1000 * (1 + rnd.Next(3))),
                            Source = (LineSource)rnd.Next(4),
                        });
                    }
                    BillCalculator.Compute(bill, shopHasGstin: true);
                    if (tender == TenderType.Cash) bill.CashReceived = bill.GrandTotal;
                    var voided = tender == TenderType.Cash && rnd.Next(100) == 0;
                    if (voided)
                    {
                        bill.Status = BillStatus.Void;
                        bill.VoidedAt = at.AddMinutes(2);
                        bill.VoidReason = "తప్పు బిల్లు";
                    }

                    bills.Add(ToRow(bill));
                    lines.AddRange(bill.Lines.Select((l, i) => ToRow(l, bill.Id, i)));
                    foreach (var l in bill.Lines) Move(l.ItemId, -l.Qty.Milli, StockMoveReason.Sale, bill.Id, at);
                    if (voided)
                        foreach (var l in bill.Lines) Move(l.ItemId, l.Qty.Milli, StockMoveReason.VoidRestore, bill.Id, bill.VoidedAt!.Value);

                    if (customer is { } cid) perCustomer[cid] = perCustomer.GetValueOrDefault(cid) + 1;
                    if (tender == TenderType.Udhaar && customer is { } owing)
                    {
                        dues.Add(new DuesEntryRow { Id = Guid.NewGuid(), CustomerId = owing, Kind = (int)DuesEntryKind.Charge, AmountPaise = bill.GrandTotal.Value, BillId = bill.Id, CreatedAtMs = Ms(at) });
                        balance[owing] += bill.GrandTotal.Value;
                        Event("Customer", new DuesCharged(owing, at, bill.GrandTotal.Value, bill.Id));
                    }
                    Event("Bill", new BillFinalized(bill.Id, at, bill.GrandTotal.Value));
                    if (voided) Event("Bill", new BillVoided(bill.Id, bill.VoidedAt!.Value, bill.VoidReason!));
                    billCount++;
                }

                // Evening: a few regulars clear part of their udhaar.
                var evening = dayStart.AddHours(13);
                foreach (var owing in balance.Where(kv => kv.Value > 0).Select(kv => kv.Key).Where(_ => rnd.Next(100) < 8).ToList())
                {
                    var paid = balance[owing] / 2;
                    if (paid <= 0) continue;
                    dues.Add(new DuesEntryRow { Id = Guid.NewGuid(), CustomerId = owing, Kind = (int)DuesEntryKind.Payment, AmountPaise = paid, CreatedAtMs = Ms(evening) });
                    balance[owing] -= paid;
                    Event("Customer", new DuesPaymentRecorded(owing, evening, paid));
                }

                c.InsertAll(bills, runInTransaction: false);
                c.InsertAll(lines, runInTransaction: false);
                c.InsertAll(moves, runInTransaction: false);
                c.InsertAll(dues, runInTransaction: false);
                c.InsertAll(outbox, runInTransaction: false);
                lineCount += lines.Count;
                moveCount += moves.Count;
                outboxCount += outbox.Count;
                moves.Clear();
                dues.Clear();
                outbox.Clear();
            }

            foreach (var (id, milli) in stock) c.Execute("UPDATE Item SET StockMilli = ? WHERE Id = ?", milli, id);
            foreach (var (id, paise) in balance) c.Execute("UPDATE Customer SET DuesBalancePaise = ? WHERE Id = ?", paise, id);
            c.Execute("UPDATE ShopProfile SET NextBillSequence = ?", billCount + 1);
        }));

        var reloaded = items.Select(i => { i.Stock = new Qty(stock[i.Id]); return i; }).ToList();
        return new YearData(billCount, lineCount, moveCount, outboxCount, reloaded, items[1234].Barcode!,
            perCustomer.MaxBy(kv => kv.Value).Key);
    }

    /// <summary>Skewed pick: a few items (and regulars) account for most sales, as in a real shop.</summary>
    private static int Popular(Random rnd, int n) => (int)(n * Math.Pow(rnd.NextDouble(), 2.5));

    private static string Ean13(long first12)
    {
        var digits = first12.ToString("D12", CultureInfo.InvariantCulture);
        var sum = 0;
        for (int i = 11, w = 3; i >= 0; i--, w = 4 - w) sum += (digits[i] - '0') * w;
        return digits + ((10 - sum % 10) % 10).ToString(CultureInfo.InvariantCulture);
    }
}
