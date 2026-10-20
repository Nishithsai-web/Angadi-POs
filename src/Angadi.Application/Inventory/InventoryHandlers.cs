using Angadi.Application.Common;
using Angadi.Application.Persistence;
using Angadi.Application.Platform;
using Angadi.Domain.Common;

namespace Angadi.Application.Inventory;

internal sealed class InventoryHandlers(IItemRepository items, IStockLedger ledger, ILocalizer text) :
    IRequestHandler<GetInventoryReportQuery, InventoryReport>,
    IRequestHandler<GetStockMovesQuery, IReadOnlyList<StockMoveLine>>
{
    public async Task<InventoryReport> Handle(GetInventoryReportQuery q, CancellationToken ct)
    {
        var active = await items.GetActiveAsync(ct);
        var totals = await ledger.LedgerTotalsAsync(ct);

        var lines = active
            .Select(i => new InventoryLine(i.Id, text.Name(i), i.Unit, i.Stock,
                // value at selling price, half up; negative stock (sold before stock-in) counts as zero value
                new Paise(i.Stock.Milli <= 0 ? 0 : (2 * i.Price.Value * i.Stock.Milli + 1000) / 2000),
                i.IsLowStock, i.Stock.Milli <= 0))
            .OrderBy(l => !l.IsOut).ThenBy(l => !l.IsLow).ThenBy(l => l.Name, StringComparer.Ordinal)
            .ToList();

        var mismatches = active
            .Select(i => (item: i, ledger: totals.TryGetValue(i.Id, out var sum) ? sum : 0L))
            .Where(x => x.item.Stock.Milli != x.ledger)
            .Select(x => new LedgerMismatch(x.item.Id, text.Name(x.item), x.item.Stock, new Qty(x.ledger)))
            .ToList();

        return new InventoryReport(
            lines.Count, lines.Count(l => l.IsLow && !l.IsOut), lines.Count(l => l.IsOut),
            new Paise(lines.Sum(l => l.Value.Value)), lines, mismatches);
    }

    public async Task<IReadOnlyList<StockMoveLine>> Handle(GetStockMovesQuery q, CancellationToken ct)
    {
        var moves = await ledger.ListMovesAsync(q.From, q.To, ct);
        var byId = (await items.GetActiveAsync(ct)).ToDictionary(i => i.Id);
        var result = new List<StockMoveLine>(moves.Count);
        foreach (var m in moves)
        {
            var item = byId.TryGetValue(m.ItemId, out var found) ? found : await items.GetAsync(m.ItemId, ct);
            result.Add(new StockMoveLine(m.ItemId, item is null ? "?" : text.Name(item), item?.Unit ?? default,
                m.Delta, m.Reason, m.CreatedAt, m.Note));
        }
        return result;
    }
}
