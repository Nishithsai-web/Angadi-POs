using Angadi.Application.Common;
using Angadi.Application.Platform;
using Angadi.Domain.Inventory;
using Angadi.Domain.Items;

namespace Angadi.Maui.Areas.Items;

/// <summary>One row on the Items and Low-stock lists. Display strings are computed once (compiled bindings, no converters).</summary>
public sealed class ItemRow
{
    private readonly Item _item;

    public ItemRow(Item item, ILocalizer text)
    {
        _item = item;
        Id = item.Id;
        var te = (item.TeluguName ?? "").Trim();
        var en = (item.EnglishName ?? "").Trim();
        PrimaryName = te.Length > 0 ? te : en;
        SecondaryName = te.Length > 0 ? en : "";
        Category = (item.Category ?? "").Trim();
        PriceText = IndianFormat.Rupees(item.Price);
        StockText = ItemsInput.QtyWithUnit(text, item.Stock, item.Unit);
        ThresholdText = text.Format("items.lowstock.row", ItemsInput.QtyWithUnit(text, item.LowStockThreshold, item.Unit));
        IsLow = item.IsLowStock;
        IsQuickSale = item.IsQuickSale;
    }

    public Guid Id { get; }
    public string PrimaryName { get; }
    public string SecondaryName { get; }
    public bool HasSecondary => SecondaryName.Length > 0;
    public string Category { get; }
    public string PriceText { get; }
    public string StockText { get; }
    public string ThresholdText { get; }
    public bool IsLow { get; }
    public bool IsQuickSale { get; }

    /// <summary>FR-05: Telugu name, English name, alias or barcode. Case-insensitive contains.</summary>
    internal bool Matches(string term)
    {
        if (Contains(_item.TeluguName, term) || Contains(_item.EnglishName, term)) return true;
        if (_item.Barcode is { Length: > 0 } barcode && barcode.Contains(term, StringComparison.OrdinalIgnoreCase)) return true;
        return _item.Aliases is { } aliases && aliases.Any(a => Contains(a, term));
    }

    private static bool Contains(string? haystack, string term) =>
        !string.IsNullOrEmpty(haystack) && haystack.Contains(term, StringComparison.OrdinalIgnoreCase);
}

/// <summary>One row of an item's stock history (FR-26).</summary>
public sealed class StockMoveRow
{
    public StockMoveRow(StockMove move, Unit unit, ILocalizer text)
    {
        DateText = move.CreatedAt.ToLocalTime().ToString("dd-MM-yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        ReasonText = text[ReasonKey(move.Reason)];
        QtyText = ItemsInput.SignedQtyWithUnit(text, move.Delta, unit);
        IsNegative = move.Delta.Milli < 0;
        Note = (move.Note ?? "").Trim();
    }

    public string DateText { get; }
    public string ReasonText { get; }
    public string QtyText { get; }
    public bool IsNegative { get; }
    public string Note { get; }
    public bool HasNote => Note.Length > 0;

    public static string ReasonKey(StockMoveReason reason) => reason switch
    {
        StockMoveReason.Sale => "items.reason.sale",
        StockMoveReason.VoidRestore => "items.reason.voidrestore",
        StockMoveReason.Purchase => "stock.reason.purchase",
        StockMoveReason.Damage => "stock.reason.damage",
        StockMoveReason.CountCorrection => "stock.reason.count",
        StockMoveReason.Opening => "items.reason.opening",
        _ => "items.reason.other",
    };
}
