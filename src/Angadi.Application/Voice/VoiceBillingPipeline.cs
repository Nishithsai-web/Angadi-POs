using Angadi.Application.Common;
using Angadi.Application.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Angadi.Application.Voice;

/// <summary>Utterance → parsed phrases → matches (blueprint §4). Deterministic and free per bill.</summary>
public sealed class VoiceBillingPipeline(
    ITeluguParser parser,
    IItemRepository items,
    IOptions<VoiceOptions> options,
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
