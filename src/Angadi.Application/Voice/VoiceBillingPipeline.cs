using Angadi.Application.Common;
using Angadi.Application.Persistence;
using Angadi.Application.Platform;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Angadi.Application.Voice;

/// <summary>Utterance → parsed phrases → matches (blueprint §4). Deterministic and free per bill.</summary>
public sealed class VoiceBillingPipeline(
    ITeluguParser parser,
    IItemRepository items,
    IOptions<VoiceOptions> options,
    IClock clock,
    ILogger<VoiceBillingPipeline> log) : IVoiceBillingPipeline
{
    public async Task<Result<IReadOnlyList<MatchResult>>> ProcessUtteranceAsync(string utterance, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(utterance))
            return Result.Failure<IReadOnlyList<MatchResult>>(ErrorCodes.VoiceNothingHeard);

        var lines = parser.Parse(utterance);
        if (lines.Count == 0)
            return Result.Failure<IReadOnlyList<MatchResult>>(ErrorCodes.VoiceNoMatch, "No item phrase in utterance");

        var active = await items.GetActiveAsync(ct);
        var results = lines.Select(l => ItemMatcher.Match(l, active, options.Value)).ToList();

        // FIX(VOICE-7, 2026-10-20, FIX-1): Flaw: LearnedAlias.LastUsedMs was stamped only when an
        // alias was LEARNED, never when it was USED — so AliasLearner's "LRU" cap evicted the
        // least-recently-LEARNED alias: a daily-used alias learned long ago could be evicted while
        // a never-used newer one stayed. Fix: when a learned alias decides an AutoAdd, stamp its
        // LastUsedMs with the clock and save the item.
        // Why: LRU must mean least-recently-USED, or the cap un-teaches the shop's most-used words.
        // Logged in docs/AGENT_FIX_LOG.md.
        var now = clock.Now.ToUnixTimeMilliseconds();
        foreach (var r in results.Where(r => r.Kind == MatchKind.AutoAdd && r.Best?.Learned is not null))
        {
            var item = active.First(i => i.Id == r.Best!.ItemId);
            var usedKey = TeluguText.MatchKey(r.Best!.Learned!.Text);
            var idx = item.LearnedAliases.FindIndex(a => TeluguText.MatchKey(a.Text) == usedKey);
            if (idx < 0) continue;
            item.LearnedAliases[idx] = item.LearnedAliases[idx] with { LastUsedMs = now };
            await items.SaveAsync(item, ct);
        }

        var auto = results.Count(r => r.Kind == MatchKind.AutoAdd);
        var confirm = results.Count(r => r.Kind == MatchKind.Confirm);
        var miss = results.Count - auto - confirm;
        // Counts only — never the utterance text (NFR-08).
        if (confirm + miss > 0)
            log.LogWarning("Voice utterance: {Phrases} phrases, {Auto} auto, {Confirm} confirm, {Miss} no-match", results.Count, auto, confirm, miss);
        else
            log.LogInformation("Voice utterance: {Phrases} phrases, all auto-added", results.Count);

        return Result.Success<IReadOnlyList<MatchResult>>(results);
    }
}
