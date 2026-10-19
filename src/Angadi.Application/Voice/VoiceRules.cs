using System.Text.Json;
using Angadi.Domain.Items;

namespace Angadi.Application.Voice;

/// <summary>
/// The Telugu number, unit and grammar rules, loaded once from the embedded JSON in
/// Resources/voice. Fix a misheard word by editing the JSON — never the parser (ADR-5).
/// </summary>
public sealed class VoiceRules
{
    public required IReadOnlyDictionary<string, long> Numbers { get; init; }          // units + tens + hundreds + fractions, in milli
    public required IReadOnlySet<string> HundredMultipliers { get; init; }
    public required string HalfSuffix { get; init; }
    public required IReadOnlyDictionary<string, Unit> Units { get; init; }
    public required IReadOnlyDictionary<string, int> Multipliers { get; init; }
    public required IReadOnlyList<string> Separators { get; init; }
    public required IReadOnlySet<string> Fillers { get; init; }
    public required IReadOnlySet<string> Currency { get; init; }

    private static readonly Lazy<VoiceRules> Embedded = new(LoadEmbedded);

    public static VoiceRules Default => Embedded.Value;

    private static VoiceRules LoadEmbedded()
    {
        var asm = typeof(VoiceRules).Assembly;
        JsonDocument Load(string name)
        {
            using var s = asm.GetManifestResourceStream($"Angadi.Resources.voice.{name}")
                ?? throw new InvalidOperationException($"Missing voice rules: {name}");
            return JsonDocument.Parse(s);
        }

        using var numbers = Load("numbers.te.json");
        using var units = Load("units.te.json");
        using var grammar = Load("grammar.te.json");
        return FromJson(numbers.RootElement, units.RootElement, grammar.RootElement);
    }

    public static VoiceRules FromJson(JsonElement numbers, JsonElement units, JsonElement grammar)
    {
        var numberMap = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var group in new[] { "units", "tens", "hundreds", "fractions" })
            foreach (var p in numbers.GetProperty(group).EnumerateObject())
                numberMap[TeluguText.Normalize(p.Name)] = p.Value.GetInt64();

        var unitMap = new Dictionary<string, Unit>(StringComparer.Ordinal);
        foreach (var p in units.GetProperty("units").EnumerateObject())
            unitMap[TeluguText.Normalize(p.Name)] = Enum.Parse<Unit>(p.Value.GetString()!);

        var multipliers = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var p in units.GetProperty("multipliers").EnumerateObject())
            multipliers[TeluguText.Normalize(p.Name)] = p.Value.GetInt32();

        static HashSet<string> Set(JsonElement e) =>
            e.EnumerateArray().Select(x => TeluguText.Normalize(x.GetString())).Where(x => x.Length > 0).ToHashSet(StringComparer.Ordinal);

        return new VoiceRules
        {
            Numbers = numberMap,
            HundredMultipliers = Set(numbers.GetProperty("hundredMultipliers")),
            HalfSuffix = numbers.GetProperty("halfSuffix").GetString()!,
            Units = unitMap,
            Multipliers = multipliers,
            // Raw strings: punctuation separators must survive (Normalize keeps them, but be explicit).
            Separators = grammar.GetProperty("separators").EnumerateArray()
                .Select(x => TeluguText.Normalize(x.GetString())).Where(x => x.Length > 0)
                .OrderByDescending(x => x.Length).ToList(),
            Fillers = Set(grammar.GetProperty("fillers")),
            Currency = Set(grammar.GetProperty("currency")),
        };
    }
}
