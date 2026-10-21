# Data safety form: answers for Angadi

Play Console → **Policy → App content → Data safety → Start**. Play counts data collected by SDKs inside the app as
collected by the app. Angadi itself has no server, no analytics and no ads, but the barcode scanner
(BarcodeScanning.Native.Maui → **Google ML Kit**) sends Google diagnostics. Source:
<https://developers.google.com/ml-kit/android-data-disclosure>. If any SDK is added or removed, this form changes in the same release.

## 1. Data collection and security

| Play asks | Answer |
|---|---|
| Does your app collect or share any of the required user data types? | **Yes** |
| Is all of the user data collected by your app encrypted in transit? | **Yes** (ML Kit sends over HTTPS) |
| Which of the following methods of account creation does your app support? | **My app does not allow users to create an account** |
| Do you provide a way for users to request that their data is deleted? | **No** (nothing identifies a user; the data is held by Google's ML Kit service, not by the developer) |

## 2. Data types: tick exactly these two

| Category → type | What ML Kit sends |
|---|---|
| **App info and performance → Diagnostics** | Device model, manufacturer, OS version; app package name and version; performance metrics (latency); API configuration; feature version; event types; error codes |
| **Device or other IDs → Device or other IDs** | A per-installation / device identifier used for diagnostics (not intended to identify the user) |

Leave everything else unticked: location, personal info, financial info, contacts, messages, photos and videos, audio,
files, calendar, health, web browsing, app activity. (Bills, customers, stock and settings never leave the device through the app.)

## 3. Questions for each of the two types (same answers for both)

| Play asks | Answer |
|---|---|
| Is this data collected, shared, or both? | **Collected** (not shared: Google states ML Kit does not transfer it to third parties) |
| Is this data processed ephemerally? | **No** |
| Is this data required for your app, or can users choose whether it's collected? | **Data collection is required** (users can't turn it off) |
| Why is this user data collected? | **Analytics** (Google: "diagnostics and usage analytics"). Also tick **App functionality** only if Play review asks. |

## Why the rest is not "collected"

| What happens | Why it is not collection |
|---|---|
| **Android Auto Backup** (database + app settings, `backup_rules.xml` / `data_extraction_rules.xml`) | Android's own backup service copies them to the **user's own** Google account, handled entirely by Android. The developer cannot read it. |
| **Voice billing** | The mic uses Android's speech recognizer (Google's own app, under Google's privacy policy). Angadi receives only the text and keeps it on the device. Angadi never stores or sends audio. |
| **Google Play Billing** (Pro subscription) | Payment happens in Google Play's own purchase sheet. Angadi only asks Play "is Pro active?" and caches the answer on the device. |
| **Receipts, dues reminders, backups, log export** | Only when the user taps Share / WhatsApp / SMS / Backup / Export logs, to the app the user picks. User-initiated sharing is not collection. |
| **UPI QR** | Drawn on the device from the shop's own UPI ID. Payment is between the customer's UPI app and the shop's bank. |

Expected store label: **"Data collected: App info and performance, Device or other IDs" · "No data shared with third
parties" · "Data is encrypted in transit" · "You can't request that data be deleted".** The privacy policy must say the
same (another change updates `docs/PRIVACY_POLICY.md` for ML Kit).

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
| Data deletion URL | Not asked: no account creation. |
