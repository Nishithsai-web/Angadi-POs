using Angadi.Application.Common;

namespace Angadi.Application.Voice;

/// <summary>
/// A1-11: "the shopkeeper picked this item for these heard words". Through the pipeline (correlation id, logging,
/// one transaction for the multi-item alias rewrite) instead of calling IAliasLearner from the view model.
/// </summary>
public sealed record LearnAliasCommand(Guid ItemId, string Heard) : ICommand<Result>;

internal sealed class LearnAliasHandler(IAliasLearner learner) : IRequestHandler<LearnAliasCommand, Result>
{
    public async Task<Result> Handle(LearnAliasCommand command, CancellationToken ct)
    {
        await learner.LearnAsync(command.ItemId, command.Heard, ct);
        return Result.Success();
    }
}
