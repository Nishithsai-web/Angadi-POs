using Angadi.Application.Billing;
using Angadi.Application.Catalog;
using Angadi.Application.Common;
using Angadi.Application.Customers;
using Angadi.Domain.Billing;
using Angadi.Domain.Common;
using Angadi.Domain.Customers;
using Angadi.Domain.Items;

namespace Angadi.Tests.Application.Customers;

/// <summary>
/// The udhaar/appu credit book under seeded random sequences of udhaar bills, payments (partial, exact, too much,
/// zero), cash/UPI bills and voids, checked against an independent model after EVERY step:
/// balance = Σ charges − Σ payments − Σ reversals, never negative, FIFO aging, and the customer report.
/// </summary>
public class DuesLedgerTests : IDisposable
{
    private readonly AppHost _h = new();
    public void Dispose() => _h.Dispose();

    private sealed class Charge(Guid billId, long amount, DateTimeOffset at)
    {
        public Guid BillId { get; } = billId;
        public long Amount { get; } = amount;
        public DateTimeOffset At { get; } = at;
        public bool Void { get; set; }
    }

    /// <summary>What the credit book must say, computed without the app's code.</summary>
    private sealed class Model(Customer customer)
    {
        public Customer Customer { get; } = customer;
        public List<Charge> Charges { get; } = [];
        public List<(Guid Id, long Total, TenderType Tender, bool Void)> Bills { get; } = [];
        public long Payments { get; set; }
        public long Balance => Charges.Where(c => !c.Void).Sum(c => c.Amount) - Payments;

        /// <summary>Payments settle the oldest live charge first.</summary>
        public List<(Guid BillId, long Remaining, DateTimeOffset At)> Unpaid()
        {
            var paid = Payments;
            var open = new List<(Guid, long, DateTimeOffset)>();
            foreach (var c in Charges.Where(c => !c.Void).OrderBy(c => c.At))
            {
                if (paid >= c.Amount) { paid -= c.Amount; continue; }
                open.Add((c.BillId, c.Amount - paid, c.At));
                paid = 0;
            }
            return open;
        }
    }

    private async Task<(Item rice, Item milk)> SeedAsync()
    {
        await _h.SeedShopAsync();
        _h.Subscription.IsPro = true;   // sequences run past the 50 free bills
        var rice = await _h.AddItemAsync("బియ్యం", 6000, stockMilli: 1_000_000);
        var milk = await _h.AddItemAsync("పాలు", 3000, stockMilli: 1_000_000, unit: Unit.Pack);
        return (rice, milk);
    }

    private async Task<Customer> CustomerAsync(string name, string phone)
    {
        var c = new Customer { Name = name, Phone = phone };
        var r = await _h.Sender.Send(new SaveCustomerCommand(c));
        Assert.True(r.IsSuccess, r.Error.Code);
        return c;
    }

    private async Task<FinalizedBill> SellAsync(TenderType tender, Guid? customerId, params (Item item, long qtyMilli)[] lines)
    {
        var draft = AppHost.Draft(tender, lines);
        draft.CustomerId = customerId;
        var r = await _h.Sender.Send(new FinalizeBillCommand(draft));
        Assert.True(r.IsSuccess, r.Error.Code);
        return r.Value;
    }

    // Times stay between 11:00 and 19:00 IST so the IST calendar day is the same day in UTC (CI runners) too.
    private sealed class Calendar(FakeClock clock)
    {
        private readonly DateTimeOffset _start = new(2026, 10, 19, 11, 0, 0, TimeSpan.FromHours(5.5));
        private int _day;
        private int _minute;

        public void Tick(Random rng)
        {
            if (rng.Next(4) == 0) { _day += rng.Next(1, 5); _minute = 0; }
            else _minute += rng.Next(1, 25);
            if (_minute > 480) { _day++; _minute = 0; }
            clock.Now = _start.AddDays(_day).AddMinutes(_minute);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(42)]
    [InlineData(2026)]
    [InlineData(90210)]
    public async Task RandomSequences_KeepEveryInvariant_AfterEveryStep(int seed)
    {
        var (rice, milk) = await SeedAsync();
        var models = new[]
        {
            new Model(await CustomerAsync("రవి", "98765 43210")),
            new Model(await CustomerAsync("Sita", "+91 90000 11223")),
        };
        var rng = new Random(seed);
        var calendar = new Calendar(_h.Clock);

        for (var step = 0; step < 45; step++)
        {
            calendar.Tick(rng);
            var m = models[rng.Next(models.Length)];
            var where = $"seed {seed} step {step} ({m.Customer.Name})";
            var roll = rng.Next(100);

            if (roll < 35)
            {
                var bill = rng.Next(2) == 0
                    ? await SellAsync(TenderType.Udhaar, m.Customer.Id, (rice, rng.Next(250, 5000)))
                    : await SellAsync(TenderType.Udhaar, m.Customer.Id, (milk, 1000L * rng.Next(1, 6)), (rice, rng.Next(100, 2000)));
                Assert.True(bill.GrandTotal.Value > 0, where);
                m.Charges.Add(new Charge(bill.BillId, bill.GrandTotal.Value, _h.Clock.Now));
                m.Bills.Add((bill.BillId, bill.GrandTotal.Value, TenderType.Udhaar, false));
            }
            else if (roll < 45)
            {
                var tender = rng.Next(2) == 0 ? TenderType.Cash : TenderType.Upi;
                var bill = await SellAsync(tender, m.Customer.Id, (milk, 1000L * rng.Next(1, 4)));
                m.Bills.Add((bill.BillId, bill.GrandTotal.Value, tender, false));   // never touches dues
            }
            else if (roll < 75)
            {
                var balance = m.Balance;
                var mode = rng.Next(3);
                long amount = balance == 0 ? 100
                    : mode == 0 && balance > 1 ? rng.NextInt64(1, balance)   // partial, odd paise included
                    : mode == 1 ? balance                                    // exact: clears everything
                    : balance + rng.Next(1, 1000);                           // more than owed
                var r = await _h.Sender.Send(new RecordDuesPaymentCommand(m.Customer.Id, new Paise(amount), "step " + step));
                if (amount <= balance)
                {
                    Assert.True(r.IsSuccess, where + " payment " + r.Error.Code);
                    m.Payments += amount;
                }
                else
                {
                    Assert.Equal(ErrorCodes.DuesExceedsBalance, r.Error.Code);
                    Assert.Equal(IndianFormat.Rupees(new Paise(balance)), Assert.Single(r.Error.Args!));
                }
            }
            else if (roll < 82)
            {
                var r = await _h.Sender.Send(new RecordDuesPaymentCommand(m.Customer.Id, new Paise(-rng.Next(0, 5000)), null));
                Assert.Equal(ErrorCodes.AmountInvalid, r.Error.Code);
            }
            else
            {
                if (m.Bills.Count == 0) continue;
                var i = rng.Next(m.Bills.Count);
                var b = m.Bills[i];
                // A2-14: the sequence crosses months; the shopkeeper confirms voiding a filed month's bill.
                var r = await _h.Sender.Send(new VoidBillCommand(b.Id, "తప్పు బిల్లు", ConfirmPastMonth: true));
                if (b.Void) Assert.Equal(ErrorCodes.BillAlreadyVoid, r.Error.Code);
                else if (b.Tender == TenderType.Udhaar && m.Balance < b.Total)
                    Assert.Equal(ErrorCodes.BillVoidBlockedByPayments, r.Error.Code);
                else
                {
                    Assert.True(r.IsSuccess, where + " void " + r.Error.Code);
                    m.Bills[i] = b with { Void = true };
                    if (m.Charges.FirstOrDefault(c => c.BillId == b.Id) is { } charge) charge.Void = true;
                }
            }

            foreach (var model in models) await CheckAsync(model, $"seed {seed} step {step} ({model.Customer.Name})");
        }
    }

    private async Task CheckAsync(Model m, string where)
    {
        var balance = m.Balance;
        var customer = (await _h.Sender.Send(new GetCustomerQuery(m.Customer.Id)))!;
        Same(balance, customer.DuesBalance.Value, where, "stored balance");
        Assert.True(customer.DuesBalance.Value >= 0, where + ": balance went negative");

        var ledger = await _h.Sender.Send(new GetCustomerLedgerQuery(m.Customer.Id));
        long Sum(DuesEntryKind k) => ledger.Where(e => e.Kind == k).Sum(e => e.Amount.Value);
        Same(balance, Sum(DuesEntryKind.Charge) - Sum(DuesEntryKind.Payment) - Sum(DuesEntryKind.Reversal), where, "ledger sum");
        Same(m.Charges.Count, ledger.Count(e => e.Kind == DuesEntryKind.Charge), where, "charge rows");
        Same(m.Payments, Sum(DuesEntryKind.Payment), where, "payments");
        Assert.All(ledger, e => Assert.True(e.Amount.Value > 0, where + ": ledger amounts are always positive"));
        Assert.All(ledger, e => Assert.Equal(m.Customer.Id, e.CustomerId));
        // Exactly one reversal per voided udhaar bill, for that bill's full charge.
        var reversals = ledger.Where(e => e.Kind == DuesEntryKind.Reversal).OrderBy(e => e.BillId).Select(e => (e.BillId, e.Amount.Value)).ToList();
        var voided = m.Charges.Where(c => c.Void).OrderBy(c => (Guid?)c.BillId).Select(c => ((Guid?)c.BillId, c.Amount)).ToList();
        Assert.Equal(voided, reversals);

        var expectedOpen = m.Unpaid();
        var open = DuesAging.UnpaidCharges(ledger);
        Assert.Equal(expectedOpen.Select(o => ((Guid?)o.BillId, o.Remaining)), open.Select(o => (o.BillId, o.Remaining.Value)));
        Same(balance, open.Sum(o => o.Remaining.Value), where, "unpaid list total");

        int? expectedDays = expectedOpen.Count == 0 ? null
            : _h.Clock.Today.DayNumber - DateOnly.FromDateTime(expectedOpen[0].At.DateTime).DayNumber;
        Assert.Equal(expectedDays, DuesAging.OldestUnpaidDays(ledger, _h.Clock.Today));

        var report = (await _h.Sender.Send(new GetCustomerReportQuery(m.Customer.Id)))!;
        var live = m.Bills.Where(b => !b.Void).ToList();
        Same(balance, report.DuesBalance.Value, where, "report balance");
        Same(m.Payments, report.DuesPaidSoFar.Value, where, "report paid so far");
        Same(live.Count, report.BillCount, where, "report bill count");
        Same(live.Sum(b => b.Total), report.TotalSpent.Value, where, "report total spent");
        Same(live.Where(b => b.Tender == TenderType.Udhaar).Sum(b => b.Total), report.OnUdhaar.Value, where, "report on udhaar");
        Same(report.OnUdhaar.Value - report.DuesPaidSoFar.Value, report.DuesBalance.Value, where, "udhaar − paid = balance");
        Same(report.PaidCash.Value + report.PaidUpi.Value + report.OnUdhaar.Value, report.TotalSpent.Value, where, "tender split");
        Assert.Equal(expectedDays, report.OldestUnpaidDays);

        // The reminder list agrees with the book: nobody with nothing owed, and the same balance and age.
        var due = (await _h.Sender.Send(new GetDueRemindersQuery())).FirstOrDefault(d => d.CustomerId == m.Customer.Id);
        if (balance == 0) Assert.Null(due);
        if (due is not null)
        {
            Same(balance, due.Balance.Value, where, "reminder balance");
            Assert.Equal(expectedDays, due.OldestUnpaidDays);
        }
    }

    private static void Same(long expected, long actual, string where, string what) =>
        Assert.True(expected == actual, $"{where}: {what} expected {expected}, got {actual}");

    [Fact]
    public async Task OverPayment_ByOnePaisa_IsRejected_AndChangesNothing()
    {
        var (rice, _) = await SeedAsync();
        var ravi = await CustomerAsync("రవి", "9876543210");
        await SellAsync(TenderType.Udhaar, ravi.Id, (rice, 1000));   // ₹60

        var r = await _h.Sender.Send(new RecordDuesPaymentCommand(ravi.Id, new Paise(6001), null));
        Assert.Equal(ErrorCodes.DuesExceedsBalance, r.Error.Code);
        Assert.Equal("₹60.00", Assert.Single(r.Error.Args!));
        Assert.Equal(6000, (await _h.Sender.Send(new GetCustomerQuery(ravi.Id)))!.DuesBalance.Value);
        Assert.DoesNotContain(await _h.Sender.Send(new GetCustomerLedgerQuery(ravi.Id)), e => e.Kind == DuesEntryKind.Payment);

        Assert.True((await _h.Sender.Send(new RecordDuesPaymentCommand(ravi.Id, new Paise(6000), null))).IsSuccess);
        Assert.Equal(0, (await _h.Sender.Send(new GetCustomerQuery(ravi.Id)))!.DuesBalance.Value);
        // Nothing owed: any payment now is "more than owed", never a negative balance (shop owing the customer).
        Assert.Equal(ErrorCodes.DuesExceedsBalance, (await _h.Sender.Send(new RecordDuesPaymentCommand(ravi.Id, new Paise(1), null))).Error.Code);
    }

    [Fact]
    public async Task Payment_ForAnUnknownCustomer_IsCustomerNotFound() =>
        Assert.Equal(ErrorCodes.CustomerNotFound,
            (await _h.Sender.Send(new RecordDuesPaymentCommand(Guid.NewGuid(), new Paise(100), null))).Error.Code);

    [Fact]
    public async Task UpiDuesPayment_KeepsItsReference_AndTrimmedNote()
    {
        var (rice, _) = await SeedAsync();
        var ravi = await CustomerAsync("రవి", "9876543210");
        await SellAsync(TenderType.Udhaar, ravi.Id, (rice, 2000));
        Assert.True((await _h.Sender.Send(new RecordDuesPaymentCommand(ravi.Id, Paise.FromRupees(50), "  PhonePe  ", " 412345678901 "))).IsSuccess);
        var payment = Assert.Single(await _h.Sender.Send(new GetCustomerLedgerQuery(ravi.Id)), e => e.Kind == DuesEntryKind.Payment);
        Assert.Equal("PhonePe", payment.Note);
        Assert.Equal("412345678901", payment.UpiRef);
    }

    [Fact]
    public async Task VoidingTheOldUdhaar_MovesAnEarlierPaymentOntoTheNextBill()
    {
        var (rice, _) = await SeedAsync();
        var ravi = await CustomerAsync("రవి", "9876543210");
        _h.Clock.Now = new DateTimeOffset(2026, 10, 19, 11, 0, 0, TimeSpan.FromHours(5.5));
        var a = await SellAsync(TenderType.Udhaar, ravi.Id, (rice, 1000));   // ₹60 on 19 Oct
        _h.Clock.Advance(TimeSpan.FromDays(3));
        await SellAsync(TenderType.Udhaar, ravi.Id, (rice, 1000));           // ₹60 on 22 Oct
        await _h.Sender.Send(new RecordDuesPaymentCommand(ravi.Id, Paise.FromRupees(30), null));   // half of A
        _h.Clock.Advance(TimeSpan.FromDays(2));                              // 24 Oct

        Assert.True((await _h.Sender.Send(new VoidBillCommand(a.BillId, "wrong customer"))).IsSuccess);
        var report = (await _h.Sender.Send(new GetCustomerReportQuery(ravi.Id)))!;
        Assert.Equal(3000, report.DuesBalance.Value);
        Assert.Equal(2, report.OldestUnpaidDays);   // only B is left, half paid
        var open = Assert.Single(DuesAging.UnpaidCharges(await _h.Sender.Send(new GetCustomerLedgerQuery(ravi.Id))));
        Assert.Equal(3000, open.Remaining.Value);
    }

    [Fact]
    public async Task VoidOfAPartlyPaidUdhaar_IsBlocked_AndChangesNothing()
    {
        var (rice, _) = await SeedAsync();
        var ravi = await CustomerAsync("రవి", "9876543210");
        var a = await SellAsync(TenderType.Udhaar, ravi.Id, (rice, 1000));
        await _h.Sender.Send(new RecordDuesPaymentCommand(ravi.Id, Paise.FromRupees(10), null));
        var stock = await _h.StockAsync(rice.Id);

        Assert.Equal(ErrorCodes.BillVoidBlockedByPayments, (await _h.Sender.Send(new VoidBillCommand(a.BillId, "x"))).Error.Code);
        Assert.Equal(5000, (await _h.Sender.Send(new GetCustomerQuery(ravi.Id)))!.DuesBalance.Value);
        Assert.Equal(stock, await _h.StockAsync(rice.Id));
        Assert.Equal(BillStatus.Finalized, Assert.Single(await _h.Sender.Send(new GetCustomerBillsQuery(ravi.Id))).Status);
        Assert.DoesNotContain(await _h.Sender.Send(new GetCustomerLedgerQuery(ravi.Id)), e => e.Kind == DuesEntryKind.Reversal);
    }

    [Fact]
    public async Task UdhaarWithoutACustomer_IsRejected_BeforeAnythingIsWritten()
    {
        var (rice, _) = await SeedAsync();
        var draft = AppHost.Draft(TenderType.Udhaar, (rice, 1000));
        var r = await _h.Sender.Send(new FinalizeBillCommand(draft));
        Assert.Equal(ErrorCodes.BillUdhaarNeedsCustomer, r.Error.Code);
        Assert.Equal(1_000_000, await _h.StockAsync(rice.Id));   // 1,000 kg untouched
    }
}
