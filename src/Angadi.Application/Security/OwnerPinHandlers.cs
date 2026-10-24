using Angadi.Application.Common;
using Angadi.Application.Persistence;
using Angadi.Application.Platform;

namespace Angadi.Application.Security;

internal sealed class SetOwnerPinValidator : IValidator<SetOwnerPinCommand>
{
    public Error? Validate(SetOwnerPinCommand c) =>
        !OwnerPin.IsValidFormat(c.NewPin) ? new Error(ErrorCodes.OwnerPinInvalid)
        : !string.Equals(c.NewPin, c.ConfirmPin, StringComparison.Ordinal) ? new Error(ErrorCodes.OwnerPinMismatch)
        : null;
}

/// <summary>V2S: owner PIN set / change / remove / unlock / lock / forgotten-PIN reset.</summary>
internal sealed class OwnerPinHandlers(
    IOwnerSession owner, IDeviceCredential device, IShopProfileRepository shop, ISubscriptionService subscription, ILocalizer text) :
    IRequestHandler<GetOwnerPinStatusQuery, OwnerPinStatus>,
    IRequestHandler<UnlockOwnerCommand, Result>,
    IRequestHandler<LockOwnerCommand, Result>,
    IRequestHandler<SetOwnerPinCommand, Result>,
    IRequestHandler<RemoveOwnerPinCommand, Result>,
    IRequestHandler<ResetOwnerPinCommand, Result>,
    IRequestHandler<PurchaseSubscriptionCommand, Result<SubscriptionStatus>>
{
    public Task<OwnerPinStatus> Handle(GetOwnerPinStatusQuery query, CancellationToken ct) => owner.GetStatusAsync(ct);

    public Task<Result> Handle(UnlockOwnerCommand command, CancellationToken ct) => owner.UnlockAsync(command.Pin ?? "", ct);

    public Task<Result> Handle(LockOwnerCommand command, CancellationToken ct)
    {
        owner.Lock();
        return Task.FromResult(Result.Success());
    }

    public async Task<Result> Handle(SetOwnerPinCommand command, CancellationToken ct)
    {
        var changing = await owner.IsPinSetAsync(ct);
        if (changing)
        {
            // Changing the PIN is an owner action: the current PIN is checked with the same wrong-try lock.
            var proved = await owner.UnlockAsync(command.CurrentPin ?? "", ct);
            if (proved.IsFailure) return proved;
        }
        await owner.SetPinAsync(command.NewPin, ct);
        await owner.AuditAsync(changing ? "OwnerPinChanged" : "OwnerPinSet", ct);
        return Result.Success();
    }

    public async Task<Result> Handle(RemoveOwnerPinCommand command, CancellationToken ct)
    {
        var proved = await owner.UnlockAsync(command.Pin ?? "", ct);
        if (proved.IsFailure) return proved;
        await owner.AuditAsync("OwnerPinRemoved", ct);
        await owner.ClearPinAsync(ct);
        return Result.Success();
    }

    public async Task<Result> Handle(ResetOwnerPinCommand command, CancellationToken ct)
    {
        if (!await owner.IsPinSetAsync(ct)) return Result.Failure(ErrorCodes.OwnerPinNotSet);
        bool proved;
        if (device.IsAvailable)
        {
            proved = await device.ConfirmAsync(text["owner.pin.reset.title"], text["owner.pin.reset.device"], ct);
        }
        else
        {
            // No screen lock on this phone: the owner types the shop name exactly as saved.
            var name = (await shop.GetAsync(ct))?.Name;
            proved = !string.IsNullOrWhiteSpace(name) && string.Equals(command.ShopNameTyped?.Trim(), name.Trim(), StringComparison.Ordinal);
        }
        if (!proved) return Result.Failure(ErrorCodes.OwnerPinResetDenied);
        await owner.AuditAsync("OwnerPinReset", ct);
        await owner.ClearPinAsync(ct);
        return Result.Success();
    }

    public Task<Result<SubscriptionStatus>> Handle(PurchaseSubscriptionCommand command, CancellationToken ct) =>
        subscription.PurchaseAsync(command.Plan, ct);
}

/// <summary>For requests that are owner-only only in some cases: checked inside the handler.</summary>
internal static class OwnerGuard
{
    /// <summary>Null when allowed (and audited when a PIN is set); OWNER_PIN_REQUIRED otherwise.</summary>
    public static async Task<Error?> CheckAsync(IOwnerSession owner, string action, CancellationToken ct)
    {
        if (!await owner.IsPinSetAsync(ct)) return null;
        if (!await owner.IsOwnerAsync(ct)) return new Error(ErrorCodes.OwnerPinRequired, action);
        await owner.AuditAsync(action, ct);
        return null;
    }
}
