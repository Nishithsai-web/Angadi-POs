# BATTLE_PLAN_2DAY.md — 48-hour push to "ready for testing"
**Goal:** compiling app, core flows working on a tablet, named tests green — ready for the UAT script in `docs/deployment-and-rollback.md`.
**Not in 48h:** Play closed-track submission, the 14-day tester period, visual polish, Tier-2 AI (v2).

## The one rule that matters most
**File ownership.** With ~20 agents in one repo, two agents editing the same file = merge conflicts and lost work. Each workstream below owns its files. If you must touch another stream's file, say so in the commit message.

## DAY 1 — Make it compile, make money safe (hours 0–24)

| # | Workstream | Owner files | Fixes |
|---|---|---|---|
| 1 | **Build** | `Infrastructure/DependencyInjection.cs`, `Infrastructure/Receipts/*`, `UnitConversion`, DI self-test | **C1** — implement `ReceiptRenderer` + `UnitConversion`; DI self-test resolving every handler. NOTHING ELSE STARTS until `dotnet build` is green. |
| 2 | **Billing stub** | `ISubscriptionService` stub + DI registration, quota handler | **C2** — stub returns `IsPro=false`; Pro/non-Pro tests. (After #1 if touching DI, else parallel.) |
| 3 | **Money safety** | `Application/Billing/*`, `Domain/Bill*`, validators | **H1** (void fail-closed), **H8** (legacy slabs), **H9** (`UpiRef`), **M9** (discount cap), **M7** (checked arithmetic) |
| 4 | **Data safety** | `Infrastructure/Persistence/*`, backup/restore, Meta/quota | **H2** (monotonic quota clock), **H3** (safe restore), **M6** (outbox events) |
| 5 | **Tests** | test projects | Every fix above ships with its named test from `docs/testing.md`. **H10** handler tests. |

**Day-1 exit check:** `dotnet build` green · `dotnet test` green on money/data suites · no `Result` failures on: create bill → finalize (cash/UPI/udhaar) → void → restore backup.

## DAY 2 — Voice, UI, and the full pass (hours 24–48)

| # | Workstream | Owner files | Fixes |
|---|---|---|---|
| 6 | **Voice** (ONE agent owns this folder) | `Application/Voice/*`, seed JSON, `strings.te.json` | **H4** (decimals), **H6** (ranges/dative), **H5** (seed harvest + demote generics), **M1/M2/M3/M13**, then AUDIT_FIXES voice items |
| 7 | **UI** | MAUI heads, pages, `docs/UI_SPEC.md` | Billing screen first (mic, grid, draft, tender), then Inventory, Udhaar, Reports, Settings. Adaptive 7"/10" triggers from the start — not as a retrofit. |
| 8 | **Remaining mediums/lows** | per-file ownership | M4, M5 (owner sign-off — flag in chat), M8, M10–M12, M14, M15, lows |
| 9 | **Full verification** | — | Entire `docs/testing.md` catalog green · voice batteries re-run (30-phrase ≥29, 40-phrase ≥35) · 2,000-bill fuzz still green · fresh-install + upgrade test on a real tablet |

**Day-2 exit check ("ready for testing"):** APK installs on 7" and 10" tablets · Telugu voice bills an item end-to-end · cash/UPI/udhaar finalize · void works · reports render · backup/restore round-trips · every named test in `docs/testing.md` green or explicitly marked UNVERIFIED with a reason.

## If something slips
Cut scope in this order — never cut correctness: (1) UI polish → (2) mediums → (3) reports beyond day-end → **never** cut: compile, money safety (H1/H8/H9), voice decimal/range fixes (H4/H6), backup/restore (H3). A smaller correct app beats a bigger broken one.

## Why this plan
22 agents without sequencing is a traffic jam; with it, it's a pipeline. Compile gates everything (C1), money safety gates trust (H1/H3/H8), voice fixes gate usability (H4/H5/H6). The order is the strategy.
