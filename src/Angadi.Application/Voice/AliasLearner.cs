using Angadi.Application.Common;
using Angadi.Application.Persistence;
using Angadi.Application.Platform;
using Angadi.Domain.Items;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Angadi.Application.Voice;

/// <summary>
/// FR-07 + M2-CORRECTION (ARCHITECT_CORRECTIONS): learning that gets SAFER with use.
///   • A confirmed correction adds the heard words to the chosen item's LearnedAliases at confidence 1 (ask-only).
///   • Each further confirmation raises it; at 2 it auto-adds.
///   • The same words learned on a DIFFERENT item lose one confidence (the shopkeeper contradicted them); 0 removes.
///   • Never learns words that already belong to another item's curated names/aliases (seed-collision guard).
///   • Curated Aliases are never touched; LearnedAliases are LRU-capped.
/// </summary>
public sealed class AliasLearner(IItemRepository items, IClock clock, IOptions<VoiceOptions> options, ILogger<AliasLearner> log) : IAliasLearner
{
    private int MaxLearnedPerItem => Math.Max(1, options.Value.MaxLearnedAliasesPerItem);

    public async Task LearnAsync(Guid itemId, string heard, CancellationToken ct = default)
    {
        var alias = TeluguText.Normalize(heard);
        var key = TeluguText.MatchKey(alias);
        if (key.Length < 2) return;

        var all = await items.GetActiveAsync(ct);
        var chosen = all.FirstOrDefault(i => i.Id == itemId) ?? await items.GetAsync(itemId, ct);
        if (chosen is null) return;

        // Already a curated name/alias of the chosen item: nothing to learn.
        if (CuratedKeys(chosen).Contains(key)) return;

        // Seed-collision guard: words that are another item's curated name must keep meaning that item.
        if (all.Any(i => i.Id != itemId && CuratedKeys(i).Contains(key)))
        {
            log.LogInformation("Alias not learned: it belongs to another item");
            return;
        }

        var now = clock.Now.ToUnixTimeMilliseconds();

        // Contradiction: the same words learned elsewhere lose confidence.
        foreach (var other in all.Where(i => i.Id != itemId))
        {
            var idx = other.LearnedAliases.FindIndex(a => TeluguText.MatchKey(a.Text) == key);
            if (idx < 0) continue;
            var demoted = other.LearnedAliases[idx] with { Confidence = other.LearnedAliases[idx].Confidence - 1 };
            if (demoted.Confidence <= 0) other.LearnedAliases.RemoveAt(idx);
            else other.LearnedAliases[idx] = demoted;
            await items.SaveAsync(other, ct);
        }

        var existing = chosen.LearnedAliases.FindIndex(a => TeluguText.MatchKey(a.Text) == key);
        if (existing >= 0)
        {
            var found = chosen.LearnedAliases[existing];
            chosen.LearnedAliases[existing] = found with { Confidence = found.Confidence + 1, LastUsedMs = now };
        }
        else
        {
            chosen.LearnedAliases.Add(new LearnedAlias(alias, 1, now));
            if (chosen.LearnedAliases.Count > MaxLearnedPerItem)
                chosen.LearnedAliases.Remove(chosen.LearnedAliases.MinBy(x => x.LastUsedMs)!);   // LRU
        }

        await items.SaveAsync(chosen, ct);
        log.LogInformation("Voice alias learned for item {ItemId}", itemId);
        log.LogDebug("Learned alias text: {Alias}", alias);
    }

    private static HashSet<string> CuratedKeys(Item i) =>
        new[] { i.TeluguName, i.EnglishName }.Concat(i.Aliases).Select(TeluguText.MatchKey).Where(k => k.Length > 0).ToHashSet(StringComparer.Ordinal);
}
