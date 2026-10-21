<!-- OWNER-FILL before store submission: replace [Developer name] and [support email] at the bottom of this file, then host the page at a public URL and paste that URL into Play Console. Do not submit with the placeholders in place. -->
<!-- FIX(PRIVACY-1, 2026-10-20, FIX-5): this policy was rewritten against the code on 2026-10-20. The old text (a) claimed "no background uploads" while the app re-checks the Play subscription in the background, (b) never disclosed the automatic on-device safety copies, (c) omitted the Internet/Billing/Vibrate permissions and the feedback path. Logged in docs/AGENT_FIX_LOG.md. -->
# Angadi POS — Privacy Policy (v1)
**Effective:** on first Play release · **App:** Angadi POS (`com.angadi.pos`)

> Owner action before publishing: fill in the two `[…]` fields, then host this page at a public URL (Play Console requires one).

## The short version
Angadi keeps your shop's data **on your phone or tablet only**. There is no Angadi server, no account and no analytics. We (the developers) cannot see your bills, customers or stock.

## What the app stores, and where
| Data | Where | Why |
|---|---|---|
| Shop profile (name, address, GSTIN, UPI ID) | On the device (SQLite) | Printed on receipts and the UPI QR |
| Items, prices, stock | On the device | Billing and low-stock alerts |
| Bills and payments (cash / UPI reference / udhaar) | On the device | Receipts, reports, GST summary |
| Customers (name, phone, address, notes, promised-pay date) and their dues | On the device | Udhaar ledger and WhatsApp reminders |
| App logs | On the device, 7 days, max 5 MB per file | Fixing problems. **Customer phone numbers and learned voice words are never written to logs.** |
| Automatic safety copies of the database (daily snapshots, a copy taken before each app update and before each restore, and a damaged file moved aside if one is found) | On the device only | So a shop's records survive a crash, a failed update or a damaged file. Never uploaded anywhere. |

## When data leaves the device — only when you act
- **Voice billing:** the mic button uses Android's built-in speech recognition. Depending on your phone, Google may process the audio to turn it into text, under [Google's privacy policy](https://policies.google.com/privacy). Angadi receives only the text. If you don't want this, type instead; billing works fully without voice.
- **Receipts and reminders:** when you tap *Share* or *WhatsApp*, the receipt PDF or message goes to the app you pick (WhatsApp, etc.). Angadi does not send anything by itself.
- **Backups and log export:** only when you tap *Backup* or *Export logs*, and only to the place you choose.
- **Feedback:** if you use Settings → Notes & feedback and choose to attach logs, your message and the log file are sent through the app you pick (for example WhatsApp). Logs contain no customer phone numbers and no learned voice words.
- **Android's own phone backup:** if backup is turned on in your phone's settings, Android copies Angadi's data (the shop database and app settings, nothing else) to **your own** Google account, encrypted, and puts it back when you install Angadi on a new phone with the same account. Angadi's makers cannot see these backups. Turn it off in Android Settings → Google → Backup.
- **Barcode scanner:** the camera scanner uses Google ML Kit, which reads barcodes on the phone and sends Google anonymous diagnostics about the scanner itself (device and app version, performance, error codes) — never images, barcodes or shop data. See [ML Kit data disclosure](https://developers.google.com/ml-kit/android-data-disclosure).
- **Subscription (when enabled):** payments are handled by Google Play. Angadi never sees card or UPI details. The app also re-checks your subscription status with Google Play automatically in the background, so a renewal or cancellation is picked up without you opening the app; this check sends no shop data — no bills, customers or stock.

## What we don't do
No ads. No selling or sharing data. No location tracking. No contacts access. No background uploads of your shop data — the only automatic network use is Google Play checking the subscription, Android's speech service when you use voice, ML Kit's scanner diagnostics, and Android's own phone backup if you turned it on.

## Permissions
- **Microphone:** only while you hold the voice button.
- **Camera:** only while the barcode scanner is open. Barcodes are read on the device; no images are saved or sent.
- **Internet:** used by Android's speech recognition when you bill by voice, and by Google Play for subscription checks and payments.
- **Billing (Google Play):** to buy and verify the Angadi subscription.
- **Vibrate:** a short buzz confirms a barcode scan, so you know it registered without looking down.

## Deleting your data
Uninstalling the app deletes everything on the device, including the automatic safety copies described above. Backups you saved elsewhere (for example a file you shared to Drive or WhatsApp) stay where you put them — delete those in that app. Android's own phone backup in your Google account can be deleted from Google Drive → Backups.

## Children
Angadi is a business tool for shop owners and is not directed at children.

## Contact
[Developer name] · [support email]
