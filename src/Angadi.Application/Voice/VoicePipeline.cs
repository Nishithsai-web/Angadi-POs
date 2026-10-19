using Angadi.Application.Common;
using Angadi.Domain.Common;
using Angadi.Domain.Items;

namespace Angadi.Application.Voice;

/// <summary>
/// One spoken item phrase after parsing. "బియ్యం రెండు కేజీలు" → Name "బియ్యం", Qty 2000, Unit Kilogram.
/// Qty/Unit are null when not spoken (pipeline defaults to 1 of the item's own unit).
/// </summary>
public sealed record ParsedLine(string Name, Qty? Qty, Unit? Unit, string Raw);

/// <summary>Deterministic, rule-based, rules from Resources/voice/*.json (ADR-5). No LLM.</summary>
public interface ITeluguParser
{
    /// <summary>Splits an utterance on commas / "ఇంకా" / "మరియు" and parses each part.</summary>
    IReadOnlyList<ParsedLine> Parse(string utterance);
}

public sealed record MatchCandidate(Guid ItemId, string DisplayName, double Score);

public enum MatchKind { AutoAdd = 0, Confirm = 1, NoMatch = 2 }

/// <summary>Top candidates, best first. Kind is decided by VoiceOptions thresholds (FR-07).</summary>
public sealed record MatchResult(ParsedLine Line, MatchKind Kind, IReadOnlyList<MatchCandidate> Candidates);

/// <summary>Fuzzy match against the shop's own items + aliases, not open dictation.</summary>
public interface IItemMatcher
{
    Task<MatchResult> MatchAsync(ParsedLine line, CancellationToken ct = default);
}

/// <summary>FR-07: a confirmed correction becomes a shop-local alias on the item.</summary>
public interface IAliasLearner
{
    Task LearnAsync(Guid itemId, string heard, CancellationToken ct = default);
}

/// <summary>Parser → matcher for a whole utterance. The UI adds AutoAdd results and asks about Confirm.</summary>
public interface IVoiceBillingPipeline
{
    Task<Result<IReadOnlyList<MatchResult>>> ProcessUtteranceAsync(string utterance, CancellationToken ct = default);
}
