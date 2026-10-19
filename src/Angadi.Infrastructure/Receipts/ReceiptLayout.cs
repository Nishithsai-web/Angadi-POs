using System.Globalization;
using Angadi.Application.Billing;
using Angadi.Application.Common;
using Angadi.Application.Platform;
using Angadi.Domain.Billing;
using Angadi.Domain.Common;
using Angadi.Domain.Items;
using Angadi.Domain.Shop;

namespace Angadi.Infrastructure.Receipts;

/// <summary>What a receipt row is; the renderer picks font, alignment and spacing from this.</summary>
public enum ReceiptRowKind
{
    /// <summary>Shop name. Centred, large, bold.</summary>
    Title,
    /// <summary>"Tax Invoice" / "Bill of Supply". Centred, bold.</summary>
    Heading,
    /// <summary>Centred plain text (address, phone, GSTIN, void reason).</summary>
    Text,
    /// <summary>Large boxed void marker (FR-16).</summary>
    Void,
    Separator,
    /// <summary>Column header: <see cref="ReceiptRow.Text"/> left, <see cref="ReceiptRow.Value"/> right.</summary>
    Columns,
    /// <summary>
    /// A bill line: <see cref="ReceiptRow.Text"/> = Telugu name, <see cref="ReceiptRow.Secondary"/> = English name,
    /// <see cref="ReceiptRow.Middle"/> = "qty unit × price", <see cref="ReceiptRow.Value"/> = line amount.
    /// </summary>
    Item,
    /// <summary>Indented note under an item (line discount).</summary>
    ItemNote,
    /// <summary>Label left, value right (bill number, date, tender, cash received, change).</summary>
    Field,
    /// <summary>A totals row (subtotal, discount, GST total, rounding).</summary>
    Total,
    GrandTotal,
    /// <summary>Tax table header: slab, taxable, tax.</summary>
    TaxHeader,
    /// <summary>One GST slab: <see cref="ReceiptRow.Text"/> slab, <see cref="ReceiptRow.Middle"/> taxable, <see cref="ReceiptRow.Value"/> tax.</summary>
    Tax,
    /// <summary>UPI QR: <see cref="ReceiptRow.Text"/> = upi:// payload, <see cref="ReceiptRow.Secondary"/> = caption.</summary>
    Qr,
    Footer,
}

/// <summary>One row of a receipt. Pure data, so the content is testable without Skia.</summary>
public sealed record ReceiptRow(ReceiptRowKind Kind)
{
    public string Text { get; init; } = "";
    public string Secondary { get; init; } = "";
    public string Middle { get; init; } = "";
    public string Value { get; init; } = "";
}

/// <summary>
/// Builds the receipt content (FR-17, FR-18). Rules:
/// <list type="bullet">
/// <item>The line amount is <see cref="BillLine.LineTotal"/> (after the line discount, before the bill-discount
/// share). The lines then add up to <see cref="Bill.SubTotal"/>, and the bill discount prints once as its own
/// row. Using <see cref="BillLine.Net"/> would print the bill discount twice.</item>
/// <item>The per-slab tax table prints only on a Tax Invoice (FR-18).</item>
/// <item>The rounding row prints only when it is non-zero (FR-13).</item>
/// <item>The UPI QR prints for UPI, udhaar and untendered bills, and never for cash. A cash bill is settled, so
/// a QR would invite a second payment. For udhaar, the QR lets the customer clear the dues later. For UPI, v1
/// cannot confirm the payment, so the QR lets a customer whose payment failed retry it against the same bill
/// number (tn=). The QR is also left out for void bills, zero totals and shops with no UPI ID.</item>
/// </list>
/// </summary>
public static class ReceiptLayout
{
    public static IReadOnlyList<ReceiptRow> Build(Bill bill, ShopProfile shop, ILocalizer text)
    {
        ArgumentNullException.ThrowIfNull(bill);
        ArgumentNullException.ThrowIfNull(shop);
        ArgumentNullException.ThrowIfNull(text);

        var rows = new List<ReceiptRow>();
        var isVoid = bill.Status == BillStatus.Void;

        // Shop header.
        rows.Add(new(ReceiptRowKind.Title) { Text = shop.Name.Trim() });
        if (!string.IsNullOrWhiteSpace(shop.Address)) rows.Add(new(ReceiptRowKind.Text) { Text = shop.Address.Trim() });
        if (!string.IsNullOrWhiteSpace(shop.Phone)) rows.Add(new(ReceiptRowKind.Text) { Text = shop.Phone.Trim() });
        if (!string.IsNullOrWhiteSpace(shop.Gstin))
            rows.Add(new(ReceiptRowKind.Text) { Text = $"{text["receipt.gstin"]}: {shop.Gstin.Trim().ToUpperInvariant()}" });

        rows.Add(new(ReceiptRowKind.Heading) { Text = text[bill.IsTaxInvoice ? "bill.taxinvoice" : "bill.billofsupply"] });

        if (isVoid)
        {
            rows.Add(new(ReceiptRowKind.Void) { Text = text["receipt.void"] });
            if (!string.IsNullOrWhiteSpace(bill.VoidReason))
                rows.Add(new(ReceiptRowKind.Text) { Text = $"{text["bill.void.reason"]}: {bill.VoidReason.Trim()}" });
        }

        rows.Add(new(ReceiptRowKind.Separator));
        rows.Add(new(ReceiptRowKind.Field) { Text = text["receipt.bill"], Value = bill.Number });
        var when = bill.FinalizedAt ?? bill.CreatedAt;
        rows.Add(new(ReceiptRowKind.Field) { Text = text["receipt.date"], Value = FormatDate(when) });

        // Lines.
        rows.Add(new(ReceiptRowKind.Separator));
        rows.Add(new(ReceiptRowKind.Columns) { Text = text["receipt.item"], Value = text["receipt.amount"] });
        foreach (var line in bill.Lines)
        {
            var telugu = line.TeluguName.Trim();
            var english = line.EnglishName.Trim();
            var primary = telugu.Length > 0 ? telugu : english;
            var secondary = telugu.Length > 0 && !string.Equals(telugu, english, StringComparison.Ordinal) ? english : "";
            rows.Add(new(ReceiptRowKind.Item)
            {
                Text = primary,
                Secondary = secondary,
                Middle = $"{IndianFormat.Quantity(line.Qty)} {text.UnitLabel(line.Unit)} × {IndianFormat.Rupees(line.UnitPrice)}",
                Value = IndianFormat.Rupees(line.LineTotal),
            });

            var lineDiscount = line.Gross - line.LineTotal;
            if (lineDiscount > Paise.Zero)
                rows.Add(new(ReceiptRowKind.ItemNote)
                {
                    Text = DiscountLabel(text, line.LineDiscount),
                    Value = IndianFormat.Rupees(-lineDiscount),
                });
        }

        // Totals.
        rows.Add(new(ReceiptRowKind.Separator));
        rows.Add(new(ReceiptRowKind.Total) { Text = text["bill.subtotal"], Value = IndianFormat.Rupees(bill.SubTotal) });
        if (bill.BillDiscountAmount > Paise.Zero)
            rows.Add(new(ReceiptRowKind.Total)
            {
                Text = DiscountLabel(text, bill.BillDiscount),
                Value = IndianFormat.Rupees(-bill.BillDiscountAmount),
            });

        if (bill.IsTaxInvoice && bill.TaxSplits.Count > 0)
        {
            rows.Add(new(ReceiptRowKind.TaxHeader) { Text = text["bill.tax"], Middle = text["receipt.taxable"], Value = text["bill.tax"] });
            foreach (var split in bill.TaxSplits)
                rows.Add(new(ReceiptRowKind.Tax)
                {
                    Text = SlabLabel(split.Slab),
                    Middle = IndianFormat.Rupees(split.Taxable),
                    Value = IndianFormat.Rupees(split.Tax),
                });
            rows.Add(new(ReceiptRowKind.Total) { Text = text["bill.tax"], Value = IndianFormat.Rupees(bill.TaxTotal) });
        }

        if (bill.RoundingAdjustment != Paise.Zero)
            rows.Add(new(ReceiptRowKind.Total) { Text = text["bill.rounding"], Value = Signed(bill.RoundingAdjustment) });

        rows.Add(new(ReceiptRowKind.GrandTotal) { Text = text["bill.total"], Value = IndianFormat.Rupees(bill.GrandTotal) });

        // Tender.
        if (bill.Tender is { } tender)
        {
            rows.Add(new(ReceiptRowKind.Separator));
            rows.Add(new(ReceiptRowKind.Field) { Text = text["receipt.paidby"], Value = text[TenderKey(tender)] });
            if (tender == TenderType.Cash && bill.CashReceived > Paise.Zero)
            {
                var change = bill.CashReceived - bill.GrandTotal;
                if (change < Paise.Zero) change = Paise.Zero;
                rows.Add(new(ReceiptRowKind.Field) { Text = text["tender.received"], Value = IndianFormat.Rupees(bill.CashReceived) });
                rows.Add(new(ReceiptRowKind.Field) { Text = text["tender.change"], Value = IndianFormat.Rupees(change) });
            }
        }

        if (ShowsQr(bill, shop))
        {
            rows.Add(new(ReceiptRowKind.Separator));
            rows.Add(new(ReceiptRowKind.Qr)
            {
                Text = UpiLink.Build(shop.UpiId, shop.Name, bill.GrandTotal, bill.Number),
                Secondary = text["tender.upi.scan"],
            });
        }

        rows.Add(new(ReceiptRowKind.Separator));
        rows.Add(new(ReceiptRowKind.Footer) { Text = text["receipt.thanks"] });
        return rows;
    }

    /// <summary>The QR rule from the class summary.</summary>
    public static bool ShowsQr(Bill bill, ShopProfile shop) =>
        bill.Status != BillStatus.Void
        && bill.Tender != TenderType.Cash
        && !string.IsNullOrWhiteSpace(shop.UpiId)
        && bill.GrandTotal > Paise.Zero;

    /// <summary>The time the bill was finalized, in the offset it was recorded with (the shop's local time).</summary>
    internal static string FormatDate(DateTimeOffset when) =>
        when.ToString("dd-MM-yyyy HH:mm", CultureInfo.InvariantCulture);

    private static string TenderKey(TenderType tender) => tender switch
    {
        TenderType.Cash => "tender.cash",
        TenderType.Upi => "tender.upi",
        TenderType.Udhaar => "tender.udhaar",
        _ => "tender.pay",
    };

    private static string SlabLabel(GstSlab slab) =>
        (((int)slab) / 100m).ToString("0.##", CultureInfo.InvariantCulture) + "%";

    private static string DiscountLabel(ILocalizer text, Discount discount) =>
        discount.Kind == DiscountKind.PercentBasisPoints
            ? $"{text["bill.discount"]} ({(discount.Amount / 100m).ToString("0.##", CultureInfo.InvariantCulture)}%)"
            : text["bill.discount"];

    /// <summary>"+₹0.01" / "−₹0.49": rounding always shows its sign.</summary>
    private static string Signed(Paise amount) =>
        amount > Paise.Zero ? "+" + IndianFormat.Rupees(amount) : IndianFormat.Rupees(amount);
}
