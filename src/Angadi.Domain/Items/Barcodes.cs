namespace Angadi.Domain.Items;

/// <summary>
/// One form for every barcode, so the same packet always finds the same item:
///   • spaces and hyphens removed;
///   • a 12-digit UPC-A becomes its 13-digit EAN-13 form (a leading 0) — scanners report either;
///   • EAN-13 / UPC-A check digits are verified, so a misread or a typo can't land on the wrong item.
/// Shop-made codes of any other shape are kept as typed.
/// </summary>
public static class Barcodes
{
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var code = new string(raw.Where(c => !char.IsWhiteSpace(c) && c != '-').ToArray());
        if (code.Length == 12 && code.All(char.IsAsciiDigit)) code = "0" + code;
        return code.Length == 0 ? null : code;
    }

    /// <summary>The other stored form of the same code (pre-normalization data): EAN-13 starting with 0 ↔ UPC-A.</summary>
    public static string? Alternate(string normalized) =>
        normalized.Length == 13 && normalized[0] == '0' && normalized.All(char.IsAsciiDigit) ? normalized[1..] : null;

    /// <summary>False only for a 13-digit or 8-digit EAN whose check digit is wrong. Other shapes are accepted.</summary>
    public static bool IsValid(string normalized)
    {
        if (!normalized.All(char.IsAsciiDigit) || normalized.Length is not (13 or 8)) return true;
        var sum = 0;
        // Weights 3,1,3,1… from the rightmost data digit (the one left of the check digit).
        for (int i = normalized.Length - 2, w = 3; i >= 0; i--, w = 4 - w) sum += (normalized[i] - '0') * w;
        var check = (10 - sum % 10) % 10;
        return check == normalized[^1] - '0';
    }
}
