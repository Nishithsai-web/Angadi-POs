using System.Globalization;
using System.Text;
using Angadi.Domain.Common;

namespace Angadi.Maui.Areas.Pos;

/// <summary>
/// Pure parsing/formatting helpers for the counter's number entries. Always invariant culture
/// ('.' decimal point), Telugu digits (౦–౯) accepted, ',' treated as a grouping mark and ignored.
/// No MAUI types here, so this file can be linked into a unit test project as-is.
/// </summary>
public static class PosInput
{
    private const decimal MaxValue = 10_000_000m;   // guards long overflow on silly input

    /// <summary>Trims, maps Telugu digits to ASCII, drops ₹, spaces and grouping commas.</summary>
    public static string Normalise(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var sb = new StringBuilder(text.Length);
        foreach (var c in text.Trim())
        {
            if (c >= '౦' && c <= '౯') sb.Append((char)('0' + (c - '౦')));
            else if (c == ',' || c == '₹' || char.IsWhiteSpace(c)) continue;
            else sb.Append(c);
        }
        return sb.ToString();
    }

    public static bool TryParseDecimal(string? text, out decimal value)
    {
        value = 0;
        var s = Normalise(text);
        if (s.Length == 0) return false;
        return decimal.TryParse(s, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value)
               && value >= 0 && value <= MaxValue;
    }

    /// <summary>"1.5" → 1500 milli. Zero is allowed (the draft removes the line).</summary>
    public static bool TryParseQty(string? text, out Qty qty)
    {
        qty = Qty.Zero;
        if (!TryParseDecimal(text, out var v)) return false;
        qty = new Qty((long)Math.Round(v * 1000m, MidpointRounding.AwayFromZero));
        return true;
    }

    /// <summary>"60.5" → 6050 paise.</summary>
    public static bool TryParseRupees(string? text, out Paise paise)
    {
        paise = Paise.Zero;
        if (!TryParseDecimal(text, out var v)) return false;
        paise = new Paise((long)Math.Round(v * 100m, MidpointRounding.AwayFromZero));
        return true;
    }

    /// <summary>Empty or zero → Discount.None. Percent above 100 is rejected.</summary>
    public static bool TryParseDiscount(string? text, bool isPercent, out Discount discount)
    {
        discount = Discount.None;
        if (Normalise(text).Length == 0) return true;
        if (!TryParseDecimal(text, out var v)) return false;
        if (v == 0) return true;
        if (isPercent)
        {
            var bp = (long)Math.Round(v * 100m, MidpointRounding.AwayFromZero);
            if (bp > 10_000) return false;
            discount = Discount.Percent((int)bp);
            return true;
        }
        discount = Discount.Flat(new Paise((long)Math.Round(v * 100m, MidpointRounding.AwayFromZero)));
        return true;
    }

    /// <summary>Paise → editable text without ₹ or grouping: 6050 → "60.5", 6000 → "60".</summary>
    public static string PlainRupees(Paise paise) =>
        (paise.Value / 100m).ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>Discount → (isPercent, text) for the edit sheet. None defaults to percent mode.</summary>
    public static (bool IsPercent, string Text) ToEdit(Discount discount) => discount.Kind switch
    {
        DiscountKind.PercentBasisPoints => (true, (discount.Amount / 100m).ToString("0.##", CultureInfo.InvariantCulture)),
        DiscountKind.FlatPaise => (false, PlainRupees(new Paise(discount.Amount))),
        _ => (true, ""),
    };

    /// <summary>
    /// Customer phone to E.164 without '+' (Customer.Phone contract). Empty → null (allowed).
    /// 10 digits → 91 prefix; 12 digits starting 91 kept; anything else is invalid.
    /// </summary>
    public static bool TryNormalisePhone(string? text, out string? e164)
    {
        e164 = null;
        var digits = new string(Normalise(text).Where(char.IsAsciiDigit).ToArray());
        if (digits.Length == 0) return Normalise(text).Length == 0;
        if (digits.Length == 11 && digits[0] == '0') digits = digits[1..];
        if (digits.Length == 10) { e164 = "91" + digits; return true; }
        if (digits.Length == 12 && digits.StartsWith("91", StringComparison.Ordinal)) { e164 = digits; return true; }
        return false;
    }

    /// <summary>Typed into the search box and submitted: treat as a barcode (FR-09).</summary>
    public static bool LooksLikeBarcode(string? text)
    {
        var s = Normalise(text);
        return s.Length is >= 8 and <= 14 && s.All(char.IsAsciiDigit);
    }
}
