using Angadi.Application.Platform;

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
    /// <summary>True = the action may proceed (no PIN set, or the correct PIN was entered).</summary>
    public static async Task<bool> CheckAsync(IPinService pin, IDialogService dialogs, ILocalizer text)
    {
        if (!await pin.HasPinAsync()) return true;
        var entered = await dialogs.PromptAsync(text["pin.title"], text["pin.enter"], text["common.ok"], text["common.cancel"], keyboard: Keyboard.Numeric);
        if (entered is null) return false;
        var check = await pin.VerifyAsync(entered.Trim());
        switch (check.Result)
        {
            case PinCheck.Ok:
            case PinCheck.NoPin:
                return true;
            case PinCheck.Locked:
                await dialogs.ToastAsync(text.Format("pin.locked", check.LockedSecondsRemaining));
                return false;
            default:
                await dialogs.ToastAsync(text["pin.wrong"]);
                return false;
        }
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
