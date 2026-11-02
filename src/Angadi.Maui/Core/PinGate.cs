using Angadi.Application.Platform;
using Angadi.Application.Security;
using Microsoft.Extensions.DependencyInjection;

namespace Angadi.Maui.Core;

/// <summary>
/// FIX(SEC-4, 2026-10-20, FIX-3): the UI half of the owner-PIN design. Sensitive actions (void bill,
/// record dues payment/write-off, backup export, backup restore, UPI-ID/GSTIN edits) call
/// <see cref="CheckAsync"/> first. Everyday billing never touches this — the counter stays
/// friction-free. With NO PIN set the action proceeds (the owner hasn't chosen protection yet;
/// Settings shows a persistent nudge instead). All text comes from the JSON string resources —
/// nothing is hard-coded here. Logged in docs/AGENT_FIX_LOG.md.
/// </summary>
public static class PinGate
{
    /// <summary>
    /// RBAC-1: true = the action may proceed. Allowed for the person using the app (no PIN set = everyone is the
    /// owner), or someone allowed typed their PIN just now. The request pipeline checks the same rule again, so this is
    /// only the friendly early ask (e.g. before a file is picked for restore).
    /// </summary>
    public static async Task<bool> CheckAsync(Permission permission, IDialogService dialogs, ILocalizer text)
    {
        var access = SafeUi.Services?.GetService<IAccessControl>();
        if (access is null || await access.AllowsAsync(permission)) return true;
        var pin = await AskAsync(permission, dialogs, text);
        if (pin is null) return false;
        var approved = await access.ApproveAsync(pin, permission);
        if (approved.IsSuccess) return true;
        await dialogs.ToastAsync(text.Error(approved.Error));
        return false;
    }

    /// <summary>"A manager or the owner must allow this: type your PIN". Null = cancelled.</summary>
    public static async Task<string?> AskAsync(Permission permission, IDialogService dialogs, ILocalizer text)
    {
        var ask = AccessPolicy.OwnerOnly.Contains(permission) ? "access.ask.owner" : "access.ask.manager";
        var entered = await dialogs.PromptAsync(text["access.title"], text[ask], text["common.ok"], text["common.cancel"], keyboard: Keyboard.Numeric);
        return entered?.Trim();
    }

    /// <summary>The set/change-PIN flow: 4–6 digits, entered twice, stored only as a hash by IPinService.</summary>
    public static async Task SetPinAsync(IPinService pin, IDialogService dialogs, ILocalizer text)
    {
        var first = await dialogs.PromptAsync(text["pin.set.title"], text["pin.set.prompt"], text["common.ok"], text["common.cancel"], keyboard: Keyboard.Numeric);
        if (first is null) return;
        first = first.Trim();
        if (first.Length is < 4 or > 6 || !first.All(char.IsDigit))
        {
            await dialogs.ToastAsync(text["pin.invalid"]);
            return;
        }
        var again = await dialogs.PromptAsync(text["pin.set.title"], text["pin.set.again"], text["common.ok"], text["common.cancel"], keyboard: Keyboard.Numeric);
        if (again is null || again.Trim() != first)
        {
            await dialogs.ToastAsync(text["pin.mismatch"]);
            return;
        }
        await pin.SetPinAsync(first);
        await dialogs.ToastAsync(text["pin.set.done"]);
    }
}

/// <summary>RBAC-1: the pipeline's PIN prompt on the phone (see IApprovalPrompt).</summary>
internal sealed class ApprovalPrompt(IDialogService dialogs, ILocalizer text) : IApprovalPrompt
{
    public Task<string?> AskPinAsync(Permission permission, CancellationToken ct = default) => PinGate.AskAsync(permission, dialogs, text);
}
