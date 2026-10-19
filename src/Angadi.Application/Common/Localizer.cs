using System.Globalization;
using System.Text;
using System.Text.Json;
using Angadi.Application.Platform;
using Angadi.Domain.Common;
using Angadi.Domain.Items;
using Angadi.Domain.Shop;

namespace Angadi.Application.Common;

/// <summary>Reads Resources/strings.te.json, strings.en.json and errors.json (FR-02, NFR-04).</summary>
public sealed class JsonLocalizer : ILocalizer
{
    private readonly Dictionary<string, string> _te;
    private readonly Dictionary<string, string> _en;
    private readonly Dictionary<string, (string te, string en)> _errors;

    public JsonLocalizer()
    {
        _te = LoadAll("te");
        _en = LoadAll("en");
        _errors = LoadErrors();
    }

    public UiLanguage Language { get; set; } = UiLanguage.Telugu;

    /// <summary>Current language, then the other language, then the key itself (never a crash).</summary>
    public string this[string key]
    {
        get
        {
            var (first, second) = Language == UiLanguage.Telugu ? (_te, _en) : (_en, _te);
            return first.TryGetValue(key, out var v) ? v : second.TryGetValue(key, out var w) ? w : key;
        }
    }

    public string English(string key) => _en.TryGetValue(key, out var v) ? v : key;

    public string Error(Error error)
    {
        var entry = _errors.TryGetValue(error.Code, out var e) ? e : _errors[ErrorCodes.Unexpected];
        return Language == UiLanguage.Telugu ? entry.te : entry.en;
    }

    public string Format(string key, params object[] args) => string.Format(CultureInfo.InvariantCulture, this[key], args);

    public string UnitLabel(Unit unit) => this[$"unit.{unit}"];

    private static Stream Open(string name) =>
        typeof(JsonLocalizer).Assembly.GetManifestResourceStream($"Angadi.Resources.{name}")
        ?? throw new InvalidOperationException($"Missing resource {name}");

    /// <summary>
    /// Merges strings.te.json with every per-area file strings.&lt;area&gt;.te.json (one per UI area, so
    /// parallel work never edits the same file). A key defined twice is a bug and throws.
    /// </summary>
    internal static Dictionary<string, string> LoadAll(string lang)
    {
        var asm = typeof(JsonLocalizer).Assembly;
        var names = asm.GetManifestResourceNames()
            .Where(n => n.StartsWith("Angadi.Resources.strings", StringComparison.Ordinal) && n.EndsWith($".{lang}.json", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal);

        var all = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            using var s = asm.GetManifestResourceStream(name)!;
            using var doc = JsonDocument.Parse(s);
            foreach (var p in doc.RootElement.EnumerateObject())
                if (!all.TryAdd(p.Name, p.Value.GetString() ?? ""))
                    throw new InvalidOperationException($"String key '{p.Name}' is defined twice ({name}).");
        }
        return all;
    }

    private static Dictionary<string, (string, string)> LoadErrors()
    {
        using var s = Open("errors.json");
        using var doc = JsonDocument.Parse(s);
        return doc.RootElement.EnumerateObject().ToDictionary(
            p => p.Name,
            p => (p.Value.GetProperty("te").GetString() ?? "", p.Value.GetProperty("en").GetString() ?? ""),
            StringComparer.Ordinal);
    }
}

/// <summary>Indian number formatting: ₹1,23,456.50 (lakh grouping).</summary>
public static class IndianFormat
{
    public static string Rupees(Paise amount, bool alwaysShowPaise = true)
    {
        var negative = amount.Value < 0;
        var abs = Math.Abs(amount.Value);
        var rupees = abs / 100;
        var paise = abs % 100;
        var body = Group(rupees);
        if (alwaysShowPaise || paise != 0) body += "." + paise.ToString("00", CultureInfo.InvariantCulture);
        return (negative ? "−₹" : "₹") + body;
    }

    /// <summary>1,23,45,678 — last three digits, then groups of two.</summary>
    public static string Group(long value)
    {
        var digits = value.ToString(CultureInfo.InvariantCulture);
        if (digits.Length <= 3) return digits;
        var sb = new StringBuilder();
        var head = digits[..^3];
        var firstLen = head.Length % 2 == 0 ? 2 : 1;
        sb.Append(head[..firstLen]);
        for (var i = firstLen; i < head.Length; i += 2) sb.Append(',').Append(head, i, 2);
        sb.Append(',').Append(digits[^3..]);
        return sb.ToString();
    }

    /// <summary>Quantity in its own unit, trailing zeros trimmed: 2000 → "2", 1500 → "1.5", 250 → "0.25".</summary>
    public static string Quantity(Qty qty)
    {
        var value = qty.Milli / 1000m;
        return value.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
