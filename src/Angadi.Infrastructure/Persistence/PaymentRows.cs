using System.Text.Json;
using Angadi.Domain.Billing;
using Angadi.Domain.Common;
using Angadi.Domain.Items;
using SQLite;
using static Angadi.Infrastructure.Persistence.Mapping;

namespace Angadi.Infrastructure.Persistence;

// v2 split tender + returns (schema 4, docs/DB_SCHEMA.md). Additive tables only — no existing column changes.

/// <summary>One part of a bill's payment. Written for every bill finalized from schema 4 on; older bills have none.</summary>
[Table("BillPayment")]
internal sealed class BillPaymentRow
{
    [PrimaryKey] public Guid Id { get; set; }
    [Indexed] public Guid BillId { get; set; }
    public int Position { get; set; }
    public int Tender { get; set; }
    public long AmountPaise { get; set; }
    public string? UpiRef { get; set; }
    public long CashReceivedPaise { get; set; }
}

[Table("SalesReturn")]
internal sealed class SalesReturnRow
{
    [PrimaryKey] public Guid Id { get; set; }
    [Indexed(Name = "UX_SalesReturn_Number", Unique = true)] public string Number { get; set; } = "";
    [Indexed] public Guid BillId { get; set; }
    public string BillNumber { get; set; } = "";
    [Indexed] public Guid? CustomerId { get; set; }
    public long LinesTotalPaise { get; set; }
    public long RoundingPaise { get; set; }
    public long RefundTotalPaise { get; set; }
    public bool IsTaxInvoice { get; set; }
    public string TaxSplitsJson { get; set; } = "[]";
    public long TaxTotalPaise { get; set; }
    public int Method { get; set; }
    public long CashRefundPaise { get; set; }
    public long UpiRefundPaise { get; set; }
    public long DuesCreditPaise { get; set; }
    public string? UpiRef { get; set; }
    public string? Reason { get; set; }
    public long ReturnedAtMs { get; set; }
    public int ReturnedOffsetMinutes { get; set; }
    [Indexed] public int Day { get; set; }   // yyyymmdd local, the return's own day (reports net returns there)
    public long CreatedAtMs { get; set; }
}

[Table("ReturnLine")]
internal sealed class ReturnLineRow
{
    [PrimaryKey] public Guid Id { get; set; }
    [Indexed] public Guid ReturnId { get; set; }
    public int Position { get; set; }
    [Indexed] public Guid BillLineId { get; set; }
    [Indexed] public Guid ItemId { get; set; }
    public string TeluguName { get; set; } = "";
    public string EnglishName { get; set; } = "";
    public int Unit { get; set; }
    public int GstBasisPoints { get; set; }
    public long QtyMilli { get; set; }
    public long AmountPaise { get; set; }
}

internal static class PaymentMapping
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static BillPaymentRow ToRow(BillPayment p, Guid billId, int position) => new()
    {
        Id = p.Id, BillId = billId, Position = position, Tender = (int)p.Tender, AmountPaise = p.Amount.Value,
        UpiRef = string.IsNullOrWhiteSpace(p.UpiRef) ? null : p.UpiRef.Trim(), CashReceivedPaise = p.CashReceived.Value,
    };

    public static List<BillPayment> ToDomain(IEnumerable<BillPaymentRow> rows) => rows.OrderBy(r => r.Position).Select(r => new BillPayment
    {
        Id = r.Id, Tender = (TenderType)r.Tender, Amount = new Paise(r.AmountPaise), UpiRef = r.UpiRef, CashReceived = new Paise(r.CashReceivedPaise),
    }).ToList();

    public static SalesReturnRow ToRow(SalesReturn r) => new()
    {
        Id = r.Id, Number = r.Number, BillId = r.BillId, BillNumber = r.BillNumber, CustomerId = r.CustomerId,
        LinesTotalPaise = r.LinesTotal.Value, RoundingPaise = r.RoundingAdjustment.Value, RefundTotalPaise = r.RefundTotal.Value,
        IsTaxInvoice = r.IsTaxInvoice,
        TaxSplitsJson = JsonSerializer.Serialize(r.TaxSplits.Select(s => new[] { (long)s.Slab, s.Taxable.Value, s.Tax.Value }), Json),
        TaxTotalPaise = r.TaxTotal.Value, Method = (int)r.Method,
        CashRefundPaise = r.CashRefund.Value, UpiRefundPaise = r.UpiRefund.Value, DuesCreditPaise = r.DuesCredit.Value,
        UpiRef = r.UpiRef, Reason = r.Reason,
        ReturnedAtMs = Ms(r.ReturnedAt), ReturnedOffsetMinutes = (int)r.ReturnedAt.Offset.TotalMinutes, Day = Day(r.ReturnedAt),
        CreatedAtMs = Ms(r.CreatedAt),
    };

    public static ReturnLineRow ToRow(ReturnLine l, Guid returnId, int position) => new()
    {
        Id = l.Id, ReturnId = returnId, Position = position, BillLineId = l.BillLineId, ItemId = l.ItemId,
        TeluguName = l.TeluguName, EnglishName = l.EnglishName, Unit = (int)l.Unit, GstBasisPoints = (int)l.GstSlab,
        QtyMilli = l.Qty.Milli, AmountPaise = l.Amount.Value,
    };

    public static SalesReturn ToDomain(SalesReturnRow r, IEnumerable<ReturnLineRow> lines) => new()
    {
        Id = r.Id, Number = r.Number, BillId = r.BillId, BillNumber = r.BillNumber, CustomerId = r.CustomerId,
        LinesTotal = new Paise(r.LinesTotalPaise), RoundingAdjustment = new Paise(r.RoundingPaise), RefundTotal = new Paise(r.RefundTotalPaise),
        IsTaxInvoice = r.IsTaxInvoice,
        TaxSplits = (JsonSerializer.Deserialize<List<long[]>>(r.TaxSplitsJson, Json) ?? [])
            .Select(a => new TaxSplit((GstSlab)a[0], new Paise(a[1]), new Paise(a[2]))).ToList(),
        TaxTotal = new Paise(r.TaxTotalPaise), Method = (RefundMethod)r.Method,
        CashRefund = new Paise(r.CashRefundPaise), UpiRefund = new Paise(r.UpiRefundPaise), DuesCredit = new Paise(r.DuesCreditPaise),
        UpiRef = r.UpiRef, Reason = r.Reason,
        ReturnedAt = At(r.ReturnedAtMs, r.ReturnedOffsetMinutes),
        CreatedAt = At(r.CreatedAtMs), UpdatedAt = At(r.CreatedAtMs),
        Lines = lines.OrderBy(l => l.Position).Select(l => new ReturnLine
        {
            Id = l.Id, BillLineId = l.BillLineId, ItemId = l.ItemId, TeluguName = l.TeluguName, EnglishName = l.EnglishName,
            Unit = (Unit)l.Unit, GstSlab = (GstSlab)l.GstBasisPoints, Qty = new Qty(l.QtyMilli), Amount = new Paise(l.AmountPaise),
        }).ToList(),
    };
}
