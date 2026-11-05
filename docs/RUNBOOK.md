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
| "The bill is still saving…" | The phone was slow (very old phone, storage nearly full) | Tap the payment again. It is safe: a bill that already saved is shown again, never charged twice. |
| Bill lines came back after reopening | The app was closed mid-bill (call, Android memory) | By design: the unfinished bill is kept. Pay it, or tap **New bill** to drop it. |
| "Phone storage is full" | Android Settings → Storage | Delete photos/videos/WhatsApp media. Nothing was lost; the action that failed was not saved, so repeat it. |
| "₹X short" | Cash typed is less than the bill | Re-enter cash, or switch tender to UPI/udhaar. |
| "Amount too large" | A typo like 1,00,000 kg | Fix the quantity. Limits: 1,00,000 units per line, ₹1 crore per item price. |
| Void refused: "payments already recorded" | The udhaar bill's customer has paid part | By design (H1). Use **Return items** on the bill instead (v2): refund cash/UPI or cut udhaar. |
| Void refused: "this bill has returns" | Bills → the bill shows R- rows | By design: goods already came back on a return. Return the rest with **Return items**. |
| "The parts add up to ₹X but the bill is ₹Y" | Split screen: the remaining amount must be ₹0 after "Rest goes to" | Re-open **Split**; type the parts again (the bill changed after the split was entered). |
| Return refused: "only N can come back" | Bill detail lists earlier returns | Everything up to the sold quantity can be returned in total, never more. |
| Return refund came partly as cash although "Cut udhaar" was chosen | Customer page → dues ledger | By design: udhaar is never cut below zero; the rest is cash in hand. |
| Day-end "Net sales" lower than "Sales" | Reports → Returns (n) | Returns count on the day they happen, whatever day the bill was. GST summary does the same. |

## Voice
| Symptom | Check | Fix |
|---|---|---|
| Mic does nothing / "no internet" | Some phones need Google speech data for Telugu | Settings → Google → Voice → Offline speech → download Telugu; or type. |
| Always asks "which one?" for a word | Generic words (నూనె, సబ్బు, పప్పు) ask on purpose | Say the specific name once and pick it; after **two** confirmations it auto-adds (M2). |
| Learned the wrong item | Picking a different item for the same words lowers the wrong one; two contradictions remove it | Pick the right item twice. |

## Data
| Symptom | Check | Fix |
|---|---|---|
| "Data repaired" message at startup | The data file was damaged (power cut while writing, failing storage) | Angadi already put back its newest safety copy (taken whenever the app goes to the background; 3 days kept). Re-enter only bills made after the time shown. The damaged file is kept as `angadi.db.damaged-<time>` for a developer. |
| "Data repaired… no safety copy" | First day of use, or storage too full for copies | Settings → Restore from backup with the last shared backup. Then free storage space. |
| Restore says "not an Angadi backup" | File was edited, truncated or is from another app | Use an untouched backup. The live data was not changed (H3). |
| Data looks wrong after restore | `angadi.db.pre-restore` is kept next to the database | Support can swap it back via a debug build; never delete it on the phone. |
| New phone | Same Google account and Android backup on? | Install Angadi from Play: Android restores the data by itself. Otherwise: old phone Settings → Backup → share the file; new phone: install, Settings → Restore. |

## Purchases, batches & expiry (v2-stock)
| Symptom | Check | Fix |
|---|---|---|
| "This supplier's bill … is already entered" | The same supplier + bill number was saved before (case-insensitive) | Correct: a second entry would double the stock. If it really is a different bill, add a suffix (e.g. `123-B`). |
| A purchase has a wrong quantity | Saved purchases are immutable by design | Purchases → the bill → *Correct a line* (owner PIN): takes the quantity back out, linked to that purchase. Never more than the line added. |
| "… needs a batch number and an expiry" | The item has *Track batch & expiry* on | Enter the batch no and MM/YY from the strip/box. Switch tracking off on the item if the shop doesn't want it for that item. |
| Bill asks "sells from an expired batch" | Only expired stock is left for that item (Expiring list) | The owner confirms with the PIN, or removes the line. Expired stock should go out as *Damaged* on the item's batch. |
| Batches show a "No batch" row | Stock that existed before tracking was switched on, or sold before its purchase was entered (negative) | Normal. Count the shelf and use *Count correction* on the right batch. |
| Report says "batches don't match stock" | Should never happen | Escalate with the logs zip. A restore of a backup with this problem is refused ("batch(es) whose quantity does not match"). |
| Margin looks wrong | It is an ESTIMATE from each item's latest purchase cost; sold lines without a cost are left out (count shown) | Enter purchases for the top sellers so they get a cost. Never use it for GST filing. |

## Several shops on one phone (v2)
| Symptom | Check | Fix |
|---|---|---|
| "A second shop needs Angadi Pro" | The phone is on the free tier | Correct: one shop free. Buy Pro (Settings → Subscription); it covers every shop on the phone. |
| Free bills ran out "too early" | The 50 free bills count the bills of ALL shops on the phone this month | Correct by design. Pro removes the limit for every shop. |
| A shop's items/bills are "gone" | The top bar shows which shop is open (with 2+ shops) | Settings → Shops → tap the right shop. Nothing is shared between shops. |
| Restored a backup into the wrong shop | Backup/restore act on the open shop only | The previous data is kept as `<shop db>.pre-restore`; restore the right backup in each shop. |
| "Could not open that shop" | The other shop's file could not be opened (storage full, permissions) | The app stays on the current shop. Free storage and retry; send logs if it repeats. A damaged shop file is repaired from that shop's own safety copy ("Data repaired" on opening it). |
| Shop list lost after reinstall/restore | `files/shops.json` missing | Every `files/shops/<id>.db` found is listed again (name blank until the shop's setup name is read or it is renamed). |
## Receipt printer (v2 printing)
Bluetooth thermal printers, 58 mm (384 dots) or 80 mm (576 dots), ESC/POS raster (GS v 0). Angadi connects only to a
printer already **paired** in the phone's Bluetooth settings (classic SPP); it never scans, so it never asks for
location. Android 12+ asks once for "Nearby devices" when the owner opens Settings → Receipt printer → Choose. A
failed print never affects the bill: it is already saved; the shopkeeper can share it on WhatsApp instead.

| Symptom | Check | Fix |
|---|---|---|
| "No paired printer…" | Phone Settings → Bluetooth: is the printer under *Paired devices*? | Switch the printer on, pair it there (PIN usually 0000 or 1234), then Choose again in Angadi. |
| "No receipt printer set up yet…" | Settings → Receipt printer shows "No printer chosen yet" | Choose the paired printer there (owner PIN if one is set). |
| "Allow Angadi 'Nearby devices'…" | Permission was denied, or revoked later | Phone Settings → Apps → Angadi → Permissions → Nearby devices → Allow. |
| "Bluetooth is off…" | Quick settings Bluetooth tile | Turn Bluetooth on, print again. |
| "Can't reach the printer…" (after ~20 s) | Printer off / flat battery / more than ~10 m away / connected to another phone | Switch it on, bring it close, disconnect it from the other phone (or switch that phone's Bluetooth off), print again. |
| "The printer is out of paper…" | Paper-end light on the printer | Load a new roll (shiny side towards the print head), print again. Printers that don't report paper status just print blank or nothing — check the roll. |
| "The printer disconnected while printing…" | The job was cut off mid-way (battery, distance) | Tear the partial slip, print again. |
| "The printer did not answer in time…" | A very long 80 mm bill on a slow printer, or the printer hung | Switch the printer off and on, print again. |
| Prints garbage / Chinese characters | The printer is not ESC/POS (some label printers) or the wrong paper width is chosen | Pick the right width (58/80 mm) and print the test page; ESC/POS-compatible printers only. |
| Receipt is cut short at the right edge, or very narrow | Paper width setting doesn't match the printer | Settings → Receipt printer → choose the other width, print the test page. |
| Telugu letters broken on paper | Should never happen (same shaping as the PDF) | Escalate with a photo of the test page and the logs zip. |

Hardware note: printing over Bluetooth is not exercised in CI (the emulator has no printer). The ESC/POS bytes and the
raster are unit-tested; the Android SPP link is tested on a real printer before each release.

## Escalate to development when
A crash repeats, totals don't match the receipt, or stock doesn't match bills. Send the logs zip, the bill number and the time. These are bugs, not support questions.

## Chain report packs (CHAIN-1)
- "Pack rejected" = the chain code differs between the two phones, or the file was edited. Re-enter the same code on both and resend.
- "Pack unreadable" = not an Angadi file (or over 256 KB). Resend from the branch.
- The owner's view is only as new as the last file imported; there is no live sync in v1. Chain data lives in the per-phone `chain.json`; it is not in backups, so re-import after a reinstall.
