# Angadi POS — Support Runbook (v1)
For whoever answers the shopkeeper's phone call. Each entry: **symptom → check → fix**.
Build and release steps live in `deployment-and-rollback.md`; this page is for after install.

## First ask for the logs
Settings → *Export logs* → share to the support WhatsApp. The zip holds 7 days of `angadi-YYYYMMDD.log`.
Each line has a correlation id, so one failed action can be followed end to end. Phone numbers are never logged.
If export itself fails ("couldn't prepare the log file"), the phone is out of storage: free space first.

## Billing
| Symptom | Check | Fix |
|---|---|---|
| "This month's 50 free bills are used" | Settings → Subscription | Expected on the free tier. The count is per calendar month and cannot be reset by changing the phone date (H2). |
| Paid but still limited | Is the app installed from Play (not a sideloaded APK)? Same Google account? | Settings → Subscription → *Restore*. Status is re-checked with Play daily; offline, a confirmed subscription is kept for 7 days. |
| Customer's UPI app says "invalid" / wrong name | Settings → Shop details: is the UPI ID right? | Tap **Scan my UPI QR** and scan the QR the bank/PhonePe/Paytm gave the shop: it fills the exact payee and merchant code. |
| Owner wants their printed QR on screen | Settings → Shop details | **Upload QR photo**; at payment tap **Show my shop QR** (the customer then types the amount). |
| Scanner opens the typing box instead | Camera permission denied, or no camera | Android Settings → Apps → Angadi → Permissions → Camera → Allow. |
| "Old GST slab" when billing | Items page shows an orange notice listing them | Open each item, pick 0/5/18/40%. 12% and 28% ended with GST 2.0. |
| "₹X short" | Cash typed is less than the bill | Re-enter cash, or switch tender to UPI/udhaar. |
| "Amount too large" | A typo like 1,00,000 kg | Fix the quantity. Limits: 1,00,000 units per line, ₹1 crore per item price. |
| Void refused: "payments already recorded" | The udhaar bill's customer has paid part | By design (H1): no refunds in v1. Record a correcting entry instead. |

## Voice
| Symptom | Check | Fix |
|---|---|---|
| Mic does nothing / "no internet" | Some phones need Google speech data for Telugu | Settings → Google → Voice → Offline speech → download Telugu; or type. |
| Always asks "which one?" for a word | Generic words (నూనె, సబ్బు, పప్పు) ask on purpose | Say the specific name once and pick it; after **two** confirmations it auto-adds (M2). |
| Learned the wrong item | Picking a different item for the same words lowers the wrong one; two contradictions remove it | Pick the right item twice. |

## Data
| Symptom | Check | Fix |
|---|---|---|
| Restore says "not an Angadi backup" | File was edited, truncated or is from another app | Use an untouched backup. The live data was not changed (H3). |
| Data looks wrong after restore | `angadi.db.pre-restore` is kept next to the database | Support can swap it back via a debug build; never delete it on the phone. |
| New phone | Old phone: Settings → Backup → share the file | New phone: install, Settings → Restore. |

## Escalate to development when
A crash repeats, totals don't match the receipt, or stock doesn't match bills. Send the logs zip, the bill number and the time. These are bugs, not support questions.
