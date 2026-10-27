using Angadi.Domain.Common;

namespace Angadi.Domain.Inventory;

/// <summary>Every stock change, without exception (FR-24/26, NFR-06). Item.Stock = Σ Delta.</summary>
public sealed class StockMove : Entity
{
    public Guid ItemId { get; set; }
    public Qty Delta { get; set; }   // negative for sales
    public StockMoveReason Reason { get; set; }
    public Guid? BillId { get; set; }
    public string? Note { get; set; }
}

/// <summary>Return (v2): goods brought back on a SalesReturn; BillId = the original bill, Note = the return number.</summary>
public enum StockMoveReason { Sale = 0, VoidRestore = 1, Purchase = 2, Damage = 3, CountCorrection = 4, Opening = 5, Return = 6 }
