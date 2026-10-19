using Angadi.Application.Common;
using Angadi.Application.Persistence;
using Angadi.Domain.Items;
using Microsoft.Extensions.Options;

namespace Angadi.Application.Voice;

/// <summary>
/// Scores a spoken name against every active item's Telugu name, English name and aliases (FR-07).
///   exact key match                → 1.00
///   one name contains the other    → 0.80–0.90 (always a confirm, never silent)
///   otherwise                      → 1 − edit distance / length
/// Two near-equal winners above the auto threshold are downgraded to Confirm.
/// </summary>
public sealed class ItemMatcher(IItemRepository items, IOptions<VoiceOptions> options) : IItemMatcher
{
    private const double AmbiguityGap = 0.05;
    private const double MinCandidateScore = 0.40;
    private readonly VoiceOptions _o = options.Value;

    public async Task<MatchResult> MatchAsync(ParsedLine line, CancellationToken ct = default)
    {
        var active = await items.GetActiveAsync(ct);
        return Match(line, active, _o);
    }

    internal static MatchResult Match(ParsedLine line, IReadOnlyList<Item> active, VoiceOptions o)
    {
        var query = TeluguText.MatchKey(line.Name);
        if (query.Length == 0) return new MatchResult(line, MatchKind.NoMatch, []);

        var ranked = active
            .Select(item => (item, score: ScoreItem(query, item)))
            .Where(x => x.score >= MinCandidateScore)
            .OrderByDescending(x => x.score)
            .ThenBy(x => x.item.TeluguName, StringComparer.Ordinal)
            .Take(Math.Max(1, o.MaxCandidates))
            .Select(x => new MatchCandidate(x.item.Id, DisplayName(x.item), Math.Round(x.score, 4)))
            .ToList();

        if (ranked.Count == 0) return new MatchResult(line, MatchKind.NoMatch, []);

        var best = ranked[0].Score;
        var kind = best >= o.AutoAddThreshold ? MatchKind.AutoAdd
                 : best >= o.ConfirmThreshold ? MatchKind.Confirm
                 : MatchKind.NoMatch;

        if (kind == MatchKind.AutoAdd && ranked.Count > 1 && best - ranked[1].Score < AmbiguityGap)
            kind = MatchKind.Confirm;

        return new MatchResult(line, kind, ranked);
    }

    internal static double ScoreItem(string query, Item item)
    {
        var best = 0.0;
        foreach (var name in Names(item))
        {
            var key = TeluguText.MatchKey(name);
            if (key.Length == 0) continue;
            best = Math.Max(best, Score(query, key));
            if (best >= 1.0) break;
        }
        return best;
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

    private static IEnumerable<string> Names(Item item)
    {
        yield return item.TeluguName;
        yield return item.EnglishName;
        foreach (var a in item.Aliases) yield return a;
    }

    public static string DisplayName(Item item) =>
        string.IsNullOrWhiteSpace(item.TeluguName) ? item.EnglishName : item.TeluguName;
}
