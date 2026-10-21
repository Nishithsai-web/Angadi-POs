using System.Globalization;
using System.Text.RegularExpressions;
using Angadi.Domain.Common;
using Angadi.Domain.Items;

namespace Angadi.Application.Voice;

/// <summary>
/// Rule-based Telugu billing parser (FR-06). Word order is free: number, unit and name may come
/// in any order ("బియ్యం రెండు కేజీలు" = "రెండు కేజీల బియ్యం"). Everything that is not a number,
/// unit, multiplier, currency word or filler is the item name.
/// </summary>
public sealed partial class TeluguParser(VoiceRules rules) : ITeluguParser
{
    public TeluguParser() : this(VoiceRules.Default) { }

    // ASCII digits only: .NET's \d matches every script's digits; Telugu digits are mapped by Normalize.
    [GeneratedRegex(@"^([0-9]+(?:\.[0-9]+)?)([^0-9.].*)$")]
    private static partial Regex DigitsThenWord();

    [GeneratedRegex(@"^(.*[^0-9.])([0-9]+(?:\.[0-9]+)?)$")]
    private static partial Regex WordThenDigits();

    [GeneratedRegex(@"^[0-9]+(?:\.[0-9]+)?$")]
    private static partial Regex Decimal();

    [GeneratedRegex(@"^[0-9][0-9,]*(\.[0-9]+)?$")]
    private static partial Regex GroupedDigits();

    [GeneratedRegex(@"^([0-9]+)/([0-9]+)$")]
    private static partial Regex Fraction();

    public IReadOnlyList<ParsedLine> Parse(string utterance)
    {
        var text = TeluguText.Normalize(utterance);
        if (text.Length == 0) return [];

        var result = new List<ParsedLine>();
        foreach (var phrase in SplitPhrases(text))
        {
            var line = ParsePhrase(phrase);
            if (line is not null) result.Add(line);
        }
        return result;
    }

    /// <summary>
    /// Replaces single-character punctuation separators with a phrase marker, except a "." or ","
    /// that sits between two digits (decimal point / thousands mark), so "2.5 కేజీలు" stays 2.5 (H4).
    /// </summary>
    internal string MarkPunctuationSeparators(string text)
    {
        var punct = rules.Separators.Where(s => s.Length == 1 && !char.IsLetterOrDigit(s[0])).Select(s => s[0]).ToHashSet();
        var sb = new System.Text.StringBuilder(text.Length + 8);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var betweenDigits = i > 0 && i < text.Length - 1 && char.IsAsciiDigit(text[i - 1]) && char.IsAsciiDigit(text[i + 1]);
            if (punct.Contains(c) && !(betweenDigits && c is '.' or ','))
                sb.Append(" \u0001 ");
            else
                sb.Append(c);
        }
        return sb.ToString();
    }

    internal IEnumerable<IReadOnlyList<string>> SplitPhrases(string text)
    {
        // Punctuation separators become spaces around a marker; word separators are matched as whole tokens.
        // H4: a "." between two digits is a decimal point ("2.5 కేజీలు"), never a phrase break.
        text = MarkPunctuationSeparators(text);

        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var wordSeps = rules.Separators
            .Where(s => !(s.Length == 1 && !char.IsLetterOrDigit(s[0])))
            .Select(s => s.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .ToList();

        var phrases = new List<IReadOnlyList<string>>();
        var current = new List<string>();
        for (var i = 0; i < tokens.Length; i++)
        {
            if (tokens[i] == "\u0001") { Flush(); continue; }
            var matched = wordSeps.FirstOrDefault(sep => sep.Length > 0 && i + sep.Length <= tokens.Length
                && sep.Select((w, k) => tokens[i + k] == w).All(x => x));
            if (matched is not null) { Flush(); i += matched.Length - 1; continue; }
            current.Add(tokens[i]);
        }
        Flush();
        return phrases;

        void Flush()
        {
            if (current.Count > 0) phrases.Add(current.ToArray());
            current.Clear();
        }
    }

    internal ParsedLine? ParsePhrase(IReadOnlyList<string> rawTokens)
    {
        var tokens = rawTokens.SelectMany(Explode).ToList();
        long number = 0;
        long lastPlace = long.MaxValue;   // H6: numbers may only add when place value descends (20 + 5, 200 + 50)
        var ambiguous = false;
        var sawNumber = false;
        Unit? unit = null;
        var multiplier = 1L;
        var isAmount = false;
        var name = new List<string>();

        var nameDigits = DigitsThatBelongToName(tokens);

        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (rules.Fillers.Contains(t)) continue;
            if (nameDigits.Contains(i)) { name.Add(t); continue; }

            if (sawNumber && ScaleFactor(t) is { } factor)
            {
                // FIX(VOICE-3/VOICE-4, 2026-10-20, FIX-1): Flaw: only వంద had multiplier treatment.
                // వెయ్యి sat in the plain number map, so "ఐదు వెయ్యి" tripped the range rule below
                // (5,000,000 ≥ lastPlace) and the amount came out ₹5, flagged ambiguous; లక్ష/కోటి
                // were not numbers at all and fell into the item name.
                // Fix: every scale word multiplies the number before it, exactly like వంద always did
                // (ఐదు వెయ్యి = 5000, రెండు లక్షలు = 200000); standalone, the word still reads as a
                // plain number from the map. Why: "ఐదు వెయ్యి రూపాయలు" is how a ₹5,000 order is spoken.
                // Logged in docs/AGENT_FIX_LOG.md.
                // FIX(A1-10, 2026-10-21, S2): Flaw: a huge spoken number followed by a scale word ("…కోటి")
                // overflowed long silently into a garbage quantity. Fix: past MaxSpokenNumber the line is ambiguous
                // and the scale word is ignored. Why: the pipeline asks instead of billing a wrapped number.
                // Logged in docs/AGENT_FIX_LOG.md.
                if ((number == 0 ? 1000 : number) > MaxSpokenNumber * 1000 / factor) { ambiguous = true; continue; }
                number = (number == 0 ? 1000 : number) * factor;
                lastPlace = factor * 1000;
                continue;
            }

            if (TryNumber(t, out var value))
            {
                if (sawNumber && value >= lastPlace)
                {
                    // "రెండు మూడు కేజీలు" (two-three kg) is a range, not 5. Keep the first number, flag it, let the pipeline ask.
                    ambiguous = true;
                    continue;
                }
                // FIX(VOICE-5, 2026-10-20, FIX-1): Flaw: two descending BARE quantities added
                // silently — "మూడు రెండు కేజీలు" billed 5 kg as if it were a compound number.
                // Fix: when the running total is still a single bare number under ten (number ==
                // lastPlace, nothing scaled or compounded yet) and the new value is another whole
                // bare number under ten, flag ambiguous and keep the first — the pipeline asks.
                // Tens compounds ("ఇరవై ఐదు" = 25) start at ≥ 10_000, scale compounds
                // ("రెండు వందల యాభై") move lastPlace off the total, and fraction tails
                // ("రెండు అర" = 2.5) are < 1000 — none of them can trip this guard.
                // Why: a doubled quantity word is an ASR stutter or two items mushed together;
                // summing it bills the wrong amount. Logged in docs/AGENT_FIX_LOG.md.
                if (sawNumber && number == lastPlace && number is > 0 and < 10_000 && value is >= 1000 and < 10_000)
                {
                    ambiguous = true;
                    continue;
                }
                number += value;
                lastPlace = value;
                sawNumber = true;
                continue;
            }
            if (rules.Units.TryGetValue(t, out var u)) { unit = u; continue; }
            if (rules.Multipliers.TryGetValue(t, out var m)) { multiplier *= m; unit ??= Unit.Piece; continue; }
            if (rules.Currency.Contains(t)) { isAmount = true; continue; }

            name.Add(t);
        }

        // M1 guard: a bare leading number ≥ 100 with no unit ("650 పారాసెటమాల్") may be a dosage, not a count — ask.
        if (tokens.Count > 0 && !nameDigits.Contains(0) && Decimal().IsMatch(tokens[0]) && unit is null && !isAmount
            && TryNumber(tokens[0], out var lead) && lead >= 100_000)
            ambiguous = true;

        // M13: a stutter ("బి బియ్యం") repeats the start of the next word — drop the fragment.
        for (var k = name.Count - 2; k >= 0; k--)
            if (name[k].Length < name[k + 1].Length && name[k + 1].StartsWith(name[k], StringComparison.Ordinal)) name.RemoveAt(k);

        if (name.Count == 0) return null;
        var raw = string.Join(' ', rawTokens);
        var itemName = string.Join(' ', name);

        long? qty = sawNumber ? number * multiplier : multiplier > 1 ? 1000 * multiplier : null;

        if (isAmount)
        {
            // number is in milli-rupees: ₹10 → 10000 → 1000 paise.
            Paise? amount = qty is > 0 ? new Paise(qty.Value / 10) : null;
            return new ParsedLine(itemName, null, null, amount, raw, ambiguous);
        }

        return new ParsedLine(itemName, qty is null ? null : new Qty(qty.Value), unit, null, raw, ambiguous);
    }

    /// <summary>
    /// Written numbers inside product names ("dolo 650", "pan 40") must not become quantities.
    /// A digit token is a QUANTITY when a unit/multiplier/currency word follows it, a currency word
    /// precedes it, or it is under 100 with no other number in the phrase. Otherwise it is part of the name.
    /// </summary>
    internal HashSet<int> DigitsThatBelongToName(IReadOnlyList<string> tokens)
    {
        var result = new HashSet<int>();
        for (var i = 0; i < tokens.Count; i++)
        {
            if (!Decimal().IsMatch(tokens[i])) continue;
            var next = i + 1 < tokens.Count ? tokens[i + 1] : null;
            var prev = i > 0 ? tokens[i - 1] : null;
            var unitFollows = next is not null && (rules.Units.ContainsKey(next) || rules.Multipliers.ContainsKey(next) || rules.Currency.Contains(next));
            var currencyBefore = prev is not null && rules.Currency.Contains(prev);
            if (unitFollows || currencyBefore) continue;

            TryNumber(tokens[i], out var value);
            var otherNumber = tokens.Where((t, k) => k != i && !rules.Fillers.Contains(t) && TryNumber(t, out _)).Any();
            // M1: a bare leading number with nothing else numeric is a bulk count ("200 గుడ్లు"), never a code.
            if (i == 0 && !otherNumber) continue;
            if (value >= 100_000 || otherNumber) result.Add(i);
        }
        return result;
    }

    /// <summary>Splits fused tokens: "2కేజీలు" → "2","కేజీలు"; "రెండుకేజీలు" → "రెండు","కేజీలు".</summary>
    internal IEnumerable<string> Explode(string token)
    {
        // FIX(VOICE-1, 2026-10-20, FIX-1): Flaw: DigitsThenWord split "1/2" into "1" + "/2" before
        // TryNumber could see it, so the fraction branch below was dead code — "1/2 కేజీ" billed 1 kg
        // and "/2" fell into the item name. Fix: a digits/digits token stays whole.
        // Why: "half a kilo" is a top counter phrase; billing double on it is a money error.
        // Logged in docs/AGENT_FIX_LOG.md.
        if (Fraction().IsMatch(token)) return [token];

        // FIX(VOICE-2, 2026-10-20, FIX-1): Flaw: MarkPunctuationSeparators keeps "2,500" whole (the
        // comma sits between digits), but Decimal() rejects commas, so DigitsThenWord split it into
        // "2" + ",500" — quantity 2 and a polluted name. Fix: a digit/comma group is one number with
        // the grouping commas stripped. Why: Indian-locale speech recognisers emit comma-grouped
        // digits ("2,500 రూపాయలు" is ₹2,500, not 2). Logged in docs/AGENT_FIX_LOG.md.
        // Build fix (2026-10-20, FIX-1): CA1307 — the comparison-less Contains/Replace overloads are
        // ERRORS under this repo's warnings-as-errors analyzers (break found by Muse main's local
        // build of e657514); StringComparison.Ordinal overloads are the analyzer-clean forms.
        if (token.Contains(",", StringComparison.Ordinal) && GroupedDigits().IsMatch(token)) return [token.Replace(",", "", StringComparison.Ordinal)];

        var m = DigitsThenWord().Match(token);
        if (m.Success) return [m.Groups[1].Value, m.Groups[2].Value];

        var w = WordThenDigits().Match(token);   // "₹10", "rs20"
        if (w.Success && rules.Currency.Contains(w.Groups[1].Value)) return [w.Groups[1].Value, w.Groups[2].Value];

        // M13: fused number + unit/multiplier + name ("అరకేజీబియ్యం", "అరడజను"). Only split when what follows the
        // number word is itself a unit/multiplier/currency word, so names like "అరటిపండ్లు" stay whole.
        if (!rules.Numbers.ContainsKey(token) && !rules.Units.ContainsKey(token))
        {
            foreach (var num in rules.Numbers.Keys.Where(k => k.Length >= 2 && token.Length > k.Length && token.StartsWith(k, StringComparison.Ordinal)).OrderByDescending(k => k.Length))
            {
                var rest = token[num.Length..];
                var lead = rules.Units.Keys.Concat(rules.Multipliers.Keys).Concat(rules.Currency)
                    .Where(u => rest.StartsWith(u, StringComparison.Ordinal)).OrderByDescending(u => u.Length).FirstOrDefault();
                if (lead is null) continue;
                var tail = rest[lead.Length..];
                return tail.Length == 0 ? [num, lead] : new[] { num, lead }.Concat(Explode(tail));
            }
        }

        foreach (var suffix in rules.Units.Keys.Concat(rules.Currency).OrderByDescending(k => k.Length))
        {
            if (token.Length > suffix.Length && token.EndsWith(suffix, StringComparison.Ordinal))
            {
                var prefix = token[..^suffix.Length];
                if (TryNumber(prefix, out _)) return [prefix, suffix];
            }
        }
        return [token];
    }

    /// <summary>The factor a scale word applies to the number before it (వంద 100, వెయ్యి 1000, లక్ష 100000, కోటి 10⁷).</summary>
    private long? ScaleFactor(string token) =>
        rules.ScaleMultipliers.TryGetValue(token, out var f) ? f
        : rules.HundredMultipliers.Contains(token) ? 100L
        : null;
    /// <summary>Largest number a spoken line can carry (keeps milli-units far inside long).</summary>
    internal const long MaxSpokenNumber = 1_000_000_000_000;

    internal bool TryNumber(string token, out long milli)
    {
        // A1-10: a 16+ digit token (a barcode read aloud, a phone number) used to throw OverflowException out of the
        // voice pipeline. Out-of-range numbers are simply "not a number" now.
        if (Decimal().IsMatch(token))
        {
            if (decimal.TryParse(token, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) && value <= MaxSpokenNumber)
            {
                milli = (long)Math.Round(value * 1000m, MidpointRounding.AwayFromZero);
                return true;
            }
            milli = 0;
            return false;
        }

        var f = Fraction().Match(token);
        if (f.Success && long.TryParse(f.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var den) && den > 0
            && long.TryParse(f.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var num) && num <= MaxSpokenNumber)
        {
            milli = (long)Math.Round(num * 1000m / den, MidpointRounding.AwayFromZero);
            return true;
        }

        if (rules.Numbers.TryGetValue(token, out milli)) return true;

        var half = rules.HalfSuffix;
        if (token.Length > half.Length && token.EndsWith(half, StringComparison.Ordinal)
            && rules.Numbers.TryGetValue(token[..^half.Length], out var stem))
        {
            milli = stem + 500;
            return true;
        }

        milli = 0;
        return false;
    }
}
