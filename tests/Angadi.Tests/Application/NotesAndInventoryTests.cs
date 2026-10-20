using Angadi.Application.Billing;
using Angadi.Application.Catalog;
using Angadi.Application.Common;
using Angadi.Application.Inventory;
using Angadi.Application.Notes;
using Angadi.Domain.Billing;
using Angadi.Domain.Common;
using Angadi.Domain.Inventory;
using Angadi.Domain.Items;
using Angadi.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Angadi.Tests.Application;

/// <summary>Shop notes, in-app feedback, the inventory report and the ledger check.</summary>
public class NotesAndInventoryTests : IDisposable
{
    private readonly AppHost _h = new();
    public void Dispose() => _h.Dispose();

    // ---------- Notes ----------

    [Fact]
    public async Task Notes_AddEditDoneDelete_OpenOnesListedFirst()
    {
        var a = (await _h.Sender.Send(new SaveNoteCommand(null, "  వెంకట్ దగ్గర బియ్యం ఆర్డర్  "))).Value;
        _h.Clock.Advance(TimeSpan.FromMinutes(1));
        var b = (await _h.Sender.Send(new SaveNoteCommand(null, "Ravi pays Sunday"))).Value;

        Assert.True((await _h.Sender.Send(new SetNoteDoneCommand(b, true))).IsSuccess);
        var list = await _h.Sender.Send(new ListNotesQuery());
        Assert.Equal([a, b], list.Select(n => n.Id));
        Assert.Equal("వెంకట్ దగ్గర బియ్యం ఆర్డర్", list[0].Text);   // trimmed

        Assert.True((await _h.Sender.Send(new SaveNoteCommand(a, "edited"))).IsSuccess);
        Assert.True((await _h.Sender.Send(new DeleteNoteCommand(b))).IsSuccess);
        Assert.Equal("edited", Assert.Single(await _h.Sender.Send(new ListNotesQuery())).Text);
        Assert.Equal(ErrorCodes.NoteNotFound, (await _h.Sender.Send(new DeleteNoteCommand(b))).Error.Code);
    }

    [Fact]
    public async Task Notes_EmptyOrTooLong_AreRejected()
    {
        Assert.Equal(ErrorCodes.NoteTextRequired, (await _h.Sender.Send(new SaveNoteCommand(null, "   "))).Error.Code);
        Assert.Equal(ErrorCodes.NoteTooLong, (await _h.Sender.Send(new SaveNoteCommand(null, new string('x', 1001)))).Error.Code);
    }

    // ---------- Feedback ----------

    [Fact]
    public async Task Feedback_WithoutSupportNumber_OpensShareSheet_WithShopName()
    {
        await _h.SeedShopAsync();
        Assert.True((await _h.Sender.Send(new SendFeedbackCommand("Voice is great"))).IsSuccess);
        var text = Assert.Single(_h.Share.SharedText);
        Assert.Contains("Voice is great", text, StringComparison.Ordinal);
        Assert.Contains("రాము కిరాణా", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Feedback_WithSupportNumber_GoesToWhatsApp_AndCanAttachLogs()
    {
        using var h = new AppHost(configure: s => s.Configure<SupportOptions>(o => o.WhatsAppNumber = "98765 43210"));
        await h.SeedShopAsync();
        Assert.True((await h.Sender.Send(new SendFeedbackCommand("Please add expiry", AttachLogs: true))).IsSuccess);
        Assert.Equal("919876543210", Assert.Single(h.Share.WhatsApp).Phone);
        Assert.EndsWith(".zip", Assert.Single(h.Share.SharedFiles), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Feedback_Empty_IsRejected()
    {
        Assert.Equal(ErrorCodes.FeedbackTextRequired, (await _h.Sender.Send(new SendFeedbackCommand(" "))).Error.Code);
    }

    // ---------- Inventory ----------

    [Fact]
    public async Task Inventory_StockValueLowOut_AndLedgerMatchesAfterEveryKindOfMove()
    {
        await _h.SeedShopAsync();
        var rice = await _h.AddItemAsync("బియ్యం", 6000, stockMilli: 10_000);           // 10 kg × ₹60
        var dal = await _h.AddItemAsync("కంది పప్పు", 16000, stockMilli: 1_000);        // 1 kg
        dal.LowStockThreshold = Qty.Whole(2);
        await _h.Sender.Send(new SaveItemCommand(dal));
        await _h.AddItemAsync("ఉప్పు", 2800, stockMilli: 0, unit: Unit.Pack);              // out of stock

        // sale, void, purchase, damage, count correction
        var sold = await _h.Sender.Send(new FinalizeBillCommand(AppHost.Draft(TenderType.Cash, (rice, 2000))));
        await _h.Sender.Send(new VoidBillCommand(sold.Value.BillId, "wrong item"));
        await _h.Sender.Send(new FinalizeBillCommand(AppHost.Draft(TenderType.Cash, (rice, 1000))));
        await _h.Sender.Send(new AdjustStockCommand(rice.Id, Qty.Whole(5), StockMoveReason.Purchase, null));
        await _h.Sender.Send(new AdjustStockCommand(rice.Id, new Qty(-500), StockMoveReason.Damage, null));

        var r = await _h.Sender.Send(new GetInventoryReportQuery());
        Assert.True(r.LedgerMatches);
        Assert.Equal(3, r.ItemCount);
        Assert.Equal(1, r.LowCount);
        Assert.Equal(1, r.OutCount);
        Assert.Equal(13_500, await _h.StockAsync(rice.Id));                               // 10 − 2 + 2 − 1 + 5 − 0.5
        Assert.Equal(81_000 + 16_000, r.StockValue.Value);                                // 13.5 kg × ₹60 + 1 kg × ₹160

        var moves = await _h.Sender.Send(new GetStockMovesQuery(_h.Clock.Today, _h.Clock.Today));
        Assert.Contains(moves, m => m.Reason == StockMoveReason.VoidRestore && m.Delta.Milli == 2000);
        Assert.Contains(moves, m => m.Reason == StockMoveReason.Damage && m.Delta.Milli == -500);
    }

    [Fact]
    public async Task Inventory_StockEditedOutsideTheLedger_IsFlagged()
    {
        var rice = await _h.AddItemAsync("బియ్యం", 6000, stockMilli: 10_000);
        await _h.Services.GetRequiredService<AngadiDb>().RunAsync(c => c.Execute("UPDATE Item SET StockMilli = 99000 WHERE Id = ?", rice.Id));

        var r = await _h.Sender.Send(new GetInventoryReportQuery());
        var bad = Assert.Single(r.Mismatches);
        Assert.Equal(99_000, bad.Stock.Milli);
        Assert.Equal(10_000, bad.Ledger.Milli);
    }
}
