using Angadi.Application.Common;
using Angadi.Domain.Common;
using Angadi.Domain.Items;

namespace Angadi.Application.Voice;

/// <summary>
/// One spoken item phrase after parsing.
///   "బియ్యం రెండు కేజీలు"      → Name "బియ్యం", Qty 2000, Unit Kilogram
///   "పది రూపాయల ఉల్లిపాయలు"    → Name "ఉల్లిపాయలు", Amount ₹10 (quantity comes from the item's price)
/// Qty/Unit are null when not spoken: the bill then takes 1 of the item's own unit.
/// Qty is in the SPOKEN unit; UnitConversion maps it to the item's unit. IsAmbiguous (H6) marks a range like
/// "రెండు మూడు కేజీలు": the parser reports the uncertainty, the matcher downgrades it to Confirm.
/// </summary>
public sealed record ParsedLine(string Name, Qty? Qty, Unit? Unit, Paise? Amount, string Raw, bool IsAmbiguous = false);

/// <summary>Deterministic, rule-based, rules from Resources/voice/*.json (ADR-5). No LLM.</summary>
public interface ITeluguParser
{
    /// <summary>Splits an utterance on commas / "ఇంకా" / "మరియు" and parses each part.</summary>
    IReadOnlyList<ParsedLine> Parse(string utterance);
}

public sealed record MatchCandidate(Guid ItemId, string DisplayName, double Score);

public enum MatchKind { AutoAdd = 0, Confirm = 1, NoMatch = 2 }

/// <summary>Top candidates, best first. Kind is decided by VoiceOptions thresholds (FR-07).</summary>
public sealed record MatchResult(ParsedLine Line, MatchKind Kind, IReadOnlyList<MatchCandidate> Candidates)
{
    public MatchCandidate? Best => Candidates.Count > 0 ? Candidates[0] : null;
}

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
