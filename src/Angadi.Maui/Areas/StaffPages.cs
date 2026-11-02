using System.Globalization;
using Angadi.Application.Common;
using Angadi.Application.Platform;
using Angadi.Application.Security;
using Angadi.Maui.Core;

namespace Angadi.Maui.Areas;

/// <summary>RBAC-1 display names for roles and people (all text from the JSON resources).</summary>
internal static class AccessText
{
    public static string RoleName(ILocalizer text, Role role) => role switch
    {
        Role.Owner => text["role.owner"],
        Role.Manager => text["role.manager"],
        _ => text["role.cashier"],
    };

    /// <summary>"Ravi (cashier)", or "Owner" / "Counter" when nobody named is signed in.</summary>
    public static string Who(ILocalizer text, string name, Role role) =>
        name.Length > 0 ? $"{name} ({RoleName(text, role)})" : role == Role.Owner ? text["role.owner"] : text["role.counter"];
}

/// <summary>
/// RBAC-1 Settings → Staff &amp; roles (owner only): add managers and cashiers with their own PINs, change a role or a
/// PIN, turn someone off when they leave, and choose what cashiers may do without calling a manager.
/// </summary>
public sealed class StaffPage : ContentPage
{
    private readonly ISender _sender;
    private readonly IDialogService _dialogs;
    private readonly ILocalizer _text;
    private readonly VerticalStackLayout _list = new() { Spacing = 8 };
    private readonly VerticalStackLayout _grants = new() { Spacing = 6 };
    private bool _loading;

    public StaffPage(ISender sender, IDialogService dialogs, ILocalizer text)
    {
        _sender = sender;
        _dialogs = dialogs;
        _text = text;
        Title = text["settings.staff"];
        var add = Ui.Button(text["staff.add"], big: true);
        add.Clicked += SafeUi.On(AddAsync);
        Content = Ui.Scroll(
            Ui.Title(text["staff.title"]), Ui.Caption(text["staff.hint"]), _list, add,
            Ui.Title(text["staff.grants.title"]), Ui.Caption(text["staff.grants.hint"]), _grants);
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await SafeUi.Run(RefreshAsync);
    }

    private async Task RefreshAsync()
    {
        var staff = await _sender.Send(new ListStaffQuery());
        _list.Clear();
        if (staff.Count == 0) _list.Add(Ui.Caption(_text["staff.none"]));
        foreach (var s in staff)
        {
            var label = AccessText.Who(_text, s.Name, s.Role) + (s.IsActive ? "" : " · " + _text["staff.off"]);
            var b = s.IsActive ? Ui.Secondary(label) : Ui.Button(label, Theme.TextSecondary);
            b.HorizontalOptions = LayoutOptions.Fill;
            var member = s;
            b.Clicked += SafeUi.On(async () => await EditAsync(member));
            _list.Add(b);
        }

        _loading = true;
        try
        {
            var grants = await _sender.Send(new GetCashierGrantsQuery());
            _grants.Clear();
            foreach (var (permission, key) in new[] { (Permission.VoidBill, "staff.grant.void"), (Permission.Refund, "staff.grant.refund"), (Permission.CollectDues, "staff.grant.dues") })
            {
                var toggle = new Switch { IsToggled = grants.Contains(permission) };
                var p = permission;
                toggle.Toggled += SafeUi.On<ToggledEventArgs>(async e => await SetGrantAsync(p, e.Value));
                var row = new Grid { ColumnSpacing = 8, ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) } };
                row.Add(Ui.Body(_text[key]), 0);
                row.Add(toggle, 1);
                _grants.Add(row);
            }
        }
        finally { _loading = false; }
    }

    private async Task SetGrantAsync(Permission permission, bool on)
    {
        if (_loading) return;
        var current = new HashSet<Permission>(await _sender.Send(new GetCashierGrantsQuery()));
        if (on) current.Add(permission); else current.Remove(permission);
        var r = await _sender.Send(new SaveCashierGrantsCommand(current));
        await _dialogs.ToastAsync(r.IsSuccess ? _text["settings.saved"] : _text.Error(r.Error));
        if (r.IsFailure) await RefreshAsync();
    }

    private async Task<Role?> PickRoleAsync()
    {
        var manager = _text["role.manager"];
        var cashier = _text["role.cashier"];
        var choice = await _dialogs.ChooseAsync(_text["staff.role.pick"], _text["common.cancel"], cashier, manager);
        return choice is null ? null : choice == manager ? Role.Manager : Role.Cashier;
    }

    /// <summary>Entered twice; null when cancelled or the two don't match (already said).</summary>
    private async Task<string?> AskNewPinAsync(string name)
    {
        var first = await _dialogs.PromptAsync(_text["staff.pin.title"], _text.Format("staff.pin.prompt", name), _text["common.ok"], _text["common.cancel"], keyboard: Keyboard.Numeric);
        if (first is null) return null;
        var again = await _dialogs.PromptAsync(_text["staff.pin.title"], _text["pin.set.again"], _text["common.ok"], _text["common.cancel"], keyboard: Keyboard.Numeric);
        if (again is null) return null;
        if (!string.Equals(first.Trim(), again.Trim(), StringComparison.Ordinal))
        {
            await _dialogs.ToastAsync(_text["pin.mismatch"]);
            return null;
        }
        return first.Trim();
    }

    private async Task AddAsync()
    {
        var name = await _dialogs.PromptAsync(_text["staff.add"], _text["staff.name.prompt"], _text["common.ok"], _text["common.cancel"]);
        if (string.IsNullOrWhiteSpace(name)) return;
        if (await PickRoleAsync() is not { } role) return;
        if (await AskNewPinAsync(name.Trim()) is not { } pin) return;
        await SaveAsync(new SaveStaffCommand(null, name, role, pin));
    }

    private async Task EditAsync(StaffMember s)
    {
        var changeRole = _text["staff.action.role"];
        var changePin = _text["staff.action.pin"];
        var toggle = s.IsActive ? _text["staff.action.off"] : _text["staff.action.on"];
        var choice = await _dialogs.ChooseAsync(AccessText.Who(_text, s.Name, s.Role), _text["common.cancel"], changeRole, changePin, toggle);
        if (choice is null) return;
        if (choice == changeRole)
        {
            if (await PickRoleAsync() is { } role) await SaveAsync(new SaveStaffCommand(s.Id, s.Name, role, null));
        }
        else if (choice == changePin)
        {
            if (await AskNewPinAsync(s.Name) is { } pin) await SaveAsync(new SaveStaffCommand(s.Id, s.Name, s.Role, pin));
        }
        else
        {
            var r = await _sender.Send(new SetStaffActiveCommand(s.Id, !s.IsActive));
            await _dialogs.ToastAsync(r.IsSuccess ? _text["settings.saved"] : _text.Error(r.Error));
            await RefreshAsync();
        }
    }

    private async Task SaveAsync(SaveStaffCommand command)
    {
        var r = await _sender.Send(command);
        await _dialogs.ToastAsync(r.IsSuccess ? _text["settings.saved"] : _text.Error(r.Error));
        await RefreshAsync();
    }
}

/// <summary>RBAC-1 Settings → Activity log (owner only): who voided, refunded, wrote off or changed what, last 30 days.</summary>
public sealed class ActivityPage : ContentPage
{
    private readonly ISender _sender;
    private readonly IDialogService _dialogs;
    private readonly ILocalizer _text;
    private readonly VerticalStackLayout _list = new() { Spacing = 6 };

    public ActivityPage(ISender sender, IDialogService dialogs, ILocalizer text)
    {
        _sender = sender;
        _dialogs = dialogs;
        _text = text;
        Title = text["settings.activity"];
        Content = Ui.Scroll(Ui.Title(text["settings.activity"]), Ui.Caption(text["activity.hint"]), _list);
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await SafeUi.Run(async () =>
        {
            var r = await _sender.Send(new ListActivityQuery(30));
            _list.Clear();
            if (r.IsFailure) { await _dialogs.ToastAsync(_text.Error(r.Error)); return; }
            if (r.Value.Count == 0) _list.Add(Ui.Caption(_text["activity.empty"]));
            foreach (var e in r.Value)
            {
                var line = e.At.ToString("dd MMM HH:mm", CultureInfo.InvariantCulture) + " · " + _text["activity." + e.Action] + " · " + AccessText.Who(_text, e.Who, e.Role);
                if (e.ApprovedByRole is { } approverRole)
                    line += " · " + _text.Format("activity.approved", AccessText.Who(_text, e.ApprovedBy ?? "", approverRole));
                _list.Add(Ui.Card(Ui.Body(line, 14), padding: 8));
            }
        });
    }
}
