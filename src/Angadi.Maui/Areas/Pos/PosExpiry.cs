using Angadi.Application.Platform;
using Angadi.Application.Stock;
using Angadi.Domain.Billing;
using Angadi.Maui.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;

namespace Angadi.Maui.Areas;

/// <summary>
/// v2-stock, kept out of PosViewModel.cs so the counter's main file barely changes: the expiry banner on the bill
/// screen and the "this sells from an expired batch" owner confirmation before finalize. Shops without batch-tracked
/// items never see either (the check returns nothing, the banner stays empty).
/// </summary>
public sealed partial class PosViewModel
{
    /// <summary>"2 batches expired, 3 expire within 30 days — tap" (empty = hidden).</summary>
    [ObservableProperty] private string _expiryBanner = "";

    private async Task RefreshExpiryBannerAsync()
    {
        var expiring = await Sender.Send(new GetExpiringQuery(30));
        ExpiryBanner = expiring.HasAlerts ? Text.Format("inv2.expiry.banner", expiring.Expired.Count, expiring.Within30.Count) : "";
    }

    [RelayCommand]
    private Task OpenExpiring() => SafeAsync(() => Nav.GoToAsync(StockRoutes.Expiring));

    /// <summary>
    /// False = nothing expired on this bill; true = the owner confirmed (PIN gate) selling expired stock;
    /// null = cancelled, do not finalize. Never silent: the finalize guard refuses an unconfirmed expired sale anyway.
    /// </summary>
    private async Task<bool?> ConfirmExpiredSaleAsync(Bill bill)
    {
        var expired = await Sender.Send(new CheckExpiredSaleQuery(bill.Lines.Select(l => new SaleQty(l.ItemId, l.Qty)).ToList()));
        if (expired.Count == 0) return false;
        var what = string.Join(", ", expired.Select(e => e.BatchNo.Length == 0 ? e.Name : $"{e.Name} ({e.BatchNo})").Distinct(StringComparer.Ordinal));
        if (!await Dialogs.ConfirmAsync(Text["inv2.expired.sale.title"], Text.Format("inv2.expired.sale.confirm", what), Text["common.yes"], Text["common.no"]))
            return null;
        if (!await PinGate.CheckAsync(Angadi.Application.Security.Permission.SellExpired, Dialogs, Text)) return null;
        return true;
    }
}
