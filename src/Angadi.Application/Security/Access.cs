using Angadi.Application.Billing;
using Angadi.Application.Catalog;
using Angadi.Application.Common;
using Angadi.Application.Customers;
using Angadi.Application.Printing;
using Angadi.Application.Shops;
using Angadi.Application.Stock;
using Angadi.Domain.Shop;

namespace Angadi.Application.Security;

// FIX(RBAC-1, 2026-11-02, Claude): Flaw: one optional owner PIN was the only access control, checked only by the
// screens. A shop with staff could not tell a cashier from a manager, a forgotten screen check let anyone do anything,
// and nothing recorded who voided a bill. Fix: three roles, one permission table (AccessPolicy), enforced in the
// request pipeline (Sender) so it holds even when a screen forgets to ask, plus an activity log of every protected
// action with the person's name. Why: counter fraud (fake voids, UPI swaps, write-offs) is the real threat for a
// kirana or pharmacy with staff, and a check in one place cannot be skipped by one forgotten screen. Logged in
// docs/AGENT_FIX_LOG.md.

/// <summary>Who is using the app. Owner: everything in this shop. Manager: runs the shop day to day. Cashier: the counter.</summary>
public enum Role { Owner = 0, Manager = 1, Cashier = 2 }

/// <summary>Protected actions. Billing itself (adding, changing, removing lines before payment, taking payment) is never protected.</summary>
public enum Permission
{
    VoidBill = 1,
    Refund = 2,
    CollectDues = 3,
    WriteOffDues = 4,
    EditCatalog = 5,
    AdjustStock = 6,
    Purchases = 7,
    ViewCosts = 8,
    ExportData = 9,
    PrinterSetup = 10,
    SellExpired = 11,
    // Owner only
    RestoreData = 20,
    ShopSettings = 21,
    ManageShops = 22,
    ManageStaff = 23,
    Subscription = 24,
    ViewActivity = 25,
}

public static class AccessPolicy
{
    /// <summary>Only the owner, never a manager: the actions that move money away from the owner or rewrite the books.</summary>
    public static readonly IReadOnlySet<Permission> OwnerOnly = new HashSet<Permission>
    {
        Permission.RestoreData, Permission.ShopSettings, Permission.ManageShops, Permission.ManageStaff,
        Permission.Subscription, Permission.ViewActivity,
    };

    /// <summary>What the owner may hand to cashiers in a shop (Settings → Staff), so the counter need not wait for a manager.</summary>
    public static readonly IReadOnlySet<Permission> GrantableToCashier = new HashSet<Permission>
    {
        Permission.VoidBill, Permission.Refund, Permission.CollectDues,
    };

    public static bool Allows(Role role, Permission permission, IReadOnlySet<Permission>? cashierGrants = null) => role switch
    {
        Role.Owner => true,
        Role.Manager => !OwnerOnly.Contains(permission),
        Role.Cashier => GrantableToCashier.Contains(permission) && cashierGrants is not null && cashierGrants.Contains(permission),
        _ => false,
    };

    /// <summary>
    /// The permission a request needs, checked by the pipeline before its handler runs. Every request here returns a
    /// Result, so a refusal is an ordinary NOT_ALLOWED error (AccessPolicyTests checks that). Screens that only show
    /// data (cost reports) ask through AccessGate instead.
    /// </summary>
    public static Permission? Required(Type requestType) => Map.TryGetValue(requestType, out var p) ? p : null;

    internal static IReadOnlyDictionary<Type, Permission> Map { get; } = new Dictionary<Type, Permission>
    {
        [typeof(VoidBillCommand)] = Permission.VoidBill,
        [typeof(RecordReturnCommand)] = Permission.Refund,
        [typeof(RecordDuesPaymentCommand)] = Permission.CollectDues,
        [typeof(RecordDuesWriteOffCommand)] = Permission.WriteOffDues,
        [typeof(SaveItemCommand)] = Permission.EditCatalog,
        [typeof(SeedItemsCommand)] = Permission.EditCatalog,
        [typeof(AdjustStockCommand)] = Permission.AdjustStock,
        [typeof(AdjustBatchStockCommand)] = Permission.AdjustStock,
        [typeof(RecordPurchaseCommand)] = Permission.Purchases,
        [typeof(CorrectPurchaseLineCommand)] = Permission.Purchases,
        [typeof(StockInBatchCommand)] = Permission.Purchases,
        [typeof(SavePurchaseOrderCommand)] = Permission.Purchases,
        [typeof(MarkPurchaseOrderSentCommand)] = Permission.Purchases,
        [typeof(SaveSupplierCommand)] = Permission.Purchases,
        [typeof(ExportBackupCommand)] = Permission.ExportData,
        [typeof(SavePrinterSettingsCommand)] = Permission.PrinterSetup,
        [typeof(RestoreBackupCommand)] = Permission.RestoreData,
        [typeof(SaveShopProfileCommand)] = Permission.ShopSettings,
        [typeof(AddShopCommand)] = Permission.ManageShops,
        [typeof(RenameShopCommand)] = Permission.ManageShops,
        [typeof(SwitchShopCommand)] = Permission.ManageShops,
        [typeof(SaveStaffCommand)] = Permission.ManageStaff,
        [typeof(SetStaffActiveCommand)] = Permission.ManageStaff,
        [typeof(SaveCashierGrantsCommand)] = Permission.ManageStaff,
        [typeof(ListActivityQuery)] = Permission.ViewActivity,
    };
}

/// <param name="StaffId">Null for the owner and for the shared counter (nobody signed in).</param>
public sealed record AccessUser(string Name, Role Role, Guid? StaffId)
{
    public bool IsCounter => StaffId is null && Role == Role.Cashier;
}

public sealed record StaffMember(Guid Id, string Name, Role Role, bool IsActive);

/// <param name="Who">Staff name; empty for the owner and for the shared counter (the screen shows the role).</param>
/// <param name="ApprovedBy">Who typed their PIN to allow it, when that was someone else; empty name = the owner.</param>
public sealed record ActivityEntry(DateTimeOffset At, string Action, string Who, Role Role, string? ApprovedBy, Role? ApprovedByRole);

/// <summary>
/// The person using the app right now and what they may do. With no owner PIN set the app behaves exactly as before:
/// everyone is the owner. Once a PIN is set, the app starts at the shared counter (cashier) and a protected action asks
/// for the PIN of someone allowed to do it (a manager in the shop, or the owner); that approval lasts
/// <see cref="ApprovalSeconds"/> seconds. Switching shops signs everyone out, so one shop's PIN never opens another.
/// </summary>
public interface IAccessControl
{
    public const int ApprovalSeconds = 120;
    public const int MaxStaff = 20;

    Task<AccessUser> CurrentAsync(CancellationToken ct = default);
    Task<bool> AllowsAsync(Permission permission, CancellationToken ct = default);
    /// <summary>Someone allowed types their PIN: the action may run for the next couple of minutes. NOT_ALLOWED / PIN_WRONG / PIN_LOCKED.</summary>
    Task<Result<AccessUser>> ApproveAsync(string pin, Permission permission, CancellationToken ct = default);
    /// <summary>A staff member (or the owner) starts using the app; their name goes on everything they do.</summary>
    Task<Result<AccessUser>> SignInAsync(string pin, CancellationToken ct = default);
    /// <summary>Back to the shared counter; any approval ends.</summary>
    void SignOut();
    /// <summary>Activity log line for a protected action that just succeeded. Never throws.</summary>
    Task RecordAsync(string action, CancellationToken ct = default);

    Task<IReadOnlyList<StaffMember>> ListStaffAsync(CancellationToken ct = default);
    Task<Result<StaffMember>> SaveStaffAsync(Guid? id, string name, Role role, string? pin, CancellationToken ct = default);
    Task<Result> SetStaffActiveAsync(Guid id, bool active, CancellationToken ct = default);
    Task<IReadOnlySet<Permission>> CashierGrantsAsync(CancellationToken ct = default);
    Task SaveCashierGrantsAsync(IReadOnlySet<Permission> grants, CancellationToken ct = default);
    Task<IReadOnlyList<ActivityEntry>> ListActivityAsync(int days, CancellationToken ct = default);
}

/// <summary>
/// The app's "ask for a PIN" dialog (MAUI head only). When a protected request is refused, the pipeline asks through
/// this for the PIN of someone allowed and carries on if it is right, so every screen gets the same approval flow
/// without its own check. Null = the person cancelled.
/// </summary>
public interface IApprovalPrompt
{
    Task<string?> AskPinAsync(Permission permission, CancellationToken ct = default);
}

public sealed record GetCurrentUserQuery : IQuery<AccessUser>;
public sealed record ListStaffQuery : IQuery<IReadOnlyList<StaffMember>>;
public sealed record GetCashierGrantsQuery : IQuery<IReadOnlySet<Permission>>;

/// <summary>Add (Id null, PIN required) or edit a staff member (PIN null keeps the old one). Owner only.</summary>
public sealed record SaveStaffCommand(Guid? Id, string Name, Role Role, string? Pin) : ICommand<Result<StaffMember>>, INoTransaction
{
    public override string ToString() => nameof(SaveStaffCommand);   // never print the PIN
}

public sealed record SetStaffActiveCommand(Guid Id, bool Active) : ICommand<Result>, INoTransaction;
public sealed record SaveCashierGrantsCommand(IReadOnlySet<Permission> Grants) : ICommand<Result>, INoTransaction;
public sealed record ListActivityQuery(int Days) : IQuery<Result<IReadOnlyList<ActivityEntry>>>;

/// <summary>Language only: anyone may switch Telugu/English without touching the owner-only shop details.</summary>
public sealed record SetShopLanguageCommand(UiLanguage Language) : ICommand<Result>;

internal sealed class AccessHandlers(IAccessControl access, Persistence.IShopProfileRepository shops) :
    IRequestHandler<GetCurrentUserQuery, AccessUser>,
    IRequestHandler<ListStaffQuery, IReadOnlyList<StaffMember>>,
    IRequestHandler<GetCashierGrantsQuery, IReadOnlySet<Permission>>,
    IRequestHandler<SaveStaffCommand, Result<StaffMember>>,
    IRequestHandler<SetStaffActiveCommand, Result>,
    IRequestHandler<SaveCashierGrantsCommand, Result>,
    IRequestHandler<ListActivityQuery, Result<IReadOnlyList<ActivityEntry>>>,
    IRequestHandler<SetShopLanguageCommand, Result>
{
    public Task<AccessUser> Handle(GetCurrentUserQuery request, CancellationToken ct) => access.CurrentAsync(ct);
    public Task<IReadOnlyList<StaffMember>> Handle(ListStaffQuery request, CancellationToken ct) => access.ListStaffAsync(ct);
    public Task<IReadOnlySet<Permission>> Handle(GetCashierGrantsQuery request, CancellationToken ct) => access.CashierGrantsAsync(ct);

    public Task<Result<StaffMember>> Handle(SaveStaffCommand r, CancellationToken ct) =>
        access.SaveStaffAsync(r.Id, r.Name, r.Role, r.Pin, ct);

    public Task<Result> Handle(SetStaffActiveCommand r, CancellationToken ct) => access.SetStaffActiveAsync(r.Id, r.Active, ct);

    public async Task<Result> Handle(SaveCashierGrantsCommand r, CancellationToken ct)
    {
        if (r.Grants.Any(g => !AccessPolicy.GrantableToCashier.Contains(g)))
            return Result.Failure(ErrorCodes.ValidationFailed, "grant");
        await access.SaveCashierGrantsAsync(r.Grants, ct);
        return Result.Success();
    }

    public async Task<Result<IReadOnlyList<ActivityEntry>>> Handle(ListActivityQuery r, CancellationToken ct) =>
        Result<IReadOnlyList<ActivityEntry>>.Success(await access.ListActivityAsync(Math.Clamp(r.Days, 1, 90), ct));

    public async Task<Result> Handle(SetShopLanguageCommand r, CancellationToken ct)
    {
        var profile = await shops.GetAsync(ct);
        if (profile is null) return Result.Failure(ErrorCodes.ValidationFailed, "shop profile missing");
        if (profile.Language == r.Language) return Result.Success();
        profile.Language = r.Language;
        await shops.SaveAsync(profile, ct);
        return Result.Success();
    }
}
