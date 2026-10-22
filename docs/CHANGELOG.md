# CHANGELOG.md — Angadi POS v1
Every meaningful change: date, what, the issue it addresses, and the one-line why. Newest first.

## 2026-10-22 (Claude Code, S1 money lane, round 2)
- **Fixed: paying again for an edited bill no longer reports the old bill as paid.** If a bill already saved and the shopkeeper added an item and paid again, the app now says which bill saved and for how much, and opens a new empty bill for the extra items. Why: "paid" must always mean the total on screen (T1-01).
- **Day-end shows refunds:** cash/UPI handed back today for bills sold on an earlier day. Why: the drawer count must match the report (S1-F1).
- **Fixed: a save that failed (phone storage full) no longer uses up a free bill** (S1-F2).

## 2026-10-21 (Claude Code, S1 money lane)
- **GST receipts landed:** composition shops print a Bill of Supply, regular shops show CGST/SGST, paid UPI receipts no longer say "scan to pay", the customer's name/address print on the receipt, voiding a filed month's bill needs confirmation, bill prefixes are validated, a failed save can be retried, quantities are capped. Why: these are legal receipt requirements and money-integrity fixes (F1, A2-01/02/05/14/15/16, A1-01/06/08, T2-01..04).
- **Fixed: a Pro shop's first bill each month was counted twice** toward the free-bill quota. Why: a lapsed subscription lost a free bill (T1-03).
- **Fixed: paying off udhaar now clears the promise date**, so the next new udhaar is not "due" the same day (T3-1).
- **Customer search finds a number typed with spaces, +91, a leading 0 or a dash** (T3-2).
- **A WhatsApp bill with more than 15 items now says how many more**, so the listed items never seem to miss the total (T3-3).
- **Voice: "650 పారాసెటమాల్" offers the 650 mg strip first** (VOICE-10).

## 2026-10-20 (afternoon, Claude Code) — production audit, round 1
- **The shipped build is now tested:** CI builds the Release APK (trimmed, as users get it) and runs the full emulator test on it next to the debug build. Why: every earlier E2E ran the debug build only; trimming bugs would have reached users first.
- **Google Play policy gate:** targetSdk pinned to 36 (required from 31 Aug 2026) and checked on every build, with debuggable releases and sensitive library permissions failing CI. Why: a rejected upload costs days.
- **Android Auto Backup scoped** to the database and settings. Why: by default it also copied safety copies and logs, and Android stops backing up an app past 25 MB, so a busy shop would silently lose its free Google backup.
- **Reports on a busy month:** bill lines are loaded in chunks of 500 instead of one query parameter per bill. Why: older SQLite builds fail past 999 parameters (33 bills a day for a month).
## 2026-10-20 (Claude Code) — build unblocked
- **Fixed: the CS0819 compile error that blocked every build** (`Barcodes.cs`, commit `80a34291`): an implicitly-typed local whose type could not be inferred is now spelled out as `int`. Why: nothing ships while the solution does not compile. (This line was missing from the changelog; added 2026-10-20 by FIX-5 — the changelog names every meaningful change, FEATURE-1(f).)

## 2026-10-20 (FIX-2, Muse fix agent) — money & data-integrity fixes from the production audit
- **Fixed: a backup with edited bill totals would have restored as if it were genuine.** The restore checks reconciled stock and dues against their ledgers but never recomputed a single bill — a hand-edited backup became the books of record and every GST report after it trusted the fake numbers. Restore now recomputes every bill with the same calculator the counter uses, compares every total and tax split, rejects bill numbers that collide with the numbering counter, and rejects ledger rows pointing at items or customers that don't exist. Why: a backup is disaster recovery — whatever it accepts becomes the truth (RESTORE-2).
- **Fixed: restoring a backup could freeze billing and reopen spent free quota.** Restoring yesterday's backup after today's sales rewound the bill-number counter, so the next bill tried to reuse an already-printed number and saving it failed — and the free-bill count rewound with it. Bill numbers and the quota count now only ever move forward across a restore: the books roll back, the numbering doesn't. The swap also verifies the restored file as it reopens, falling back to the pre-restore data if the copy was damaged. Why: a duplicate bill number means two different sales claiming to be the same bill (RESTORE-1, F6).
- **Fixed: one accidental clock jump could have paywalled a shop for a year.** If the phone clock was ever set a year ahead and one bill sold, the free-bill counter treated that future month as "current" until real time caught up — 50 free bills total for a year, then a hard stop. A last-seen time more than 48 hours in the future is now ignored and repaired. Rollbacks within 48 hours still can't reopen a spent month, and months with real bills are counted from the bills themselves. Why: a date accident must not become a year-long lockout (QUOTA-1).
- **Fixed: negative cash received showed a shortfall bigger than the bill.** Typing −₹500 against a ₹120 bill said "₹620 short". Negative cash is now rejected at validation. Why: numbers at the counter must always make sense (MONEY-3).
- **Fixed: a broken subscription service could have stopped every sale.** The guard inside billing only handled a slow Play service; one that throws would have failed every bill as "something went wrong" — the counter freeze one layer up. Any failure now falls back to the free tier, and both fallbacks write a warning to the log so support can see it happened. Why: billing must never depend on Google Play behaving (MONEY-2, LOG-3a).
- **Fixed: a bill line with an invented GST slab was taxed at an invented rate.** The finalize validator checked the tender was a real value but never the slab; a slab smuggled in through a restored draft (e.g. 9999) extracted ~50% tax, and a negative one made tax negative. The validator now rejects any undefined slab, exactly like item save does. Why: wrong tax on a receipt silently corrupts the shop's GST summary (MONEY-1).

## 2026-10-20 (later, Claude Code) — robustness at the counter
- **Fixed: a slow save could charge a bill twice.** The 45 s watchdog left the bill on screen while it was still committing; paying again made a second bill (second number, stock and dues taken twice). Finalize is now idempotent on the bill's id: a retry returns the bill that saved. Why: money must never be counted twice.
- **The unfinished bill survives the app being closed** (phone call, Android memory, crash): saved after every change and restored on reopen. Finalize clears it in the same transaction, so a paid bill never comes back. Why: a lost half-built bill means re-scanning everything with a customer waiting.
- **A damaged database repairs itself at startup.** Validated safety copies are kept automatically (one per day, refreshed whenever the app goes to the background, 3 days). A file that fails SQLite's checks is moved aside, never deleted, and the newest good copy is put back, with a plain message. Before, a damaged file stopped the app at launch. Why: power cuts and cheap storage are normal in small shops.
- **Clear messages for a full phone and a busy database** instead of "something went wrong". Why: the shopkeeper can fix a full phone; a generic error tells them nothing.
- **New logo and Play Store art:** a kirana awning over అ (first letter of అంగడి). It is the app icon (adaptive, safe under every launcher mask) and the splash, with a 512 px store icon, a 1024×500 feature graphic and suggested listing text in `store/`. A generator rebuilds them from the font outline (`tools/brand/`). Why: the old icon was a generic placeholder; the store listing needs both images.
- **Emulator test** now kills the app mid-bill and corrupts the data file, and checks both recover. Why: proven on the real app, not only in unit tests.

## 2026-10-20 (Claude Code) — the counter freeze, barcodes, reminders, release readiness
- **Fixed: the counter froze after the first payment.** The subscription status was fetched from Google Play inside the bill's database transaction. Without Play services (emulator, some phones) the transaction never ended, so every later action waited forever. Now status comes from the on-device cache; Play is checked in the background, every Play call has a time limit, and the database lock wait is bounded. Why: billing must never depend on the network.
- **Never-fail UI:** every button and event goes through one safety net (logged, a message on screen, double taps ignored). Tapping a payment button on an empty bill now says so. Why: a button must never look dead.
- **Barcodes:** UPC/EAN normalization and check digits, two-frame confirm, torch, attach an unknown code to an existing item, scan-to-find on Items. Why: wrong-item bills from misreads are unacceptable.
- **Reminders:** the WhatsApp/SMS message lists each unpaid udhaar with its date, to the customer's registered number. "Send to <customer> on WhatsApp" after a bill. A due banner on the bill screen. Why: owner requirement.
- **Emulator end-to-end test in CI** drives the real app through setup, cash, UPI, udhaar, barcode, reports and the language switch. Why: "works" is observed on the app, every push.
- **Release:** signed AAB workflow (`release.yml`), `docs/RELEASE.md`, a pre-update database copy before migrations, Release trimming roots, logcat error tag. Why: production readiness.
- **How-to-use presentation** (20 slides): who, where, when and every feature.

## 2026-10-19 (late night, Claude Code) — customer accounts
- **Customer accounts:** address, notes, every bill (any tender), their own report, udhaar/appu aging, promised-to-pay date, "Due for reminder" list, reminders by WhatsApp or SMS. Why: owner requirement. A customer-side app with push notifications needs a server (out of v1).

## 2026-10-19 (late night, Claude Code)
- **One app-wide language switch** (తెలుగు ⇄ English): asked on first launch, one tap in every tab's top bar; item names, reports, insights and voice choices follow it. Why: owner requirement.
- **Inventory report + ledger check, stock movement log.** Why: the owner must see stock value, low/out items and proof that every stock change was recorded.
- **Shop notes and in-app feedback** (Settings → Notes & feedback). Why: owner requirement.
- **Local build:** fonts committed, SDK pinned by global.json, `docs/BUILD_LOCALLY.md` (VS 2026, or VS 2022 + .NET 10 command line). `main` fast-forwarded to the green branch. Why: owner's VS 2022 pull failed on an old main / .NET 9.

## 2026-10-19 (night, Claude Code)
- **Camera barcode scanning** (on-device, no network), with the typed/USB-gun box as fallback. Why: locked scope; fastest way to bill packaged goods.
- **Google Play subscription client** replaces the never-Pro stub on devices. Why: ₹200/month must be payable; status is cached so billing never waits on the network.
- **LOW items + NFR-07/08 tests** (correlation id on every request line, phone numbers never logged). Why: support logs must be traceable and private.
- CI run 39352632912: core + Android green.

## 2026-10-19 (late, Claude Code)
- **Highs closed: H2/B2 quota clock defence, H3 safe restore, H9 UPI reference.** Why: free-tier abuse by changing the phone date, and a bad backup wiping a shop, were the two ways v1 could lose money or data.
- **Mediums closed: M2, M4, M6, M7, M8, M12, M14 + voice pass H5/H6/M1/M3/M13.** Why: learning that can't poison the catalog, no crash on typos or broken resources, sync-ready outbox.
- **Every rejection now has its own Telugu message, with the numbers in it** (cash shortfall, dues balance, free limit and price). Why: "something is wrong" at the counter means a phone call; "₹20 short" doesn't.
- **H8 Items-page warning, H11 log export through the dispatcher.** Why: legacy-slab items surface before billing fails; log export can't crash on a full disk.
- CI run 39350733740: core + Android green, 178/178 tests.

## 2026-10-19 (evening, Claude Code)
- **First green CI on .NET 10** (run 39345220912): core build with warnings as errors, all tests pass, coverage gate passes, Android Debug APK built. Why: definition of done is "CI observed, not assumed".
- **C1/C2/P1–P4/H1/H4/H7/H8/B1/M9/M10/V1 fixed** with regression tests. Status per fix ID: `docs/CLAUDE_STATUS.md`. Why: the Muse review queue, in severity order.
- **Culture-resource bug found by CI:** `*.te.json` resources were being compiled into a Telugu satellite assembly (91 failing tests). Why it matters: the app would have shipped with no voice rules or Telugu strings.
- **Golden-path UI** (POS, items, bills, customers, reports, settings, setup, UPI QR, receipt) built in C#. Why: the app needs to be usable on a tablet for UAT; C# keeps every value compile-checked.
- **CI error reporting** (`tools/ci_report.py`, `ci_tests.py`, `ci_wait.sh`). Why: raw Actions logs aren't reachable from the build sandbox, so compiler errors come back as annotations.

## 2026-10-19
- **Added `docs/AGENT_TEST_FIXES.md`** — 5-agent QA sweep (2 critical, 11 high, 15 medium). Why: adversarial testing found a broken build, unwired billing, and a void-flow that bricks customer accounts — none visible to the implementing agents.
- **Added `docs/CLAUDE_BRIEF.md`** — running brief telling Claude Code what's new and what to do. Why: owner ordered explicit instructions; two AIs with no shared memory need written handoffs.
- **Added `docs/AGENT_WORKFLOW.md` §9 ("Why" rule)** — every pushed change must document what, the issue, and why. Why: owner directive; a change whose why can't be written clearly doesn't get pushed.
- **Added `docs/AGENT_WORKFLOW.md`** — 10-role team model, repo-as-coordination-channel, review gates, approval matrix. Why: formalize the Muse+Claude operating model before the team doubles in size.
- **Added `docs/AUDIT_FIXES.md`** — independent audit: 4 medium fixes, 7 test gaps, 6 nits. Why: agents never compiled locally; outside review before more code stacked on unverified foundations.
- **Created Issue #1** — v1 build tracking hub. Why: one linkable place for scope, docs, batches, definition of done.
- **Day-1–5 build (Claude Code agents, 8 commits)** — SRS with owner decisions D1–D4; domain/app contracts; billing engine (paise math, GST 2.0, 2,000-bill fuzz); Telugu voice pipeline (parser/matcher, 30/30 battery); SQLite layer (UoW, WAL, backup/restore); handlers + Tier-1 insights + MAUI groundwork; GitHub Actions CI. Why: the v1 foundation per the 14-day plan.
