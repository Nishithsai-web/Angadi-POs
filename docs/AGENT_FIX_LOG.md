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
- **Status:** OPEN (claimed by Claude Code commit `0b25f05`, "Production audit round 1", 2026-10-20 — UNVERIFIED by independent read)
- **Question to verify in code:** when Android restores DB+WAL together, is the pair consistent (checkpoint state)? Does restore pair/mismatch handling exist, or can a stale WAL replay against a newer DB?
- **Why:** Backup that restores corrupt books is worse than no backup — the shop trusts it.

### VERIFY-2 — Chunked report loads (500) after SQLite parameter-limit fix
- **Status:** OPEN (claimed by commit `0b25f05` — UNVERIFIED)
- **Question to verify:** chunk boundaries in `BillRepository.Load` must not drop or duplicate bill lines; load must be all-or-nothing per bill. Check `ScaleTests` (1,200 bills/day) asserts *all lines present + totals equal*, not merely "no exception".

### VERIFY-3 — Data-layer invariant sweep (T3 lane, re-run pending)
- **Status:** OPEN
- **Scope:** AngadiDb (WAL, busy timeout, FKs), UnitOfWork commit-only-on-success, relative stock UPDATEs, stock = Σ ledger, DuesAging FIFO edge cases, staged restore (integrity_check + invariant sweep + atomic swap, tampered backup rejected), schema-version path for future non-additive migrations, quota Meta counters, outbox rows per event, seed idempotency.
- **Why:** This lane's first run was stopped by a GitHub API rate limit (429) before any file was read — nothing in this scope is verified yet, and unverified must never be reported as passed.

### MONEY-1 (MEDIUM) — Finalize validator never checks `GstSlab` is a defined value; a cast-in slab is taxed at a nonsense rate instead of failing closed
- **Status:** FIXED-AWAITING-VERIFY · **Fixed by:** FIX-2, 2026-10-20 (validator check + regression test `T1_04_Finalize_UndefinedGstSlab_FailsClosed`; commit SHA recorded by the verifier — the push carrying this note. CI not observable from the fix agent: UNVERIFIED-CI.)
- **Found by:** Testing agent T1 (money), 2026-10-20, code-read at HEAD 0b25f05
- **Evidence:** `FinalizeBillValidator` (BillingHandlers.cs) guards `bill.Tender` with `Enum.IsDefined` and rejects legacy slabs, but has **no equivalent check on `l.GstSlab`**. Entry path: `BillDraft.Restore` copies `l.GstSlab` verbatim from a draft snapshot (JSON), bypassing the item-save guard (`CatalogHandlers.cs:29`). `BillCalculator.SplitFor` trusts the raw value: `(GstSlab)9999` extracts tax at ~50% of net; `(GstSlab)(-500)` yields **negative tax**. Violates the H8 fail-closed principle.
- **Fix:** in `FinalizeBillValidator.Validate`, before the legacy check: `if (bill.Lines.Any(l => !Enum.IsDefined(l.GstSlab))) return new Error(ErrorCodes.ValidationFailed, "unknown GST slab");` — mirror of CatalogHandlers.cs:29. Regression test: T1-04 (below).
- **Why:** Wrong tax on a receipt corrupts the shop's GST summary silently. Reachability today is low (tampered/corrupt draft or DB), impact is wrong tax — MEDIUM.

### MONEY-2 (LOW) — `SubscriptionGuard` only catches `TimeoutException`; a throwing subscription service fails every finalize
- **Status:** OPEN · **Found by:** T1, 2026-10-20
- **Evidence:** BillingHandlers.cs `SubscriptionGuard.StatusAsync` catches only `TimeoutException` → returns Free. Any other exception from an `ISubscriptionService` implementation propagates → Sender converts to `UNEXPECTED` → **every sale fails**. (Current implementations are exception-free by contract; the guard itself doesn't enforce it — the counter-freeze failure class, one layer up.)
- **Fix:** `catch (Exception ex when (ex is not OperationCanceledException))` → log + return Free.

### MONEY-3 (LOW, cosmetic, fail-closed) — Negative `CashReceived` produces a shortfall message larger than the bill
- **Status:** OPEN · **Found by:** T1, 2026-10-20
- **Evidence:** Nothing validates `CashReceived >= 0`; −₹500 on a ₹120 bill reports "short ₹620". No money moves (fails closed).
- **Fix:** validator: `if (bill.CashReceived.Value < 0) return new Error(ErrorCodes.ValidationFailed, "negative cash received");`

### TESTS-1 — Ten missing/weak money test specs (paste-ready specs in the T1 report, held by Muse)
- **Status:** OPEN · **Found by:** T1, 2026-10-20
- **Specs:** T1-01 finalize overflow → `BILL_AMOUNT_TOO_LARGE` has **zero coverage** (code exists at BillingHandlers.cs:115, no test anywhere) · T1-02 line-level discount >100% at finalize · T1-03 legacy **28%** slab at finalize (only 12% tested) · T1-04 undefined-slab regression (pairs with MONEY-1) · T1-05 failed finalize consumes no quota / burns no bill number (3 failure modes) · T1-06 quota boundary: two concurrent finalizes at 49 → exactly one succeeds · T1-07 line-gross exact-half-paisa rounding · T1-08 exact-max line qty boundary accepted · T1-09 negative dues payment rejected · **T1-10 GST summary + day-end report totals over persisted bills — NO test exists for `GetGstSummaryQuery`/`GetDayEndReportQuery` aggregations incl. void exclusion, the very numbers a shop files GST from.**
- **Verified strong (no action):** GST-inclusive math, 40% slab, discount order + largest-remainder apportionment, change-due cash-only, cash-short args, 50/mo quota + H2 clock defence, B2 in-transaction increment, finalize idempotency (strongest tests in the suite), H1 void fail-closed, 2,000-bill invariant fuzz.

### LOCAL-1 (LOW) — `TreatWarningsAsErrors=false` in local builds; only CI enforces it
- **Status:** OPEN · **Found by:** Architecture/flow audit, 2026-10-20 (file read at 94a9684f)
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
- **Items:** (a) `SubscriptionGuard` timeout is silent — log a warning on the timeout branch (+ a hanging-service test; pairs with MONEY-2). (b) `GlobalExceptions.Report` puts the correlation id in the message text, not a scope — the file template's `{CorrelationId}` renders BLANK on unhandled lines; wrap in BeginScope, and set a bootstrap logger before `builder.Build()`. (c) `LogExporter` catches only IO/Unauthorized — other exceptions (InvalidData, OOM) surface as UNEXPECTED instead of LOG_EXPORT_FAILED; broaden the catch + add the failure-path test (only success is tested today). (d) Settings export discards the share `Result` (SetupAndSettingsPages.cs:263) — a failed share gives the shopkeeper no feedback; toast it. (e) `DatabaseRecovery.Recover` (DataSafety.cs:39) skips failed safety-copy candidates with no log line — include skip reasons in the recovery report. (f) `AngadiDb.BackupBeforeMigration` swallows any SQLiteException silently — if the probe fails from corruption (not "no such table"), migration proceeds WITHOUT the `.pre-update` copy, unlogged; only swallow the missing-table result. (g) `JsonLocalizer.Error`/`Format` can show a raw `{0}` template on an arg-count FormatException — fall back to the placeholder-stripped template. (h) AppShell first-run/language-switch failures are logged but never toasted — the shopkeeper can land on an empty POS with no setup prompt and no message; call `SafeUi.ShowUnexpectedAsync()` in both catches. (i) Retention doc says "7 days / 5 MB" but Serilog's limit counts FILES (7 files ≈ 35 MB; a busy day shortens real coverage) — correct the comment or prune by age.
- **Also noted (test-hardening, no code flaw):** NFR-08 asserts the customer PHONE never reaches logs but never asserts the NAME; cancellation (OperationCanceledException) is logged nowhere end-to-end by design; the exported log zip has no redaction pass — PII hygiene is by write-side convention, verified clean today (no log call anywhere takes name/phone/address/utterance/token — A1 swept every Log call).
- **Verified strong (no action):** correlation scope opens BEFORE validation (rejections carry it), 50-code bilingual error catalog test-enforced both languages, storage failures classified (STORAGE_FULL/DATABASE_BUSY), bounded everything (2 s guard / 5 s busy / 30 s gate / 45 s watchdog), no raw exception text reaches any screen (grep: zero `ex.Message` in UI), purchase token never logged.

### SEC-1 (HIGH) — Manual backup export is a plaintext copy of the entire shop book
- **Status:** OPEN · **Found by:** Audit agent A2 (security), 2026-10-20, code read at HEAD 0b25f05
- **Evidence:** `BackupService.ExportAsync` copies the raw DB to `CacheDirectory/backups/angadi-backup-*.db` — no encryption, no passphrase. The file holds every customer name, phone, dues balance, and bill history. It leaves the device by user share — the realistic leak: the shopkeeper WhatsApps it to a personal number or a shared drive, and whoever holds that one file owns the dues book (customer phone+dues is personal data under DPDP Act 2023; pharmacy histories are especially sensitive).
- **Fix:** encrypt exports with an owner-set passphrase (AES-256-GCM, PBKDF2/Argon2 key derivation) before Play production — the v2 build plan's W4 already designs exactly this envelope for cloud backup; apply the same envelope to manual exports. Interim minimum: a Telugu warning dialog on export ("this file contains all customer dues — keep it private").
- **Why:** This is the single most likely real-world data leak in the product — not a hacker, a forwarded file.

### SEC-2 (HIGH, OWNER DECISION) — Database is plaintext at rest (no SQLCipher)
- **Status:** OWNER-DECISION · **Found by:** A2, 2026-10-20
- **Evidence:** `AngadiDb.Open` uses a plain `SQLiteConnection` (WAL); zero SQLCipher references repo-wide. DB + snapshots + `.pre-update`/`.pre-restore` copies all sit plaintext in app-private storage.
- **Decision for Sai:** (a) accept for v1 single-shop tablets behind the device lock screen — app-private storage is not readable by other apps on a non-rooted device; or (b) SQLCipher build with the key in Android Keystore + a plaintext→encrypted migration, recommended before pharmacy scale or any multi-staff rollout. Record the acceptance consciously; don't carry it silently.

### SEC-3 (MEDIUM-HIGH) — No audit record when the shop's UPI ID or an item's price changes — the two frauds that actually happen in shops
- **Status:** OPEN · **Found by:** A2, 2026-10-20
- **Evidence:** `ShopProfileRow` and `ItemRow` carry only Created/Updated stamps; there is no change-history entity in Rows.cs. (a) **UPI swap fraud:** someone changes the shop UPI ID to their own; customer payments silently go to the wrong account and there is NO record of when or to what it changed. (b) **Price-shrinkage:** staff lowers a price, sells to an accomplice, restores it — no record the price ever changed (old bills are safe: lines snapshot price at sale).
- **Fix:** append an outbox event (or history rows) with old→new value + timestamp on every UPI-ID/GSTIN change and every item price edit; surface profile/price history in-app. Cheap to build on the existing Outbox.

### SEC-4 (MEDIUM-HIGH, OWNER DECISION) — No app lock: anyone holding the tablet can void, fake a dues payment, or export the database
- **Status:** OWNER-DECISION · **Found by:** A2, 2026-10-20
- **Evidence:** zero PIN/biometric/auth code repo-wide; AppShell opens straight into the POS. Void, dues-payment recording, backup, restore, and log export are all one tap from the counter screen. Classic kirana insider-fraud setup — the threat is staff and bystanders, not remote attackers (permissions are minimal, no deep links/receivers/providers, no analytics — external surface verified small).
- **Decision for Sai:** ship an optional owner PIN (or biometric) gating ONLY sensitive actions — void, dues payment, restore, export, price/UPI edits — with counter billing PIN-free for speed. A2 recommends ON by default for pharmacy.

### SEC-5 (MEDIUM) — No dues write-off: bad debt can only be "forgiven" as a fake payment
- **Status:** OPEN · **Found by:** A2, 2026-10-20
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
- **Status:** OPEN · **Found by:** Flaw hunter, 2026-10-20, code read at 94a9684f
- **Evidence:** Restore is a whole-file swap (`BackupService` → `db.ReplaceWithAsync(staged)`) with NO post-restore merge. `NextBillSequence` and the `quota.count.*` / `last_seen` Meta keys live in that same file. Restore Monday's backup on Friday and the app **re-issues Friday's already-printed bill numbers to different customers** (GST sequential-invoicing violation; the unique index can't catch it — the wiped bills are gone) and this month's free-bill count rewinds with it.
- **Fix:** before the swap, snapshot the live sequence + quota counters + `last_seen`; after the swap, write back the element-wise MAXIMUM of (restored, pre-restore). Regression test beside the H3 tests: sell → backup → sell 5 more → restore → numbering and quota continue from the live values, never backwards.
- **Why:** Duplicate invoice numbers are a compliance defect a GST officer can see on paper. This is a launch blocker.

### RELEASE-1 (HIGH, verify-at-HEAD first) — CI may never build the Release configuration; the shippable AAB path is exercised only on release day
- **Status:** OPEN · **Found by:** Flaw hunter (at 94a9684f: ci.yml Android job is literally "Build Android (Debug, unsigned)"); T4 notes commit 0b25f05 claims Release-build testing + an e2e [debug, release] matrix — **reconcile at current HEAD before fixing**
- **Evidence:** Everything Release-only in the csproj (`AndroidPackageFormat=aab`, the Release `TrimmerRootAssembly` group) has zero CI coverage on the audited HEAD. A linker/AAB break would surface for the first time when v1.0.0 is tagged.
- **Fix (if still open at HEAD):** add a Release AAB step/job to ci.yml; fail if no `*.aab` is produced; upload as artifact. Also pin the Release perf properties (L-item: no `PublishTrimmed`/`RunAOTCompilation` is set explicitly — SDK defaults decide what ships on low-RAM tablets).

### RELEASE-2 (HIGH) — The documented rollback cannot roll anything back
- **Status:** OPEN · **Found by:** Flaw hunter, 2026-10-20
- **Evidence:** `docs/deployment-and-rollback.md` says "re-publish the previous AAB. Play handles downgrade delivery." Play NEVER delivers a lower versionCode to a device on a higher one, and release.yml makes codes strictly increase. Following this doc during a data-corrupting incident does nothing.
- **Fix:** rewrite the doc: halt the rollout → hotfix → publish a NEW AAB with a HIGHER versionCode containing the good code → test upgrading FROM the bad version explicitly.

### RELEASE-3 (HIGH) — Store listing is not submittable: zero screenshots, no full description
- **Status:** OPEN · **Found by:** Flaw hunter, 2026-10-20
- **Evidence:** `store/` has the icon, feature graphic, launcher preview, and a README whose listing text stops at the short description. Play requires phone screenshots to publish at all, plus 7″/10″ tablet screenshots for the tablet listing — the exact devices this POS targets.
- **Fix:** capture phone + 7″ + 10″ screenshots (the CI e2e job already screenshots the app on an emulator — harvest from there); write the full description (EN + TE) into `store/README.md`.

### RELEASE-4 (MEDIUM) — Release plumbing: coverage gate dropped in release.yml; versionCode derived from run number
- **Status:** OPEN · **Found by:** Flaw hunter, 2026-10-20
- **Evidence:** release.yml runs only `dotnet test` — the NFR-10 coverage gate exists only in ci.yml, and nothing verifies the tagged commit was green in CI; with the Play secret set, a red/never-run commit still auto-uploads a signed bundle. versionCode = `100 + GITHUB_RUN_NUMBER` — run numbers reset if the workflow is recreated → code ≤ one already on Play → release-day rejection; it also contradicts the docs and the csproj's static version 1.
- **Fix:** add the coverage step + a CI-conclusion check for `github.sha` to release.yml; single version source of truth — a committed `version.json` the workflow reads and bumps; align both docs.

### QUOTA-1 (MEDIUM) — Quota `last_seen` has no sanity bound: one accidental far-future clock jump poisons the quota for the whole jump
- **Status:** OPEN · **Found by:** Flaw hunter, 2026-10-20
- **Evidence:** `QuotaStore` uses `Math.Max(nowMs, lastSeen)` uncapped, and Increment persists it. Tested case is a 2-month jump. The realistic accident — clock set +1 year, one bill sold, clock corrected — leaves the shop with **50 free bills total for a year**, then a hard paywall; any backup carries the poison to a new phone.
- **Fix:** ignore `lastSeen` beyond `now + 48h` for month selection; clamp `quota.last_seen_ms` to now on restore (ties into RESTORE-1); add the +1-year test.
- **Related (M-3, owner accept/document):** reinstall/clear-data fully resets the 50-free quota (counters live only in the DB). Cheap mitigation: a monotonic lifetime counter in Preferences (survives DB restore, not reinstall). At minimum record the limitation in BILLING_RULES.md as a chosen risk.

### RESTORE-2 (MEDIUM) — The restore invariant sweep never checks the money inside bills
- **Status:** OPEN · **Found by:** Flaw hunter (extends VERIFY-1/2 and the T3 lane), 2026-10-20
- **Evidence:** `BackupService.PassesInvariants` checks integrity, stock-vs-ledger, dues-vs-ledger, negative dues, orphan lines — it never recomputes a bill, never parses `TaxSplitsJson`, never checks `NextBillSequence` against existing numbers. Reports then sum stored fields verbatim. A checksum-valid backup with inflated totals restores cleanly and every GST report afterwards trusts the fake numbers.
- **Fix:** during the sweep, recompute every finalized bill with `BillCalculator.Compute` and compare all totals + splits; add sequence-vs-numbers and orphan dues/stock checks. (Pairs with the VERIFY-2 chunk-boundary check.)

### DATA-1 (MEDIUM) — Migration strategy is additive-only; the first v1.1 restructure has no framework and will silently not happen
- **Status:** OPEN · **Found by:** Flaw hunter, 2026-10-20
- **Evidence:** `AngadiDb` Migrate = ten bare `CreateTable` calls ("additive migrations are automatic"), then `schema_version` is overwritten. Zero `ALTER TABLE` in src/. A column-type change or table split (e.g. a payments table for split tender) will be skipped or improvised against live shop data.
- **Fix:** ordered `Migrations[]` keyed by from-version, run in ONE transaction, version stamp bumped only after all steps succeed; backfill the existing v2/v3 changes as the first entries. Must land before ANY v1.1 schema change.
- **Adjacent LOWs (same lane):** (a) `int.Parse` (not TryParse) on `schema_version` can crash-loop startup on a non-numeric value — TryParse → throw DamagedDatabaseException so snapshot recovery handles it. (b) Two backup exports in the same minute overwrite each other (filename has minute precision, `overwrite: true`) — INCLUDING the pre-restore safety copy: export at 10:42, restore at 10:42, and the safety copy IS the overwritten file. Fix: seconds/GUID suffix; safety copies `overwrite: false`.

### VOICE-9 (MEDIUM) — Speech has no offline/on-device path, and the "manual" fallback never opens typed entry
- **Status:** OPEN · **Found by:** Flaw hunter (extends VOICE-8), 2026-10-20
- **Evidence:** `AndroidSpeechToText` gates on internet FIRST (`VoiceNoInternet` before availability is checked), sets no `EXTRA_PREFER_OFFLINE`, uses only `SpeechRecognizer.CreateSpeechRecognizer` (no `CreateOnDeviceSpeechRecognizer`, API 31+). And `PosViewModel.MicCore`'s "manual" branch just toasts and returns — the POS search is NOT focused, despite the fallback comment claiming "the POS opens typed search". On de-Googled / GMS-less budget tablets (a large slice of this market) voice billing — the headline feature — is permanently dead.
- **Fix:** try the on-device recognizer first; drop the internet pre-gate when it exists; set prefer-offline; make the manual branch actually focus the search field.

### PRIVACY-1 (MEDIUM) — Learned voice words ARE written to logs at Debug level; the policy's "never" is false and the NFR-08 test can't catch it
- **Status:** OPEN · **Found by:** Flaw hunter, 2026-10-20 (the crack in A1's otherwise-clean sweep — A1 verified the pipeline/handlers; this line lives in AliasLearner)
- **Evidence:** `AliasLearner.cs:69-70`: `LogDebug("Learned alias text: {Alias}", alias)`. PRIVACY_POLICY.md and the feedback UI both promise learned voice words are NEVER written to logs. Release builds are saved only by the Information-level filter; any Debug/sideload build's exported log zip contains the shop's learned vocabulary. `LoggingTests.NFR08…` never calls `LearnAsync`, so the leaking line is never exercised.
- **Fix:** delete the Debug line entirely (log the count, never the text); add a LearnAsync call to the NFR-08 test.
- **Policy mismatches (L-9, same agent batch):** policy also (b) claims no background uploads while PlaySubscriptionService auto-starts a background Play re-check from inside billing; (c) never discloses the automatic on-device copies (3 snapshots, .pre-update, .pre-restore, damaged copies) that a "deleted" customer persists in; (d) omits Internet/Billing/Vibrate from its permissions list and the feedback path from data-leaving. Rewrite the policy against the code, then fill + host it (SEC-7).

### SEND-1 (MEDIUM) — WhatsApp senders trust the stored phone blindly at the send layer
- **Status:** OPEN · **Found by:** Flaw hunter, 2026-10-20
- **Evidence:** `SendBillToCustomerHandler` / `SendDuesReminderHandler` check only non-empty, then `ReceiptShare` builds `https://wa.me/` + phone unescaped/unvalidated (contrast SendFeedbackHandler, which does Normalize). Today the invariant holds only because save normalizes; a row arriving via an old-backup restore or future import can send a full bill (name, 15 lines, totals, TOTAL DUES BALANCE) to a malformed/wrong-country number.
- **Fix:** `PhoneNumbers.Normalize(customer.Phone) is not { } → CustomerNoPhone` inside both handlers, and defensively in ReceiptShare.

### FEATURE-1 — Owner-scheduled gaps (recorded, not launch blockers)
- **Status:** OWNER-DECISION · **Compiled by:** Flaw hunter + A2, 2026-10-20
- **Items:** (a) **Split tender** — part-cash/part-UPI can't be recorded truthfully (validator forbids cash on non-cash tender); everyday payment, shopkeepers will misrecord or split bills (2 quota slots, wrong invoice). Needs a `BillPayment` child table — v1.1, and it needs DATA-1's migration framework first. (b) **UpiRef optional at every layer** — keep it skippable for speed but make it explicit (≥4-char ref or a deliberate "No ref" tap; day-end shows a count of UPI bills without refs) for month-end reconciliation. (c) **₹0 bills finalize silently** — 100% discount / ₹0 item burns a bill number + quota slot and decrements stock with no warning; require an explicit free-bill confirmation flag. (d) **Feedback attach-logs does two separate shares** — the zip never travels inside the WhatsApp message; with the empty support number the shopkeeper believes logs reached support when nothing arrived (LOG lane + SEC-7 overlap). (e) **Min-SDK floor (API 28)** — excludes budget Android 8/Go tablets common in kirana; decide from real target devices, test API 26, else record the deliberate floor in RELEASE.md. (f) **CHANGELOG omits the CS0819 fix by name** — one line (commit 80a34291), against the repo's own What/Issue/Why rule.
- **Reconciliations (flaw hunt vs other agents — read before fixing):** H-A's Auto-Backup-rules portion is ALREADY FIXED at HEAD 0b25f05 (A2 verified backup_rules.xml + data_extraction_rules.xml scope to DB+WAL+prefs) — do NOT redo it; the open parts of H-A are SEC-1/SEC-2 (plaintext DB + exports). H-C = SEC-4 (PIN decision). H-D's new detail: manual exports land in CacheDirectory and exist only if the share sheet is completed — fold the overdue-backup nag into the v2 W4 work. M-11 (Outbox grows forever, sparse payloads) is the same root as SEC-3 — fix together: audit records with old→new + an Outbox prune beyond the backup window. M-14 = TESTS-1/T1-10 (report-handler tests) — one batch. M-15 (platform adapters untested) partially covered by VOICE-8 SPEC-6/7.

<!-- New entries go ABOVE this line, newest first within their section order: GOV, CRITICAL, HIGH, MEDIUM, LOW, MINOR, VERIFY. Keep the Flaw → Fix → Why shape. -->
