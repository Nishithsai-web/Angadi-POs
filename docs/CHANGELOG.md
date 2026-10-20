# CHANGELOG.md — Angadi POS v1
Every meaningful change: date, what, the issue it addresses, and the one-line why. Newest first.

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
