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
    /// <summary>
    /// FIX(VOICE-3/VOICE-4, 2026-10-20, FIX-1): scale words and their factors (వంద ×100, వెయ్యి ×1000,
    /// లక్ష ×100000, కోటి ×10⁷). A scale word multiplies the number spoken before it. Loaded from
    /// numbers.te.json "scaleMultipliers"; when that section is absent, derived from hundredMultipliers ×100.
    /// Logged in docs/AGENT_FIX_LOG.md.
    /// </summary>
    public IReadOnlyDictionary<string, long> ScaleMultipliers { get; init; } = new Dictionary<string, long>();
    /// <summary>
    /// FIX(VD-3, 2026-10-22, dialects): Flaw: Hyderabadi "పావ్ కిలో" (¼ kg) could not be a number
    /// because "పావ్" alone is pav bread. Fix: words in numbers.te.json "unitBoundNumbers" count as a
    /// number ONLY when a unit/multiplier word follows; otherwise they stay in the item name.
    /// Why: one word, two meanings — the next word decides. Logged in docs/AGENT_FIX_LOG.md.
    /// </summary>
    public IReadOnlyDictionary<string, long> UnitBoundNumbers { get; init; } = new Dictionary<string, long>();
    public required string HalfSuffix { get; init; }
    public required IReadOnlyDictionary<string, Unit> Units { get; init; }
    public required IReadOnlyDictionary<string, int> Multipliers { get; init; }
    public required IReadOnlyList<string> Separators { get; init; }
    public required IReadOnlySet<string> Fillers { get; init; }
    public required IReadOnlySet<string> Currency { get; init; }
    /// <summary>
    /// FIX(VOICE-6, 2026-10-20, FIX-1): generic category words (నూనె, పప్పు, …) as MatchKeys, from
    /// grammar.te.json "genericAliases". An item named/aliased with exactly one of these is capped
    /// at ask-only by the matcher — a category word must never silently pick a product.
    /// Logged in docs/AGENT_FIX_LOG.md.
    /// </summary>
    public IReadOnlySet<string> GenericAliases { get; init; } = new HashSet<string>();

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

        var hundredMultipliers = Set(numbers.GetProperty("hundredMultipliers"));
        var scaleMultipliers = new Dictionary<string, long>(StringComparer.Ordinal);
        if (numbers.TryGetProperty("scaleMultipliers", out var scaleJson))
            foreach (var p in scaleJson.EnumerateObject())
                scaleMultipliers[TeluguText.Normalize(p.Name)] = p.Value.GetInt64();
        else
            foreach (var w in hundredMultipliers) scaleMultipliers[w] = 100;   // older rules files: వంద treatment only

        // FIX(VOICE-6, 2026-10-20, FIX-1): generic category words, stored as MatchKeys so the
        // matcher compares like with like. Absent section = no generic words (older rules files).
        var genericAliases = new HashSet<string>(StringComparer.Ordinal);
        if (grammar.TryGetProperty("genericAliases", out var genericJson))
            foreach (var x in genericJson.EnumerateArray())
            {
                var key = TeluguText.MatchKey(x.GetString());
                if (key.Length > 0) genericAliases.Add(key);
            }

        // FIX(VD-3): absent section = no unit-bound words (older rules files).
        var unitBound = new Dictionary<string, long>(StringComparer.Ordinal);
        if (numbers.TryGetProperty("unitBoundNumbers", out var unitBoundJson))
            foreach (var p in unitBoundJson.EnumerateObject())
                unitBound[TeluguText.Normalize(p.Name)] = p.Value.GetInt64();

        return new VoiceRules
        {
            UnitBoundNumbers = unitBound,
            Numbers = numberMap,
            HundredMultipliers = hundredMultipliers,
            ScaleMultipliers = scaleMultipliers,
            HalfSuffix = numbers.GetProperty("halfSuffix").GetString()!,
            Units = unitMap,
            Multipliers = multipliers,
            // Raw strings: punctuation separators must survive (Normalize keeps them, but be explicit).
            Separators = grammar.GetProperty("separators").EnumerateArray()
                .Select(x => TeluguText.Normalize(x.GetString())).Where(x => x.Length > 0)
                .OrderByDescending(x => x.Length).ToList(),
            Fillers = Set(grammar.GetProperty("fillers")),
            Currency = Set(grammar.GetProperty("currency")),
            GenericAliases = genericAliases,
        };
    }
}
