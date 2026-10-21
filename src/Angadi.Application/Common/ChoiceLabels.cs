using System.Globalization;

namespace Angadi.Application.Common;

/// <summary>
/// A1-06: labels for an action sheet whose answer is mapped back to a list entry. The sheet returns the chosen TEXT,
/// so two equal labels ("Ramesh · ₹0.00" twice) would always resolve to the first — udhaar charged to the wrong
/// person. Numbering every row ("1. …", "2. …") makes each label unique, so <c>labels.IndexOf(pick)</c> is exact.
/// </summary>
public static class ChoiceLabels
{
    public static List<string> Numbered(IEnumerable<string> labels) =>
        labels.Select((label, i) => (i + 1).ToString(CultureInfo.InvariantCulture) + ". " + label).ToList();

    /// <summary>The last 4 digits of a phone number as "••3210", or "" when there are fewer than 4 digits.</summary>
    public static string PhoneTail(string? phone)
    {
        var digits = new string((phone ?? "").Where(char.IsAsciiDigit).ToArray());
        return digits.Length >= 4 ? "••" + digits[^4..] : "";
    }
}
