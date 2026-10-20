using System.Text.Json;
using Angadi.Application.Billing;
using Angadi.Application.Catalog;
using Angadi.Application.Common;
using Angadi.Application.Notes;
using Angadi.Domain.Billing;
using Angadi.Domain.Common;
using Angadi.Domain.Customers;
using Angadi.Domain.Inventory;
using Angadi.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Angadi.Tests.Application;

/// <summary>
/// T3-05 / T3-04 (data-layer audit): the "outbox row lands in the same transaction" claim, proven by
/// counting rows — each money action appends exactly ONE pending row, and a failed finalize appends
/// none and consumes no quota.
/// </summary>
public class OutboxAtomicityTests : IDisposable
{
    private readonly AppHost _h = new();
    public void Dispose() => _h.Dispose();

    private sealed class PayloadRow { public string PayloadJson { get; set; } = ""; }

    private Task<int> RowsAsync(string? eventType = null)
    {
        var db = _h.Services.GetRequiredService<AngadiDb>();
        return eventType is null
            ? db.RunAsync(c => c.ExecuteScalar<int>("SELECT COUNT(*) FROM Outbox"))
            : db.RunAsync(c => c.ExecuteScalar<int>("SELECT COUNT(*) FROM Outbox WHERE EventType = ?", eventType));
    }

    [Fact]
    public async Task T3_05_EachMoneyAction_AppendsExactlyOnePendingOutboxRow()
    {
        await _h.SeedShopAsync();
        var ravi = new Customer { Name = "రవి", Phone = "98765 43210" };
        Assert.True((await _h.Sender.Send(new SaveCustomerCommand(ravi))).IsSuccess);
        var rice = await _h.AddItemAsync("బియ్యం", 6000, stockMilli: 100_000);

        // Finalize (cash) → exactly one BillFinalized.
        var draft = AppHost.Draft(TenderType.Cash, (rice, 1000));
        var fin = await _h.Sender.Send(new FinalizeBillCommand(draft));
        Assert.True(fin.IsSuccess, fin.Error.Code);
        Assert.Equal(1, await RowsAsync("BillFinalized"));

        // Void → exactly one BillVoided.
        Assert.True((await _h.Sender.Send(new VoidBillCommand(fin.Value.BillId, "wrong item"))).IsSuccess);
        Assert.Equal(1, await RowsAsync("BillVoided"));

        // Udhaar sale → its BillFinalized plus the dues seam's DuesCharged (M6, by design), then a dues
        // payment → exactly one DuesPaymentRecorded.
        var udhaar = AppHost.Draft(TenderType.Udhaar, (rice, 1000));
        udhaar.CustomerId = ravi.Id;
        Assert.True((await _h.Sender.Send(new FinalizeBillCommand(udhaar))).IsSuccess);
        Assert.Equal(2, await RowsAsync("BillFinalized"));
        Assert.Equal(1, await RowsAsync("DuesCharged"));
        Assert.True((await _h.Sender.Send(new RecordDuesPaymentCommand(ravi.Id, Paise.FromRupees(20), null))).IsSuccess);
        Assert.Equal(1, await RowsAsync("DuesPaymentRecorded"));

        // Stock adjust → exactly one StockAdjusted.
        Assert.True((await _h.Sender.Send(new AdjustStockCommand(rice.Id, new Qty(5000), StockMoveReason.CountCorrection, "bag torn"))).IsSuccess);
        Assert.Equal(1, await RowsAsync("StockAdjusted"));

        // Note save → exactly one NoteChanged.
        var note = await _h.Sender.Send(new SaveNoteCommand(null, "రేపు పాలు ఎక్కువ"));
        Assert.True(note.IsSuccess, note.Error.Code);
        Assert.Equal(1, await RowsAsync("NoteChanged"));

        // Every row ever written is still pending (SyncState 0) at payload version 1, and every payload
        // parses and names its aggregate + timestamp — the shape v2 sync will drain.
        var db = _h.Services.GetRequiredService<AngadiDb>();
        Assert.Equal(0, await db.RunAsync(c => c.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM Outbox WHERE SyncState <> 0 OR PayloadVersion <> 1")));
        var payloads = await db.RunAsync(c => c.Query<PayloadRow>("SELECT PayloadJson FROM Outbox").Select(r => r.PayloadJson).ToList());
        Assert.NotEmpty(payloads);
        foreach (var p in payloads)
        {
            using var doc = JsonDocument.Parse(p);
            Assert.True(doc.RootElement.TryGetProperty("aggregateId", out _), p);
            Assert.True(doc.RootElement.TryGetProperty("occurredAt", out _), p);
        }
    }

    [Fact]
    public async Task T3_04_FailedFinalize_AppendsNoOutboxRow_AndConsumesNoQuota()
    {
        await _h.SeedShopAsync();
        var rice = await _h.AddItemAsync("బియ్యం", 6000, stockMilli: 100_000);
        var rowsBefore = await RowsAsync();
        var quotaBefore = (await _h.Sender.Send(new GetBillQuotaQuery())).UsedThisMonth;

        var draft = AppHost.Draft(TenderType.Cash, (rice, 1000));
        draft.CustomerId = Guid.NewGuid();                     // a customer that does not exist
        var r = await _h.Sender.Send(new FinalizeBillCommand(draft));
        Assert.True(r.IsFailure);
        Assert.Equal(ErrorCodes.CustomerNotFound, r.Error.Code);

        Assert.Equal(rowsBefore, await RowsAsync());           // the transaction left nothing behind
        Assert.Equal(quotaBefore, (await _h.Sender.Send(new GetBillQuotaQuery())).UsedThisMonth);
    }
}
