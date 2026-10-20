using Angadi.Domain.Billing;
using Angadi.Domain.Common;
using Angadi.Domain.Items;
using Angadi.Infrastructure.Persistence;

namespace Angadi.Tests.Infrastructure;

/// <summary>A busy shop's month: reports must load every bill with its lines, past any SQLite parameter limit.</summary>
public sealed class ScaleTests : IDisposable
{
    private readonly TempDb _t = new();
    public void Dispose() => _t.Dispose();

    [Fact]
    public async Task ManyBillsInOneRange_LoadWithAllTheirLines()
    {
        var bills = BillRepository.LineQueryChunk * 2 + 200;   // 1,200: more than 999 (old SQLite limit) and > 2 chunks
        var day = DateOnly.FromDateTime(_t.Clock.Now.DateTime);
        await _t.Db.ExecuteAsync(async () =>
        {
            for (var i = 0; i < bills; i++)
            {
                var bill = new Bill
                {
                    Number = $"A-{i + 1:D6}", Tender = TenderType.Cash, Status = BillStatus.Finalized, FinalizedAt = _t.Clock.Now,
                    Lines =
                    [
                        new BillLine { ItemId = Guid.NewGuid(), TeluguName = "బియ్యం", EnglishName = "Rice", Unit = Unit.Kilogram, UnitPrice = new Paise(6000), GstSlab = GstSlab.Five, Qty = Qty.Whole(1) },
                        new BillLine { ItemId = Guid.NewGuid(), TeluguName = "పాలు", EnglishName = "Milk", Unit = Unit.Pack, UnitPrice = new Paise(3000), GstSlab = GstSlab.Zero, Qty = Qty.Whole(2) },
                    ],
                };
                BillCalculator.Compute(bill, shopHasGstin: false);
                await _t.Bills.SaveAsync(bill);
            }
            return true;
        }, ok => ok);

        var loaded = await _t.Bills.ListByDayAsync(day, day);

        Assert.Equal(bills, loaded.Count);
        Assert.All(loaded, b => Assert.Equal(2, b.Lines.Count));
        Assert.Equal(bills * 12_000L, loaded.Sum(b => b.GrandTotal.Value));
    }
}
