using System.Globalization;
using Angadi.Application.Common;
using Angadi.Application.Platform;
using Angadi.Domain.Common;
using ItemUnit = Angadi.Domain.Items.Unit;

namespace Angadi.Maui.Areas.Items;

/// <summary>
/// Parses what the shopkeeper types into exact integer storage (Qty milli, Paise) without ever throwing.
/// Invariant culture, '.' as the decimal point, no thousands separators, no sign (signs come from the reason).
/// </summary>
public static class ItemsInput
{
    private const NumberStyles Styles = NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite;

    /// <summary>Upper bound so value * 1000 always fits a long (and catches typos like 1e12).</summary>
    private const decimal MaxValue = 100_000_000m;

    public static bool TryParseDecimal(string? text, out decimal value)
    {
        value = 0m;
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (!decimal.TryParse(text.Trim(), Styles, CultureInfo.InvariantCulture, out var parsed)) return false;
        if (parsed < 0m || parsed > MaxValue) return false;
        value = parsed;
        return true;
    }

    /// <summary>"2.5" → 2500 milli. Rounded half away from zero to 3 decimals.</summary>
    public static bool TryParseQty(string? text, out Qty qty)
    {
        qty = Qty.Zero;
        if (!TryParseDecimal(text, out var value)) return false;
        qty = new Qty((long)decimal.Round(value * 1000m, 0, MidpointRounding.AwayFromZero));
        return true;
    }

    /// <summary>Empty means zero (optional fields such as threshold and opening stock).</summary>
    public static bool TryParseOptionalQty(string? text, out Qty qty)
    {
        if (string.IsNullOrWhiteSpace(text)) { qty = Qty.Zero; return true; }
        return TryParseQty(text, out qty);
    }

    /// <summary>"45.5" → 4550 paise. Rounded half away from zero to 2 decimals.</summary>
    public static bool TryParsePaise(string? text, out Paise paise)
    {
        paise = Paise.Zero;
        if (!TryParseDecimal(text, out var value)) return false;
        paise = new Paise((long)decimal.Round(value * 100m, 0, MidpointRounding.AwayFromZero));
        return true;
    }

    /// <summary>Value for an Entry: 4550 → "45.5", 4500 → "45".</summary>
    public static string PaiseForEntry(Paise paise) =>
        (paise.Value / 100m).ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>Value for an Entry: 2500 → "2.5". Zero shows as empty so the field looks optional.</summary>
    public static string QtyForEntry(Qty qty) => qty.Milli == 0 ? "" : IndianFormat.Quantity(qty);

    /// <summary>"2.5 కేజీ".</summary>
    public static string QtyWithUnit(ILocalizer text, Qty qty, ItemUnit unit) =>
        text.Format("items.qty.unit", IndianFormat.Quantity(qty), text.UnitLabel(unit));

    /// <summary>"+2 కేజీ" / "−0.5 కేజీ" for the stock ledger.</summary>
    public static string SignedQtyWithUnit(ILocalizer text, Qty delta, ItemUnit unit)
    {
        var sign = delta.Milli < 0 ? "−" : "+";
        return sign + QtyWithUnit(text, new Qty(Math.Abs(delta.Milli)), unit);
    }
}
