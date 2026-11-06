# MARKET_REVIEW_LEARNINGS.md — what users of Hindi/Indian khata and billing apps complain about, and what Angadi does

**Sources (2026-11-06; thin samples, treat as directional):** Vyapar reviews on Software Advice (25 of 181 shown), OkCredit reviews on Techjockey (a handful), a Khatabook Play Store summary on Kimola (30 reviews, mostly loan complaints), and a vendor's own Dukaan AI article (self-promotional, no evidence).

| Complaint in those apps | Angadi today | Action |
|---|---|---|
| Needs a stable connection; no offline mode (OkCredit) | Offline-first: everything on the phone | Keep: never put a server call in the billing path |
| Data loss / stock not matching records (Vyapar) | Stock ledger check, quick_check, rolling snapshots, signed backups | Real-phone restore test before the closed test |
| Printer compatibility, barcode-print trouble (Vyapar) | Bluetooth ESC/POS printing built; unverified on real printers | Test two common 58 mm printers |
| Hard to learn; users ask for videos and guides (Vyapar) | Telugu-first screens, voice; Telugu guide in docs | Add short in-app help and a 2-minute video |
| High licence cost (Vyapar) | Rs 200/month, Rs 1,999/year | Keep |
| Unexpected logouts; weak backup options (OkCredit) | No accounts in v1; PIN sign-in; backup export | Keep; add cloud backup only with the server phase |
| No export to Excel (OkCredit) | **Added:** Settings -> Export to Excel (sales, dues, stock as CSV) | Done (spreadsheet export) |
| Late payment reminders (Vyapar/OkCredit) | Reminders are sent by the shopkeeper on demand via WhatsApp/SMS, so never late | Keep |
| Slow after updates (Vyapar) | PERF-1/2 guards (50,000 items stay quick) | Keep tests in CI |
| Slow or absent support (Vyapar) | Not yet: needs a support WhatsApp number | Owner to provide |
| Voice accuracy with accents, noise, Hindi-English mixing is never shown (vendor article) | Telugu dialect battery tests, Hindi/English number words, online retry; unverified on a real noisy counter | Real-phone test with background noise |

## What was checked on the voice example
"ara kg biyyam" (half a kilo of rice): `ara` = 0.5 is in the number rules and covered by parser tests; the line is priced from the item's price.
