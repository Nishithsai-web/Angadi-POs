using Angadi.Application.Catalog;
using Angadi.Application.Common;
using Angadi.Application.Platform;
using Angadi.Application.Security;
using Angadi.Domain.Shop;
using Angadi.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Angadi.Tests.Application;

/// <summary>RBAC-1: roles, staff PINs, approvals, the pipeline check, the activity log and shop isolation.</summary>
public sealed class AccessControlTests : IDisposable
{
    private const string OwnerPin = "4321";
    private const string ManagerPin = "1111";
    private const string CashierPin = "2222";

    private readonly AppHost _host = new();
    private IAccessControl Access => _host.Services.GetRequiredService<IAccessControl>();
    public void Dispose() => _host.Dispose();

    private async Task WithOwnerPinAsync()
    {
        await _host.SeedShopAsync();
        await _host.Services.GetRequiredService<IPinService>().SetPinAsync(OwnerPin);
    }

    private async Task AddStaffAsync()
    {
        Assert.True((await Access.SignInAsync(OwnerPin)).IsSuccess);
        Assert.True((await _host.Sender.Send(new SaveStaffCommand(null, "Lakshmi", Role.Manager, ManagerPin))).IsSuccess);
        Assert.True((await _host.Sender.Send(new SaveStaffCommand(null, "Ravi", Role.Cashier, CashierPin))).IsSuccess);
        Access.SignOut();
    }

    private Task<Result> SaveShop() => _host.Sender.Send(new SaveShopProfileCommand(new ShopProfile { Name = "రాము కిరాణా", UpiId = "ramu@okaxis" }));

    // ---- the permission table

    [Fact]
    public void Policy_OwnerDoesEverything_ManagerAllButOwnerOnly_CashierOnlyGrants()
    {
        var grants = new HashSet<Permission> { Permission.VoidBill };
        foreach (var p in Enum.GetValues<Permission>())
        {
            Assert.True(AccessPolicy.Allows(Role.Owner, p));
            Assert.Equal(!AccessPolicy.OwnerOnly.Contains(p), AccessPolicy.Allows(Role.Manager, p));
            Assert.Equal(p == Permission.VoidBill, AccessPolicy.Allows(Role.Cashier, p, grants));
            Assert.False(AccessPolicy.Allows(Role.Cashier, p));
        }
        // A grant outside the grantable set never counts.
        Assert.False(AccessPolicy.Allows(Role.Cashier, Permission.RestoreData, new HashSet<Permission> { Permission.RestoreData }));
    }

    [Fact]
    public void EveryProtectedRequest_ReturnsAResult_AndEveryProtectedCommandHasAnActivityLabel()
    {
        var text = _host.Services.GetRequiredService<ILocalizer>();
        foreach (var type in AccessPolicy.Map.Keys)
        {
            var result = type.GetInterfaces().Single(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IRequest<>)).GetGenericArguments()[0];
            Assert.True(typeof(Result).IsAssignableFrom(result), type.Name + " must return a Result so a refusal is an ordinary error");
            if (!type.Name.EndsWith("Command", StringComparison.Ordinal)) continue;
            foreach (var lang in new[] { UiLanguage.Telugu, UiLanguage.English })
            {
                text.Language = lang;
                Assert.NotEqual("activity." + type.Name, text["activity." + type.Name]);
            }
        }
    }

    // ---- behaviour

    [Fact]
    public async Task NoOwnerPin_EveryoneIsTheOwner_AsBefore()
    {
        await _host.SeedShopAsync();
        Assert.Equal(Role.Owner, (await Access.CurrentAsync()).Role);
        Assert.True((await SaveShop()).IsSuccess);
    }

    [Fact]
    public async Task WithAPin_TheCounterCannotChangeShopDetailsOrItems()
    {
        await WithOwnerPinAsync();
        Assert.True((await Access.CurrentAsync()).IsCounter);
        Assert.Equal(ErrorCodes.NotAllowedOwner, (await SaveShop()).Error.Code);
        var item = new Angadi.Domain.Items.Item { TeluguName = "బియ్యం", EnglishName = "Rice", Price = new Angadi.Domain.Common.Paise(5000) };
        Assert.Equal(ErrorCodes.NotAllowedManager, (await _host.Sender.Send(new SaveItemCommand(item, new Angadi.Domain.Common.Qty(1000)))).Error.Code);
    }

    [Fact]
    public async Task Manager_RunsTheShop_ButCannotDoOwnerOnlyThings()
    {
        await WithOwnerPinAsync();
        await AddStaffAsync();
        var signedIn = await Access.SignInAsync(ManagerPin);
        Assert.Equal(("Lakshmi", Role.Manager), (signedIn.Value.Name, signedIn.Value.Role));
        Assert.True(await Access.AllowsAsync(Permission.VoidBill));
        Assert.True(await Access.AllowsAsync(Permission.ViewCosts));
        Assert.Equal(ErrorCodes.NotAllowedOwner, (await SaveShop()).Error.Code);
        Assert.Equal(ErrorCodes.NotAllowedOwner, (await _host.Sender.Send(new SaveStaffCommand(null, "X", Role.Cashier, "9999"))).Error.Code);
    }

    [Fact]
    public async Task OwnerApproval_LetsOneActionThrough_IsLogged_AndExpires()
    {
        await WithOwnerPinAsync();
        await AddStaffAsync();
        await Access.SignInAsync(ManagerPin);
        Assert.True((await Access.ApproveAsync(OwnerPin, Permission.ShopSettings)).IsSuccess);
        Assert.True((await SaveShop()).IsSuccess);

        var log = await Access.ListActivityAsync(1);
        // The first-run shop setup (no PIN yet, the owner) is logged too; this is the manager's save.
        var entry = Assert.Single(log, e => e.Action == nameof(SaveShopProfileCommand) && e.Role == Role.Manager);
        Assert.Equal(("Lakshmi", Role.Manager, Role.Owner), (entry.Who, entry.Role, entry.ApprovedByRole!.Value));

        _host.Clock.Advance(TimeSpan.FromSeconds(IAccessControl.ApprovalSeconds + 1));
        Assert.Equal(ErrorCodes.NotAllowedOwner, (await SaveShop()).Error.Code);
    }

    [Fact]
    public async Task ACashierPin_CannotApproveAManagerAction()
    {
        await WithOwnerPinAsync();
        await AddStaffAsync();
        var r = await Access.ApproveAsync(CashierPin, Permission.AdjustStock);
        Assert.Equal(ErrorCodes.PinRoleTooLow, r.Error.Code);
        Assert.False(await Access.AllowsAsync(Permission.AdjustStock));
        Assert.True((await Access.ApproveAsync(ManagerPin, Permission.AdjustStock)).IsSuccess);
        Assert.True(await Access.AllowsAsync(Permission.AdjustStock));
    }

    [Fact]
    public async Task Cashiers_MayVoidAndRefundByDefault_AndTheOwnerCanTurnItOff()
    {
        await WithOwnerPinAsync();
        await AddStaffAsync();
        await Access.SignInAsync(CashierPin);
        Assert.True(await Access.AllowsAsync(Permission.VoidBill));
        Assert.True(await Access.AllowsAsync(Permission.Refund));
        Assert.False(await Access.AllowsAsync(Permission.CollectDues));

        await Access.SignInAsync(OwnerPin);
        Assert.True((await _host.Sender.Send(new SaveCashierGrantsCommand(new HashSet<Permission>()))).IsSuccess);
        Assert.Equal(ErrorCodes.ValidationFailed,
            (await _host.Sender.Send(new SaveCashierGrantsCommand(new HashSet<Permission> { Permission.RestoreData }))).Error.Code);
        await Access.SignInAsync(CashierPin);
        Assert.False(await Access.AllowsAsync(Permission.VoidBill));
    }

    [Fact]
    public async Task WrongPins_LockEveryPin_IncludingStaffPins()
    {
        await WithOwnerPinAsync();
        await AddStaffAsync();
        Assert.Equal(ErrorCodes.PinWrong, (await Access.SignInAsync("0000")).Error.Code);
        for (var i = 0; i < 4; i++) await Access.SignInAsync("0000");
        Assert.Equal(ErrorCodes.PinLocked, (await Access.SignInAsync(ManagerPin)).Error.Code);
        _host.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.True((await Access.SignInAsync(ManagerPin)).IsSuccess);
    }

    [Fact]
    public async Task StaffPins_AreUnique_AndNeedTheOwnerPinFirst()
    {
        await _host.SeedShopAsync();
        Assert.Equal(ErrorCodes.OwnerPinFirst, (await Access.SaveStaffAsync(null, "Ravi", Role.Cashier, CashierPin)).Error.Code);
        await _host.Services.GetRequiredService<IPinService>().SetPinAsync(OwnerPin);
        Assert.Equal(ErrorCodes.StaffPinTaken, (await Access.SaveStaffAsync(null, "Ravi", Role.Cashier, OwnerPin)).Error.Code);
        Assert.True((await Access.SaveStaffAsync(null, "Ravi", Role.Cashier, CashierPin)).IsSuccess);
        Assert.Equal(ErrorCodes.StaffPinTaken, (await Access.SaveStaffAsync(null, "Sita", Role.Cashier, CashierPin)).Error.Code);
        Assert.Equal(ErrorCodes.StaffRoleInvalid, (await Access.SaveStaffAsync(null, "Sita", Role.Owner, "7777")).Error.Code);
        Assert.Equal(ErrorCodes.StaffPinInvalid, (await Access.SaveStaffAsync(null, "Sita", Role.Cashier, "12")).Error.Code);
        Assert.Equal(ErrorCodes.StaffNameRequired, (await Access.SaveStaffAsync(null, "  ", Role.Cashier, "7777")).Error.Code);
    }

    [Fact]
    public async Task ATurnedOffStaffPin_StopsWorking()
    {
        await WithOwnerPinAsync();
        await AddStaffAsync();
        var ravi = (await Access.ListStaffAsync()).Single(s => s.Name == "Ravi");
        Assert.True((await Access.SetStaffActiveAsync(ravi.Id, false)).IsSuccess);
        Assert.Equal(ErrorCodes.PinWrong, (await Access.SignInAsync(CashierPin)).Error.Code);
    }

    [Fact]
    public async Task ThePipeline_AsksForAPin_AndCarriesOn()
    {
        using var host = new AppHost(configure: s => s.AddSingleton<IApprovalPrompt>(new FixedPrompt(OwnerPin)));
        await host.SeedShopAsync();
        await host.Services.GetRequiredService<IPinService>().SetPinAsync(OwnerPin);
        var r = await host.Sender.Send(new SaveShopProfileCommand(new ShopProfile { Name = "Ramu", UpiId = "ramu@okaxis" }));
        Assert.True(r.IsSuccess, r.Error.Code);
    }

    [Fact]
    public async Task TheCounter_CanStillSwitchLanguage()
    {
        await WithOwnerPinAsync();
        Assert.True((await _host.Sender.Send(new SetShopLanguageCommand(UiLanguage.English))).IsSuccess);
        Assert.Equal(UiLanguage.English, (await _host.Sender.Send(new GetShopProfileQuery()))!.Language);
    }

    [Fact]
    public async Task SwitchingShops_SignsEveryoneOut_SoOneShopsPinNeverOpensAnother()
    {
        await WithOwnerPinAsync();
        await AddStaffAsync();
        await Access.SignInAsync(ManagerPin);
        var db = _host.Services.GetRequiredService<AngadiDb>();
        var original = db.DatabasePath;
        var originalSnapshots = db.SnapshotDirectory;
        var other = Path.Combine(_host.Folder, "data", "shop2.db");

        await db.SwitchToAsync(other, Path.Combine(_host.Folder, "data", "snapshots2"));
        Assert.Equal(Role.Owner, (await Access.CurrentAsync()).Role);   // a new shop with no PIN of its own
        Assert.Empty(await Access.ListStaffAsync());                    // shop 1's staff do not exist here
        Assert.Equal(Role.Owner, (await Access.SignInAsync(ManagerPin)).Value.Role);   // no PIN here: the PIN means nothing

        await db.SwitchToAsync(original, originalSnapshots);
        Assert.True((await Access.CurrentAsync()).IsCounter);             // the manager is no longer signed in
    }

    private sealed class FixedPrompt(string pin) : IApprovalPrompt
    {
        public Task<string?> AskPinAsync(Permission permission, CancellationToken ct = default) => Task.FromResult<string?>(pin);
    }
}
