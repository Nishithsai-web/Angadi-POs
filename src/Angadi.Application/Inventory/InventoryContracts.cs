using Angadi.Application.Common;
using Angadi.Domain.Common;
using Angadi.Domain.Inventory;
using Angadi.Domain.Items;

namespace Angadi.Application.Inventory;

/// <summary>
/// Stock on hand right now: every active item, its stock, its value at selling price, low/out flags,
/// plus the ledger check — Item.Stock must equal the sum of its stock moves, always.
/// </summary>
public sealed record GetInventoryReportQuery : IQuery<InventoryReport>;

public sealed record InventoryReport(
    int ItemCount, int LowCount, int OutCount, Paise StockValue,
    IReadOnlyList<InventoryLine> Lines, IReadOnlyList<LedgerMismatch> Mismatches)
{
    /// <summary>True when every item's stock equals its ledger: nothing was changed outside a recorded move.</summary>
    public bool LedgerMatches => Mismatches.Count == 0;
}

public sealed record InventoryLine(Guid ItemId, string Name, Unit Unit, Qty Stock, Paise Value, bool IsLow, bool IsOut);
public sealed record LedgerMismatch(Guid ItemId, string Name, Qty Stock, Qty Ledger);

/// <summary>Every stock move (sales, voids, purchases, damage, count corrections) in the range, newest first.</summary>
public sealed record GetStockMovesQuery(DateOnly From, DateOnly To) : IQuery<IReadOnlyList<StockMoveLine>>;
public sealed record StockMoveLine(Guid ItemId, string Name, Unit Unit, Qty Delta, StockMoveReason Reason, DateTimeOffset At, string? Note);
