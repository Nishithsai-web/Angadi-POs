using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Angadi.Application.Billing;
using Angadi.Application.Catalog;
using Angadi.Application.Customers;
using Angadi.Application.Insights;
using Angadi.Application.Inventory;
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
/// waits on is timed through the real Application + Infrastructure graph. Cheap Android phones are ~5–10× slower than
/// CI, so the CI budgets are tight: counter actions under 50 ms, reports under 1 s. Timings and the database size are
/// printed (test output / TRX) and repeated in any failure message.
/// </summary>
public sealed class YearOfDataTests(ITestOutputHelper output) : IDisposable
{
    internal const int Days = 365;
    internal const int BillsPerDay = 150;
    internal const int ItemCount = 3000;
    internal const int CustomerCount = 800;

    private const double CounterBudgetMs = 50;
    private const double ReportBudgetMs = 1000;
    private const double ReportsPageBudgetMs = 2500;   // the Reports screen loads eight reports in a row
    private const double SnapshotBudgetMs = 10_000;    // background, but it holds the database lock while copying
    /// <summary>Regression guard on storage: what one finished bill costs on disk, all tables and indexes included.</summary>
    private const long MaxBytesPerBill = 3_200;

    private static readonly string[] TeluguBase = ["బియ్యం", "కంది పప్పు", "నూనె", "చక్కెర", "ఉప్పు", "పాలు", "సబ్బు", "టీ పొడి", "కారం", "పసుపు"];
    private static readonly string[] EnglishBase = ["Rice", "Toor dal", "Oil", "Sugar", "Salt", "Milk", "Soap", "Tea powder", "Chilli", "Turmeric"];
    private static readonly string[] TeluguBrand = ["సోనా", "గోల్డ్", "ప్రీమియం", "లోకల్"];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly AppHost _h = new();
    private readonly List<string> _rows = [];
    private readonly List<string> _over = [];
    private readonly List<string> _compact = [];   // TEMP (D2 measurement): CI annotations keep only 400 chars

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task BusyShopYear_CounterAndReports_StayWithinBudget()
    {
        _h.Subscription.IsPro = true;   // the quota is not under test; a year of bills is far past 50 a month
        await _h.SeedShopAsync(gstin: "36AABCU9603R1ZM");
        var db = _h.Services.GetRequiredService<AngadiDb>();
        var sw = Stopwatch.StartNew();
        var data = await BuildYearAsync(db, _h.Clock.Today);
        var buildMs = sw.Elapsed.TotalMilliseconds;

        await db.CheckpointAsync();
        var bytes = new FileInfo(db.DatabasePath).Length;
        var perBill = bytes / data.Bills;
        Log($"Dataset: {data.Bills:N0} bills, {data.Lines:N0} lines, {data.Moves:N0} stock moves, {data.Outbox:N0} outbox rows, " +
            $"{ItemCount:N0} items, {CustomerCount:N0} customers — built in {buildMs:N0} ms");
        Log($"Database: {bytes / 1e6:N1} MB = {perBill:N0} bytes per bill → {perBill * BillsPerDay * 365 / 1e6:N0} MB per busy year " +
            $"(Auto Backup cap 25 MB reached after ~{25e6 / perBill:N0} bills = ~{25e6 / perBill / BillsPerDay:N0} days at {BillsPerDay}/day)");
        await LogTableSizesAsync(db);

        var s = _h.Sender;
        var today = _h.Clock.Today;
        var yesterday = today.AddDays(-1);
        var lastMonthStart = new DateOnly(today.Year, today.Month, 1).AddMonths(-1);
        var lastMonthEnd = lastMonthStart.AddMonths(1).AddDays(-1);
        var insights = _h.Services.GetRequiredService<IInsightsService>();

        Log("");
        Log($"{"Operation",-40} {"first ms",9} {"median ms",10} {"budget ms",10}");

        // ---- Counter: the shopkeeper is standing there with a customer
        await TimeAsync("Search Telugu prefix (బియ్యం)", "tp", CounterBudgetMs, () => s.Send(new SearchItemsQuery("బియ్యం")), r => Assert.NotEmpty(r));
        await TimeAsync("Search Telugu middle (గోల్డ్)", "tm", CounterBudgetMs, () => s.Send(new SearchItemsQuery("గోల్డ్")), r => Assert.NotEmpty(r));
        await TimeAsync("Search English prefix (Rice)", "ep", CounterBudgetMs, () => s.Send(new SearchItemsQuery("Rice")), r => Assert.NotEmpty(r));
        await TimeAsync("Search English middle (dal 29)", "em", CounterBudgetMs, () => s.Send(new SearchItemsQuery("dal 29")), r => Assert.NotEmpty(r));
        await TimeAsync("Search no match (xyzq)", "nm", CounterBudgetMs, () => s.Send(new SearchItemsQuery("xyzq")), r => Assert.Empty(r));
        await TimeAsync("Barcode lookup", "bc", CounterBudgetMs, () => s.Send(new GetItemByBarcodeQuery(data.SomeBarcode)), r => Assert.NotNull(r));
        await TimeAsync("Customer search (name)", "cs", CounterBudgetMs, () => s.Send(new SearchCustomersQuery("కస్టమర్ 12")), r => Assert.NotEmpty(r));
        await TimeAsync("Bill quota (free-bills meter)", "qt", CounterBudgetMs, () => s.Send(new GetBillQuotaQuery()), _ => { });
        var sale = 0;
        await TimeAsync("Finalize one bill (4 lines, cash)", "fin", CounterBudgetMs, async () =>
        {
            var lines = Enumerable.Range(0, 4).Select(k => (data.Items[(sale * 7 + k * 13) % data.Items.Count], 1000L)).ToArray();
            sale++;
            return await s.Send(new FinalizeBillCommand(AppHost.Draft(TenderType.Cash, lines)));
        }, r => Assert.True(r.IsSuccess, r.IsSuccess ? "" : r.Error.Code));
        await TimeAsync("Dues reminders (counter banner)", "due", CounterBudgetMs, () => s.Send(new GetDueRemindersQuery()), r => Assert.NotEmpty(r));

        // ---- Reports and accounts
        await TimeAsync("Day-end report (yesterday)", "de", ReportBudgetMs, () => s.Send(new GetDayEndReportQuery(yesterday)),
            r => Assert.True(r.BillCount >= BillsPerDay - 5, "day-end bill count " + r.BillCount));
        await TimeAsync("GST summary (whole last month)", "gst", ReportBudgetMs, () => s.Send(new GetGstSummaryQuery(lastMonthStart, lastMonthEnd)), r => Assert.NotEmpty(r));
        await TimeAsync("Top items (7 days)", "top", ReportBudgetMs, () => s.Send(new GetTopItemsQuery(today.AddDays(-6), today, 5)), r => Assert.NotEmpty(r));
        await TimeAsync("Bill list (one day)", "bl", ReportBudgetMs, () => s.Send(new ListBillsQuery(yesterday, yesterday)), r => Assert.NotEmpty(r));
        await TimeAsync("Inventory report (ledger totals)", "inv", ReportBudgetMs, () => s.Send(new GetInventoryReportQuery()), r =>
        {
            Assert.Equal(ItemCount, r.ItemCount);
            Assert.True(r.LedgerMatches, "ledger mismatch after the year build");
        });
        await TimeAsync("Stock moves (one day)", "mv", ReportBudgetMs, () => s.Send(new GetStockMovesQuery(yesterday, yesterday)), r => Assert.NotEmpty(r));
        await TimeAsync("Customer bills (busiest customer)", "cb", ReportBudgetMs, () => s.Send(new GetCustomerBillsQuery(data.BusiestCustomer)), r => Assert.NotEmpty(r));
        await TimeAsync("Customer report (busiest customer)", "cr", ReportBudgetMs, () => s.Send(new GetCustomerReportQuery(data.BusiestCustomer)), r => Assert.NotNull(r));
        await TimeAsync("Customer ledger (busiest customer)", "cl", ReportBudgetMs, () => s.Send(new GetCustomerLedgerQuery(data.BusiestCustomer)), r => Assert.NotEmpty(r));
        await TimeAsync("Dues outstanding", "do", ReportBudgetMs, () => s.Send(new GetDuesOutstandingQuery()), r => Assert.NotEmpty(r.Customers));
        await TimeAsync("Insights: morning summary", "am", ReportBudgetMs, () => insights.GetMorningSummaryAsync(), r => Assert.True(r.YesterdaySales.Value > 0));
        await TimeAsync("Insights: reorder forecast", "ro", ReportBudgetMs, () => insights.GetReorderForecastAsync(), _ => { });
        await TimeAsync("Insights: dead stock (30 days)", "ds", ReportBudgetMs, () => insights.GetDeadStockAsync(30), r => Assert.NotEmpty(r));
        await TimeAsync("Reports screen (all 8 loads)", "RP", ReportsPageBudgetMs, async () =>
        {
            var month = new DateOnly(yesterday.Year, yesterday.Month, 1);
            await s.Send(new GetDayEndReportQuery(yesterday));
            await s.Send(new GetGstSummaryQuery(month, yesterday));
            await s.Send(new GetTopItemsQuery(yesterday.AddDays(-6), yesterday, 5));
            await insights.GetMorningSummaryAsync();
            await insights.GetReorderForecastAsync();
            await insights.GetDeadStockAsync(30);
            await s.Send(new GetInventoryReportQuery());
            return await s.Send(new GetStockMovesQuery(yesterday, yesterday));
        }, _ => { }, runs: 3);

        // ---- Background safety copy (app start / app to background): copies the whole file under the lock
        await TimeAsync("Safety snapshot (full copy + check)", "SN", SnapshotBudgetMs, () => s.Send(new TakeSafetySnapshotCommand()),
            r => Assert.True(r.IsSuccess, r.IsSuccess ? "" : r.Error.Code), runs: 1);

        Assert.Fail($"MB {bytes / 1e6:F1} B/bill {perBill} | " + string.Join("|", _compact));   // TEMP (D2 measurement)
        var table = string.Join(Environment.NewLine, _rows);
        Assert.True(_over.Count == 0, "Over budget: " + string.Join("; ", _over) + Environment.NewLine + table);
        Assert.True(perBill <= MaxBytesPerBill, $"{perBill} bytes per bill > {MaxBytesPerBill}" + Environment.NewLine + table);
    }

    // ------------------------------------------------------------------ timing

    private async Task TimeAsync<T>(string name, string code, double budgetMs, Func<Task<T>> op, Action<T> check, int runs = 5)
    {
        var sw = Stopwatch.StartNew();
        var first = await op();
        var firstMs = sw.Elapsed.TotalMilliseconds;
        check(first);
        var times = new List<double> { firstMs };
        for (var i = 1; i < runs; i++)
        {
            sw.Restart();
            await op();
            times.Add(sw.Elapsed.TotalMilliseconds);
        }
        var warm = times.Count > 1 ? times.Skip(1).Order().ToList() : times;
        var median = warm[warm.Count / 2];
        Log(string.Create(CultureInfo.InvariantCulture, $"{name,-40} {firstMs,9:F1} {median,10:F1} {budgetMs,10:F0}{(median > budgetMs ? "  OVER" : "")}"));
        _compact.Add(string.Create(CultureInfo.InvariantCulture, $"{code} {median:F1}"));
        if (median > budgetMs) _over.Add(string.Create(CultureInfo.InvariantCulture, $"{name} {median:F0} ms > {budgetMs:F0} ms"));
    }

    private void Log(string line)
    {
        _rows.Add(line);
        output.WriteLine(line);
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
