using System.Globalization;
using Angadi.Application.Common;
using Angadi.Application.Platform;
using Angadi.Application.Security;
using Microsoft.Extensions.Logging;

namespace Angadi.Infrastructure.Persistence;

/// <summary>
/// FIX(RBAC-1, 2026-11-02, Claude): roles and staff for one shop, on the phone. Flaw: one owner PIN, checked only by
/// screens, could not separate a cashier from a manager and recorded nobody's name. Fix: staff rows in the shop's own
/// file (so shop A's staff and PINs mean nothing in shop B), a PIN check that shares the owner PIN's lockout (a staff
/// PIN cannot be guessed while the owner PIN is locked), short approvals for one action, and an activity row for every
/// protected action. Why: the counter threat is staff misuse; everything here works offline with no server. Logged in
/// docs/AGENT_FIX_LOG.md.
/// </summary>
internal sealed class AccessControl(AngadiDb db, IPinService pins, IClock clock, ILogger<AccessControl> log) : IAccessControl
{
    private const string GrantsKey = "access.cashier_grants";
    private const int KeepActivityDays = 180;
    private const int NameMax = 40;

    private static readonly AccessUser Owner = new("", Role.Owner, null);
    private static readonly AccessUser Counter = new("", Role.Cashier, null);
    /// <summary>When the owner has made no choice yet: cashiers may void and refund (the owner can turn it off).</summary>
    // DECISION(RBAC-GRANTS, 2026-11-02, owner): cashiers may void a bill and refund WITHOUT calling a manager, by
    // default. The owner asked for this explicitly ("cashier can void ... or refunds ... or else he need to always get
    // to main single owner"). Risk accepted with two controls: the owner can turn either grant off in Settings -> Staff,
    // and every void/refund is in the activity log with the cashier's name. Collecting / writing off dues stays off.
    // Not an oversight: do not flip without the owner's word.
    private static readonly IReadOnlySet<Permission> DefaultGrants = new HashSet<Permission> { Permission.VoidBill, Permission.Refund };

    private readonly object _lock = new();
    private AccessUser? _signedIn;
    private AccessUser? _approver;
    private DateTimeOffset _approvedUntil;
    /// <summary>The shop file the session belongs to (AngadiDb.Generation): a shop switch ends it.</summary>
    private int _generation = -1;
    private long _lastPruneMs;

    public async Task<AccessUser> CurrentAsync(CancellationToken ct = default)
    {
        if (!await pins.HasPinAsync(ct).ConfigureAwait(false)) return Owner;
        var (signedIn, approver) = Session();
        return approver ?? signedIn ?? Counter;
    }

    public async Task<bool> AllowsAsync(Permission permission, CancellationToken ct = default)
    {
        var user = await CurrentAsync(ct).ConfigureAwait(false);
        if (user.Role == Role.Owner) return true;
        if (AccessPolicy.Allows(user.Role, permission, user.Role == Role.Cashier ? await CashierGrantsAsync(ct).ConfigureAwait(false) : null)) return true;
        // An approval by a cashier must never hide the signed-in manager's own rights.
        var (signedIn, _) = Session();
        return signedIn is not null && signedIn.Role != user.Role && AccessPolicy.Allows(signedIn.Role, permission, null);
    }

    public async Task<Result<AccessUser>> ApproveAsync(string pin, Permission permission, CancellationToken ct = default)
    {
        if (await AllowsAsync(permission, ct).ConfigureAwait(false)) return Result<AccessUser>.Success(await CurrentAsync(ct).ConfigureAwait(false));
        var who = await IdentifyAsync(pin, ct).ConfigureAwait(false);
        if (who.IsFailure) return who;
        var grants = who.Value.Role == Role.Cashier ? await CashierGrantsAsync(ct).ConfigureAwait(false) : null;
        if (!AccessPolicy.Allows(who.Value.Role, permission, grants))
            return Result<AccessUser>.Failure(Error.With(ErrorCodes.PinRoleTooLow, who.Value.Name));
        lock (_lock)
        {
            Reset();
            _approver = who.Value;
            _approvedUntil = clock.Now.AddSeconds(IAccessControl.ApprovalSeconds);
        }
        return who;
    }

    public async Task<Result<AccessUser>> SignInAsync(string pin, CancellationToken ct = default)
    {
        var who = await IdentifyAsync(pin, ct).ConfigureAwait(false);
        if (who.IsFailure) return who;
        lock (_lock)
        {
            Reset();
            _signedIn = who.Value;
            _approver = null;
        }
        return who;
    }

    public void SignOut()
    {
        lock (_lock)
        {
            _signedIn = null;
            _approver = null;
        }
    }

    public async Task RecordAsync(string action, CancellationToken ct = default)
    {
        try
        {
            var hasPin = await pins.HasPinAsync(ct).ConfigureAwait(false);
            var (signedIn, approver) = Session();
            var actor = !hasPin ? Owner : signedIn ?? Counter;
            var approvedBy = hasPin && approver is not null && approver != actor ? approver : null;
            var now = clock.Now.ToUnixTimeMilliseconds();
            await db.RunAsync(c =>
            {
                c.Insert(new ActivityRow
                {
                    Id = Guid.NewGuid(), AtMs = now, Action = action, Who = actor.Name, Role = (int)actor.Role,
                    ApprovedBy = approvedBy?.Name, ApprovedByRole = approvedBy is null ? null : (int)approvedBy.Role,
                });
                if (now - _lastPruneMs > TimeSpan.FromDays(1).TotalMilliseconds)
                {
                    c.Execute("DELETE FROM Activity WHERE AtMs < ?", now - (long)TimeSpan.FromDays(KeepActivityDays).TotalMilliseconds);
                    _lastPruneMs = now;
                }
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Activity for {Action} not written", action);   // the action itself already succeeded
        }
    }

    public Task<IReadOnlyList<StaffMember>> ListStaffAsync(CancellationToken ct = default) =>
        db.ReadAsync<IReadOnlyList<StaffMember>>(c => c.Table<StaffRow>().ToList()
            .OrderByDescending(r => r.IsActive).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(ToMember).ToList(), ct);

    public async Task<Result<StaffMember>> SaveStaffAsync(Guid? id, string name, Role role, string? pin, CancellationToken ct = default)
    {
        if (!await pins.HasPinAsync(ct).ConfigureAwait(false)) return Result<StaffMember>.Failure(new Error(ErrorCodes.OwnerPinFirst));
        name = (name ?? "").Trim();
        if (name.Length == 0) return Result<StaffMember>.Failure(new Error(ErrorCodes.StaffNameRequired));
        if (name.Length > NameMax) name = name[..NameMax];
        if (role is not (Role.Manager or Role.Cashier)) return Result<StaffMember>.Failure(new Error(ErrorCodes.StaffRoleInvalid));
        pin = pin?.Trim();
        if (id is null && string.IsNullOrEmpty(pin)) return Result<StaffMember>.Failure(new Error(ErrorCodes.StaffPinInvalid));
        if (!string.IsNullOrEmpty(pin) && !(pin.Length is >= 4 and <= 6 && pin.All(char.IsAsciiDigit)))
            return Result<StaffMember>.Failure(new Error(ErrorCodes.StaffPinInvalid));

        // Hash work (PBKDF2) happens outside the write lock; the row write re-checks under it.
        var (ownerHash, rows) = await db.ReadAsync(c => (c.Find<MetaRow>(PinService.HashKey)?.Value, c.Table<StaffRow>().ToList()), ct).ConfigureAwait(false);
        var existing = id is null ? null : rows.FirstOrDefault(r => r.Id == id);
        if (id is not null && existing is null) return Result<StaffMember>.Failure(new Error(ErrorCodes.StaffNotFound));
        if (existing is null && rows.Count(r => r.IsActive) >= IAccessControl.MaxStaff)
            return Result<StaffMember>.Failure(Error.With(ErrorCodes.StaffLimitReached, IAccessControl.MaxStaff));
        string? newHash = null;
        if (!string.IsNullOrEmpty(pin))
        {
            // A PIN must name exactly one person, or the activity log could not say who did it.
            if (PinService.Matches(pin, ownerHash) || rows.Any(r => r.IsActive && r.Id != id && PinService.Matches(pin, r.PinHash)))
                return Result<StaffMember>.Failure(new Error(ErrorCodes.StaffPinTaken));
            newHash = PinService.HashToString(pin);
        }

        var row = existing ?? new StaffRow { Id = Guid.NewGuid(), IsActive = true, CreatedAtMs = clock.Now.ToUnixTimeMilliseconds() };
        row.Name = name;
        row.Role = (int)role;
        if (newHash is not null) row.PinHash = newHash;
        await db.RunAsync(c => c.InsertOrReplace(row), ct).ConfigureAwait(false);
        lock (_lock)
        {
            if (_signedIn?.StaffId == row.Id) _signedIn = null;   // role or PIN changed: sign in again
            if (_approver?.StaffId == row.Id) _approver = null;
        }
        return Result<StaffMember>.Success(ToMember(row));
    }

    public async Task<Result> SetStaffActiveAsync(Guid id, bool active, CancellationToken ct = default)
    {
        // FIX(STAFF-CAP, 2026-11-06, Claude): Flaw: the 20-staff limit was checked when ADDING staff only; turning
        // people off, adding new ones and turning the old ones back on went past it. Fix: reactivating checks the same
        // limit (not counting this person). Why: one rule, enforced on every path to an active staff PIN.
        if (active)
        {
            var others = await db.RunAsync(c => c.ExecuteScalar<int>("SELECT COUNT(*) FROM Staff WHERE IsActive = 1 AND Id <> ?", id), ct).ConfigureAwait(false);
            if (others >= IAccessControl.MaxStaff) return Result.Failure(Error.With(ErrorCodes.StaffLimitReached, IAccessControl.MaxStaff));
        }
        var changed = await db.RunAsync(c => c.Execute("UPDATE Staff SET IsActive = ? WHERE Id = ?", active, id), ct).ConfigureAwait(false);
        if (changed == 0) return Result.Failure(ErrorCodes.StaffNotFound);
        if (!active)
            lock (_lock)
            {
                if (_signedIn?.StaffId == id) _signedIn = null;
                if (_approver?.StaffId == id) _approver = null;
            }
        return Result.Success();
    }

    public async Task<IReadOnlySet<Permission>> CashierGrantsAsync(CancellationToken ct = default)
    {
        var raw = await db.ReadAsync(c => c.Find<MetaRow>(GrantsKey)?.Value, ct).ConfigureAwait(false);
        if (raw is null) return DefaultGrants;
        var set = new HashSet<Permission>();
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && Enum.IsDefined((Permission)v)
                && AccessPolicy.GrantableToCashier.Contains((Permission)v))
                set.Add((Permission)v);
        return set;
    }

    public Task SaveCashierGrantsAsync(IReadOnlySet<Permission> grants, CancellationToken ct = default)
    {
        var value = string.Join(",", grants.Where(AccessPolicy.GrantableToCashier.Contains).Select(g => ((int)g).ToString(CultureInfo.InvariantCulture)).Order(StringComparer.Ordinal));
        return db.RunAsync(c => { c.InsertOrReplace(new MetaRow { Key = GrantsKey, Value = value }); }, ct);
    }

    public Task<IReadOnlyList<ActivityEntry>> ListActivityAsync(int days, CancellationToken ct = default)
    {
        var since = clock.Now.AddDays(-days).ToUnixTimeMilliseconds();
        return db.ReadAsync<IReadOnlyList<ActivityEntry>>(c => c.Table<ActivityRow>().Where(r => r.AtMs >= since).OrderByDescending(r => r.AtMs).Take(500).ToList()
            .Select(r => new ActivityEntry(DateTimeOffset.FromUnixTimeMilliseconds(r.AtMs).ToOffset(clock.Now.Offset), r.Action, r.Who, (Role)r.Role,
                r.ApprovedBy, r.ApprovedByRole is { } ar ? (Role)ar : null)).ToList(), ct);
    }

    /// <summary>Whose PIN this is: an active staff member of this shop, or the owner. Shares the owner PIN's lockout.</summary>
    private async Task<Result<AccessUser>> IdentifyAsync(string pin, CancellationToken ct)
    {
        pin = (pin ?? "").Trim();
        if (!await pins.HasPinAsync(ct).ConfigureAwait(false)) return Result<AccessUser>.Success(Owner);
        var nowMs = clock.Now.ToUnixTimeMilliseconds();
        var (lockUntil, staff) = await db.ReadAsync(c =>
        {
            var raw = c.Find<MetaRow>(PinService.LockKey)?.Value;
            var until = long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
            return (until, c.Table<StaffRow>().Where(r => r.IsActive).ToList());
        }, ct).ConfigureAwait(false);
        if (lockUntil > nowMs) return Result<AccessUser>.Failure(Error.With(ErrorCodes.PinLocked, (int)((lockUntil - nowMs + 999) / 1000)));

        if (pin.Length is >= 4 and <= 6)
            foreach (var s in staff)
                if (PinService.Matches(pin, s.PinHash))
                    return Result<AccessUser>.Success(new AccessUser(s.Name, (Role)s.Role, s.Id));

        // Not a staff PIN: the owner check counts the wrong try, so guessing is limited for every PIN on the phone.
        var owner = await pins.VerifyAsync(pin, ct).ConfigureAwait(false);
        return owner.Result switch
        {
            PinCheck.Ok or PinCheck.NoPin => Result<AccessUser>.Success(Owner),
            PinCheck.Locked => Result<AccessUser>.Failure(Error.With(ErrorCodes.PinLocked, owner.LockedSecondsRemaining)),
            _ => Result<AccessUser>.Failure(new Error(ErrorCodes.PinWrong)),
        };
    }

    private (AccessUser? SignedIn, AccessUser? Approver) Session()
    {
        lock (_lock)
        {
            Reset();
            if (_approver is not null && clock.Now >= _approvedUntil) _approver = null;
            return (_signedIn, _approver);
        }
    }

    /// <summary>Under _lock: a different shop file is open → nobody from the old shop stays signed in.</summary>
    private void Reset()
    {
        var generation = db.Generation;
        if (generation == _generation) return;
        _generation = generation;
        _signedIn = null;
        _approver = null;
    }

    private static StaffMember ToMember(StaffRow r) => new(r.Id, r.Name, (Role)r.Role, r.IsActive);
}
