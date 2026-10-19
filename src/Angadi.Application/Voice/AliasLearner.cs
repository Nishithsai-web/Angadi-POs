using Angadi.Application.Persistence;
using Microsoft.Extensions.Logging;

namespace Angadi.Application.Voice;

/// <summary>
/// FR-07: when the shopkeeper confirms or corrects a voice match, the heard words become an alias
/// of that item, so next time it matches exactly (score 1.0 → auto-add). Per shop, on device.
/// </summary>
public sealed class AliasLearner(IItemRepository items, ILogger<AliasLearner> log) : IAliasLearner
{
    public const int MaxAliasesPerItem = 30;

    public async Task LearnAsync(Guid itemId, string heard, CancellationToken ct = default)
    {
        var alias = TeluguText.Normalize(heard);
        if (alias.Length < 2) return;

        var item = await items.GetAsync(itemId, ct);
        if (item is null) return;

        var key = TeluguText.MatchKey(alias);
        var known = new[] { item.TeluguName, item.EnglishName }.Concat(item.Aliases)
            .Any(n => TeluguText.MatchKey(n) == key);
        if (known) return;

        if (item.Aliases.Count >= MaxAliasesPerItem) item.Aliases.RemoveAt(0);   // oldest learned first
        item.Aliases.Add(alias);
        await items.SaveAsync(item, ct);
        log.LogInformation("Voice alias learned for item {ItemId}", itemId);
    }
}
