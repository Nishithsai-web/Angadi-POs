using System.Globalization;
using System.Text;
using System.Text.Json;
using Angadi.Application.Platform;
using Angadi.Domain.Common;
using Angadi.Domain.Items;
using Angadi.Domain.Shop;

namespace Angadi.Application.Common;

/// <summary>Reads Resources/strings.te.json, strings.en.json and errors.json (FR-02, NFR-04).</summary>
public sealed partial class JsonLocalizer : ILocalizer
{
    private readonly Dictionary<string, string> _te;
    private readonly Dictionary<string, string> _en;
    private readonly Dictionary<string, (string te, string en)> _errors;

    /// <summary>Last-resort text if errors.json itself is unreadable (M8): the app must still open.</summary>
    private static readonly (string te, string en) Fallback =
        ("ఏదో తప్పు జరిగింది — మళ్ళీ ప్రయత్నించండి", "Something went wrong — please try again");

    [System.Text.RegularExpressions.GeneratedRegex(@"\{\d+\}")]
    private static partial System.Text.RegularExpressions.Regex Placeholder();

    public JsonLocalizer()
    {
        // M8: lenient at runtime — a corrupt or duplicated key never stops the app from opening.
        // CI runs LoadAll(strict: true), so duplicates are still caught before release.
        _te = LoadAll("te", strict: false);
        _en = LoadAll("en", strict: false);
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
        var entry = _errors.TryGetValue(error.Code, out var e) ? e
            : _errors.TryGetValue(ErrorCodes.Unexpected, out var u) ? u : Fallback;
        var template = Language == UiLanguage.Telugu ? entry.te : entry.en;
        // A templated message raised without its values must not show "{0}" to a shopkeeper: drop the slot.
        if (error.Args is not { Length: > 0 } args) return Placeholder().Replace(template, "").Replace("  ", " ", StringComparison.Ordinal).Trim();
        try { return string.Format(CultureInfo.InvariantCulture, template, args); }
        catch (FormatException) { return template; }
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
    internal static Dictionary<string, string> LoadAll(string lang, bool strict = true)
    {
        var asm = typeof(JsonLocalizer).Assembly;
        var names = asm.GetManifestResourceNames()
            .Where(n => n.StartsWith("Angadi.Resources.strings", StringComparison.Ordinal) && n.EndsWith($".{lang}.json", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal);

        var all = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            try
            {
                using var s = asm.GetManifestResourceStream(name)!;
                using var doc = JsonDocument.Parse(s);
                foreach (var p in doc.RootElement.EnumerateObject())
                    if (!all.TryAdd(p.Name, p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : "") && strict)
                        throw new InvalidOperationException($"String key '{p.Name}' is defined twice ({name}).");
            }
            catch (JsonException) when (!strict)
            {
                // A truncated area file loses only its own keys; the indexer falls back to the other language/key.
            }
        }
        return all;
    }

    private static Dictionary<string, (string, string)> LoadErrors()
    {
        var result = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        try
        {
            using var s = Open("errors.json");
            using var doc = JsonDocument.Parse(s);
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                // M8: a missing te/en entry falls back to the other language instead of throwing at startup.
                var te = p.Value.TryGetProperty("te", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";
                var en = p.Value.TryGetProperty("en", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "";
                result[p.Name] = (te.Length > 0 ? te : en, en.Length > 0 ? en : te);
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // Unreadable errors.json: every error shows the hardcoded fallback.
        }
        return result;
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
