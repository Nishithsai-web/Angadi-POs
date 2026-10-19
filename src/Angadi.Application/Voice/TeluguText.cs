using System.Globalization;
using System.Text;

namespace Angadi.Application.Voice;

/// <summary>Normalisation and similarity for Telugu (and the English recognisers mix in).</summary>
public static class TeluguText
{
    private const char Zwnj = '‌';
    private const char Zwj = '‍';

    /// <summary>NFC, Telugu digits → ASCII, zero-width joiners removed, Latin lower-cased, whitespace collapsed.</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var s = text.Normalize(NormalizationForm.FormC);
        var sb = new StringBuilder(s.Length);
        var lastSpace = true;
        foreach (var ch in s)
        {
            if (ch is Zwnj or Zwj) continue;
            var c = ch;
            if (c >= '౦' && c <= '౯') c = (char)('0' + (c - '౦'));   // ౦–౯
            if (char.IsWhiteSpace(c))
            {
                if (!lastSpace) sb.Append(' ');
                lastSpace = true;
                continue;
            }
            sb.Append(char.ToLower(c, CultureInfo.InvariantCulture));
            lastSpace = false;
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Key used for matching: normalised, spaces removed, and the common plural/oblique endings
    /// "లు" / "ల" stripped (ఉల్లిపాయలు → ఉల్లిపాయ, సబ్బులు → సబ్బు). English plural "s" stripped too.
    /// </summary>
    public static string MatchKey(string? text)
    {
        var s = Normalize(text).Replace(" ", "");
        // Recognisers write the final nasal both ways: బియ్యం / బియ్యమ్.
        if (s.EndsWith("మ్", StringComparison.Ordinal)) s = s[..^2] + "ం";
        if (s.Length > 4 && s.EndsWith("లు", StringComparison.Ordinal)) s = s[..^2];
        else if (s.Length > 4 && s.EndsWith("ల", StringComparison.Ordinal)) s = s[..^1];
        else if (s.Length > 3 && IsLatin(s) && s.EndsWith('s') && !s.EndsWith("ss", StringComparison.Ordinal)) s = s[..^1];
        return s;
    }

    /// <summary>1 − Levenshtein / max length, over UTF-16 code units (Telugu is all BMP).</summary>
    public static double Similarity(string a, string b)
    {
        if (a.Length == 0 && b.Length == 0) return 1;
        if (a.Length == 0 || b.Length == 0) return 0;
        if (string.Equals(a, b, StringComparison.Ordinal)) return 1;
        return 1.0 - (double)Levenshtein(a, b) / Math.Max(a.Length, b.Length);
    }

    public static int Levenshtein(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }
        return prev[b.Length];
    }

    private static bool IsLatin(string s)
    {
        foreach (var c in s) if (c > 0x024F) return false;
        return true;
    }
}
