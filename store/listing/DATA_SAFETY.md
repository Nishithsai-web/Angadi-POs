# Data safety form: answers for Angadi

Play Console → **Policy → App content → Data safety → Start**. Answers below match what the app does today
(no server, no analytics SDK, no ads SDK). If any of that changes, this form must change in the same release.

## The questions

| Play asks | Answer | Why it is true |
|---|---|---|
| Does your app collect or share any of the required user data types? | **No** | Angadi has no server and no analytics, crash or ads SDK. Bills, customers, stock and settings live in the app's private storage on the device. The developer never receives any of it. |

With **No**, Play skips the per-data-type and security questions and shows "No data collected / No data shared". Save, then **Submit**.

## Why each "edge case" is still "No"

Play counts data as *collected* only when it is sent off the device by the app to the developer or a third party.
These do not count, and the privacy policy already says so in plain words:

| What happens | Why it is not collection by Angadi |
|---|---|
| **Android Auto Backup** (database + app settings, `backup_rules.xml` / `data_extraction_rules.xml`) | Android's own backup service copies them to the **user's own** Google account, handled entirely by Android. The developer cannot read it. The user turns it off in Android settings. |
| **Voice billing** | The mic uses Android's speech recognizer (Google's app, under Google's privacy policy). Angadi receives only the recognised text and keeps it on the device. Audio is never stored or sent by Angadi. Billing works fully without voice. |
| **Google Play Billing** (Pro subscription) | Payment happens inside Google Play's own purchase sheet. Angadi only asks Play "is Pro active?" and caches the answer on the device. No card, UPI or purchase data goes to the developer. |
| **Receipts, dues reminders, backups, log export** | Only when the user taps Share / WhatsApp / SMS / Backup / Export logs, to the app the user picks. User-initiated sharing is not collection. |
| **UPI QR** | The QR is drawn on the device from the shop's own UPI ID. Payment happens between the customer's UPI app and the shop's bank. |

**Conservative option (only if Play review asks about voice):** declare *Audio → Voice or sound recordings*:
collected = Yes, shared = No, **processed ephemerally**, **optional**, purpose = **App functionality**; encrypted in
transit = Yes; users can request deletion = not applicable (nothing is stored). Do not do this pre-emptively.

## Other App content declarations (same page)

| Section | Answer |
|---|---|
| Privacy policy | Public URL of `docs/PRIVACY_POLICY.md` after its `[…]` fields are filled in (`tools/release_check.py` blocks a release until then). |
| App access | **All functionality is available without special access** (no login). Pro only lifts the 50-bills-a-month limit; every screen works on the free plan. |
| Ads | **No, my app does not contain ads.** |
| Content rating | See `CONTENT_RATING.md`. |
| Target audience | **18 and over** only. Not designed for children. |
| News app | No. |
| Health apps | **No health features.** (Pharmacies use it only for billing; it holds no health or prescription data.) |
| Financial features | **My app doesn't provide any financial features.** Udhaar is a shop's own record of what customers owe; the app lends nothing, holds no money and moves no payments. |
| Government app | No. |
| Data deletion URL | Not needed: no account and no data collected. |
