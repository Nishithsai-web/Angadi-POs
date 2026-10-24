using System.Text.Json;
using Angadi.Domain.Billing;
using Angadi.Domain.Common;
using Angadi.Domain.Customers;
using Angadi.Domain.Inventory;
using Angadi.Domain.Items;
using Angadi.Domain.Shop;

namespace Angadi.Infrastructure.Persistence;

internal static class Mapping
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static long Ms(DateTimeOffset t) => t.ToUnixTimeMilliseconds();
    public static DateTimeOffset At(long ms, int offsetMinutes = 0)
        => DateTimeOffset.FromUnixTimeMilliseconds(ms).ToOffset(TimeSpan.FromMinutes(offsetMinutes));
    public static int Day(DateOnly d) => d.Year * 10000 + d.Month * 100 + d.Day;
    public static int Day(DateTimeOffset t) => Day(DateOnly.FromDateTime(t.DateTime));
    public static DateOnly FromDay(int day) => new(day / 10000, day / 100 % 100, day % 100);

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    // ---- Item
    public static ItemRow ToRow(Item i) => new()
    {
        Id = i.Id, TeluguName = i.TeluguName.Trim(), EnglishName = i.EnglishName.Trim(),
        AliasesJson = JsonSerializer.Serialize(i.Aliases, Json), LearnedAliasesJson = JsonSerializer.Serialize(i.LearnedAliases, Json), Category = i.Category,
        Unit = (int)i.Unit, PricePaise = i.Price.Value, GstBasisPoints = (int)i.GstSlab, Hsn = NullIfBlank(i.Hsn),
        Barcode = NullIfBlank(i.Barcode), StockMilli = i.Stock.Milli, LowStockMilli = i.LowStockThreshold.Milli,
        IsQuickSale = i.IsQuickSale, IsActive = i.IsActive, CreatedAtMs = Ms(i.CreatedAt), UpdatedAtMs = Ms(i.UpdatedAt),
        TracksBatches = i.TracksBatches, CostPaise = i.Cost.Value,   // v2-stock
    };

    public static Item ToDomain(ItemRow r) => new()
    {
        Id = r.Id, TeluguName = r.TeluguName, EnglishName = r.EnglishName,
        Aliases = JsonSerializer.Deserialize<List<string>>(r.AliasesJson, Json) ?? [],
        LearnedAliases = string.IsNullOrEmpty(r.LearnedAliasesJson) ? [] : JsonSerializer.Deserialize<List<LearnedAlias>>(r.LearnedAliasesJson, Json) ?? [],
        Category = r.Category, Unit = (Unit)r.Unit, Price = new Paise(r.PricePaise), GstSlab = (GstSlab)r.GstBasisPoints,
        Hsn = r.Hsn, Barcode = r.Barcode, Stock = new Qty(r.StockMilli), LowStockThreshold = new Qty(r.LowStockMilli),
        IsQuickSale = r.IsQuickSale, IsActive = r.IsActive, CreatedAt = At(r.CreatedAtMs), UpdatedAt = At(r.UpdatedAtMs),
        TracksBatches = r.TracksBatches, Cost = new Paise(r.CostPaise),   // v2-stock
    };

    // ---- Bill
    public static BillRow ToRow(Bill b) => new()
    {
        Id = b.Id, Number = b.Number, Status = (int)b.Status,
        BillDiscountKind = (int)b.BillDiscount.Kind, BillDiscountAmount = b.BillDiscount.Amount,
        Tender = b.Tender is null ? null : (int)b.Tender, CustomerId = b.CustomerId, CashReceivedPaise = b.CashReceived.Value, UpiRef = NullIfBlank(b.UpiRef),
        SubTotalPaise = b.SubTotal.Value, BillDiscountPaise = b.BillDiscountAmount.Value, TaxTotalPaise = b.TaxTotal.Value,
        RoundingPaise = b.RoundingAdjustment.Value, GrandTotalPaise = b.GrandTotal.Value,
        TaxSplitsJson = JsonSerializer.Serialize(b.TaxSplits.Select(s => new[] { (long)s.Slab, s.Taxable.Value, s.Tax.Value }), Json),
        IsTaxInvoice = b.IsTaxInvoice,
        FinalizedAtMs = b.FinalizedAt is null ? null : Ms(b.FinalizedAt.Value),
        FinalizedOffsetMinutes = b.FinalizedAt is null ? 0 : (int)b.FinalizedAt.Value.Offset.TotalMinutes,
        Day = b.FinalizedAt is null ? 0 : Day(b.FinalizedAt.Value),
        VoidedAtMs = b.VoidedAt is null ? null : Ms(b.VoidedAt.Value),
        VoidedOffsetMinutes = b.VoidedAt is null ? 0 : (int)b.VoidedAt.Value.Offset.TotalMinutes, VoidReason = b.VoidReason,
        CreatedAtMs = Ms(b.CreatedAt), UpdatedAtMs = Ms(b.UpdatedAt),
    };

    public static BillLineRow ToRow(BillLine l, Guid billId, int position) => new()
    {
        Id = l.Id, BillId = billId, Position = position, ItemId = l.ItemId, TeluguName = l.TeluguName, EnglishName = l.EnglishName,
        Unit = (int)l.Unit, UnitPricePaise = l.UnitPrice.Value, GstBasisPoints = (int)l.GstSlab, QtyMilli = l.Qty.Milli,
        LineDiscountKind = (int)l.LineDiscount.Kind, LineDiscountAmount = l.LineDiscount.Amount,
        GrossPaise = l.Gross.Value, LineTotalPaise = l.LineTotal.Value, BillDiscountSharePaise = l.BillDiscountShare.Value,
        Source = (int)l.Source,
    };

    public static Bill ToDomain(BillRow r, IEnumerable<BillLineRow> lines) => new()
    {
        Id = r.Id, Number = r.Number, Status = (BillStatus)r.Status,
        BillDiscount = new Discount((DiscountKind)r.BillDiscountKind, r.BillDiscountAmount),
        Tender = r.Tender is null ? null : (TenderType)r.Tender, CustomerId = r.CustomerId, CashReceived = new Paise(r.CashReceivedPaise), UpiRef = r.UpiRef,
        SubTotal = new Paise(r.SubTotalPaise), BillDiscountAmount = new Paise(r.BillDiscountPaise), TaxTotal = new Paise(r.TaxTotalPaise),
        RoundingAdjustment = new Paise(r.RoundingPaise), GrandTotal = new Paise(r.GrandTotalPaise),
        TaxSplits = (JsonSerializer.Deserialize<List<long[]>>(r.TaxSplitsJson, Json) ?? [])
            .Select(a => new TaxSplit((GstSlab)a[0], new Paise(a[1]), new Paise(a[2]))).ToList(),
        IsTaxInvoice = r.IsTaxInvoice,
        FinalizedAt = r.FinalizedAtMs is null ? null : At(r.FinalizedAtMs.Value, r.FinalizedOffsetMinutes),
        VoidedAt = r.VoidedAtMs is null ? null : At(r.VoidedAtMs.Value, r.VoidedOffsetMinutes), VoidReason = r.VoidReason,   // M12
        CreatedAt = At(r.CreatedAtMs), UpdatedAt = At(r.UpdatedAtMs),
        Lines = lines.OrderBy(l => l.Position).Select(l => new BillLine
        {
            Id = l.Id, ItemId = l.ItemId, TeluguName = l.TeluguName, EnglishName = l.EnglishName, Unit = (Unit)l.Unit,
            UnitPrice = new Paise(l.UnitPricePaise), GstSlab = (GstSlab)l.GstBasisPoints, Qty = new Qty(l.QtyMilli),
            LineDiscount = new Discount((DiscountKind)l.LineDiscountKind, l.LineDiscountAmount),
            Gross = new Paise(l.GrossPaise), LineTotal = new Paise(l.LineTotalPaise),
            BillDiscountShare = new Paise(l.BillDiscountSharePaise), Source = (LineSource)l.Source,
        }).ToList(),
    };

    // ---- Customer
    public static CustomerRow ToRow(Customer c) => new()
    {
        Id = c.Id, Name = c.Name.Trim(), Phone = NullIfBlank(c.Phone), DuesBalancePaise = c.DuesBalance.Value,
        CreatedAtMs = Ms(c.CreatedAt), UpdatedAtMs = Ms(c.UpdatedAt),
        Address = NullIfBlank(c.Address), Notes = NullIfBlank(c.Notes),
        PromisedPayDay = c.PromisedPayDate is { } d ? Day(d) : null,
        LastRemindedAtMs = c.LastRemindedAt is { } t ? Ms(t) : null,
    };

    public static Customer ToDomain(CustomerRow r) => new()
    {
        Id = r.Id, Name = r.Name, Phone = r.Phone, DuesBalance = new Paise(r.DuesBalancePaise),
        CreatedAt = At(r.CreatedAtMs), UpdatedAt = At(r.UpdatedAtMs),
        Address = r.Address, Notes = r.Notes,
        PromisedPayDate = r.PromisedPayDay is { } d ? FromDay(d) : null,
        LastRemindedAt = r.LastRemindedAtMs is { } t ? At(t) : null,
    };

    public static DuesEntryRow ToRow(DuesEntry e) => new()
    {
        Id = e.Id, CustomerId = e.CustomerId, Kind = (int)e.Kind, AmountPaise = e.Amount.Value, BillId = e.BillId, Note = e.Note, UpiRef = NullIfBlank(e.UpiRef),
        CreatedAtMs = Ms(e.CreatedAt),
    };

    public static DuesEntry ToDomain(DuesEntryRow r) => new()
    {
        Id = r.Id, CustomerId = r.CustomerId, Kind = (DuesEntryKind)r.Kind, Amount = new Paise(r.AmountPaise), BillId = r.BillId, Note = r.Note, UpiRef = r.UpiRef,
        CreatedAt = At(r.CreatedAtMs), UpdatedAt = At(r.CreatedAtMs),
    };

    public static StockMove ToDomain(StockMoveRow r) => new()
    {
        Id = r.Id, ItemId = r.ItemId, Delta = new Qty(r.DeltaMilli), Reason = (StockMoveReason)r.Reason, BillId = r.BillId, Note = r.Note,
        CreatedAt = At(r.CreatedAtMs), UpdatedAt = At(r.CreatedAtMs), BatchId = r.BatchId, PurchaseId = r.PurchaseId,
    };

    // ---- Note
    public static NoteRow ToRow(Angadi.Domain.Notes.Note n) => new()
    {
        Id = n.Id, Text = n.Text, IsDone = n.IsDone, CreatedAtMs = Ms(n.CreatedAt), UpdatedAtMs = Ms(n.UpdatedAt),
    };

    public static Angadi.Domain.Notes.Note ToDomain(NoteRow r) => new()
    {
        Id = r.Id, Text = r.Text, IsDone = r.IsDone, CreatedAt = At(r.CreatedAtMs), UpdatedAt = At(r.UpdatedAtMs),
    };

    // ---- Shop
    public static ShopProfileRow ToRow(ShopProfile s) => new()
    {
        Id = s.Id, Name = s.Name.Trim(), Address = s.Address.Trim(), Phone = s.Phone.Trim(), UpiId = s.UpiId.Trim(),
        Gstin = NullIfBlank(s.Gstin)?.ToUpperInvariant(), BillPrefix = string.IsNullOrWhiteSpace(s.BillPrefix) ? "A" : s.BillPrefix.Trim(),
        NextBillSequence = Math.Max(1, s.NextBillSequence), Language = (int)s.Language,
        CreatedAtMs = Ms(s.CreatedAt), UpdatedAtMs = Ms(s.UpdatedAt),
        UpiQrPayload = NullIfBlank(s.UpiQrPayload), UpiQrImage = s.UpiQrImage is { Length: > 0 } img ? img : null,
    };

    public static ShopProfile ToDomain(ShopProfileRow r) => new()
    {
        Id = r.Id, Name = r.Name, Address = r.Address, Phone = r.Phone, UpiId = r.UpiId, Gstin = r.Gstin,
        BillPrefix = r.BillPrefix, NextBillSequence = r.NextBillSequence, Language = (UiLanguage)r.Language,
        CreatedAt = At(r.CreatedAtMs), UpdatedAt = At(r.UpdatedAtMs),
        UpiQrPayload = r.UpiQrPayload, UpiQrImage = r.UpiQrImage,
    };
}
