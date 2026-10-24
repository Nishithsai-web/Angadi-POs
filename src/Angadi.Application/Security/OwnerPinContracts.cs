using Angadi.Application.Common;
using Angadi.Application.Platform;

namespace Angadi.Application.Security;

/// <summary>
/// V2S: the owner PIN and the owner session. Only a salted PBKDF2-SHA256 hash of the PIN is stored (Meta); the PIN
/// itself is never logged or kept. A correct PIN unlocks owner actions for <see cref="UnlockMinutes"/> minutes.
/// Wrong PINs: <see cref="FreeAttempts"/> tries, then a lock of 60 s that doubles with each further miss, up to
/// 15 minutes; the count and lock survive an app restart.
/// </summary>
public interface IOwnerSession
{
    public const int UnlockMinutes = 5;
    public const int FreeAttempts = 5;

    Task<bool> IsPinSetAsync(CancellationToken ct = default);
    /// <summary>True when owner actions may run now: no PIN is set, or the owner unlocked within the last few minutes.</summary>
    Task<bool> IsOwnerAsync(CancellationToken ct = default);
    Task<OwnerPinStatus> GetStatusAsync(CancellationToken ct = default);
    /// <summary>Checks the PIN (counting wrong tries) and unlocks on success. OWNER_PIN_WRONG / OWNER_PIN_LOCKED / OWNER_PIN_NOT_SET.</summary>
    Task<Result> UnlockAsync(string pin, CancellationToken ct = default);
    /// <summary>Ends the owner session now.</summary>
    void Lock();
    /// <summary>Stores a new PIN hash (the caller has checked the format and, when a PIN exists, the old PIN). Unlocks.</summary>
    Task SetPinAsync(string pin, CancellationToken ct = default);
    /// <summary>Removes the PIN hash and the wrong-try state.</summary>
    Task ClearPinAsync(CancellationToken ct = default);
    /// <summary>Audit trail: an outbox event and a log line naming the action (never the PIN, never customer data).</summary>
    Task AuditAsync(string action, CancellationToken ct = default);
}

/// <param name="LockedSeconds">Seconds until another try is allowed; 0 when not locked.</param>
public sealed record OwnerPinStatus(bool IsSet, bool IsUnlocked, int LockedSeconds);

/// <summary>
/// The phone's own screen lock (PIN, pattern, password, fingerprint) as proof that the person resetting a forgotten
/// owner PIN owns the phone. Android: KeyguardManager.createConfirmDeviceCredentialIntent.
/// </summary>
public interface IDeviceCredential
{
    /// <summary>False when the phone has no screen lock set.</summary>
    bool IsAvailable { get; }
    /// <summary>Shows the phone's lock screen; true only when the owner unlocked it.</summary>
    Task<bool> ConfirmAsync(string title, string description, CancellationToken ct = default);
}

public static class OwnerPin
{
    public static bool IsValidFormat(string? pin) =>
        pin is { Length: >= 4 and <= 6 } && pin.All(char.IsAsciiDigit);
}

public sealed record GetOwnerPinStatusQuery : IQuery<OwnerPinStatus>;

/// <summary>Owner types the PIN: owner actions unlock for a few minutes. Not a transaction: wrong tries must persist.</summary>
public sealed record UnlockOwnerCommand(string Pin) : ICommand<Result>, INoTransaction
{
    public override string ToString() => nameof(UnlockOwnerCommand);   // never print the PIN
}

/// <summary>"Lock now" in Settings.</summary>
public sealed record LockOwnerCommand : ICommand<Result>, INoTransaction;

/// <summary>Set (no PIN yet: <paramref name="CurrentPin"/> null) or change (needs the current PIN) the owner PIN.</summary>
public sealed record SetOwnerPinCommand(string? CurrentPin, string NewPin, string ConfirmPin) : ICommand<Result>, INoTransaction
{
    public override string ToString() => nameof(SetOwnerPinCommand);
}

/// <summary>Turn the owner PIN off (needs the PIN).</summary>
public sealed record RemoveOwnerPinCommand(string Pin) : ICommand<Result>, INoTransaction
{
    public override string ToString() => nameof(RemoveOwnerPinCommand);
}

/// <summary>
/// Forgotten PIN. Cleared only after the phone's own screen lock is confirmed; on a phone without a screen lock,
/// only when <paramref name="ShopNameTyped"/> matches the shop name exactly.
/// </summary>
public sealed record ResetOwnerPinCommand(string? ShopNameTyped) : ICommand<Result>, INoTransaction;

/// <summary>Buying the Pro plan goes through the dispatcher so it is owner-only like every other money setting.</summary>
public sealed record PurchaseSubscriptionCommand(SubscriptionPlan Plan) : ICommand<Result<SubscriptionStatus>>, INoTransaction, IOwnerOnly;
