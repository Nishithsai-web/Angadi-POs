# CLAUDE_BRIEF.md — what the Muse side added and what Claude Code must do
**Date:** 2026-10-19 · **From:** Muse (orchestrator) · **To:** Claude Code agents
**Why this file exists:** Per the owner's "Why" rule (§9 of `docs/AGENT_WORKFLOW.md`), every change pushed by the Muse side explains what changed, what the issue was, and why — so you understand it on sight.

---

## 1. WHAT WAS ADDED (by the Muse side)

| File | What it is | Why it exists |
|---|---|---|
| `docs/AUDIT_FIXES.md` | Independent code audit of the Day-1–5 build: 4 medium fixes + 7 named test gaps + 6 nits | The agents never compiled locally (no .NET SDK in sandbox); an outside review was needed before more code stacked on unverified foundations. |
| `docs/AGENT_TEST_FIXES.md` | 5-agent QA sweep: 2 CRITICAL, 11 HIGH, 15 MEDIUM, LOW condensed — every item Flaw → Fix → Rationale | Adversarial testing (money math, Telugu voice, robustness, architecture) found what implementation agents can't see in their own code. **This is your primary work queue.** |
| `docs/ARCHITECT_CORRECTIONS.md` | Senior architect review: design corrections to the QA fixes + new findings (P1/P2/B1/B2/P3/P4/V1) + Wave 0/1/2 sequencing | The QA sweep found the right problems but several fix designs were wrong (H2 hole, H3's illegal negative-stock check, H8's silent slab remap) and two bigger criticals were missed (`MauiProgram.cs` absent, `Resources/` absent). **Where this file and AGENT_TEST_FIXES.md disagree, this file wins.** |
| `docs/AGENT_WORKFLOW.md` | Team operating model: 10-role mapping, Muse↔Claude collaboration via this repo, task lifecycle, review gates, approval matrix, §9 "Why" rule | Two AIs with no shared memory need written rules of engagement, or they overwrite and duplicate each other. |
| `docs/CLAUDE_BRIEF.md` | This file — running brief from Muse to Claude Code | The owner ordered: "let Claude know in instructions what's added." |
| `docs/CHANGELOG.md` | Running change log (seeded) | Every meaningful change needs a dated record with its why. |
| `docs/testing.md` | Test strategy + the named test catalog | Tests were being added ad hoc; this pins what "tested" means per area and lists every named test the audits require. |
| `docs/deployment-and-rollback.md` | Release checklist + rollback plan | v1 ships to the Play closed-test track soon; the rollback path must exist before it's needed. |
| `docs/data-flow.md` | Bill lifecycle data-flow (voice → draft → finalize → outbox) | New agents (10 just added) need the money path in one page instead of reverse-engineering it. |
| `docs/UI_SPEC.md` | Modern responsive UI spec: Telugu-first, 7"/10" adaptive layouts, kirana/pharmacy modes | Owner ordered a modern flexible responsive UI; 10 new agents need one design authority, not ten opinions. Pharmacy billing+inventory confirmed; expiry tracking stays OUT (owner cut). |
| GitHub Issue #1 | v1 build tracking hub | One place linking scope, docs, batches, and definition of done. |

**Scope re-confirmed by owner (2026-10-19):** inventory management (stock ledger, low-stock, adjust — already in build), modern flexible responsive UI (new `docs/UI_SPEC.md`), medical-shop/pharmacy ready (catalog + units + invoice labeling via business-type toggle; **expiry tracking remains OUT** per the owner's earlier scope cut).

## 2. WHAT IT MEANS FOR YOU

- **Review gates are live.** After you implement a batch, the Muse side independently reviews the actual diff (not the plan). Findings come back as `docs/*_FIXES.md` in the same Flaw → Fix → Rationale format. Implement, don't debate — unless you have evidence, in which case put it in the commit message.
- **Muse never writes application code.** Reviewer independence is structural. If you see a `docs/` commit from the Muse side, it's specs, findings, or process — never `src/`.
- **The "Why" rule cuts both ways now.** Your commit messages should state the fix ID and the why in one line, e.g. `Fix H1: void of partially-paid udhaar bill now fails closed (BillVoidBlockedByPayments) — prevents negative dues bricking the account`.
- **A 5-architect strike team is reviewing your fix designs** on the Muse side (billing, voice, data, platform, integrator). If they propose a better approach for C1/C2, it will arrive as an update to this brief or a new fix file. Don't treat the current fix text as frozen where a better design exists — but don't stall waiting either; C1/C2 as specified are safe to implement now.

## 3. YOUR ORDERS (in priority order — see `docs/BATTLE_PLAN_2DAY.md` for the 48-hour sequencing and file ownership)

1. **Compile the build (C1).** Implement `Receipts/ReceiptRenderer.cs` (FR-17 fields + Telugu shaping) and the `UnitConversion` type. Add the DI self-test resolving every `IRequestHandler<>`. Nothing else matters until `dotnet build` is green.
2. **Wire billing (C2).** Register the `ISubscriptionService` stub (`IsPro=false`) now; build the real Android Play Billing client next. Add Pro/non-Pro handler tests.
3. **Work `docs/AGENT_TEST_FIXES.md` top-down** — H1…H11, then mediums. Do not cherry-pick easy mediums while highs are open. **Read `docs/ARCHITECT_CORRECTIONS.md` first** — it corrects several of those fix designs and adds P1/P2/B1/B2/P3/P4/V1. Where they disagree, the corrections file wins.
4. **Then `docs/AUDIT_FIXES.md`** — 4 mediums + 7 test gaps + nits.
5. **Keep the docs you own current** as you build: `docs/CHANGELOG.md` (every meaningful change), `docs/SRS.md` (if a requirement changes, say so in the commit), `docs/ARCHITECTURE.md` (new ADRs for new decisions).
6. **Never force-push.** `git pull --rebase` before every push. Verify the head SHA before and after.
7. **Build the UI to `docs/UI_SPEC.md`.** Telugu-first, adaptive 7"/10" layouts, business-type toggle (Kirana/Pharmacy/Both). No expiry tracking — it is cut, do not build it.
8. **Read `docs/data-flow.md`** before touching the money path. If your change breaks that flow, your change is wrong.

## 4. DEFINITION OF DONE (per task)

Requirements documented → implemented in repo → tests actually run and green (CI observed, not assumed) → review findings resolved → docs updated → diff reviewed → limitations disclosed. A code edit is not a verified solution.
