# In-app products: the two Pro subscriptions

The app looks for exactly these IDs (`appsettings.json` → `Billing`; `tools/release_check.py` fails a release if they drift).

| Product ID | Name | Base plan ID | Billing period | Price (India) |
|---|---|---|---|---|
| `angadi_pro_monthly` | Angadi Pro – monthly | `monthly` | 1 month, auto-renewing | ₹200.00 |
| `angadi_pro_yearly` | Angadi Pro – yearly | `yearly` | 1 year, auto-renewing | ₹1,999.00 |

Product IDs can never be changed or reused once created, so type them carefully.

## Before you start (one time)
1. **Payments profile:** Play Console → **Settings → Payments profile** → create it (bank account for payouts). Products cannot be sold without it.
2. **Upload a build first:** Play only offers subscriptions once a bundle with the billing permission is in some track. Upload the first `.aab` to **Testing → Internal testing** (docs/RELEASE.md, B.3).

## Create each subscription (do this twice: monthly, then yearly)
1. Play Console → the Angadi app → **Monetize with Play → Products → Subscriptions → Create subscription**.
2. **Product ID:** `angadi_pro_monthly` · **Name:** `Angadi Pro – monthly` → **Create**.
3. **Subscription details** (shown to buyers): add a description in English, then **Add translation → Telugu**:
   - EN: `Unlimited bills every month. Voice, scan, UPI, udhaar and reports stay on your phone.`
   - TE: `ప్రతి నెలా అపరిమిత బిల్లులు. మీ డేటా మీ ఫోన్‌లోనే.`
   - Benefits (optional): `Unlimited bills` / `అపరిమిత బిల్లులు`.
4. **Add base plan** → **Base plan ID:** `monthly` → **Type:** Auto-renewing → **Billing period:** Monthly →
   **Grace period:** 7 days (default) → **Resubscribe:** Allow.
5. **Prices → Set prices** → select **India** → enter **200.00 INR** → **Update**. Other countries: leave them
   unselected (the app is sold in India only), or let Play convert if you want them.
6. **Save** → **Activate** the base plan.
7. Repeat with `angadi_pro_yearly`, name `Angadi Pro – yearly`, base plan `yearly`, period **Yearly**, **1,999.00 INR**.

**Keep exactly one active base plan and no offers (no free trial, no intro price) per subscription.** The app buys the
subscription's first plan; a second plan or an offer could be the one a shopkeeper gets charged for.

## Test before going live
1. **Setup → License testing** → add the testers' Gmail addresses → License response: **RESPOND_NORMALLY**.
   Their purchases are free and renew every few minutes.
2. Install the internal-testing build on a tester's phone → **More → Settings → Subscription** → tap **₹200 / month**
   → buy → the page must show "Pro — unlimited bills". Cancel in Play → **Restore purchase** → back to free.

## Money, plainly
Prices in India include GST. Google keeps its service fee (15% on subscriptions at the time of writing) and pays the
rest to the payments profile's bank account monthly. Shopkeepers cancel in the Play Store app; Angadi has no cancel button.
