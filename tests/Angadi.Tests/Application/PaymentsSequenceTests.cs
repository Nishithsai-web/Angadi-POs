using Angadi.Application.Billing;
using Angadi.Application.Catalog;
using Angadi.Application.Common;
using Angadi.Application.Inventory;
using Angadi.Application.Reports;
using Angadi.Domain.Billing;
using Angadi.Domain.Common;
using Angadi.Domain.Customers;
using Angadi.Domain.Items;

namespace Angadi.Tests.Application;

/// <summary>
/// v2 payments soak: a seeded random run of cash / UPI / udhaar / split sales, partial returns (cash, UPI, dues),
/// dues payments and voids over several days, checked against an independent model after every day:
/// stock = ledger = model, dues = ledger = model, day-end and GST sums = the documents behind them.
/// </summary>
public sealed class PaymentsSequenceTests : IDisposable
{
    private const string Gstin = "36ABCDE1234F1Z5";
    private readonly AppHost _h = new();
    public PaymentsSequenceTests() => _h.Subscription.IsPro = true;
    public void Dispose() => _h.Dispose();

    private sealed record Sold(Bill Bill, DateOnly Day);

    [Theory]
    [InlineData(20261101)]
    [InlineData(7)]
    public async Task RandomSellSplitReturnPay_KeepsEveryBookExact(int seed)
    {
        var rng = new Random(seed);
        await _h.SeedShopAsync(Gstin);
        var items = new List<Item>
        {
            await _h.AddItemAsync("బియ్యం", 6_333, GstSlab.Five, 1_000_000),
            await _h.AddItemAsync("నూనె", 19_917, GstSlab.Eighteen, 1_000_000, Unit.Litre),
            await _h.AddItemAsync("సబ్బు", 3_499, GstSlab.Eighteen, 1_000_000, Unit.Piece),
            await _h.AddItemAsync("పాలు", 2_800, GstSlab.Zero, 1_000_000, Unit.Litre),
            await _h.AddItemAsync("చక్కెర", 4_550, GstSlab.Five, 1_000_000),
        };
        var customers = new List<Guid>();
        for (var i = 0; i < 3; i++) customers.Add((await _h.Sender.Send(new SaveCustomerCommand(new Customer { Name = "c" + i }))).Value);

        var stock = items.ToDictionary(i => i.Id, _ => 1_000_000L);
        var dues = customers.ToDictionary(c => c, _ => 0L);
        var sold = new List<Sold>();
        var returnsByDay = new Dictionary<DateOnly, long>();
        var hasReturn = new HashSet<Guid>();
        var days = new HashSet<DateOnly>();
        var laterVoided = new List<Sold>();   // T2-01: a bill voided on a later day stays in its own day's sales

        for (var step = 0; step < 160; step++)
        {
            if (rng.Next(12) == 0) _h.Clock.Advance(TimeSpan.FromHours(rng.Next(5, 30)));
            var today = _h.Clock.Today;
            days.Add(today);
            var op = rng.Next(10);
            if (op < 5 || sold.Count == 0)
            {
                // A sale: 1–3 lines, maybe a bill discount, a random tender (split half the time).
                var lines = Enumerable.Range(0, rng.Next(1, 4)).Select(_ => (items[rng.Next(items.Count)], (long)rng.Next(1, 40) * 250)).ToArray();
                var draft = AppHost.Draft(TenderType.Cash, lines);
                if (rng.Next(3) == 0) draft.BillDiscount = Discount.Percent(rng.Next(1, 15) * 100);
                var customer = customers[rng.Next(customers.Count)];
                var copy = AppHost.Draft(TenderType.Cash, lines);
                copy.BillDiscount = draft.BillDiscount;
                BillCalculator.Compute(copy, shopHasGstin: true);
                var total = copy.GrandTotal.Value;
                var kind = rng.Next(6);
                if (kind == 0) draft.Tender = TenderType.Upi;
                else if (kind == 1) { draft.Tender = TenderType.Udhaar; draft.CustomerId = customer; }
                else if (kind >= 3 && total >= 200)
                {
                    var first = (long)rng.Next(1, (int)(total / 100)) * 100;
                    var pair = rng.Next(3) switch
                    {
                        0 => (TenderType.Cash, TenderType.Upi),
                        1 => (TenderType.Cash, TenderType.Udhaar),
                        _ => (TenderType.Upi, TenderType.Udhaar),
                    };
                    draft.Tender = TenderType.Split;
                    draft.Payments =
                    [
                        new BillPayment { Tender = pair.Item1, Amount = new Paise(first) },
                        new BillPayment { Tender = pair.Item2, Amount = new Paise(total - first) },
                    ];
                    if (pair.Item2 == TenderType.Udhaar) draft.CustomerId = customer;
                    else if (rng.Next(2) == 0) draft.CustomerId = customer;
                }
                var r = await _h.Sender.Send(new FinalizeBillCommand(draft));
                Assert.True(r.IsSuccess, r.IsFailure ? r.Error.Code : "");
                var bill = (await _h.Sender.Send(new GetBillQuery(r.Value.BillId)))!;
                Assert.Equal(total, bill.GrandTotal.Value);
                Assert.Equal(bill.GrandTotal.Value, BillPayments.PartsOf(bill).Sum(p => p.Amount.Value));
                sold.Add(new Sold(bill, today));
                foreach (var l in bill.Lines) stock[l.ItemId] -= l.Qty.Milli;
                var udhaar = BillPayments.AmountBy(bill, TenderType.Udhaar).Value;
                if (udhaar > 0) dues[bill.CustomerId!.Value] += udhaar;
            }
            else if (op < 8)
            {
                // A partial return of what is left on a random bill.
                var pick = sold[rng.Next(sold.Count)];
                var state = (await _h.Sender.Send(new GetBillReturnsQuery(pick.Bill.Id)))!;
                if (!state.CanReturn) continue;
                var lines = state.Lines.Where(l => l.Remaining.Milli > 0 && rng.Next(2) == 0)
                    .Select(l => new ReturnRequestLine(l.Line.Id, new Qty(Math.Max(1, l.Remaining.Milli * rng.Next(1, 5) / 4)))).ToList();
                if (lines.Count == 0) continue;
                var method = (RefundMethod)rng.Next(pick.Bill.CustomerId is null ? 2 : 3);
                var r = await _h.Sender.Send(new RecordReturnCommand(Guid.NewGuid(), pick.Bill.Id, lines, method));
                Assert.True(r.IsSuccess, r.IsFailure ? r.Error.Code : "");
                var done = r.Value;
                Assert.Equal(done.RefundTotal.Value, done.CashRefund.Value + done.UpiRefund.Value + done.DuesCredit.Value);
                Assert.True(done.DuesCredit.Value >= 0);
                hasReturn.Add(pick.Bill.Id);
                foreach (var l in lines) stock[pick.Bill.Lines.Single(b => b.Id == l.BillLineId).ItemId] += l.Qty.Milli;
                if (done.DuesCredit.Value > 0) dues[pick.Bill.CustomerId!.Value] -= done.DuesCredit.Value;
                returnsByDay[today] = returnsByDay.GetValueOrDefault(today) + done.RefundTotal.Value;
            }
            else if (op == 8)
            {
                // A dues payment of part of what one customer owes.
                var c = customers[rng.Next(customers.Count)];
                if (dues[c] < 100) continue;
                var amount = (long)rng.Next(1, (int)(dues[c] / 100) + 1) * 100;
                var r = await _h.Sender.Send(new RecordDuesPaymentCommand(c, new Paise(amount), null));
                Assert.True(r.IsSuccess, r.IsFailure ? r.Error.Code : "");
                dues[c] -= amount;
            }
            else
            {
                // A void: refused once a return exists; otherwise it may be refused when the udhaar was paid off.
                var pick = sold[rng.Next(sold.Count)];
                var r = await _h.Sender.Send(new VoidBillCommand(pick.Bill.Id, "test", ConfirmPastMonth: true));
                if (hasReturn.Contains(pick.Bill.Id)) { Assert.Equal(ErrorCodes.BillHasReturns, r.Error.Code); continue; }
                if (r.IsFailure) { Assert.Equal(ErrorCodes.BillVoidBlockedByPayments, r.Error.Code); continue; }
                sold.Remove(pick);
                if (pick.Day != today) laterVoided.Add(pick);
                foreach (var l in pick.Bill.Lines) stock[l.ItemId] += l.Qty.Milli;
                var udhaar = BillPayments.AmountBy(pick.Bill, TenderType.Udhaar).Value;
                if (udhaar > 0) dues[pick.Bill.CustomerId!.Value] -= udhaar;
            }
        }

        // Stock: item = ledger = model.
        Assert.True((await _h.Sender.Send(new GetInventoryReportQuery())).LedgerMatches);
        foreach (var item in items) Assert.Equal(stock[item.Id], await _h.StockAsync(item.Id));

        // Dues: balance = ledger = model, never negative.
        foreach (var c in customers)
        {
            var balance = (await _h.Sender.Send(new GetCustomerQuery(c)))!.DuesBalance.Value;
            var ledger = (await _h.Sender.Send(new GetCustomerLedgerQuery(c))).Sum(e => e.Kind == DuesEntryKind.Charge ? e.Amount.Value : -e.Amount.Value);
            Assert.Equal(dues[c], balance);
            Assert.Equal(balance, ledger);
            Assert.True(balance >= 0);
        }

        // Day-end: each day's sales are its (unvoided) bills, tenders add up, returns are that day's refunds.
        foreach (var day in days)
        {
            var report = await _h.Sender.Send(new GetDayEndReportQuery(day));
            var bills = sold.Concat(laterVoided).Where(s => s.Day == day).Select(s => s.Bill).ToList();
            Assert.Equal(bills.Sum(b => b.GrandTotal.Value), report.SalesTotal.Value);
            Assert.Equal(report.SalesTotal.Value, report.Cash.Value + report.Upi.Value + report.Udhaar.Value);
            Assert.Equal(bills.Sum(b => BillPayments.AmountBy(b, TenderType.Cash).Value), report.Cash.Value);
            Assert.Equal(returnsByDay.GetValueOrDefault(day), report.Returns.Value);
            Assert.Equal(report.Returns.Value, report.CashRefunds.Value + report.UpiRefunds.Value + report.DuesCredited.Value);
            Assert.Equal(report.SalesTotal.Value - report.Returns.Value, report.NetSales.Value);
        }

        // GST over the whole run: the bills' slabs minus every return's reversed slabs.
        var from = days.Min();
        var to = days.Max();
        var gst = await _h.Sender.Send(new GetGstSummaryQuery(from, to));
        var allReturns = new List<SalesReturn>();
        foreach (var s in sold) allReturns.AddRange((await _h.Sender.Send(new GetBillReturnsQuery(s.Bill.Id)))!.Returns);
        foreach (var slab in gst)
        {
            var billTax = sold.SelectMany(s => s.Bill.TaxSplits).Where(t => t.Slab == slab.Slab).Sum(t => t.Tax.Value);
            var returnTax = allReturns.SelectMany(r => r.TaxSplits).Where(t => t.Slab == slab.Slab).Sum(t => t.Tax.Value);
            Assert.Equal(billTax - returnTax, slab.Tax.Value);
        }
        Assert.Equal(returnsByDay.Values.Sum(), allReturns.Sum(r => r.RefundTotal.Value));
    }
}
