using SQLite;

namespace Angadi.Infrastructure.Persistence;

// Storage shapes (ADR-12): Domain stays attribute-free; these map 1:1 to tables.
// Money = paise (long), quantity = milli (long), time = Unix ms UTC (long), day = yyyymmdd local (int).

[Table("Item")]
internal sealed class ItemRow
{
    [PrimaryKey] public Guid Id { get; set; }
    [NotNull] public string TeluguName { get; set; } = "";
    [NotNull] public string EnglishName { get; set; } = "";
    [NotNull] public string AliasesJson { get; set; } = "[]";
    public string LearnedAliasesJson { get; set; } = "[]";
    public string Category { get; set; } = "";
    public int Unit { get; set; }
    public long PricePaise { get; set; }
    public int GstBasisPoints { get; set; }
    public string? Hsn { get; set; }
    [Indexed(Name = "UX_Item_Barcode", Unique = true)] public string? Barcode { get; set; }
    public long StockMilli { get; set; }
    public long LowStockMilli { get; set; }
    public bool IsQuickSale { get; set; }
    [Indexed] public bool IsActive { get; set; }
    public long CreatedAtMs { get; set; }
    public long UpdatedAtMs { get; set; }
    public bool TracksBatches { get; set; }   // v2-stock (schema 5)
    public long CostPaise { get; set; }       // v2-stock: latest purchase cost per unit, before GST; owned by purchase entry
}

[Table("Bill")]
internal sealed class BillRow
{
    [PrimaryKey] public Guid Id { get; set; }
    [Indexed(Name = "UX_Bill_Number", Unique = true)] public string Number { get; set; } = "";
    public int Status { get; set; }
    public int BillDiscountKind { get; set; }
    public long BillDiscountAmount { get; set; }
    public int? Tender { get; set; }
    [Indexed] public Guid? CustomerId { get; set; }
    public long CashReceivedPaise { get; set; }
    public string? UpiRef { get; set; }
    public long SubTotalPaise { get; set; }
    public long BillDiscountPaise { get; set; }
    public long TaxTotalPaise { get; set; }
    public long RoundingPaise { get; set; }
    public long GrandTotalPaise { get; set; }
    public string TaxSplitsJson { get; set; } = "[]";
    public bool IsTaxInvoice { get; set; }
    public long? FinalizedAtMs { get; set; }
    public int FinalizedOffsetMinutes { get; set; }
    [Indexed] public int Day { get; set; }   // yyyymmdd in the shop's local time at finalize
    public long? VoidedAtMs { get; set; }
    public int VoidedOffsetMinutes { get; set; }
    public string? VoidReason { get; set; }
    public long CreatedAtMs { get; set; }
    public long UpdatedAtMs { get; set; }
}

[Table("BillLine")]
internal sealed class BillLineRow
{
    [PrimaryKey] public Guid Id { get; set; }
    [Indexed] public Guid BillId { get; set; }
    public int Position { get; set; }
    [Indexed] public Guid ItemId { get; set; }
    public string TeluguName { get; set; } = "";
    public string EnglishName { get; set; } = "";
    public int Unit { get; set; }
    public long UnitPricePaise { get; set; }
    public int GstBasisPoints { get; set; }
    public long QtyMilli { get; set; }
    public int LineDiscountKind { get; set; }
    public long LineDiscountAmount { get; set; }
    public long GrossPaise { get; set; }
    public long LineTotalPaise { get; set; }
    public long BillDiscountSharePaise { get; set; }
    public int Source { get; set; }
}

[Table("Customer")]
internal sealed class CustomerRow
{
    [PrimaryKey] public Guid Id { get; set; }
    [NotNull] public string Name { get; set; } = "";
    [Indexed] public string? Phone { get; set; }
    public long DuesBalancePaise { get; set; }
    public long CreatedAtMs { get; set; }
    public long UpdatedAtMs { get; set; }
    public string? Address { get; set; }
    public string? Notes { get; set; }
    public int? PromisedPayDay { get; set; }       // yyyymmdd
    public long? LastRemindedAtMs { get; set; }
}

[Table("DuesEntry")]
internal sealed class DuesEntryRow
{
    [PrimaryKey] public Guid Id { get; set; }
    [Indexed] public Guid CustomerId { get; set; }
    public int Kind { get; set; }
    public long AmountPaise { get; set; }
    public Guid? BillId { get; set; }
    public string? Note { get; set; }
    public string? UpiRef { get; set; }
    public long CreatedAtMs { get; set; }
}

[Table("StockMove")]
internal sealed class StockMoveRow
{
    [PrimaryKey] public Guid Id { get; set; }
    [Indexed] public Guid ItemId { get; set; }
    public long DeltaMilli { get; set; }
    public int Reason { get; set; }
    [Indexed] public Guid? BillId { get; set; }
    public string? Note { get; set; }
    [Indexed] public int Day { get; set; }
    public long CreatedAtMs { get; set; }
    [Indexed] public Guid? BatchId { get; set; }      // v2-stock (schema 5)
    [Indexed] public Guid? PurchaseId { get; set; }   // v2-stock (schema 5)
}

[Table("Outbox")]
internal sealed class OutboxRow
{
    [PrimaryKey] public Guid Id { get; set; }
    public string AggregateType { get; set; } = "";
    public Guid AggregateId { get; set; }
    public string EventType { get; set; } = "";
    public string PayloadJson { get; set; } = "";
    /// <summary>v2 retrofit: bump when an event's JSON shape changes; EventType names are stable contracts.</summary>
    public int PayloadVersion { get; set; } = 1;
    public long CreatedAtMs { get; set; }
    [Indexed] public int SyncState { get; set; }   // 0 = pending; v2 ISyncService drains
}

[Table("ShopProfile")]
internal sealed class ShopProfileRow
{
    [PrimaryKey] public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Address { get; set; } = "";
    public string Phone { get; set; } = "";
    public string UpiId { get; set; } = "";
    public string? Gstin { get; set; }
    public string BillPrefix { get; set; } = "A";
    public long NextBillSequence { get; set; } = 1;
    public int Language { get; set; }
    public long CreatedAtMs { get; set; }
    public long UpdatedAtMs { get; set; }
    public string? UpiQrPayload { get; set; }
    public byte[]? UpiQrImage { get; set; }   // in the database, so it travels in backups
}

[Table("Meta")]
internal sealed class MetaRow
{
    [PrimaryKey] public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}

[Table("Note")]
internal sealed class NoteRow
{
    [PrimaryKey] public Guid Id { get; set; }
    public string Text { get; set; } = "";
    public bool IsDone { get; set; }
    public long CreatedAtMs { get; set; }
    [Indexed] public long UpdatedAtMs { get; set; }
}

// ---- v2-stock (schema 5): batches, suppliers, purchases, purchase orders

[Table("Batch")]
internal sealed class BatchRow
{
    [PrimaryKey] public Guid Id { get; set; }
    [Indexed] public Guid ItemId { get; set; }
    public string BatchNo { get; set; } = "";
    public int? ExpiryDay { get; set; }   // yyyymmdd; null = unknown (the "no batch" placeholder)
    public long QtyMilli { get; set; }
    public long CostPaise { get; set; }
    public long CreatedAtMs { get; set; }
    public long UpdatedAtMs { get; set; }
}

[Table("Supplier")]
internal sealed class SupplierRow
{
    [PrimaryKey] public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string? Phone { get; set; }
    public string? Gstin { get; set; }
    public string? Notes { get; set; }
    public long CreatedAtMs { get; set; }
    public long UpdatedAtMs { get; set; }
}

[Table("Purchase")]
internal sealed class PurchaseRow
{
    [PrimaryKey] public Guid Id { get; set; }
    [Indexed] public Guid SupplierId { get; set; }
    public string SupplierName { get; set; } = "";
    public string SupplierBillNo { get; set; } = "";
    [Indexed] public int BillDay { get; set; }   // yyyymmdd of the supplier's bill
    public Guid? PurchaseOrderId { get; set; }
    public long TotalPaise { get; set; }
    public long CreatedAtMs { get; set; }
}

[Table("PurchaseLine")]
internal sealed class PurchaseLineRow
{
    [PrimaryKey] public Guid Id { get; set; }
    [Indexed] public Guid PurchaseId { get; set; }
    public int Position { get; set; }
    [Indexed] public Guid ItemId { get; set; }
    public string TeluguName { get; set; } = "";
    public string EnglishName { get; set; } = "";
    public int Unit { get; set; }
    public long QtyMilli { get; set; }
    public long CostPaise { get; set; }
    public int GstBasisPoints { get; set; }
    public string? BatchNo { get; set; }
    public int? ExpiryDay { get; set; }
    public Guid? BatchId { get; set; }
    public long LineTotalPaise { get; set; }
}

[Table("PurchaseOrder")]
internal sealed class PurchaseOrderRow
{
    [PrimaryKey] public Guid Id { get; set; }
    [Indexed] public Guid SupplierId { get; set; }
    public int Status { get; set; }
    public string LinesJson { get; set; } = "[]";   // [[itemId, qtyMilli], ...] — an order is a note to a supplier, not a ledger
    public Guid? PurchaseId { get; set; }
    public long CreatedAtMs { get; set; }
    public long UpdatedAtMs { get; set; }
}
