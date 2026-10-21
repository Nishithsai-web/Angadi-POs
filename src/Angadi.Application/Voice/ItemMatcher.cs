using Angadi.Application.Common;
using Angadi.Application.Persistence;
using Angadi.Application.Platform;
using Angadi.Domain.Items;
using Angadi.Domain.Shop;
using Microsoft.Extensions.Options;

namespace Angadi.Application.Voice;

/// <summary>
/// Scores a spoken name against every active item's Telugu name, English name and aliases (FR-07).
///   exact key match                → 1.00
///   one name contains the other    → 0.80–0.90 (always a confirm, never silent)
///   otherwise                      → 1 − edit distance / length
/// Two near-equal winners above the auto threshold are downgraded to Confirm.
/// </summary>
public sealed class ItemMatcher(IItemRepository items, IOptions<VoiceOptions> options, ILocalizer text) : IItemMatcher
{
    private const double AmbiguityGap = 0.05;
    private const double MinCandidateScore = 0.40;
    private readonly VoiceOptions _o = options.Value;

    public async Task<MatchResult> MatchAsync(ParsedLine line, CancellationToken ct = default)
    {
        var active = await items.GetActiveAsync(ct);
        return Match(line, active, _o, text.Language);
    }

    internal static MatchResult Match(ParsedLine line, IReadOnlyList<Item> active, VoiceOptions o, UiLanguage language = UiLanguage.Telugu,
        IReadOnlySet<string>? genericKeys = null)
    {
        genericKeys ??= VoiceRules.Default.GenericAliases;
        var query = TeluguText.MatchKey(line.Name);
        if (query.Length == 0) return new MatchResult(line, MatchKind.NoMatch, []);
        // M1-CORRECTION: "650 పారాసెటమాల్" — a dosage spoken first; also try it rotated to the end of the name.
        // FIX(VOICE-10, 2026-10-21, S1): Flaw: for the BARE phrase "650 పారాసెటమాల్" the parser (M1) takes 650 as the
        // quantity and flags it ambiguous, so the name holds no digits and the rotation above never ran: both
        // paracetamols tied on "పారాసెటమాల్" and the alphabetical tie-break put 500 mg on top (SPEC-11 test, added by
        // VOICE-8, never passed). Fix: when the parser flagged an ambiguous bare leading number (no unit, not an
        // amount), also try that number as the end of the name. Why: the shopkeeper said 650; the Confirm sheet must
        // offer the 650 mg strip first. Logged in docs/AGENT_FIX_LOG.md.
        var rotated = (RotateLeadingDigits(line.Name) ?? LeadingDosageAsName(line)) is { } r ? TeluguText.MatchKey(r) : null;

        // PERF: scores every active item per phrase — fine to ~2k items; add a first-letter prefilter beyond that.
        var ranked = active
            .Select(item =>
            {
                var a = ScoreItemDetailed(query, item, genericKeys);
                if (rotated is null) return (item, score: a.Score, learned: a.Learned);
                var b = ScoreItemDetailed(rotated, item, genericKeys);
                return b.Score > a.Score ? (item, score: b.Score, learned: b.Learned) : (item, score: a.Score, learned: a.Learned);
            })
            .Where(x => x.score >= MinCandidateScore)
            .OrderByDescending(x => x.score)
            .ThenBy(x => x.item.TeluguName, StringComparer.Ordinal)
            .Take(Math.Max(1, o.MaxCandidates))
            .Select(x => new MatchCandidate(x.item.Id, ItemNames.Pick(language, x.item.TeluguName, x.item.EnglishName), Math.Round(x.score, 4), x.learned))
            .ToList();

        if (ranked.Count == 0) return new MatchResult(line, MatchKind.NoMatch, []);

        var best = ranked[0].Score;
        var kind = best >= o.AutoAddThreshold ? MatchKind.AutoAdd
                 : best >= o.ConfirmThreshold ? MatchKind.Confirm
                 : MatchKind.NoMatch;

        if (kind == MatchKind.AutoAdd && ranked.Count > 1 && best - ranked[1].Score < AmbiguityGap)
            kind = MatchKind.Confirm;
        if (kind == MatchKind.AutoAdd && line.IsAmbiguous)
            kind = MatchKind.Confirm;   // H6: the item is right, the quantity is uncertain — ask

        return new MatchResult(line, kind, ranked);
    }

    /// <summary>Learned aliases below auto-confidence top out just under the auto threshold: they ask (M2).</summary>
    internal const double UnconfirmedLearnedCap = 0.89;

    /// <summary>A generic category word (నూనె, పప్పు…) tops out just under the auto threshold: it always asks (VOICE-6).</summary>
    internal const double GenericAliasCap = 0.89;

    internal static double ScoreItem(string query, Item item, IReadOnlySet<string>? genericKeys = null) =>
        ScoreItemDetailed(query, item, genericKeys).Score;

    /// <summary>
    /// Score plus the learned alias that produced it (VOICE-7): the pipeline stamps that alias's
    /// LastUsedMs when it decides an AutoAdd, so the learner's LRU cap evicts by use, not by age.
    /// A curated name/alias win — or a tie it reached first — reports null.
    /// </summary>
    internal static (double Score, LearnedAlias? Learned) ScoreItemDetailed(string query, Item item, IReadOnlySet<string>? genericKeys = null)
    {
        genericKeys ??= VoiceRules.Default.GenericAliases;
        var best = 0.0;
        LearnedAlias? decisive = null;
        foreach (var name in Names(item))
        {
            var key = TeluguText.MatchKey(name);
            if (key.Length == 0) continue;
            var score = Score(query, key);
            // FIX(VOICE-6, 2026-10-20, FIX-1): Flaw: "generic word asks" was an accident of big
            // catalogs — with ONE oil whose curated alias is exactly "నూనె", the alias scored 1.0
            // and auto-added silently. Fix: an item whose own name/alias IS a generic category
            // word is capped at ask-only, like an unconfirmed learned alias.
            // Why: "నూనె" names a shelf, not a product; the app must ask which oil, every time.
            // Logged in docs/AGENT_FIX_LOG.md.
            if (genericKeys.Contains(key)) score = Math.Min(score, GenericAliasCap);
            if (score > best) { best = score; decisive = null; }
            if (best >= 1.0) return (best, decisive);
        }
        foreach (var learned in item.LearnedAliases)
        {
            var key = TeluguText.MatchKey(learned.Text);
            if (key.Length == 0) continue;
            var score = Score(query, key);
            score = learned.CanAutoAdd ? score : Math.Min(score, UnconfirmedLearnedCap);
            if (score > best) { best = score; decisive = learned; }
        }
        return (best, decisive);
    }

    internal static double Score(string query, string key)
    {
        if (string.Equals(query, key, StringComparison.Ordinal)) return 1.0;

        var sim = TeluguText.Similarity(query, key);
        var shorter = Math.Min(query.Length, key.Length);
        var longer = Math.Max(query.Length, key.Length);
        if (shorter >= 3 && (key.Contains(query, StringComparison.Ordinal) || query.Contains(key, StringComparison.Ordinal)))
            sim = Math.Max(sim, 0.80 + 0.10 * shorter / longer);

        // Only an exact key (name/alias) match scores 1.0. Containment tops out below 0.90, so it
        // always asks; a one-letter slip on a long name can still clear the auto threshold.
        return Math.Min(sim, 0.99);
    }

    internal static string? RotateLeadingDigits(string name)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 1 && parts[0].All(char.IsAsciiDigit) ? string.Join(' ', parts.Skip(1).Append(parts[0])) : null;
    }

    /// <summary>VOICE-10: "650 పారాసెటమాల్" parsed as qty 650 (ambiguous) → "పారాసెటమాల్ 650" as a name to try.</summary>
    internal static string? LeadingDosageAsName(ParsedLine line)
    {
        if (!line.IsAmbiguous || line.Unit is not null || line.Amount is not null) return null;
        var first = line.Raw.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return first is { Length: > 0 } && first.All(char.IsAsciiDigit) ? line.Name + " " + first : null;
    }

    private static IEnumerable<string> Names(Item item)
    {
        yield return item.TeluguName;
        yield return item.EnglishName;
        foreach (var a in item.Aliases) yield return a;
    }

    public static string DisplayName(Item item) =>
        string.IsNullOrWhiteSpace(item.TeluguName) ? item.EnglishName : item.TeluguName;
}
