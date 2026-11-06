using Angadi.Application.Billing;
using Angadi.Application.Common;
using Angadi.Application.Persistence;
using Angadi.Application.Platform;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Angadi.Application.Shops;

/// <summary>v2 several shops: Settings → Shops (list, add, switch, rename).</summary>
internal sealed class ShopHandlers(
    IShopDirectory shops, IShopProfileRepository profile, ISubscriptionService subscription, IDataSafety safety,
    ILocalizer text, IOptions<BillingOptions> billing, ILogger<ShopHandlers> log)
    : IRequestHandler<ListShopsQuery, ShopList>,
      IRequestHandler<AddShopCommand, Result<ShopEntry>>,
      IRequestHandler<SwitchShopCommand, Result>,
      IRequestHandler<RenameShopCommand, Result>
{
    public const int MaxNameLength = 40;

    /// <summary>The current shop's list name follows its profile name until the owner renames it.</summary>
    public async Task<ShopList> Handle(ListShopsQuery query, CancellationToken ct)
    {
        var list = shops.List();
        var current = list.First(s => s.IsCurrent);
        if (current.Name.Length == 0 && await profile.GetAsync(ct) is { Name.Length: > 0 } p
            && shops.Rename(current.Id, Clip(p.Name)).IsSuccess)
            list = shops.List();
        return new ShopList(list);
    }

    public async Task<Result<ShopEntry>> Handle(AddShopCommand command, CancellationToken ct)
    {
        // Fail closed like the bill quota: a subscription service that does not answer counts as free.
        var status = await SubscriptionGuard.StatusAsync(subscription, ct, log);
        if (!status.IsPro)
            return Result.Failure<ShopEntry>(Error.With(ErrorCodes.ShopNeedsPro, billing.Value.MonthlyPriceRupees));
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
        var switched = await shops.SwitchAsync(target.Id, ct);
        if (switched.IsFailure) return switched;

        // The language is one switch for the whole phone: the shop just opened follows it (its profile's language is
        // what the app applies at the next start).
        try
        {
            if (await profile.GetAsync(ct) is { } p && p.Language != text.Language)
            {
                p.Language = text.Language;
                await profile.SaveAsync(p, ct);
            }
        }
        // FIX(SHOP-SWITCH-2, 2026-11-06, Claude): Flaw: only IO/timeout failures were caught here, so a database error
        // while copying the language (busy, damaged profile) escaped AFTER the switch had already happened: the screen
        // said "switch failed" while the other shop was in fact open. Fix: any failure but cancellation is logged and the
        // switch still reports success. Why: the language is cosmetic; the switch result must tell the truth.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Language not carried over to the opened shop");
        }
        return Result.Success();
    }

    public Task<Result> Handle(RenameShopCommand command, CancellationToken ct)
    {
        var name = (command.Name ?? "").Trim();
        if (name.Length == 0) return Task.FromResult(Result.Failure(ErrorCodes.ShopNameRequired));
        return Task.FromResult(shops.Rename(command.ShopId, Clip(name)));
    }

    private static string Clip(string name)
    {
        var trimmed = name.Trim();
        return trimmed.Length <= MaxNameLength ? trimmed : trimmed[..MaxNameLength].TrimEnd();
    }

    /// <summary>A last safety copy of the shop being left. Never blocks the switch.</summary>
    private async Task SnapshotQuietlyAsync(CancellationToken ct)
    {
        var r = await safety.SnapshotAsync(ct);
        if (r.IsFailure) log.LogWarning("Safety copy before shop switch skipped: {Code}", r.Error.Code);
    }
}

/// <summary>Explicit registrations (trim-safe, no assembly scanning — ADR-2).</summary>
internal static class ShopsRegistration
{
    public static void AddShops(IServiceCollection services)
    {
        services.AddTransient<ShopHandlers>();
        services.AddTransient<IRequestHandler<ListShopsQuery, ShopList>>(sp => sp.GetRequiredService<ShopHandlers>());
        services.AddTransient<IRequestHandler<AddShopCommand, Result<ShopEntry>>>(sp => sp.GetRequiredService<ShopHandlers>());
        services.AddTransient<IRequestHandler<SwitchShopCommand, Result>>(sp => sp.GetRequiredService<ShopHandlers>());
        services.AddTransient<IRequestHandler<RenameShopCommand, Result>>(sp => sp.GetRequiredService<ShopHandlers>());
    }
}
