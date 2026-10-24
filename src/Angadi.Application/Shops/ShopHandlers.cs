using Angadi.Application.Common;
using Angadi.Application.Persistence;
using Angadi.Application.Platform;
using Microsoft.Extensions.Logging;

namespace Angadi.Application.Shops;

internal sealed class ShopDirectoryHandlers(
    IShopDirectory shops, IShopProfileRepository profile, ISubscriptionService subscription, IDataSafety safety,
    ILogger<ShopDirectoryHandlers> log)
    : IRequestHandler<ListShopsQuery, ShopList>,
      IRequestHandler<AddShopCommand, Result<ShopEntry>>,
      IRequestHandler<SwitchShopCommand, Result>,
      IRequestHandler<RenameShopCommand, Result>
{
    /// <summary>The current shop's list name follows its profile name until the owner renames it.</summary>
    public async Task<ShopList> Handle(ListShopsQuery query, CancellationToken ct)
    {
        var list = shops.List();
        var current = list.First(s => s.IsCurrent);
        if (current.Name.Length == 0 && await profile.GetAsync(ct) is { Name.Length: > 0 } p && shops.Rename(current.Id, p.Name).IsSuccess)
            list = shops.List();
        return new ShopList(list);
    }

    public async Task<Result<ShopEntry>> Handle(AddShopCommand command, CancellationToken ct)
    {
        var status = await subscription.GetStatusAsync(ct);
        if (!status.IsPro && shops.List().Count >= 1) return Result.Failure<ShopEntry>(ErrorCodes.ShopNeedsPro);
        await SnapshotQuietlyAsync(ct);
        var added = await shops.AddAndSwitchAsync(ct);
        if (added.IsSuccess) log.LogInformation("Shop added and opened ({ShopCount} shops)", shops.List().Count);
        return added;
    }

    public async Task<Result> Handle(SwitchShopCommand command, CancellationToken ct)
    {
        if (shops.List().FirstOrDefault(s => string.Equals(s.Id, command.ShopId, StringComparison.Ordinal)) is not { } target)
            return Result.Failure(ErrorCodes.ShopNotFound);
        if (target.IsCurrent) return Result.Success();
        await SnapshotQuietlyAsync(ct);
        return await shops.SwitchAsync(target.Id, ct);
    }

    public Task<Result> Handle(RenameShopCommand command, CancellationToken ct)
    {
        var name = (command.Name ?? "").Trim();
        if (name.Length == 0) return Task.FromResult(Result.Failure(ErrorCodes.ShopNameRequired));
        return Task.FromResult(shops.Rename(command.ShopId, name));
    }

    /// <summary>A last safety copy of the shop being left. Never blocks the switch.</summary>
    private async Task SnapshotQuietlyAsync(CancellationToken ct)
    {
        var r = await safety.SnapshotAsync(ct);
        if (r.IsFailure) log.LogWarning("Safety copy before shop switch skipped: {Code}", r.Error.Code);
    }
}
