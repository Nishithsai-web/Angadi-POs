using System.Globalization;
using Angadi.Application.Common;
using Angadi.Application.Platform;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Plugin.InAppBilling;

namespace Angadi.Maui.Platform;

/// <summary>
/// FR-34/35 Google Play Billing adapter (replaces the C2 stub on devices).
///   • Status is cached on the device and re-checked with Play at most once a day, so finalizing a bill never
///     waits on the network.
///   • Offline: a Pro status confirmed within the last 7 days is kept (a shop with no signal can still bill);
///     after that the app falls back to the free tier until Play confirms again.
///   • Every purchase is acknowledged (Play refunds unacknowledged purchases after 3 days).
///   • No server in v1: trust is the Play client's purchase list. Server-side verification is v2.
/// A sideloaded APK or missing Play products simply reads as free; purchases fail with PURCHASE_FAILED.
/// </summary>
internal sealed class PlaySubscriptionService(IOptions<BillingOptions> options, IClock clock, ILogger<PlaySubscriptionService> log)
    : ISubscriptionService
{
    private const string Key = "angadi.pro";            // "plan|confirmedAtMs"
    private static readonly TimeSpan Recheck = TimeSpan.FromDays(1);
    private static readonly TimeSpan OfflineGrace = TimeSpan.FromDays(7);
    private static readonly SubscriptionStatus Free = new(false, null, null);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly BillingOptions _o = options.Value;

    public async Task<SubscriptionStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var cached = ReadCache();
        if (cached is { } c && clock.Now - c.ConfirmedAt < Recheck) return ToStatus(c);

        var refreshed = await RefreshAsync(ct);
        if (refreshed.IsSuccess) return refreshed.Value;
        return cached is { } stale && clock.Now - stale.ConfirmedAt < OfflineGrace ? ToStatus(stale) : Free;
    }

    public Task<Result<SubscriptionStatus>> RestoreAsync(CancellationToken ct = default) => RefreshAsync(ct);

    public async Task<Result<SubscriptionStatus>> PurchaseAsync(SubscriptionPlan plan, CancellationToken ct = default)
    {
        var productId = plan == SubscriptionPlan.Yearly ? _o.YearlyProductId : _o.MonthlyProductId;
        await _gate.WaitAsync(ct);
        var billing = CrossInAppBilling.Current;
        try
        {
            if (!await billing.ConnectAsync()) return Fail("not_connected");
            var purchase = await billing.PurchaseAsync(productId, ItemType.Subscription);
            if (purchase is null || purchase.State != PurchaseState.Purchased) return Fail("not_purchased");

            await AcknowledgeAsync(billing, purchase);
            WriteCache(plan);
            log.LogInformation("Subscription purchased: {Plan}", plan);
            return Result.Success(ToStatus((plan, clock.Now)));
        }
        catch (InAppBillingPurchaseException ex)
        {
            log.LogWarning("Purchase not completed: {Error}", ex.PurchaseError);
            return Fail(ex.PurchaseError.ToString());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Purchase failed");
            return Fail(ex.GetType().Name);
        }
        finally
        {
            await DisconnectAsync(billing);
            _gate.Release();
        }
    }

    private async Task<Result<SubscriptionStatus>> RefreshAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        var billing = CrossInAppBilling.Current;
        try
        {
            if (!await billing.ConnectAsync()) return Fail("not_connected");
            var purchases = await billing.GetPurchasesAsync(ItemType.Subscription);
            var active = purchases?.FirstOrDefault(p => p.State == PurchaseState.Purchased
                && (p.ProductId == _o.YearlyProductId || p.ProductId == _o.MonthlyProductId));

            if (active is null)
            {
                ClearCache();
                return Result.Success(Free);
            }

            await AcknowledgeAsync(billing, active);
            var plan = active.ProductId == _o.YearlyProductId ? SubscriptionPlan.Yearly : SubscriptionPlan.Monthly;
            WriteCache(plan);
            return Result.Success(ToStatus((plan, clock.Now)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning("Subscription check failed: {Type}", ex.GetType().Name);
            return Fail(ex.GetType().Name);
        }
        finally
        {
            await DisconnectAsync(billing);
            _gate.Release();
        }
    }

    private async Task AcknowledgeAsync(IInAppBilling billing, InAppBillingPurchase purchase)
    {
        if (purchase.IsAcknowledged == true || string.IsNullOrEmpty(purchase.PurchaseToken)) return;
        try { await billing.FinalizePurchaseAsync([purchase.PurchaseToken]); }
        catch (Exception ex) { log.LogWarning("Acknowledge failed, will retry on next check: {Type}", ex.GetType().Name); }
    }

    private static async Task DisconnectAsync(IInAppBilling billing)
    {
        try { await billing.DisconnectAsync(); } catch (Exception) { /* already closed */ }
    }

    // Status: RenewsOn is the next time Angadi re-confirms with Play (Play gives no expiry date to the client).
    private static SubscriptionStatus ToStatus((SubscriptionPlan Plan, DateTimeOffset ConfirmedAt) c) =>
        new(true, c.Plan, c.ConfirmedAt + Recheck);

    private (SubscriptionPlan Plan, DateTimeOffset ConfirmedAt)? ReadCache()
    {
        var raw = Preferences.Default.Get(Key, "");
        var parts = raw.Split('|');
        if (parts.Length == 2 && Enum.TryParse<SubscriptionPlan>(parts[0], out var plan)
            && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms))
        {
            var at = DateTimeOffset.FromUnixTimeMilliseconds(ms);
            return at <= clock.Now ? (plan, at) : null;   // a future stamp means the clock moved back: re-check
        }
        return null;
    }

    private void WriteCache(SubscriptionPlan plan) =>
        Preferences.Default.Set(Key, plan.ToString() + "|" + clock.Now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));

    private static void ClearCache() => Preferences.Default.Remove(Key);

    private static Result<SubscriptionStatus> Fail(string detail) => Result.Failure<SubscriptionStatus>(ErrorCodes.PurchaseFailed, detail);
}
