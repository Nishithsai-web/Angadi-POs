using Angadi.Application.Common;
using Angadi.Application.Platform;

namespace Angadi.Infrastructure.Billing;

/// <summary>
/// C2 (AGENT_TEST_FIXES / ARCHITECT_CORRECTIONS): a stateless stand-in for Google Play Billing.
/// It never grants Pro, so the 50-free-bills quota is always enforced; purchases fail closed.
/// The Android Play Billing adapter replaces this registration in the platform layer.
/// </summary>
public sealed class StubSubscriptionService : ISubscriptionService
{
    private static readonly SubscriptionStatus Free = new(IsPro: false, Plan: null, RenewsOn: null);

    public Task<SubscriptionStatus> GetStatusAsync(CancellationToken ct = default) => Task.FromResult(Free);

    public Task<Result<SubscriptionStatus>> PurchaseAsync(SubscriptionPlan plan, CancellationToken ct = default)
        => Task.FromResult(Result.Failure<SubscriptionStatus>(ErrorCodes.PurchaseFailed, "stub_billing_unavailable"));

    public Task<Result<SubscriptionStatus>> RestoreAsync(CancellationToken ct = default)
        => Task.FromResult(Result.Failure<SubscriptionStatus>(ErrorCodes.PurchaseFailed, "stub_billing_unavailable"));
}
