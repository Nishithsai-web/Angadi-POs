using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Angadi.Application.Common;
using Angadi.Domain.Shop;

namespace Angadi.Tests.Contracts;

/// <summary>
/// T4 / NFR-04: the one app-wide language switch only works if every message exists, non-empty, in BOTH Telugu and
/// English, with the same {n} slots — a slot missing in one language shows "{0}" or drops the amount on screen.
/// </summary>
public partial class LocalizerCompletenessTests
{
    private static readonly Assembly App = typeof(Result).Assembly;

    [GeneratedRegex(@"\{(\d+)(?:[,:][^}]*)?\}")]
    private static partial Regex Slot();

    private static SortedSet<int> Slots(string s) => new(Slot().Matches(s).Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)));

    private static string[] StringFiles(string lang) => App.GetManifestResourceNames()
        .Where(n => n.StartsWith("Angadi.Resources.strings", StringComparison.Ordinal) && n.EndsWith($".{lang}.json", StringComparison.Ordinal))
        .OrderBy(n => n, StringComparer.Ordinal).ToArray();

    private static Dictionary<string, JsonElement> Read(string resource)
    {
        using var s = App.GetManifestResourceStream(resource)!;
        using var doc = JsonDocument.Parse(s);
        return doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
    }

    [Fact]
    public void EveryTeluguFile_HasAnEnglishTwin_WithTheSameKeys()
    {
        var te = StringFiles("te");
        var en = StringFiles("en");
        Assert.NotEmpty(te);
        Assert.Equal(te.Select(n => n[..^".te.json".Length]), en.Select(n => n[..^".en.json".Length]));
        foreach (var teFile in te)
        {
            var enFile = teFile[..^".te.json".Length] + ".en.json";
            var teKeys = Read(teFile).Keys.ToHashSet();
            var enKeys = Read(enFile).Keys.ToHashSet();
            Assert.True(teKeys.SetEquals(enKeys),
                $"{teFile} vs {enFile}: only te [{string.Join(", ", teKeys.Except(enKeys))}], only en [{string.Join(", ", enKeys.Except(teKeys))}]");
        }
    }

    [Fact]
    public void NoString_IsEmptyOrNotText()
    {
        foreach (var file in StringFiles("te").Concat(StringFiles("en")))
            foreach (var (key, value) in Read(file))
            {
                Assert.True(value.ValueKind == JsonValueKind.String, $"{file}: {key} is not a string");
                Assert.False(string.IsNullOrWhiteSpace(value.GetString()), $"{file}: {key} is empty");
            }
    }

    [Fact]
    public void Placeholders_MatchBetweenTeluguAndEnglish()
    {
        var te = JsonLocalizer.LoadAll("te");
        var en = JsonLocalizer.LoadAll("en");
        var bad = te.Keys.Where(k => en.ContainsKey(k) && !Slots(te[k]).SetEquals(Slots(en[k])))
            .Select(k => $"{k}: te {{{string.Join(",", Slots(te[k]))}}} en {{{string.Join(",", Slots(en[k]))}}}").ToList();
        Assert.True(bad.Count == 0, string.Join("; ", bad));
    }

    [Fact]
    public void Placeholders_AreNumberedFromZeroWithoutGaps_AndFormatCleanly()
    {
        var localizer = new JsonLocalizer();
        foreach (var lang in new[] { UiLanguage.Telugu, UiLanguage.English })
        {
            localizer.Language = lang;
            foreach (var (key, template) in JsonLocalizer.LoadAll(lang == UiLanguage.Telugu ? "te" : "en"))
            {
                var slots = Slots(template);
                if (slots.Count == 0) continue;
                Assert.True(slots.SetEquals(Enumerable.Range(0, slots.Max + 1)), $"{lang} {key}: slots {string.Join(",", slots)} have a gap");
                var args = Enumerable.Range(0, slots.Max + 1).Select(i => (object)$"<{i}>").ToArray();
                var text = localizer.Format(key, args);   // throws FormatException on a stray brace
                Assert.All(args, a => Assert.Contains((string)a, text, StringComparison.Ordinal));
            }
        }
    }

    [Fact]
    public void ErrorsJson_EveryEntryHasBothLanguages_WithMatchingSlots()
    {
        using var doc = ResourceTests.Load("errors.json");
        foreach (var entry in doc.RootElement.EnumerateObject())
        {
            Assert.True(entry.Value.ValueKind == JsonValueKind.Object, $"errors.json: {entry.Name} is not an object");
            var te = entry.Value.TryGetProperty("te", out var t) ? t.GetString() ?? "" : "";
            var en = entry.Value.TryGetProperty("en", out var e) ? e.GetString() ?? "" : "";
            Assert.False(string.IsNullOrWhiteSpace(te), $"errors.json: {entry.Name} has no te");
            Assert.False(string.IsNullOrWhiteSpace(en), $"errors.json: {entry.Name} has no en");
            Assert.True(Slots(te).SetEquals(Slots(en)), $"errors.json: {entry.Name} slots differ between te and en");
        }
    }

    [Fact]
    public void ErrorCodes_AndErrorsJson_AreTheSameSet()
    {
        using var doc = ResourceTests.Load("errors.json");
        var json = doc.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet();
        var codes = typeof(ErrorCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!).ToHashSet();
        Assert.Empty(codes.Except(json));   // a code with no message shows the generic fallback
        Assert.Empty(json.Except(codes));   // an orphan message is dead text nobody maintains
        Assert.Equal(codes.Count, typeof(ErrorCodes).GetFields(BindingFlags.Public | BindingFlags.Static).Count(f => f.IsLiteral));
    }

    [Fact]
    public void EveryError_RendersWithoutRawSlots_InBothLanguages()
    {
        var localizer = new JsonLocalizer();
        var codes = typeof(ErrorCodes).GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral)
            .Select(f => (string)f.GetRawConstantValue()!);
        foreach (var lang in new[] { UiLanguage.Telugu, UiLanguage.English })
        {
            localizer.Language = lang;
            foreach (var code in codes)
            {
                var bare = localizer.Error(new Error(code));
                Assert.False(string.IsNullOrWhiteSpace(bare), $"{lang} {code}: empty");
                Assert.DoesNotContain("{", bare, StringComparison.Ordinal);
                var withArgs = localizer.Error(Error.With(code, "₹10.00", "x", "y"));
                Assert.DoesNotContain("{", withArgs, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void MonthNames_OneToTwelve_InBothLanguages()
    {
        var te = JsonLocalizer.LoadAll("te");
        var en = JsonLocalizer.LoadAll("en");
        var teNames = Enumerable.Range(1, 12).Select(m => te.GetValueOrDefault($"month.{m}", "")).ToList();
        var enNames = Enumerable.Range(1, 12).Select(m => en.GetValueOrDefault($"month.{m}", "")).ToList();
        Assert.All(teNames, n => Assert.Matches(@"^[ఀ-౿]+$", n));   // Telugu script only
        Assert.All(enNames, n => Assert.Matches("^[A-Z][a-z]{2}$", n));
        Assert.Equal(12, teNames.Distinct().Count());
        Assert.Equal(12, enNames.Distinct().Count());
        Assert.False(te.ContainsKey("month.0") || te.ContainsKey("month.13"));

        // Each English short name is the invariant culture's own abbreviation for that month.
        for (var m = 1; m <= 12; m++)
            Assert.Equal(CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedMonthName(m), enNames[m - 1]);
    }

    [Theory]
    [InlineData(UiLanguage.English, 2026, 10, 25, "25 Oct")]
    [InlineData(UiLanguage.Telugu, 2026, 10, 25, "25 అక్టోబర్")]
    [InlineData(UiLanguage.English, 2027, 1, 1, "1 Jan")]
    [InlineData(UiLanguage.Telugu, 2026, 12, 31, "31 డిసెంబర్")]
    public void ShortDate_UsesTheMonthNameOfTheAppLanguage(UiLanguage lang, int y, int m, int d, string expected)
    {
        var localizer = new JsonLocalizer { Language = lang };
        Assert.Equal(expected, localizer.Short(new DateOnly(y, m, d)));
    }

    [Fact]
    public void EnglishText_HasNoTeluguScript_ExceptTheLanguageNames()
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal) { "lang.telugu", "lang.choose" };
        var leaks = JsonLocalizer.LoadAll("en")
            .Where(p => !allowed.Contains(p.Key) && p.Value.Any(c => c is >= 'ఀ' and <= '౿'))
            .Select(p => p.Key).ToList();
        Assert.True(leaks.Count == 0, "Telugu inside English strings: " + string.Join(", ", leaks));
    }

    [Fact]
    public void EveryUnit_HasALabelInBothLanguages()
    {
        var localizer = new JsonLocalizer();
        foreach (var lang in new[] { UiLanguage.Telugu, UiLanguage.English })
        {
            localizer.Language = lang;
            foreach (var unit in Enum.GetValues<Angadi.Domain.Items.Unit>())
            {
                var label = localizer.UnitLabel(unit);
                Assert.False(string.IsNullOrWhiteSpace(label));
                Assert.NotEqual($"unit.{unit}", label);   // the key itself = missing
            }
        }
    }
}
