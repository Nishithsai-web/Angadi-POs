namespace Angadi.Application.Platform;

/// <summary>
/// What the on-device subscription cache means (Play Billing adapter). Pure, so it is unit-tested.
/// A2-11: a shop that was Pro and has simply been offline past the grace period keeps billing — blocking a paying
/// shop at the counter because its internet is down is worse than a few unconfirmed days; the counter shows a
/// "connect to confirm" banner instead. The next successful check with Play clears the cache if the subscription
/// really ended (the shop then falls back to free).
/// </summary>
public static class SubscriptionCachePolicy
{
    public static readonly TimeSpan Recheck = TimeSpan.FromDays(1);
    public static readonly TimeSpan OfflineGrace = TimeSpan.FromDays(7);
    private static readonly SubscriptionStatus Free = new(false, null, null);

    public static SubscriptionStatus Evaluate(SubscriptionPlan? cachedPlan, DateTimeOffset? confirmedAt, DateTimeOffset now)
    {
        if (cachedPlan is not { } plan || confirmedAt is not { } at) return Free;
        return new SubscriptionStatus(true, plan, at + Recheck, NeedsReconfirm: now - at >= OfflineGrace);
    }
}
