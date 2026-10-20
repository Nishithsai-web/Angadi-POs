# Angadi POS — Privacy Policy (v1)
**Effective:** on first Play release · **App:** Angadi POS (`com.angadi.pos`)

> Owner action before publishing: fill in the two `[…]` fields, then host this page at a public URL (Play Console requires one).

## The short version
Angadi keeps your shop's data **on your phone or tablet only**. There is no Angadi server, no account and no analytics of our own. We (the developers) cannot see your bills, customers or stock. (The barcode scanner's Google library sends Google its own diagnostics — see below.)

## What the app stores, and where
| Data | Where | Why |
|---|---|---|
| Shop profile (name, address, GSTIN, UPI ID) | On the device (SQLite) | Printed on receipts and the UPI QR |
| Items, prices, stock | On the device | Billing and low-stock alerts |
| Bills and payments (cash / UPI reference / udhaar) | On the device | Receipts, reports, GST summary |
| Customers (name, phone, address, notes, promised pay date) and their dues | On the device | Udhaar ledger and WhatsApp/SMS reminders |
| App logs | On the device, 7 days, max 5 MB per file | Fixing problems. **Customer phone numbers and learned voice words are never written to logs.** |

## When data leaves the device — only when you act
- **Voice billing:** the mic button uses Android's built-in speech recognition. Depending on your phone, Google may process the audio to turn it into text, under [Google's privacy policy](https://policies.google.com/privacy). Angadi receives only the text. If you don't want this, type instead; billing works fully without voice.
- **Receipts and reminders:** when you tap *Share* or *WhatsApp*, the receipt PDF or message goes to the app you pick (WhatsApp, etc.). Angadi does not send anything by itself.
- **Backups and log export:** only when you tap *Backup* or *Export logs*, and only to the place you choose.
- **Android's own phone backup:** if backup is turned on in your phone's settings, Android copies Angadi's data (the shop database and app settings, nothing else) to **your own** Google account, encrypted, and puts it back when you install Angadi on a new phone with the same account. Angadi's makers cannot see these backups. Turn it off in Android Settings → Google → Backup. Android stops this backup once the app's data passes 25 MB (several thousand bills) and only runs on Wi-Fi, so also use *Back up data* regularly — the app shows when the last one was taken.
- **Barcode scanner:** barcodes are decoded on the device by Google ML Kit; no camera image leaves the phone. ML Kit itself sends Google diagnostic and usage data about the scanner (device and app information, performance), as described in [Google's ML Kit data disclosure](https://developers.google.com/ml-kit/android-data-disclosure). It never receives your bills, customers or stock.
- **Subscription (when enabled):** payments are handled by Google Play. Angadi never sees card or UPI details.

## What we don't do
No ads. No selling or sharing data. No location tracking. No contacts access. No uploads to Angadi's makers, ever.

## Permissions
- **Microphone:** only while you hold the voice button.
- **Camera:** only while the barcode scanner is open. Barcodes are read on the device; no images are saved or sent (ML Kit's diagnostics: see above).

## Deleting your data
Uninstalling the app deletes everything on the device. Backups you saved elsewhere stay where you put them; Android's phone backup in your Google account can be deleted from Google Drive → Backups.

## Children
Angadi is a business tool for shop owners and is not directed at children.

## Contact
[Developer name] · [support email]
