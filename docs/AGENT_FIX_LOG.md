# AGENT_FIX_LOG.md — the shared fix board for ALL agents (Muse's agents + Claude Code)

**Created:** 2026-10-20 · **Owner directive:** Sai — "they should let the Claude code know it by putting them in comments or update in a file… I can't rely only on Claude, I want perfection, industry enterprise readiness."

## Why this file exists (read this first, Claude Code)

Development no longer runs through one implementer. Muse's own dev/fix agents now push code fixes directly to this repo (owner rule change, 2026-10-20; see `docs/AGENT_WORKFLOW.md` §10). With multiple implementers, the repo is still the only coordination channel — so **every flaw, fix, and verification is recorded here and in code comments at the fix site**, where any agent (Claude Code included) sees it on sight. No fix exists only in a chat. No flaw is "known" until it's written here.

## The protocol (binding on every agent, including Claude Code agents)

1. **Flaw found → log it here first** (status `OPEN`), with file/line evidence, before fixing.
2. **Fix pushed →** the code commit AND a code comment at the fix site in this exact shape:

   ```
   // FIX(<ID>, <YYYY-MM-DD>, <agent>): Flaw: <what was wrong>.
   // Fix: <what changed>. Why: <reasoning / what breaks otherwise>.
   // Logged in docs/AGENT_FIX_LOG.md.
   ```

3. **Update the entry here in the same push:** status `FIXED-AWAITING-VERIFY`, commit SHA, tests added/changed.
4. **Independent verification** (Muse, main — never the fixing agent): file-read the fix, check tests, mark `VERIFIED` or bounce to `OPEN` with the reason. A fix is **not done** at `FIXED` — only at `VERIFIED` with CI green observed or explicitly marked `UNVERIFIED-CI`.
5. **Enterprise bar for every fix** (owner: "perfection, industry enterprise readiness"): builds with warnings-as-errors · tests added or updated and named in the entry · no layer violations · Telugu text only in JSON resources · `Result<T>` at boundaries · no PII in logs · CHANGELOG.md updated in the same push.

## Status values

`OPEN` → `IN-FIX` → `FIXED-AWAITING-VERIFY` → `VERIFIED` (or back to `OPEN` with reason) · `OWNER-DECISION` (needs Sai) · `WONTFIX` (with rationale, Sai-approved)

## HANDOFF TO CLAUDE CODE — 2026-10-20 19:14 EDT (READ THIS FIRST)

**Owner order (Sai, 2026-10-20 19:14 EDT): ALL Muse fix lanes are STOPPED.** FIX-1 (voice), FIX-2 (money/data), FIX-3 (UI/logging) were shut down mid-flight; FIX-4 and FIX-5 had already completed. No Muse agent will push code to this repo until the owner says otherwise. **Claude Code is the implementer again.** This section tells you exactly what Muse's agents changed, why, and what is left — so you don't have to re-derive it from 60+ commits.

**Final HEAD of the Muse push wave: `e4d7fea`.** Entry statuses below this section predate it; **where an entry below disagrees with this table, THIS TABLE WINS** (it was written last, against the final HEAD).

**Build state, measured by Muse main locally (.NET SDK 10.0.402, warnings-as-errors ON), 2026-10-20 ~19:20 EDT at `e4d7fea`:**
- `Angadi.Domain`, `Angadi.Application`, `Angadi.Infrastructure` — **all build 0 warnings / 0 errors.**
- `Angadi.Tests` — **DOES NOT COMPILE** → new entry **TEST-BUILD-1** at the end of this log (one missing `using`, one-line fix, blocks the whole suite). Fix that first, then run the suite: NO test results exist yet at this HEAD, and CI was never observable from Muse's side — everything below is `FIXED-AWAITING-VERIFY` / `UNVERIFIED-CI` unless an entry already says `VERIFIED`.

### What Muse's agents changed, and why (grouped by lane, newest last)

**FIX-1 — Voice** (owns Voice/*, TeluguParser, AndroidSpeechToText)
- VOICE-1 `90b05d9` — slash fractions parse: "1/2 కేజీ" now bills 500 g, not 1 kg. Why: half-kilo is a core counter phrase; it billed double.
- VOICE-2 `e657514` (+ build fix `08c455f`) — thousands-comma numbers ("2,500") parse. Why: Indian-locale STT emits comma-grouped digits; they were read as quantity 2.
- VOICE-3 + VOICE-4 `487bdcd` — వెయ్యి/లక్ష/కోటి are scale multipliers, not ranges/name words. Why: "ఐదు వెయ్యి రూపాయలు" proposed ₹5 instead of ₹5,000.
- VOICE-5 `243707f` (+ test build fix `112201b`) — descending bare-quantity pairs ask instead of silently summing. Why: "3 2 కేజీ" silently auto-added 5 kg.
- VOICE-6 `5def3ff` — a generic category word (నూనె) can never auto-add, at any catalog size. Why: single-oil catalogs silently auto-added on a generic word.
- VOICE-7 `7ef657a` — learned aliases are stamped USED when they decide an AutoAdd. Why: eviction was least-recently-learned, evicting daily-used aliases.
- VOICE-8 `a5a3cef` + config `e4d7fea` — battery/audit tests assert quantities and cannot pass vacuously; `appsettings` Voice section gains `MaxLearnedAliasesPerItem`. Why: quantity regressions passed the old battery silently.
- VOICE-9 `dde97c6` + `30462ed` — on-device speech recognizer first, testable fallback chain, and the "manual" fallback actually opens typed search. Why: on GMS-less budget tablets voice was permanently dead and the fallback was a dead-end toast.
- PRIVACY-1 (code half) `435e518` — learned alias text is never written to logs. Why: the privacy policy promised "never"; a Debug log line broke that promise.

**FIX-2 — Money / data** (owns BillingHandlers, QuotaStore, BackupService, AngadiDb)
- MONEY-1 `d82ce85` — finalize rejects an undefined GST slab. Why: a cast-in slab was taxed at a nonsense (even negative) rate instead of failing closed.
- MONEY-2 `ec83490` (+ LOG-3a) — a throwing/hanging subscription service falls back to Free tier with a warning log. Why: one bad service could fail EVERY sale.
- MONEY-3 `2fd6066` — negative cash received is rejected. Why: it produced a "shortfall" larger than the bill.
- RESTORE-1 (+F6) `ca82720` — a restore can no longer rewind bill numbers or the free-bill quota (live sequence + quota Meta snapshotted pre-swap, element-wise MAX written back; swapped file reopens under quick_check). Why: duplicate GST invoice numbers + free-tier reset in one tap — a launch blocker.
- RESTORE-2 `998fe46` — the restore sweep recomputes every bill (totals + tax splits), rejects rewound sequences and orphan stock/dues rows. Why: a doctored-but-consistent backup previously restored clean and poisoned every later GST report.
- QUOTA-1 `6b91600` — a far-future `last_seen` no longer poisons the quota for a year (48 h bound + repair-on-increment). **Tradeoff to weigh when verifying:** rollbacks longer than 48 h no longer pin by `last_seen` alone; the seed-from-bills count still protects months with real sales.
- M-3 `dd42ea1` — device-lifetime bill counter in Preferences: an out-of-band DB rewind can no longer reset the free-tier count. Why: owner-accepted reinstall mitigation, conservative (floor only for same-device restores).
- DATA-1a `fb58163` — unparseable `schema_version` recovers via DamagedDatabaseException instead of crash-looping startup; F6 swapped-file recovery compile error fixed.
- DATA-1b `e807bb2` — backup exports can no longer overwrite each other (seconds/GUID suffixes). Why: the pre-restore safety copy could BE overwritten by the very export it protected against.
- DATA-2 `c608368` — `PRAGMA synchronous=FULL`. Why: WAL at NORMAL could lose the last committed bills in a power cut.
- DATA-3 `34dd136` — a stale item save no longer silently reverts learned voice aliases (SaveAsync preserves LearnedAliasesJson, not just StockMilli). Why: catalog edits were erasing voice learning.
- SEC-6 (data part) `4afc04b` — backups carry provenance: created-at stamp + device-key HMAC + a metadata API for the pre-restore confirmation. Why: restore previously proved consistency, not genuineness or currency.
- SEC-1 `adb6c4b` — manual backup exports can be encrypted (AES-256-GCM envelope, owner passphrase). Why: a plaintext export is the whole shop book — every customer phone + dues — in one forwardable file.
- CA2016 `cc5a0fc` — build break introduced by the SEC-6 commit (cancellation token not forwarded in BackupService), found by Muse main's local build and fixed by forwarding `ct`.
- SEND-1 (handler half) + L-7 (flag) + TESTS-1 `c464ccc` — both WhatsApp handlers Normalize the phone and fail closed; `FinalizeBillCommand` carries `FreeBillConfirmed`; the ten T1 money test specs landed as tests.

**FIX-3 — UI / logging** (complete)
- UI-1 `e44f800` — all 8 secondary pages' OnAppearing loads run inside SafeUi.Run (+ grep-level architecture test). Why: unwrapped `async void` loads failed silently, leaving half-loaded pages.
- UI-2 `eb5288c`, UI-3 `7479322` — EditLine routes through SafeAsync; SearchAsync reports sender failures instead of faulting an unobserved task.
- LOG-1 `6139e82` — the 45 s finalize watchdog now logs (with bill id + correlation) when it fires. Why: a true counter hang previously left zero evidence in exported logs.
- LOG-2 `60035fc` — malformed appsettings.json degrades to defaults + one startup warning instead of crashing startup.
- LOG-3 `f8ea4f8` + `e917a31` — correlation id as a real log scope + bootstrap logger before Build(); log-export catch broadened; recovery skips recorded; localizer placeholder fallback; AppShell catches now toast. (Item (a) landed with MONEY-2; item (f) is in AngadiDb — see "still open" below.)
- SEC-4 `9f6f6d6` (service) + `14b516c` (UI) — owner PIN: salted PBKDF2-HMAC-SHA256 (100k iters) in Meta only, 5 wrong tries → persisted 60 s lockout; gates void, dues payment, write-off, backup export/restore, UPI-ID/GSTIN edits. Billing ungated; no PIN set → actions proceed + Settings nudge; pharmacy first-run gets a skippable PIN prompt. (Owner-approved design, 2026-10-20 13:45 EDT.)
- L-7 (UI) `8f78f82` — a ₹0 bill needs an explicit "give it free?" confirmation before finalize; cancelling costs no quota/stock/number.
- SEC-5 (UI) `4526697` — dues write-off action on the customer account: PIN gate → amount → mandatory reason → report shows write-offs as their own line.
- SEC-6 (UI) `3e7e591` — restore confirmation shows the backup's bill/item/customer counts + newest bill date before anything is swapped. **Note:** this commit exists on main up to three times (`88851a6`, `f6ab886`, `3e7e591`, identical content — re-pushes after lost responses). Harmless; do not "clean up" history for it.

**FIX-4 — App security** (complete)
- SEC-3 `06aa205` — audit trail: ItemChanged carries Old/New PricePaise; ShopProfileChanged carries Old/New UpiId+Gstin (additive payloads, old events still deserialize). Why: UPI-swap and price-shrinkage fraud left no record. (In-app history viewer = v1.1.)
- M-11 `0194c15` — Outbox rows pruned at 90 days at startup. Why: the outbox grew forever.
- SEC-5 `0711c94` — `DuesEntryKind.WriteOff`: bad debt is forgiven as itself, never as a fake payment; reason mandatory; reports count write-offs separately.
- DUES-1 `c85a563` — DuesAging reversals are amount-aware: a reversal cancels min(reversal, charge remaining); excess joins the payment pool. Why: one reversal previously cancelled the whole charge regardless of amount.
- T3-04/T3-05 `83f4761` — outbox same-transaction tests, proven by counting rows.
- SEND-1 (share half) `b4e0a89` — ReceiptShare normalizes the number or fails closed (choke point both senders pass through).

**FIX-5 — Release / docs** (complete)
- LOCAL-1 `4199950` (VERIFIED) — warnings are errors by default on every machine, not just CI.
- RELEASE-2 `548ad96` (VERIFIED) — rollback doc rewritten: Play cannot downgrade; roll forward with a higher versionCode.
- RELEASE-3 (text) `e28e79c` — full Play listing EN + TE + screenshot checklist in store/README.md. **Screenshots themselves still outstanding (owner/launch gate).**
- RELEASE-4 (part) `89d48b4` — version.json is the single version source. **WARNING:** release.yml does NOT read it yet (see owner-blocked items) — the two halves must land together before any tag.
- RELEASE-1 (part) `1e4d492` — PublishTrimmed pinned for Release builds (AOT deliberately off, documented in the csproj comment).
- PRIVACY-1 (policy half) `6f39bdc` (VERIFIED) — privacy policy rewritten against the code; **placeholders for developer name / support email / support WhatsApp still await the owner (hard Play gate)**.
- Owner decisions record `ef13991` — all five 2026-10-20 decisions (PIN gate, plaintext DB accepted for v1, API 28 floor, ₹0-bill confirmation, quota-reinstall acceptance) in docs/RELEASE.md.
- FEATURE-1(f) `4f3197f` — CHANGELOG names the CS0819 fix.

### Still open — for Claude Code, in priority order
1. **TEST-BUILD-1** (end of this log) — test project does not compile; one-line fix; blocks the entire suite.
2. **Verify the FIXED-AWAITING-VERIFY backlog** — run the full suite once TEST-BUILD-1 is fixed; treat QUOTA-1's 48 h tradeoff and the SEC-6 duplicate commits as called out above. Flip entries to VERIFIED only with test evidence.
3. **DATA-1 (MEDIUM)** — the ordered migration framework is still NOT built (only its two LOW companions, DATA-1a/1b, landed). Must land before ANY v1.1 schema change.
4. **LOG-3(f)** — `BackupBeforeMigration` swallowing non-missing-table SQLiteExceptions: believed addressed by `fb58163`'s AngadiDb work; confirm by reading the current code, then close.
5. **MINOR-1** — the dues-reminder handler already routes the promised date through `LocalDates.Short` (localized month names; commit `d0d703d` + handler read at HEAD): confirm with the te-IN test and close.

### Owner-side only (NOT Claude Code tasks)
- **RELEASE-1 ci.yml + RELEASE-4 release.yml halves are PREPARED and YAML-validated** at `~/workspace/angadi-review/fix5/.github/workflows/` (owner's machine copy) but could not be pushed: the GitHub App lacks the **Workflows permission** (403 on `.github/workflows/*`). Owner: grant it, or push those two files with a credentialed git client — **before the first tag** (version.json/release.yml are out of sync until then).
- Play gates: subscription products `angadi_pro_monthly` / `angadi_pro_yearly`, keystore + CI secrets, `Support:WhatsAppNumber`, privacy-policy placeholders + hosting, store screenshots, 12+ testers × 14 days closed test.
- **UAT-1** real-device list (entry below) — emulator green is necessary, not sufficient.

## Entries

### GOV-1 — Rule change: Muse's agents implement directly
- **Status:** VERIFIED (owner decision, 2026-10-20 13:20 EDT)
- **What/Issue:** Owner directed that Muse's dev/fix agents push code fixes directly instead of routing all implementation through Claude Code, to raise speed and remove the single-implementer dependency.
- **Why:** Parallel lanes (testing ×4, audit ×2, dev ×2, fix ×2) with file ownership beat a serialized queue; reviewer independence is preserved because Muse (main) verifies fixes it did not author.
- **Effect on Claude Code:** You are now one implementer among several. Check this log + fix-site comments before touching a file — another agent may already own the flaw. Claim an entry by setting it `IN-FIX` with your agent name in the same push that starts the work.

### MINOR-1 — Dues-reminder promised date renders in English inside Telugu WhatsApp messages
- **Status:** OPEN
- **Evidence:** `SendDuesReminderHandler` formats the promised-pay date with `InvariantCulture` (noted by repo watcher review, 2026-10-20 06:40 EDT) → "25 Oct" appears inside an otherwise-Telugu reminder message.
- **Fix:** format with `te-IN` culture (and the app's selected UI language for English mode). Add a test asserting the formatted month in a Telugu reminder string.
- **Why:** A reminder the customer can't smoothly read is a reminder that doesn't get paid.

### VERIFY-1 — Android Auto Backup now includes `angadi.db` + WAL + shared prefs (scoped rules)
- **Status:** VERIFIED (scoping) + WEAK (pairing) — T3 re-run, direct reads at cd2754b, 2026-10-20
- **Verified:** manifest `allowBackup=true` + `fullBackupContent`/`dataExtractionRules`; both rule files include exactly `file/angadi.db`, `file/angadi.db-wal`, `sharedpref/.` for cloud backup and device transfer; logs/snapshots excluded, as claimed.
- **Weak (new item DATA-5 below):** the db+wal pair is copied non-atomically by Android's transport and the app has NO checkpoint hook (no BackupAgent) — a torn pair can restore to an older/mixed state. The app's own snapshots ARE checkpointed but are excluded from Auto Backup. Direction: checkpoint on pause or include snapshots; device test specced as T3-10.

### VERIFY-2 — Chunked report loads (500) after SQLite parameter-limit fix
- **Status:** VERIFIED — T3 re-run, direct reads at cd2754b, 2026-10-20
- **Evidence:** `BillRepository.Load` chunks bill rows at 500, fetches ALL lines per chunk, groups by bill id — each bill sits in exactly one chunk, so lines can neither drop nor double-count. `ScaleTests.ManyBillsInOneRange_LoadWithAllTheirLines` (1,200 bills crossing both boundaries) asserts count, exactly-2-lines per bill, and Σ totals. Nit (no fix queued): line order inside a bill relies on insertion order — no ORDER BY in Load/GetAsync.

### VERIFY-3 — Data-layer invariant sweep (T3 lane, re-run pending)
- **Status:** DONE — T3 re-run completed 2026-10-20 at cd2754b. VERIFIED: WAL on, busy timeout, UoW commit-only-on-success (strong rollback test), relative stock UPDATEs (200-concurrent-adjust test, exact reconciliation), stock = Σ ledger, negative stock legal, dues balance = Σ ledger atomically, staged restore mechanics, quota mechanics within one DB, outbox happy path, seed idempotency + opening stock via ledger, finalize path end-to-end atomicity. CONFIRMED RESTORE-1 and RESTORE-2 as written in this log. WEAK: FK pragma set but Rows.cs declares NO FK constraints anywhere; nested-command commit predicate structural gap (no current flow); restore sweep tamper test covers stock only (RESTORE-2); migration path untested against a genuinely older-shaped DB (spec T3-08, lands with DATA-1). New flaws from the sweep: DATA-2, DATA-3, DUES-1, DATA-5 below.

### MONEY-1 (MEDIUM) — Finalize validator never checks `GstSlab` is a defined value; a cast-in slab is taxed at a nonsense rate instead of failing closed
- **Status:** FIXED-AWAITING-VERIFY · **Fixed by:** FIX-2, 2026-10-20 (validator check + regression test `T1_04_Finalize_UndefinedGstSlab_FailsClosed`; commit SHA recorded by the verifier — the push carrying this note. CI not observable from the fix agent: UNVERIFIED-CI.)
- **Found by:** Testing agent T1 (money), 2026-10-20, code-read at HEAD 0b25f05
- **Evidence:** `FinalizeBillValidator` (BillingHandlers.cs) guards `bill.Tender` with `Enum.IsDefined` and rejects legacy slabs, but has **no equivalent check on `l.GstSlab`**. Entry path: `BillDraft.Restore` copies `l.GstSlab` verbatim from a draft snapshot (JSON), bypassing the item-save guard (`CatalogHandlers.cs:29`). `BillCalculator.SplitFor` trusts the raw value: `(GstSlab)9999` extracts tax at ~50% of net; `(GstSlab)(-500)` yields **negative tax**. Violates the H8 fail-closed principle.
- **Fix:** in `FinalizeBillValidator.Validate`, before the legacy check: `if (bill.Lines.Any(l => !Enum.IsDefined(l.GstSlab))) return new Error(ErrorCodes.ValidationFailed, "unknown GST slab");` — mirror of CatalogHandlers.cs:29. Regression test: T1-04 (below).
- **Why:** Wrong tax on a receipt corrupts the shop's GST summary silently. Reachability today is low (tampered/corrupt draft or DB), impact is wrong tax — MEDIUM.

### MONEY-2 (LOW) — `SubscriptionGuard` only catches `TimeoutException`; a throwing subscription service fails every finalize
- **Status:** FIXED-AWAITING-VERIFY · **Fixed by:** FIX-2, 2026-10-20 (guard catches any non-cancellation exception → warning log + Free; warning log also added to the timeout branch = LOG-3 item (a); tests `Subscription_Hanging_TimesOutToFreeTier_AndTheSaleStillWorks` + `Subscription_Throwing_FallsBackToFreeTier_AndTheSaleStillWorks` in CounterResilienceTests. UNVERIFIED-CI.)
- **Found by:** T1, 2026-10-20
- **Evidence:** BillingHandlers.cs `SubscriptionGuard.StatusAsync` catches only `TimeoutException` → returns Free. Any other exception from an `ISubscriptionService` implementation propagates → Sender converts to `UNEXPECTED` → **every sale fails**. (Current implementations are exception-free by contract; the guard itself doesn't enforce it — the counter-freeze failure class, one layer up.)
- **Fix:** `catch (Exception ex when (ex is not OperationCanceledException))` → log + return Free.

### MONEY-3 (LOW, cosmetic, fail-closed) — Negative `CashReceived` produces a shortfall message larger than the bill
- **Status:** FIXED-AWAITING-VERIFY · **Fixed by:** FIX-2, 2026-10-20 (validator rejects negative cash; test `MONEY3_NegativeCashReceived_IsRejected`. UNVERIFIED-CI.)
- **Found by:** T1, 2026-10-20
- **Evidence:** Nothing validates `CashReceived >= 0`; −₹500 on a ₹120 bill reports "short ₹620". No money moves (fails closed).
- **Fix:** validator: `if (bill.CashReceived.Value < 0) return new Error(ErrorCodes.ValidationFailed, "negative cash received");`

### TESTS-1 — Ten missing/weak money test specs (paste-ready specs in the T1 report, held by Muse)
- **Status:** OPEN · **Found by:** T1, 2026-10-20
- **Specs:** T1-01 finalize overflow → `BILL_AMOUNT_TOO_LARGE` has **zero coverage** (code exists at BillingHandlers.cs:115, no test anywhere) · T1-02 line-level discount >100% at finalize · T1-03 legacy **28%** slab at finalize (only 12% tested) · T1-04 undefined-slab regression (pairs with MONEY-1) · T1-05 failed finalize consumes no quota / burns no bill number (3 failure modes) · T1-06 quota boundary: two concurrent finalizes at 49 → exactly one succeeds · T1-07 line-gross exact-half-paisa rounding · T1-08 exact-max line qty boundary accepted · T1-09 negative dues payment rejected · **T1-10 GST summary + day-end report totals over persisted bills — NO test exists for `GetGstSummaryQuery`/`GetDayEndReportQuery` aggregations incl. void exclusion, the very numbers a shop files GST from.**
- **Verified strong (no action):** GST-inclusive math, 40% slab, discount order + largest-remainder apportionment, change-due cash-only, cash-short args, 50/mo quota + H2 clock defence, B2 in-transaction increment, finalize idempotency (strongest tests in the suite), H1 void fail-closed, 2,000-bill invariant fuzz.

### LOCAL-1 (LOW) — `TreatWarningsAsErrors=false` in local builds; only CI enforces it
- **Status:** VERIFIED · **Fixed by:** FIX-5 (commit 4199950c), verified by Muse main local read: Directory.Build.props line 11 now `true`. · **Found by:** Architecture/flow audit, 2026-10-20 (file read at 94a9684f)
- **Evidence:** `Directory.Build.props` sets `TreatWarningsAsErrors=false` with the comment "Flip to true after the first green build". The build has been green since 2026-10-19 (~22:45 EDT). CI overrides with `-p:TreatWarningsAsErrors=true` (ci.yml), so the gate is real in CI but a developer in Visual Studio (the owner's maintenance path) builds without it and can merge warnings locally unnoticed.
- **Fix:** flip the props default to `true` now that the flip condition (first green build) is met. Why: the enterprise bar (§10) says warnings-as-errors — it should hold on every machine, not only in CI.

### FACT-1 — Tier-2 LLM layer does not exist in code (by design, recorded so nobody claims it does)
- **Status:** WONTFIX for v1 (SRS owner decision D2 moved Tier 2 to v2)
- **Evidence:** Architecture audit searched the full 194-file tree: no `Angadi.AI` project, no `IAiService` anywhere. Blueprint §11 is a design, not shipped code. Tier-1 on-device insights (reorder forecast, dead-stock, morning summary) ARE shipped (`Application/Insights/InsightsService.cs`).
- **Why recorded:** Any marketing or status claim of "AI features" for v1 must mean Tier-1 deterministic insights only.

### UI-1 (MEDIUM) — 8 secondary pages have unwrapped `async void OnAppearing`; exceptions escape SafeUi (no toast, half-loaded page)
- **Status:** OPEN · **Found by:** Testing agent T4 (Android/E2E), 2026-10-20, code read at HEAD 0b25f05
- **Evidence:** ItemsPage (CatalogPages.cs:76), BillsPage + ReportsPage (LedgerPages.cs:70, 186), CustomersPage + CustomerDetailPage (CustomerPages.cs:70, 228), ShopSetupPage + SubscriptionPage (SetupAndSettingsPages.cs:92, 181), NotesPage (NotesPage.cs:59) — all derive directly from `ContentPage` (not `PageBase`) and await sender calls with no try/catch, e.g. ShopSetupPage: `_existing = await _sender.Send(new GetShopProfileQuery());`. A failure lands only on the global hooks — logged, but the shopkeeper sees no UNEXPECTED toast and the page can sit half-loaded. This is the one systematic exception to the "never-fail UI" claim (41 page-level handlers + 9/10 POS commands ARE wrapped).
- **Fix:** route these pages through `PageBase` (which wraps OnAppearingAsync) or wrap each body in the SafeUi pattern. Pick one approach, apply to all 8, add a grep-level architecture test asserting no `async void OnAppearing` exists outside the wrapped base.
- **Why:** "Never-fail UI" was the fix for the owner's "nothing is working" report — a hole in 8 of 14 pages is how that class of bug returns.

### UI-2 (LOW) — `PosViewModel.EditLine` is the only POS command not routed through SafeAsync
- **Status:** OPEN · **Found by:** T4, 2026-10-20
- **Evidence:** `[RelayCommand] private Task EditLine(LineRow row) => EditLineAsync(row.Id);` — all 9 sibling async commands use SafeAsync. A prompt/parse exception faults the command task instead of showing the friendly toast.
- **Fix:** route through SafeAsync like its siblings.

### UI-3 (LOW) — `PosViewModel.SearchAsync` fire-and-forget swallows sender failures silently
- **Status:** OPEN · **Found by:** T4, 2026-10-20
- **Evidence:** called fire-and-forget from `OnSearchTextChanged`; catches only `OperationCanceledException`. A sender failure faults an unobserved task (global hook logs it) — search just returns nothing, no UI signal.
- **Fix:** catch + log via GlobalExceptions.Report with a "search" tag (no toast — search failing silently-but-logged is acceptable UX; unobserved-task faults are not).

### UAT-1 — Real-device UAT list (NOT code-fixable; owner/UAT action before Day-2 exit can honestly pass)
- **Status:** OWNER-DECISION · **Compiled by:** T4, 2026-10-20
- **The gap:** the E2E suite (15 steps, latest published run at 94a9684 is **15/15 green** — the original 8/11 failures are CLOSED, traced to harness text/freeze handling + the phone-layout bug) runs on a phone emulator with `-noaudio` and pre-granted permissions. Never exercised by any automated check: (1) Telugu voice end-to-end incl. shop noise + no-Google-app devices, (2) real camera scanning (glossy/curved packets, permission denial), (3) tablet ≥900dp layout + rotation, (4) full Telugu-first first run, (5) real UPI payment + QR correctness, (6) receipt PDF visual shaping + WhatsApp share, (7) FilePicker staged restore + cross-device backup restore, (8) Play Billing purchase/restore + free-limit redirect, (9) ItemEdit/stock-adjust/void/record-payment UI flows, (10) Release-build E2E parity (matrix added in 0b25f05, no published results yet; HEAD 0b25f05 E2E is UNVERIFIED), (11) interruption realism (call during finalize, low storage, low-RAM kill).
- **Why:** The Day-2 exit check requires these on a real device; emulator green is necessary, not sufficient.

### VOICE-1 (HIGH) — Slash fractions are dead code: "1/2 కేజీ" bills 1 kg instead of 500 g
- **Status:** OPEN · **Found by:** Testing agent T2 (voice), 2026-10-20, code read at HEAD 0b25f05
- **Evidence:** `TeluguParser.ParsePhrase` explodes tokens BEFORE number parsing. `Explode`'s `DigitsThenWord` regex splits `"1/2"` into `"1"` + `"/2"`, so `TryNumber`'s fraction branch (`^([0-9]+)/([0-9]+)$`) can never fire. Result: "1/2 కేజీ బియ్యం" → Qty **1000 milli (1 kg)**, and `"/2"` falls through into the item name, polluting the match. No test anywhere uses a slash fraction.
- **Fix:** teach `Explode` to keep a `digits/digits` token whole (check Fraction shape before DigitsThenWord), so TryNumber's fraction branch becomes reachable. Regression test: SPEC-1 in the T2 report (`T2_Fraction_SlashQuantity_Parses` — expects Name బియ్యం, Qty 500 milli, Kilogram).
- **Why:** "Half a kilo" is one of the most common phrases at a kirana counter. Billing double quantity on it is a money error customers will catch before we do.

### VOICE-2 (MEDIUM) — Thousands-comma numbers ("2,500") are protected from splitting, then never parsed
- **Status:** OPEN · **Found by:** T2, 2026-10-20
- **Evidence:** `MarkPunctuationSeparators` deliberately keeps "2,500" whole, but the only numeric recognizer (`Decimal()` = `^[0-9]+(?:\.[0-9]+)?$`) rejects commas; `Explode` then splits it to `"2"` + `",500"` (the latter pollutes the name). "2,500 రూపాయల బియ్యం" → Qty 2 instead of a ₹2,500 amount. Indian-locale STT commonly emits comma-grouped digits.
- **Fix:** strip grouping commas inside numeric tokens before Decimal matching (or extend Decimal to `[0-9][0-9,]*(\.[0-9]+)?` and remove commas in TryNumber). Test: SPEC-2.

### VOICE-3 (MEDIUM) — "N + వెయ్యి" compounds misread as ranges; the Confirm candidate is wrong by 1000×
- **Status:** OPEN · **Found by:** T2, 2026-10-20
- **Evidence:** `వెయ్యి` (1,000,000 milli) is in numbers.te.json but NOT in `hundredMultipliers` (only వంద/వందల/నూరు). After "రెండు" (lastPlace 2000), the range rule `value >= lastPlace → ambiguous, continue` drops వెయ్యి as a "range". "ఐదు వెయ్యి రూపాయల బియ్యం" → Amount candidate **500 paise (₹5) instead of ₹5,000**, flagged ambiguous. Fails safe (asks, never auto-adds) — but asks about the wrong number. Standalone వెయ్యి works (V1 test green).
- **Fix:** give వెయ్యి multiplier treatment like వంద (previous number × 1000, descending-compound rule like H6's 20+5 / 200+50 handling). Tests: SPEC-3.

### VOICE-4 (LOW) — No lakh/crore number words
- **Status:** OPEN · **Found by:** T2, 2026-10-20
- **Evidence:** numbers.te.json tops out at వెయ్యి. "ఒక లక్ష రూపాయల …" → లక్ష lands in the item name → NoMatch. Rare at a kirana counter, real for bulk/pharmacy orders. NOTE: merely adding లక్ష to the map hits the VOICE-3 trap (value ≥ lastPlace → "range") — it needs multiplier treatment.
- **Fix:** add లక్ష/లక్షలు/కోటి as multipliers alongside the VOICE-3 fix. Test: SPEC-4.

### VOICE-5 (LOW) — Descending "ranges" add silently (asymmetric ambiguity rule)
- **Status:** OPEN · **Found by:** T2, 2026-10-20
- **Evidence:** the ambiguity rule fires only when the second number ≥ previous place. "మూడు రెండు కేజీల బియ్యం" (3–2 descending) parses to 3000+2000 = **5000 milli, IsAmbiguous=false** → can auto-add 5 kg. Telugu ranges are normally ascending (covered), so impact is low.
- **Fix:** two adjacent bare quantities with no unit/connector between them → IsAmbiguous regardless of direction. Test: SPEC-10.

### VOICE-6 (MEDIUM) — "Generic word asks" is an emergent seed-catalog tie, not a rule — a single-oil catalog silently auto-adds
- **Status:** OPEN · **Found by:** T2 (WEAK-3), 2026-10-20
- **Evidence:** No generic-alias flag exists in ItemMatcher/Item. "నూనె" asks only because several seed oils tie at 1.0 (gap < 0.05 → Confirm). If a shop stocks/deletes down to ONE oil, its curated alias "నూనె" scores exactly 1.0 → **AutoAdd, silently** — the H5 failure mode returning through catalog editing. No test exercises a single-oil catalog.
- **Fix:** a seed-level `GenericAliases` set scored with the same 0.89 cap as unconfirmed learned aliases (ask-only, always). Test: SPEC-8 (`T2_GenericAlias_SingleOilCatalog_StillAsks` — fails today).

### VOICE-7 (LOW) — Learned-alias "LRU" evicts by least-recently-LEARNED, not least-recently-USED
- **Status:** OPEN · **Found by:** T2 (WEAK-4), 2026-10-20
- **Evidence:** `AliasLearner` sets `LastUsedMs` only in `LearnAsync`; the matcher never updates it on a successful match. Eviction (`MinBy(LastUsedMs)`) can evict a daily-used alias learned 31 aliases ago for a fresh one never used since. Existing LRU test advances the clock only via Learn calls, so it cannot catch this.
- **Fix:** touch LastUsedMs on auto-add/confirm via that alias (matcher returns which alias scored; pipeline persists), or rename the mechanism honestly and evict by confirmation count then age. Test must advance usage without Learn calls.

### VOICE-8 — Test-integrity gaps in the voice lane (battery asserts no quantities; one vacuous test; STT chain untested)
- **Status:** OPEN · **Found by:** T2, 2026-10-20
- **Evidence:** (a) The 40-phrase battery (gate is REAL: ≥36 correct / ≥35 auto-add, asserted) never asserts Qty/Unit/Amount for any phrase — a quantity regression inside a battery phrase passes if the item is right. (b) `Audit5_QuantityOnlyUtterance_NeverMatchesAnItem` loops over an EMPTY parse result — its assertion never executes (vacuous; the underlying code rule is verified, the test is not). (c) The entire STT fallback chain (`FallbackSpeechToText`) and `AndroidSpeechToText.MapError` have ZERO tests; the fallback class takes the concrete `AndroidSpeechToText`, making it untestable as written. (d) `appsettings.json` Voice section omits `MaxLearnedAliasesPerItem` (code default 30 governs — behaviour correct, H7 config claim incomplete).
- **Fix:** SPEC-5 (non-vacuous quantity-only tests), SPEC-6/7 (fallback + MapError tests; prerequisite refactor: FallbackSpeechToText accepts `ISpeechToText`), SPEC-9 (battery rows gain qty/unit/amount assertions + a qty-correct ≥36 gate), SPEC-11 (bare-dosage "650 పారాసెటమాల్" → Confirm with the right candidate). Add the missing appsettings key.
- **Verified strong (no action):** H4 decimal splitting, leading bulk quantities, V1 standalone వెయ్యి, H6 ascending ranges + dative currency, M1 dosage rotation, M2 graded learned aliases, M13 fused tokens/stutter/ము→ం, Telugu-script digits, transliterated English, multi-phrase splitting, voice→bill conversion defaults (M3).

### LOG-1 (MEDIUM) — The 45 s finalize watchdog logs NOTHING when it fires; a true hang leaves no trace
- **Status:** OPEN · **Found by:** Audit agent A1 (logging/exceptions), 2026-10-20, code read at 94a9684f
- **Evidence:** `PosViewModel.cs:402-408` — the watchdog's `catch (TimeoutException)` shows the BILL_SAVE_SLOW toast but the VM has no `ILogger` and never calls `GlobalExceptions.Report`. The request pipeline logs only on completion — so the exact counter-freeze scenario this whole design was built against would leave **zero evidence in the exported logs** support would ask the shopkeeper to send.
- **Fix:** in the watchdog catch: `GlobalExceptions.Report(new TimeoutException("Finalize exceeded 45 s"), "finalize-watchdog");` before the toast.

### LOG-2 (LOW-MED) — Malformed `appsettings.json` crashes startup hard, with no file log
- **Status:** OPEN · **Found by:** A1, 2026-10-20
- **Evidence:** `MauiProgram.LoadConfiguration` catches only `FileNotFoundException or IOException` (silently). Malformed JSON throws `FormatException` from the config provider — uncaught → startup crash; and because logging isn't wired yet (compounds LOG-4), nothing reaches the file log.
- **Fix:** widen the filter to include `FormatException`, keep fall-back-to-defaults, and record a startup warning once logging exists (static flag checked post-Build).

### LOG-3 (LOW) — Log/export/recovery polish batch (9 small items, one push)
- **Status:** OPEN · **Found by:** A1, 2026-10-20
- **Items:** (a) ✅ DONE by FIX-2, 2026-10-20, with MONEY-2 (warning log on the timeout branch + hanging-service test; both fallback branches now log). (b) `GlobalExceptions.Report` puts the correlation id in the message text, not a scope — the file template's `{CorrelationId}` renders BLANK on unhandled lines; wrap in BeginScope, and set a bootstrap logger before `builder.Build()`. (c) `LogExporter` catches only IO/Unauthorized — other exceptions (InvalidData, OOM) surface as UNEXPECTED instead of LOG_EXPORT_FAILED; broaden the catch + add the failure-path test (only success is tested today). (d) Settings export discards the share `Result` (SetupAndSettingsPages.cs:263) — a failed share gives the shopkeeper no feedback; toast it. (e) `DatabaseRecovery.Recover` (DataSafety.cs:39) skips failed safety-copy candidates with no log line — include skip reasons in the recovery report. (f) `AngadiDb.BackupBeforeMigration` swallows any SQLiteException silently — if the probe fails from corruption (not "no such table"), migration proceeds WITHOUT the `.pre-update` copy, unlogged; only swallow the missing-table result. (g) `JsonLocalizer.Error`/`Format` can show a raw `{0}` template on an arg-count FormatException — fall back to the placeholder-stripped template. (h) AppShell first-run/language-switch failures are logged but never toasted — the shopkeeper can land on an empty POS with no setup prompt and no message; call `SafeUi.ShowUnexpectedAsync()` in both catches. (i) Retention doc says "7 days / 5 MB" but Serilog's limit counts FILES (7 files ≈ 35 MB; a busy day shortens real coverage) — correct the comment or prune by age.
- **Also noted (test-hardening, no code flaw):** NFR-08 asserts the customer PHONE never reaches logs but never asserts the NAME; cancellation (OperationCanceledException) is logged nowhere end-to-end by design; the exported log zip has no redaction pass — PII hygiene is by write-side convention, verified clean today (no log call anywhere takes name/phone/address/utterance/token — A1 swept every Log call).
- **Verified strong (no action):** correlation scope opens BEFORE validation (rejections carry it), 50-code bilingual error catalog test-enforced both languages, storage failures classified (STORAGE_FULL/DATABASE_BUSY), bounded everything (2 s guard / 5 s busy / 30 s gate / 45 s watchdog), no raw exception text reaches any screen (grep: zero `ex.Message` in UI), purchase token never logged.

### SEC-1 (HIGH) — Manual backup export is a plaintext copy of the entire shop book
- **Status:** OPEN · **Found by:** Audit agent A2 (security), 2026-10-20, code read at HEAD 0b25f05
- **Evidence:** `BackupService.ExportAsync` copies the raw DB to `CacheDirectory/backups/angadi-backup-*.db` — no encryption, no passphrase. The file holds every customer name, phone, dues balance, and bill history. It leaves the device by user share — the realistic leak: the shopkeeper WhatsApps it to a personal number or a shared drive, and whoever holds that one file owns the dues book (customer phone+dues is personal data under DPDP Act 2023; pharmacy histories are especially sensitive).
- **Fix:** encrypt exports with an owner-set passphrase (AES-256-GCM, PBKDF2/Argon2 key derivation) before Play production — the v2 build plan's W4 already designs exactly this envelope for cloud backup; apply the same envelope to manual exports. Interim minimum: a Telugu warning dialog on export ("this file contains all customer dues — keep it private").
- **Why:** This is the single most likely real-world data leak in the product — not a hacker, a forwarded file.

### SEC-2 (HIGH, OWNER DECISION) — Database is plaintext at rest (no SQLCipher)
- **Status:** ACCEPTED-RISK — owner decision 2026-10-20 13:45 EDT: option (a) for v1; SQLCipher is a v2 hardening item. SEC-1 (encrypted exports) still ships — exports are what leave the device. Recorded in docs/RELEASE.md by FIX-5. · **Found by:** A2, 2026-10-20
- **Evidence:** `AngadiDb.Open` uses a plain `SQLiteConnection` (WAL); zero SQLCipher references repo-wide. DB + snapshots + `.pre-update`/`.pre-restore` copies all sit plaintext in app-private storage.
- **Decision for Sai:** (a) accept for v1 single-shop tablets behind the device lock screen — app-private storage is not readable by other apps on a non-rooted device; or (b) SQLCipher build with the key in Android Keystore + a plaintext→encrypted migration, recommended before pharmacy scale or any multi-staff rollout. Record the acceptance consciously; don't carry it silently.

### SEC-3 (MEDIUM-HIGH) — No audit record when the shop's UPI ID or an item's price changes — the two frauds that actually happen in shops
- **Status:** FIXED-AWAITING-VERIFY · **Fixed by:** FIX-4, commit 06aa205c (ItemChanged carries Old/New PricePaise; ShopProfileChanged carries Old/New UpiId+Gstin; additive payload fields, old payloads still deserialize; AuditTrailTests). Remaining sub-item: an in-app history VIEWER (the audit data now exists in the Outbox; surfacing it is v1.1 UI polish). M-11 (Outbox prune) fixed in the same lane, commit 0194c153: PruneOutboxCommand, 90-day retention, startup sweep + OutboxRetentionTests. · **Found by:** A2, 2026-10-20
- **Evidence:** `ShopProfileRow` and `ItemRow` carry only Created/Updated stamps; there is no change-history entity in Rows.cs. (a) **UPI swap fraud:** someone changes the shop UPI ID to their own; customer payments silently go to the wrong account and there is NO record of when or to what it changed. (b) **Price-shrinkage:** staff lowers a price, sells to an accomplice, restores it — no record the price ever changed (old bills are safe: lines snapshot price at sale).
- **Fix:** append an outbox event (or history rows) with old→new value + timestamp on every UPI-ID/GSTIN change and every item price edit; surface profile/price history in-app. Cheap to build on the existing Outbox.

### SEC-4 (MEDIUM-HIGH, OWNER DECISION) — No app lock: anyone holding the tablet can void, fake a dues payment, or export the database
- **Status:** IN-FIX (FIX-3 wave 2) — owner approved 2026-10-20 13:45 EDT: PIN gate on sensitive actions only (void, dues payment + write-off, export/restore, UPI/GSTIN edits), billing ungated; salted PBKDF2 hash in Meta; 5-attempt / 60 s lockout. · **Found by:** A2, 2026-10-20
- **Evidence:** zero PIN/biometric/auth code repo-wide; AppShell opens straight into the POS. Void, dues-payment recording, backup, restore, and log export are all one tap from the counter screen. Classic kirana insider-fraud setup — the threat is staff and bystanders, not remote attackers (permissions are minimal, no deep links/receivers/providers, no analytics — external surface verified small).
- **Decision for Sai:** ship an optional owner PIN (or biometric) gating ONLY sensitive actions — void, dues payment, restore, export, price/UPI edits — with counter billing PIN-free for speed. A2 recommends ON by default for pharmacy.

### SEC-5 (MEDIUM) — No dues write-off: bad debt can only be "forgiven" as a fake payment
- **Status:** FIXED-AWAITING-VERIFY · **Fixed by:** FIX-4, commit 0711c94b (DuesEntryKind.WriteOff=3, schema-additive int; RecordDuesWriteOffCommand — reason mandatory, cannot exceed balance; DuesWrittenOff outbox event; reports count write-offs separately via DuesWrittenOffSoFar; DuesWriteOffTests). UI action (ledger-page button, PIN-gated) → FIX-3 wave-2 item 11. · **Found by:** A2, 2026-10-20
- **Evidence:** `DuesEntryKind` = Charge/Payment/Reversal only. Forgiving bad debt requires a fake `RecordDuesPayment` (free-text Note is the only distinction) → cash/dues reports overstate real collections, and genuine payment fraud hides in the same bucket.
- **Fix:** add `DuesEntryKind.WriteOff` + a UI action with a required reason; exclude write-offs from "dues paid" totals; show them as their own report line.

### SEC-6 (MEDIUM) — Restore proves a backup is internally consistent, not that it's genuine or current
- **Status:** OPEN · **Found by:** A2, 2026-10-20
- **Evidence:** `RestoreAsync` validates marker + schema + integrity_check + ledger reconciliation, then swaps (safety export taken first — good). A doctored-but-reconciling backup is accepted; restoring an OLD backup silently rolls the books back.
- **Fix:** before swap, show backup date + bill/customer counts and require explicit confirm; write an HMAC/checksum record into Meta at export so tampered files are detectable. Full signing is v2.

### SEC-7 (LOW + hard gates) — Privacy-policy accuracy + owner Play gates
- **Status:** OWNER-DECISION (owner actions) · **Found by:** A2, 2026-10-20
- **Items:** (a) policy says voice "may" be processed by Google depending on phone — the implementation ALWAYS uses Google's online recognizer; tighten the wording and keep the Play Data Safety form consistent. (b) Policy's customer-data row omits Address/Notes/PromisedPayDate that CustomerRow stores; feedback-with-logs path unnamed. (c) Policy still has `[Developer name] · [support email]` placeholders and must be hosted at a public URL — a hard Play gate. (d) Keystore + 4 CI secrets + Play App Signing enrollment + the two subscription products + `Support:WhatsAppNumber` — owner actions (release.yml handles the keystore correctly: secrets → runner temp → deleted after build; nothing secret in the repo; appsettings holds no secrets).
- **Verified strong (no action):** append-only money ledgers (a void APPENDS a Reversal; originals never edited), reasoned voids kept forever, restore-time ledger reconciliation, Auto Backup scoping FIXED at this HEAD (only angadi.db + WAL + prefs; logs/snapshots/damaged copies excluded — verified in the rule XMLs), permissions minimal and justified (no SMS/storage/location/AD_ID), all data egress user-initiated and scoped, no customer-delete path (no ledger orphaning).

### RESTORE-1 (HIGH) — Restoring a backup rewinds the bill-number sequence AND the quota counters: duplicate GST bill numbers + free-tier reset in one tap
- **Status:** FIXED-AWAITING-VERIFY · **Fixed by:** FIX-2, 2026-10-20 (live NextBillSequence + all `quota.*` Meta snapshotted before the swap, element-wise MAX written back after; F6 folded in — ReplaceWithAsync now reopens verified with quick_check and falls into recovery on damage; regression test `Restore_NewerBillsExist_SequenceAndQuotaDoNotRewind` (T3-01). UNVERIFIED-CI.)
- **Found by:** Flaw hunter, 2026-10-20, code read at 94a9684f
- **Evidence:** Restore is a whole-file swap (`BackupService` → `db.ReplaceWithAsync(staged)`) with NO post-restore merge. `NextBillSequence` and the `quota.count.*` / `last_seen` Meta keys live in that same file. Restore Monday's backup on Friday and the app **re-issues Friday's already-printed bill numbers to different customers** (GST sequential-invoicing violation; the unique index can't catch it — the wiped bills are gone) and this month's free-bill count rewinds with it.
- **Fix:** before the swap, snapshot the live sequence + quota counters + `last_seen`; after the swap, write back the element-wise MAXIMUM of (restored, pre-restore). Regression test beside the H3 tests: sell → backup → sell 5 more → restore → numbering and quota continue from the live values, never backwards.
- **Why:** Duplicate invoice numbers are a compliance defect a GST officer can see on paper. This is a launch blocker.

### RELEASE-1 (HIGH, verify-at-HEAD first) — CI may never build the Release configuration; the shippable AAB path is exercised only on release day
- **Status:** PARTIAL — verdict CONFIRMED OPEN by FIX-5 (quoted ci.yml at HEAD: only "Build Android (Debug, unsigned)"; the 0b25f05 "[debug, release] matrix" claim is NOT borne out by the file). csproj half DONE+VERIFIED: PublishTrimmed=true pinned for Release (commit 1e4d492c; RunAOTCompilation deliberately left off, documented in the XML comment). ci.yml half PREPARED + YAML-validated (local: ~/workspace/angadi-review/fix5/.github/workflows/ci.yml; Muse main reviewed the diff — +24 lines, Release AAB step + artifact) but BLOCKED: the GitHub App lacks the Workflows permission (403 "Resource not accessible by integration" on .github/workflows/*, failed 3×) and Muse main's git push has no credentials for this remote. OWNER ACTION (before first tag, NOT on the Day-2 testing path): grant the Muse GitHub App the Workflows permission, or push those two prepared files via a credentialed git client. · **Found by:** Flaw hunter (at 94a9684f); confirmed by FIX-5, 2026-10-20
- **Evidence:** Everything Release-only in the csproj (`AndroidPackageFormat=aab`, the Release `TrimmerRootAssembly` group) has zero CI coverage on the audited HEAD. A linker/AAB break would surface for the first time when v1.0.0 is tagged.
- **Fix (if still open at HEAD):** add a Release AAB step/job to ci.yml; fail if no `*.aab` is produced; upload as artifact. Also pin the Release perf properties (L-item: no `PublishTrimmed`/`RunAOTCompilation` is set explicitly — SDK defaults decide what ships on low-RAM tablets).

### RELEASE-2 (HIGH) — The documented rollback cannot roll anything back
- **Status:** VERIFIED · **Fixed by:** FIX-5 (commit 548ad96a), verified by Muse main local read: the doc now reads halt rollout → hotfix → NEW AAB with HIGHER versionCode → test upgrade FROM the bad version; the downgrade fiction is removed. · **Found by:** Flaw hunter, 2026-10-20
- **Evidence:** `docs/deployment-and-rollback.md` says "re-publish the previous AAB. Play handles downgrade delivery." Play NEVER delivers a lower versionCode to a device on a higher one, and release.yml makes codes strictly increase. Following this doc during a data-corrupting incident does nothing.
- **Fix:** rewrite the doc: halt the rollout → hotfix → publish a NEW AAB with a HIGHER versionCode containing the good code → test upgrading FROM the bad version explicitly.

### RELEASE-3 (HIGH) — Store listing is not submittable: zero screenshots, no full description
- **Status:** PARTIAL · **Fixed by:** FIX-5 (commit e28e79c7), text verified by Muse main: store/README.md now carries the full Play listing (EN + TE full description from real features, category, data-safety notes) + a screenshot checklist naming the CI e2e source. STILL OUTSTANDING: the actual screenshots (phone set harvestable from ci-e2e artifacts; 7″/10″ tablet sets need a tablet-profile e2e pass) — a launch-gate asset task, not a code fix. · **Found by:** Flaw hunter, 2026-10-20
- **Evidence:** `store/` has the icon, feature graphic, launcher preview, and a README whose listing text stops at the short description. Play requires phone screenshots to publish at all, plus 7″/10″ tablet screenshots for the tablet listing — the exact devices this POS targets.
- **Fix:** capture phone + 7″ + 10″ screenshots (the CI e2e job already screenshots the app on an emulator — harvest from there); write the full description (EN + TE) into `store/README.md`.

### RELEASE-4 (MEDIUM) — Release plumbing: coverage gate dropped in release.yml; versionCode derived from run number
- **Status:** PARTIAL — version.json half DONE+VERIFIED (commit 89d48b42; file present at HEAD: {"versionCode":100,"versionName":"1.0.0"}; RELEASE.md documents the one manual sync point). release.yml half PREPARED + YAML-validated (local: ~/workspace/angadi-review/fix5/.github/workflows/release.yml; Muse main reviewed the diff — CI-green check-run gate, version.json read, NFR-10 coverage gate restored) but BLOCKED on the same Workflows-permission 403 as RELEASE-1. WARNING: until release.yml lands, it still derives code=100+run-number and does NOT read version.json — the two halves must land together before any tag. · **Found by:** Flaw hunter, 2026-10-20
- **Evidence:** release.yml runs only `dotnet test` — the NFR-10 coverage gate exists only in ci.yml, and nothing verifies the tagged commit was green in CI; with the Play secret set, a red/never-run commit still auto-uploads a signed bundle. versionCode = `100 + GITHUB_RUN_NUMBER` — run numbers reset if the workflow is recreated → code ≤ one already on Play → release-day rejection; it also contradicts the docs and the csproj's static version 1.
- **Fix:** add the coverage step + a CI-conclusion check for `github.sha` to release.yml; single version source of truth — a committed `version.json` the workflow reads and bumps; align both docs.

### QUOTA-1 (MEDIUM) — Quota `last_seen` has no sanity bound: one accidental far-future clock jump poisons the quota for the whole jump
- **Status:** FIXED-AWAITING-VERIFY · **Fixed by:** FIX-2, 2026-10-20 (48 h bound + repair-on-increment in QuotaStore; tests `QUOTA1_ClockJumpedAYearAhead_ThenCorrected_DoesNotPoisonTheQuota` + `T3_11_QuotaCountRowMissing_SeedsFromBills`; H2 rollback test adjusted to a sub-48 h cross-month rollback. **TRADEOFF for the verifier:** rollbacks longer than 48 h no longer pin by last_seen alone — the seed-from-bills count still protects months with real sales. UNVERIFIED-CI.)
- **Found by:** Flaw hunter, 2026-10-20
- **Evidence:** `QuotaStore` uses `Math.Max(nowMs, lastSeen)` uncapped, and Increment persists it. Tested case is a 2-month jump. The realistic accident — clock set +1 year, one bill sold, clock corrected — leaves the shop with **50 free bills total for a year**, then a hard paywall; any backup carries the poison to a new phone.
- **Fix:** ignore `lastSeen` beyond `now + 48h` for month selection; clamp `quota.last_seen_ms` to now on restore (ties into RESTORE-1); add the +1-year test.
- **Related (M-3, owner accept/document):** reinstall/clear-data fully resets the 50-free quota (counters live only in the DB). Cheap mitigation: a monotonic lifetime counter in Preferences (survives DB restore, not reinstall). At minimum record the limitation in BILLING_RULES.md as a chosen risk.

### RESTORE-2 (MEDIUM) — The restore invariant sweep never checks the money inside bills
- **Status:** FIXED-AWAITING-VERIFY · **Fixed by:** FIX-2, 2026-10-20 (sweep now recomputes every non-draft bill via BillCalculator and compares all frozen totals + tax splits, rejects bill numbers at/beyond NextBillSequence, and rejects orphan StockMove/DuesEntry rows (T3-03); tests `T3_02_RestoreBackupWithTamperedBillTotal_IsRejected`, `T3_02_RestoreBackupWithRewoundSequence_IsRejected`, `T3_03_RestoreBackupWithOrphanStockMove_IsRejected`. UNVERIFIED-CI.)
- **Found by:** Flaw hunter (extends VERIFY-1/2 and the T3 lane), 2026-10-20
- **Evidence:** `BackupService.PassesInvariants` checks integrity, stock-vs-ledger, dues-vs-ledger, negative dues, orphan lines — it never recomputes a bill, never parses `TaxSplitsJson`, never checks `NextBillSequence` against existing numbers. Reports then sum stored fields verbatim. A checksum-valid backup with inflated totals restores cleanly and every GST report afterwards trusts the fake numbers.
- **Fix:** during the sweep, recompute every finalized bill with `BillCalculator.Compute` and compare all totals + splits; add sequence-vs-numbers and orphan dues/stock checks. (Pairs with the VERIFY-2 chunk-boundary check.)

### DATA-1 (MEDIUM) — Migration strategy is additive-only; the first v1.1 restructure has no framework and will silently not happen
- **Status:** OPEN · **Found by:** Flaw hunter, 2026-10-20
- **Evidence:** `AngadiDb` Migrate = ten bare `CreateTable` calls ("additive migrations are automatic"), then `schema_version` is overwritten. Zero `ALTER TABLE` in src/. A column-type change or table split (e.g. a payments table for split tender) will be skipped or improvised against live shop data.
- **Fix:** ordered `Migrations[]` keyed by from-version, run in ONE transaction, version stamp bumped only after all steps succeed; backfill the existing v2/v3 changes as the first entries. Must land before ANY v1.1 schema change.
- **Adjacent LOWs (same lane):** (a) `int.Parse` (not TryParse) on `schema_version` can crash-loop startup on a non-numeric value — TryParse → throw DamagedDatabaseException so snapshot recovery handles it. (b) Two backup exports in the same minute overwrite each other (filename has minute precision, `overwrite: true`) — INCLUDING the pre-restore safety copy: export at 10:42, restore at 10:42, and the safety copy IS the overwritten file. Fix: seconds/GUID suffix; safety copies `overwrite: false`. — **BOTH FIXED-AWAITING-VERIFY by FIX-2, 2026-10-20** (tests `UnparseableSchemaVersion_RecoversInsteadOfCrashing` + `DATA1_TwoExportsInTheSameSecond_ProduceTwoFiles`. UNVERIFIED-CI. The MEDIUM framework above stays OPEN — FIX-2 wave-2 item 11.)

### VOICE-9 (MEDIUM) — Speech has no offline/on-device path, and the "manual" fallback never opens typed entry
- **Status:** OPEN · **Found by:** Flaw hunter (extends VOICE-8), 2026-10-20
- **Evidence:** `AndroidSpeechToText` gates on internet FIRST (`VoiceNoInternet` before availability is checked), sets no `EXTRA_PREFER_OFFLINE`, uses only `SpeechRecognizer.CreateSpeechRecognizer` (no `CreateOnDeviceSpeechRecognizer`, API 31+). And `PosViewModel.MicCore`'s "manual" branch just toasts and returns — the POS search is NOT focused, despite the fallback comment claiming "the POS opens typed search". On de-Googled / GMS-less budget tablets (a large slice of this market) voice billing — the headline feature — is permanently dead.
- **Fix:** try the on-device recognizer first; drop the internet pre-gate when it exists; set prefer-offline; make the manual branch actually focus the search field.

### PRIVACY-1 (MEDIUM) — Learned voice words ARE written to logs at Debug level; the policy's "never" is false and the NFR-08 test can't catch it
- **Status:** PARTIAL — policy half DONE+VERIFIED (FIX-5, commit 6f39bdca: rewritten against the code; OWNER-FILL comment at top; placeholders await Sai's name/email). Code half (delete the AliasLearner Debug line + NFR-08 LearnAsync coverage) is FIX-1's item 7 — status in FIX-1's report. · **Found by:** Flaw hunter, 2026-10-20 (the crack in A1's otherwise-clean sweep — A1 verified the pipeline/handlers; this line lives in AliasLearner)
- **Evidence:** `AliasLearner.cs:69-70`: `LogDebug("Learned alias text: {Alias}", alias)`. PRIVACY_POLICY.md and the feedback UI both promise learned voice words are NEVER written to logs. Release builds are saved only by the Information-level filter; any Debug/sideload build's exported log zip contains the shop's learned vocabulary. `LoggingTests.NFR08…` never calls `LearnAsync`, so the leaking line is never exercised.
- **Fix:** delete the Debug line entirely (log the count, never the text); add a LearnAsync call to the NFR-08 test.
- **Policy mismatches (L-9, same agent batch):** policy also (b) claims no background uploads while PlaySubscriptionService auto-starts a background Play re-check from inside billing; (c) never discloses the automatic on-device copies (3 snapshots, .pre-update, .pre-restore, damaged copies) that a "deleted" customer persists in; (d) omits Internet/Billing/Vibrate from its permissions list and the feedback path from data-leaving. Rewrite the policy against the code, then fill + host it (SEC-7).

### SEND-1 (MEDIUM) — WhatsApp senders trust the stored phone blindly at the send layer
- **Status:** PARTIAL · **Fixed by:** FIX-4, commit b4e0a89c — the SHARE layer (ReceiptShare choke point both handlers pass through) now normalizes and fails closed with CUSTOMER_NO_PHONE, so no send can reach an un-normalized number. Handler-side first line of defence (Normalize inside SendBillToCustomerHandler/SendDuesReminderHandler, BillingHandlers.cs) routed to FIX-2 as item 19 with FIX-4's paste-ready diff + test recipe. Note: the share-layer change has no unit test (Angadi.Tests doesn't reference the Maui project) — verified by build + read in the verification pass. · **Found by:** Flaw hunter, 2026-10-20
- **Evidence:** `SendBillToCustomerHandler` / `SendDuesReminderHandler` check only non-empty, then `ReceiptShare` builds `https://wa.me/` + phone unescaped/unvalidated (contrast SendFeedbackHandler, which does Normalize). Today the invariant holds only because save normalizes; a row arriving via an old-backup restore or future import can send a full bill (name, 15 lines, totals, TOTAL DUES BALANCE) to a malformed/wrong-country number.
- **Fix:** `PhoneNumbers.Normalize(customer.Phone) is not { } → CustomerNoPhone` inside both handlers, and defensively in ReceiptShare.

### FEATURE-1 — Owner-scheduled gaps (recorded, not launch blockers)
- **Status:** OWNER-DECISION · **Compiled by:** Flaw hunter + A2, 2026-10-20
- **Items:** (a) **Split tender** — part-cash/part-UPI can't be recorded truthfully (validator forbids cash on non-cash tender); everyday payment, shopkeepers will misrecord or split bills (2 quota slots, wrong invoice). Needs a `BillPayment` child table — v1.1, and it needs DATA-1's migration framework first. (b) **UpiRef optional at every layer** — keep it skippable for speed but make it explicit (≥4-char ref or a deliberate "No ref" tap; day-end shows a count of UPI bills without refs) for month-end reconciliation. (c) **₹0 bills finalize silently** — 100% discount / ₹0 item burns a bill number + quota slot and decrements stock with no warning; require an explicit free-bill confirmation flag. (d) **Feedback attach-logs does two separate shares** — the zip never travels inside the WhatsApp message; with the empty support number the shopkeeper believes logs reached support when nothing arrived (LOG lane + SEC-7 overlap). (e) **Min-SDK floor (API 28)** — excludes budget Android 8/Go tablets common in kirana; decide from real target devices, test API 26, else record the deliberate floor in RELEASE.md. (f) **CHANGELOG omits the CS0819 fix by name** — one line (commit 80a34291), against the repo's own What/Issue/Why rule.
- **Reconciliations (flaw hunt vs other agents — read before fixing):** H-A's Auto-Backup-rules portion is ALREADY FIXED at HEAD 0b25f05 (A2 verified backup_rules.xml + data_extraction_rules.xml scope to DB+WAL+prefs) — do NOT redo it; the open parts of H-A are SEC-1/SEC-2 (plaintext DB + exports). H-C = SEC-4 (PIN decision). H-D's new detail: manual exports land in CacheDirectory and exist only if the share sheet is completed — fold the overdue-backup nag into the v2 W4 work. M-11 (Outbox grows forever, sparse payloads) is the same root as SEC-3 — fix together: audit records with old→new + an Outbox prune beyond the backup window. M-14 = TESTS-1/T1-10 (report-handler tests) — one batch. M-15 (platform adapters untested) partially covered by VOICE-8 SPEC-6/7.

### DEPS-1 (HIGH, build-blocking) — SQLitePCLRaw.lib.e_sqlite3 2.1.10 has a known HIGH-severity vulnerability; NuGet audit fails the Infrastructure build
- **Status:** IN-FIX (FIX-2 item 18) · **Found by:** Muse main's LOCAL BUILD (SDK 10.0.402), 2026-10-20 — the first machine-witnessed build of the project
- **Evidence:** `dotnet build src/Angadi.Infrastructure` → `error NU1903: Warning As Error: Package 'SQLitePCLRaw.lib.e_sqlite3' 2.1.10 has a known high severity vulnerability, GHSA-2m69-gcr7-jv3q`. The Tests project hits the same NU1903 twice (its own restore + Infrastructure's) and is otherwise buildable on this machine — once DEPS-1 and the CA1307 line (below) are fixed, `dotnet test` runs locally. Angadi.Domain compiles 0 warnings / 0 errors. Angadi.Application's only errors: CA1307 ×2 on TeluguParser.cs:220 — FIX-1's VOICE-2 commit e657514 (`Contains`/`Replace` without StringComparison); routed back to FIX-1 with the exact analyzer text, 2026-10-20.
- **Fix:** bump the SQLitePCLRaw bundle/lib to the lowest patched version compatible with the sqlite-net-pcl version in use; rebuild Infrastructure + run data tests. If the bump forces a sqlite-net major upgrade, STOP — integrator decision.
- **Why:** a vulnerable native SQLite inside the app that holds the shop's books, and a red build on any machine with current audit data — including CI.

### DATA-2 (MEDIUM) — `PRAGMA synchronous` is never set: WAL at NORMAL can lose the last committed bills in a power cut
- **Status:** IN-FIX (FIX-2 item 14) · **Found by:** T3 re-run, full read of AngadiDb.Open at cd2754b, 2026-10-20
- **Evidence:** `AngadiDb.Open` sets journal_mode=WAL, foreign_keys=ON, BusyTimeout — and never touches `synchronous`. WAL default NORMAL is safe against app crash, NOT against OS crash/power loss — on the cheap-phone, frequent-power-cut profile this app is built for. The startup recovery net mitigates; it doesn't close the window.
- **Fix:** `PRAGMA synchronous=FULL` in Open. One line; negligible cost at billing workload.

### DATA-3 (MEDIUM) — Stale item saves silently clobber learned voice aliases
- **Status:** IN-FIX (FIX-2 item 15) · **Found by:** T3 re-run at cd2754b, 2026-10-20
- **Evidence:** `ItemRepository.SaveAsync` protects exactly one field from the stored row (`row.StockMilli = existing.StockMilli`) — no equivalent preservation for `LearnedAliasesJson`/`AliasesJson`. A flow that loads an Item, holds it, and saves after the AliasLearner wrote newer aliases reverts the learning. (CustomerRepository.SaveAsync has the same shape — preserves only DuesBalancePaise.)
- **Fix:** mirror the stock pattern — preserve the alias columns from `existing` unless the save IS the learner's own path. Test spec T3-09.

### DUES-1 (MEDIUM) — DuesAging reversal is amount-blind: a partial reversal cancels the whole charge
- **Status:** FIXED-AWAITING-VERIFY · **Fixed by:** FIX-4, commit c85a5639 (shared OpenCharges settlement core: reversal cancels min(amount, own charge remaining), excess joins the pool; DuesAgingTests incl. T3-06/T3-07 verbatim). T3-04/T3-05 outbox-atomicity evidence also landed (commit 83f47614, OutboxAtomicityTests). · **Found by:** T3 re-run at cd2754b, 2026-10-20
- **Evidence:** `DuesAging.UnpaidCharges`: a Reversal WITH a BillId removes that bill's charge in full, regardless of the reversal's Amount (`reversed` is a HashSet<Guid>; the amount is never read). Correct only because today's sole reversal flow is void (full GrandTotal).
- **Fix (semantics pinned by Muse, 2026-10-20):** a reversal cancels min(reversal amount, that charge's remaining); any excess joins the payment pool. Tests T3-06 (partial payment remainder) + T3-07 (reversal scoping + redistribution).

### DATA-5 (LOW) — Auto Backup restores DB+WAL as a non-atomic pair; no checkpoint hook
- **Status:** OPEN · **Found by:** T3 re-run (VERIFY-1 deep-dive) at cd2754b, 2026-10-20
- **Evidence:** backup_rules include db + wal (correct scoping), but Android's transport copies the two files at different moments and the app declares no BackupAgent/onBackup checkpoint hook. A torn pair can silently restore an older/mixed state on a new phone. The app's daily snapshots are checkpointed copies — but are excluded from Auto Backup.
- **Fix direction:** checkpoint the WAL on app pause, or include the snapshots folder in the rules; device behaviour pinned by spec T3-10. Pairs with SEC-1/H-D (backup trust) and v2 W4.

### VD-1 … VD-7 — Regional Telugu voice lane ("dialects", 2026-10-22) — read with docs/VOICE_PIPELINE.md "Regional Telugu"
- **Scope:** owner request "follow the regional Telugu language, different slangs… feed the system so it makes no mistakes". Data first (numbers/units/grammar JSON + ~200 seed aliases in `tools/generate_seed.py`); the five parser/matcher changes below are only where the parser structurally could not handle a pattern. Builds on VOICE-1..9 (none undone).
- **Gate:** `tests/Angadi.Tests/Voice/DialectBatteryTests.cs` — 212 phrases tagged Telangana / Hyderabadi / Coastal / Rayalaseema / Uttarandhra / Code-mix / Latin / Digits / Pharmacy; ≥ 95 % right item+quantity or correctly asking, ZERO wrong auto-adds (every phrase is its own test for the zero rule). Offline model of parser+matcher: 212/212, 0 wrong; existing 40-phrase battery unchanged (40 correct / 39 auto).
- **VD-1 (MEDIUM) — "కిలోన్నర / కేజీన్నర / లీటరున్నర" (1½ kg/L) fell into the item name and billed 1.** Status: FIXED-AWAITING-VERIFY. Fix: `TeluguParser.Explode` — a unit word + the half suffix explodes to "1.5" + unit. Tests: `VD1_UnitAndAHalf_IsOneAndAHalfOfThatUnit`.
- **VD-2 (HIGH) — "బియ్యం రెండు కేజీలు వెయ్యి" (వెయ్యి = 'put it') auto-added 2,000 kg.** The VOICE-3 scale rule multiplied across the unit. Status: FIXED-AWAITING-VERIFY. Fix: `ParsePhrase` scales only when the previous non-filler token was a number; otherwise the word is a second number → range → ask. Tests: `VD2_ThousandAfterAUnit_IsNotAMultiplier` (+ VOICE-3/4 compounds still pass).
- **VD-3 (MEDIUM) — Hyderabadi "పావ్ కిలో" (¼ kg) vs "పావ్" (pav bread).** Status: FIXED-AWAITING-VERIFY. Fix: new numbers.te.json `unitBoundNumbers` (`VoiceRules.UnitBoundNumbers`): a number only when a unit/multiplier follows. Tests: `VD3_Paav_IsAQuarterBeforeAUnit_AndBreadOtherwise`.
- **VD-4 (HIGH) — "బియ్యం వెయ్యి" auto-added 1,000 kg.** The M1 guard covered leading DIGITS only. Status: FIXED-AWAITING-VERIFY. Fix: any unit-less, non-amount count ≥ 100 is ambiguous → asks. Tests: `VD4_BareCountOfAHundredOrMore_Asks`, `VD4_UnitsAmountsAndSmallCounts_StayCertain`.
- **VD-5 (HIGH) — a strength spoken as a bare number became the quantity: "pan 40" proposed 40 strips; "650 పారాసెటమాల్" tied 500/650 mg and showed 500 mg first** (`T2_BareDosage_AsksWithParacetamolBest` was RED on the base branch, CI run 39645809679). Status: FIXED-AWAITING-VERIFY. Fix: `ItemMatcher.Match` also scores name+count; if that is an exact item name/alias, the number was the strength — the line carries no quantity and always asks. Composes with S1's VOICE-10 rotation (merged 2026-10-22): VOICE-10 puts the said strength on top; VD-5 additionally drops the bogus count and covers the trailing form ("pan 40"). Tests: `VD5_StrengthSpokenAsANumber_IsNotAQuantity`, `T2_BareDosage_AsksWithParacetamolBest`.
- **VD-6 (LOW, OPEN — finding) — Hindi "గుడ్" (gud = jaggery) keys to EGGS.** `TeluguText.MatchKey` strips the plural "లు", so "గుడ్లు" (eggs) has key "గుడ్" and the Telugu-script "గుడ్ ఒక కిలో" auto-matches eggs (only the kg-vs-piece unit check makes the bill ask). Latin "gud"/"gur" are safe aliases of jaggery. Suggested fix: when the query equals a key only through suffix stripping, cap it below auto (score the un-stripped surface too). Not in the battery for that reason.
- **VD-7 (LOW, OPEN — finding) — sack/ball words carry no weight.** "బియ్యం ఒక బస్తా", "బెల్లం ఒక ఉండ" are Piece quantities; on per-kg items the bill asks (UnitConversion exact=false), which is correct, but the ask shows "1" rather than a weight prompt. Suggested fix (UI): when needsConfirm is from a unit mismatch, open the quantity editor in the item's unit.
### A1/A2 platform batch (S2, 2026-10-21) — F2's audit items landed on the merged base (branch `claude/agent-s2-platform`)
- **Status:** FIXED-AWAITING-VERIFY (all items below) · **Fixed by:** S2 (F2's original work from `claude/agent-fix-platform`, merged onto the Muse wave and de-duplicated) · **Tests:** `tests/Angadi.Tests/Application/PlatformFixTests.cs` (named per item) + E2E `tools/e2e_steps_platform.py`, `tools/e2e_steps_zz_firstrun.py`.
- **A1-02 (HIGH)** restore swap: a reopen failure kept the DB gate → DATABASE_BUSY on every screen. Already covered by the base's F6 try/finally (`AngadiDb.ReplaceWithAsync`); kept ONE mechanism (F6's verified reopen + recovery). Test `A1_02_ReopenFailureAfterSwap_ReleasesTheDatabaseLock` (now accepts F6's recover-instead-of-throw).
- **A1-03 (MEDIUM)** fire-and-forget page loads (`_ = LoadAsync()` in ApplyQueryAttributes, tap-to-navigate commands, PosPage list selection) escaped SafeUi. Fix: `SafeUi.Run` / `SafeUi.Fire` / `SafeUi.On<T>` at each site. OnAppearing wrapping uses the base's UI-1 shape (`async void` + `await SafeUi.Run`) — F2's duplicate `_ = SafeUi.Run` variant was dropped. Search keeps the base's UI-3 catch (no toast while typing).
- **A1-04 (MEDIUM)** `PageBase.OnAppearing` only logged a failed view-model load; `PosViewModel` showed the one-time recovery report last (lost if a later query failed) and set `_draftChecked` before the restore ran. Fix: PageBase uses UI-1's `await SafeUi.Run`; the recovery notice is shown the moment it is taken; the draft is marked checked only after a successful restore.
- **A1-05 (HIGH)** database work ran inline on the UI thread (uncontended gate completes synchronously) → ANR risk. Fix: `AngadiDb` forces a yield to the pool before every query/transaction; `MauiProgram.ApplySavedLanguage` blocks via `Task.Run`. S2 also fixed F2's version, which still resumed on the UI context (the awaited helper lacked ConfigureAwait(false); this was the failing core test). Test `A1_05_DatabaseWork_RunsOffTheCallingThread`.
- **A1-07 (LOW)** `ViewModelBase.RunBusyAsync` set an ErrorMessage nothing binds. Fix: also toast it.
- **A1-09 (MEDIUM)** ignored Results: language-switch profile save, clear-promise, first-run seeding, backup share. Fix: toast the error (logs share was already fixed as LOG-3d).
- **A1-10 (MEDIUM)** a 16+ digit spoken token threw OverflowException out of voice billing. Fix: `TeluguParser.TryNumber` caps at `MaxSpokenNumber`; S2 added the same cap to the VOICE-3/4 scale-word multiply (`…కోటి` after a huge number). Tests `A1_10_*`.
- **A1-11 (LOW)** the counter called `IAliasLearner` directly (no correlation id, no transaction). Fix: `LearnAliasCommand` through the pipeline. Test `A1_11_LearnAliasCommand_LearnsThroughTheDispatcher`.
- **A1-12 (MEDIUM)** overlapping OnStart/OnSleep snapshots shared one `.tmp` path. Fix: one snapshot at a time (`DataSafety` gate). Test `A1_12_OverlappingSnapshots_AllSucceed`.
- **A1-13 (MEDIUM)** the restore staging copy (all customer data) stayed in the cache; a full disk showed "unexpected". Fix: always deleted in `finally`; STORAGE_FULL toast. Kept inside the base's SEC-4 PIN gate + SEC-6 inspector flow.
- **A1-14 (LOW)** a speech recognizer that failed to start kept its service binding. Fix: destroy it and report VOICE_NOTHING_HEARD.
- **A2-03 (HIGH)** no manual-backup reminder; Auto Backup silently stops past 25 MB. Fix: `BackupStatus`/`BackupStatusStore` (Meta rows), counter banner, Settings status line. Tests `A2_03_*`.
- **A2-04 (MEDIUM)** privacy policy: merged with PRIVACY-1's rewrite — one ML Kit line, one Auto Backup line (25 MB / Wi-Fi limit added), SMS reminders added.
- **A2-06 (HIGH)** UPI fraud: warning + payee on the UPI screen; leaving for the UPI app no longer cancels the payment (`OnNavigatedFrom` instead of `OnDisappearing`). E2E `upi-warning-and-background`.
- **A2-08 (MEDIUM)** E2E on API 36 (target SDK); scanner Back and first-run Back steps. S2: `scanner-back` now returns to the counter via "New bill" first (the receipt page from the previous step stays pushed when the Bill tab is tapped — a test-ordering issue, not an app bug).
- **A2-11 (HIGH)** offline Pro fell back to free after 7 days → a paying shop was blocked. Fix: `SubscriptionCachePolicy` — stays Pro, `NeedsReconfirm` asks to connect. Tests `A2_11_*`.
- **A2-12 (MEDIUM)** a pending Play payment read as "purchase failed". Fix: `PURCHASE_PENDING`. Test `A2_12_PendingPurchase_HasItsOwnMessage`.
- **A2-18 (MEDIUM)** permanently denied mic/camera: no way back. Fix: "Open settings" dialog.
- **A2-08b (HIGH)** Back from the camera scanner (Android 16 predictive back) logged an unhandled ObjectDisposedException from BarcodeScanning's CameraManager: OnDisappearing disabled the camera after the native pop disposed its controller. Fix: `ScanPage.OnDisappearing` only completes the result; `Finish` never touches a view without a handler. Evidence: E2E `app-log-clean` after `scanner-back` (run 39814559461).
- **VOICE-8b (LOW)** bare "650 పారాసెటమాల్": the dosage became the count, the name had no digits, and Paracetamol 500 mg won the tie. Fix: `ItemMatcher` also scores the name with the raw leading number appended when the parser flagged the line ambiguous. Test `VoiceBatteryTests.T2_BareDosage_AsksWithParacetamolBest` (was failing on the base).

<!-- New entries go ABOVE this line, newest first within their section order: GOV, CRITICAL, HIGH, MEDIUM, LOW, MINOR, VERIFY. Keep the Flaw → Fix → Why shape. -->

### TEST-BUILD-1 (HIGH, build-blocking for tests) — `Angadi.Tests` does not compile at final HEAD `e4d7fea`: a test file is missing one `using`
- **Status:** FIXED-AWAITING-VERIFY (Claude Code, 2026-10-21: `using Angadi.Application.Billing;` added after merging main into claude/new-session-g6f55q; CI result recorded below when green) · **Found by:** Muse main's LOCAL `dotnet test` run (SDK 10.0.402), 2026-10-20 ~19:20 EDT — the first full-suite attempt at the final HEAD.
- **Evidence:** `tests/Angadi.Tests/Infrastructure/BackupInspectorTests.cs(21,44): error CS0246: The type or namespace name 'FinalizeBillCommand' could not be found`. The file (added by FIX-3 for SEC-6 UI) has usings for `Angadi.Application.Platform`, `Angadi.Domain.Billing`, `Angadi.Tests.Application` — but `FinalizeBillCommand` lives in `Angadi.Application.Billing` (`src/Angadi.Application/Billing/BillingContracts.cs:15`), which is not imported. The production projects (Domain/Application/Infrastructure) all build 0 warnings / 0 errors at the same HEAD — only the test project fails, so the suite produces NO results until this is fixed.
- **Fix (one line):** add `using Angadi.Application.Billing;` to `BackupInspectorTests.cs`, then run the full suite and record the real pass/fail counts here.
- **Why:** every FIXED-AWAITING-VERIFY entry in this log is gated on this suite running; a test project that cannot compile silently nullifies the whole verification story. (Process note for all agents: a lane that cannot build locally must say so — "checked by reading" missed this; the local build rig caught it in 30 seconds.)

### ADDENDUM to the HANDOFF (2026-10-20 ~19:19 EDT) — from FIX-1's final report, received after the handoff push
- **FIX-1 confirms its full queue landed** — all 9 voice items + PRIVACY-1 code half, SHAs exactly as in the handoff table; nothing BLOCKED. Its own process note: between `243707f` and `112201bf` the TEST project briefly did not compile due to a VOICE-5 test bug (`Quantity.Value` vs `Qty.Milli`) — self-caught and fixed in `112201bf`; superseded by TEST-BUILD-1, which is the live one.
- ⚠️ **The `Angadi.Maui` project has NEVER been compiled by any agent** (Muse main's local builds covered Domain/Application/Infrastructure + the test project's references only; the Android TFM needs the MAUI workload). Highest-risk unbuilt code: **VOICE-9's Android bindings** — `SpeechRecognizer.IsOnDeviceRecognitionAvailable` / `CreateOnDeviceSpeechRecognizer` (API 31+) in `AndroidSpeechToText.cs` and the `PlatformRegistration` factory registration. Claude Code: make compiling `Angadi.Maui` (Debug, Android) an early step, before or alongside TEST-BUILD-1.
- **VOICE-9 design nuance for verification:** `SpeechRecognizer.IsRecognitionAvailable` still gates before on-device creation; a device with ONLY on-device recognition that fails that check reports `recognizer_unavailable`, which the fallback chain converts to "manual" (typed search). Acceptable v1 behaviour per FIX-1, but flagged for a conscious look during the verification pass.

## S1 (Claude Code fixer, money lane) — 2026-10-21, branch `claude/agent-s1-money`
- **F1 landed (A2-01/02/05/14/15/16, A1-01/06/08, T2-01..04)** — `origin/claude/agent-fix-gst` (f705e1d, de35352) merged onto the base. Its own A2-07/A2-13 restore carry-forward (`BackupService.ReadCounters/CarryForward` + their tests) was DROPPED: RESTORE-1 already does it (QuotaAndRestoreTests). Status: FIXED-AWAITING-VERIFY. Tests: GstReceiptIntegrityTests.
  - Test oracles corrected (the change was right, the old expectation was not): `DuesLedgerTests.RandomSequences_…` crosses months, so its voids now pass `ConfirmPastMonth: true` (A2-14). `ReportsTruthTests.GstSummary_MonthToDate_…` — since T2-01 a bill voided on a LATER day stays in its closed day-end, so Σ day-ends over 1–15 Oct exceeds the GST summary by exactly that bill (voids are not supplies); the identity now subtracts it. `MediumsTests.M7_AbsurdQuantity_…` and `BillingEdgeCaseTests` (tooMany) expect `BILL_QTY_TOO_LARGE` (A1-08's own message) instead of `BILL_QTY_INVALID`.
- **T1-03 / S1-01 (MEDIUM) — a Pro shop's first bill of each month counted twice by the quota.** `FinalizeBillHandler` short-circuited the quota read for Pro, so the month's counter was first seeded inside `IncrementAsync` AFTER the bill was saved and the seed already included it. Fix: usage is always read before the save. Status: FIXED-AWAITING-VERIFY. Tests: `BillingEdgeCaseTests.Pro_BillsAreCountedExactlyOnce` (unskipped), `GstReceiptIntegrityTests.A1_01_CommitFailsAfterTheHandler_TheSameDraftCanBeRetried` (was failing on F1's CI for this reason).
- **T1-02 (MEDIUM) — a BillDraft edit that overflows left the line mutated.** Fixed by F1's A1-08 (`SetQty` caps and restores the previous qty). Status: FIXED-AWAITING-VERIFY. Test: `BillDraftEdgeTests.Edit_ThatOverflows_LeavesTheDraftExactlyAsItWas` (unskipped).
- **T3-1 (MEDIUM) — a kept promise made the next fresh udhaar "due" at once.** `CustomerRepository.AddDuesEntryAsync` now clears `PromisedPayDay` in the same transaction whenever the balance reaches ≤ 0 (payment, void, write-off). Status: FIXED-AWAITING-VERIFY. Test: `DuesReminderTests.APromiseForUdhaarAlreadyPaidOff_DoesNotMakeNewUdhaarDueAtOnce` (unskipped).
- **T3-2 (MEDIUM) — phone search failed for "98765 43210", "+91 …", "0…", "98765-43210".** `CustomerRepository.SearchAsync`: phone-shaped text matches by its digits, plus an exact match on `PhoneNumbers.Normalize(text)` (leading 0). Status: FIXED-AWAITING-VERIFY. Test: `CustomerDirectoryTests.Search_ByPhone_AsTheShopkeeperTypesIt` (unskipped).
- **T3-3 (MEDIUM) — a WhatsApp bill of more than 15 lines was cut silently.** `SendBillToCustomerHandler` adds "… and N more items" (`receipt.customer.more`, te + en). Status: FIXED-AWAITING-VERIFY. Test: `UpiAndShareTests.LongBill_SaysThereAreMoreItems` (unskipped).
- **VOICE-10 (MEDIUM) — bare dosage "650 పారాసెటమాల్" offered Paracetamol 500 mg first.** Not a regression of VOICE-1..9 code: the SPEC-11 test added by VOICE-8 (a5a3cef) never passed. The parser's M1 rule makes a bare leading number the (ambiguous) quantity, so the name had no digits and the matcher's M1-CORRECTION rotation never ran; both paracetamols tied and the alphabetical tie-break won. Fix: `ItemMatcher.LeadingDosageAsName` also tries "<name> <leading number>" when the parser flagged a bare leading number. Status: FIXED-AWAITING-VERIFY. Test: `VoiceBatteryTests.T2_BareDosage_AsksWithParacetamolBest`.
### BUILD-2 (HIGH, build-blocking) — the Android app does not build with warnings-as-errors (LOCAL-1) after the Muse wave
- **Status:** FIXED-AWAITING-VERIFY · **Fixed by:** Claude Code, 2026-10-21
- **Evidence (CI run 39643810686, Android Debug build):** CA1416 `SpeechRecognizer.CreateOnDeviceSpeechRecognizer` (API 31) reachable on API 28 (`AndroidSpeechToText.cs`, VOICE-9); CA2016 ×3 cancellation token not forwarded to Plugin.InAppBilling `ConnectAsync`/`PurchaseAsync`/`GetPurchasesAsync` (`PlaySubscriptionService.cs`); CS0618 obsolete MAUI 10 APIs `DisplayAlert`, `DisplayActionSheet`, `MediaPicker.PickPhotoAsync` (`Services.cs`, `AppShell.cs`, `SetupAndSettingsPages.cs`). Also the test project failed: `FlakyOutbox` (T1 tests) lacked the new `IOutbox.PruneOlderThanAsync` (M-11).
- **Fix:** `OperatingSystem.IsAndroidVersionAtLeast(31)` guard before the on-device recognizer; `cancellationToken: ct` forwarded; `DisplayAlertAsync` / `DisplayActionSheetAsync` / `PickPhotosAsync(...).FirstOrDefault()`; `FlakyOutbox.PruneOlderThanAsync` delegates.
- **Why:** LOCAL-1 made warnings errors on every machine (the owner's Visual Studio included); the MAUI head had never been compiled by Muse's agents, so the app did not build anywhere. The CA1416 one is also a real crash on Android 9–11.

### BUILD-3 (CRITICAL) — the app cannot open its database on any phone: desktop SQLite shipped inside the APK
- **Status:** FIXED-AWAITING-VERIFY · **Fixed by:** Claude Code, 2026-10-22
- **Evidence:** emulator logcat (CI run for cca9748, debug and release): `dlopen failed: library "libc.so.6" not found: needed by .../lib/x86_64/libe_sqlite3.so` → `System.DllNotFoundException: e_sqlite3` in `AngadiDb.Open` at startup; every E2E step then saw an empty screen. Cause: DEPS-1 added `SQLitePCLRaw.lib.e_sqlite3` (the desktop native package) to Angadi.Infrastructure; it flowed into the Android app, where RID fallback packaged its linux-x64 (glibc) `.so` over the Android one.
- **Fix:** `PrivateAssets="all"` on that reference (Infrastructure's own restore stays patched; the tests reference it directly; the app pins `SQLitePCLRaw.lib.e_sqlite3.android` 2.1.12). `tools/play_policy_check.py` now fails CI if any `.so` in the APK needs `libc.so.6`/`ld-linux`.
- **Why:** this would have shipped as an app that opens to a blank screen for every customer. Lesson for all agents: the MAUI head and the emulator are the only proof that a dependency change works on a phone.

## S1 round 2 — 2026-10-22, branch `claude/agent-s1-money`
- **T1-01 (HIGH) — an idempotent retry of an EDITED bill returned the old saved bill as paid.** `FinalizeBillHandler`: when the bill id already saved, the retry must match it (tender, customer, bill discount, each line's item/qty/price/discount — `SameContent`); otherwise `BILL_CHANGED_AFTER_SAVE` ("Bill {0} was already saved for {1}. Put the items you added on a new bill", te + en). The POS then starts a fresh, EMPTY bill — re-adding the old lines under a new id would charge them twice. Status: FIXED-AWAITING-VERIFY. Test: `BillingEdgeCaseTests.Retry_OfABillEditedAfterItSaved_IsNotReportedAsPaidAtTheOldTotal` (unskipped, refusal branch asserted).
- **S1-F1 (MEDIUM) — a later-day void showed no refund on the void day.** `DayEndReport.Refunds` = totals of CASH/UPI bills sold on an earlier day and voided that day (`IBillRepository.SumRefundsAsync`; udhaar voids only reverse dues); shown on Reports (`reports.refunds`, te + en). Status: FIXED-AWAITING-VERIFY. Test: `ReportsTruthTests.DayEnd_VoidOnALaterDay_…` (Refunds asserted).
- **S1-F2 (LOW) — a failed commit consumed a free bill (M-3 lifetime file written outside the transaction).** `QuotaStore.IncrementAsync` no longer writes the `.lifetime` file; the committed watermark carries the bill, and every read/increment first catches the file up to the committed watermark (`SyncedLifetime`). M-3's floor is kept (`M3_*` tests unchanged). Status: FIXED-AWAITING-VERIFY. Test: `GstReceiptIntegrityTests.S1_F2_AFailedCommit_NeverCountsTowardTheFreeBills`.

### SCAN-1 (MEDIUM) — Back from the barcode scanner logged an unhandled ObjectDisposedException
- **Status:** FIXED-AWAITING-VERIFY · **Fixed by:** Claude Code, 2026-10-24 (supersedes the partial A2-08b fix)
- **Evidence:** E2E `app-log-clean` on claude/agent-s2-platform (debug + release): `Unhandled in Android ... ObjectDisposedException ... LifecycleCameraController` from `BarcodeScanning.CameraManager.<.ctor>b__23_0` → `CameraController.get_CameraInfo`.
- **Cause:** the library queues a camera-init callback; MAUI's automatic handler disconnect disposed the controller when the page closed, before the callback ran.
- **Fix:** `HandlerDisconnectPolicy.Manual` on the CameraView, released 3 s after the page disappears (camera stopped first); and `GlobalExceptions` recognises this exact third-party shape and logs it as a warning without the "unexpected" toast, in case a very slow device still hits it.
- **Why:** Back from the scanner is an everyday action; it must not produce a crash-class error.
