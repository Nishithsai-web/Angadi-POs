# AGENT_WORKFLOW.md — Muse + Claude Collaborative Engineering Team
**Adopted:** 2026-10-19 · **Owner:** Sai · **Repo:** Nishithsai-web/Angadi-POs · **Coordinator:** Muse (Candy)

## 1. The team (10 roles → actual agents)

| # | Role | Who fills it | Notes |
|---|---|---|---|
| 1 | Product Owner / BA | Muse | Holds SRS, owner decisions (D1–D4), acceptance criteria. Asks Sai only when a decision is consequential. |
| 2 | Solution Architect | Muse | Blueprint + ADRs. Any contract change needs architect sign-off before agents build on it. |
| 3 | Backend Engineer | Claude Code agents | C#, .NET, business logic, services. |
| 4 | Database & Reporting Engineer | Claude Code agents | SQLite schema, UoW/repos, reports, backup/restore. |
| 5 | Frontend & Integration Engineer | Claude Code agents | MAUI UI, platform adapters (speech/scanner/billing), validation. |
| 6 | QA & Test Automation | Muse's QA agents + CI | 5-agent QA team (functional, money, Telugu, architecture, robustness) + GitHub Actions gates. |
| 7 | Security & Reliability | Muse's QA agent 5 + watcher | Input hostility, PII-in-logs, free-tier bypass, backup integrity. |
| 8 | Independent Code Reviewer | Muse + repo watcher | Reviews diffs/commits against blueprint. **Never writes application code** — reviewer independence is structural, not polite. |
| 9 | Documentation Engineer | Claude Agent E + Muse | Agent E keeps docs/ current as it builds; Muse maintains workflow/audit docs. |
| 10 | DevOps & Release Engineer | GitHub Actions + Muse | CI compiles/tests/gates; Muse verifies release readiness. **Merges, deploys, and production releases are human-only (Sai).** |

## 2. How Muse and Claude actually collaborate

There is **no direct agent-to-agent channel and no shared memory** between Muse and Claude Code. The GitHub repository is the coordination mechanism:

- **Muse → Claude:** requirements, architecture, and fix instructions as repo files (`docs/SRS.md`, `docs/ARCHITECTURE.md`, `docs/AUDIT_FIXES.md`, …). Claude Code agents read them from the working tree.
- **Claude → Muse:** commits (with descriptive messages). Muse reviews via the connector + the 8-hour watcher.
- **Decisions/disputes:** recorded in ADRs and commit messages, never in chat alone. Evidence beats preference; smallest safe fix wins.

## 3. Task lifecycle

`PLANNED → IN PROGRESS → NEEDS REVIEW → NEEDS FIXES → VERIFIED → COMPLETE` (`BLOCKED` anytime)

A task is **COMPLETE** only when: requirements + acceptance criteria documented · implementation in repo · tests actually run and passed (CI green observed, not assumed) · review findings resolved or explicitly accepted · docs updated · final diff reviewed · limitations disclosed.

## 4. Review gates (the non-negotiable loop)

1. Agents implement from the approved contracts.
2. Independent review (Muse / QA agents / watcher) inspects the actual code — never just the plan.
3. Findings land in the repo as `docs/*_FIXES.md`, severity-ordered, paste-ready.
4. Agents correct; reviewer re-verifies the fix commits.
5. QA reruns affected tests; watcher confirms no regressions in later commits.

## 5. GitHub workflow

**Current sprint state (conscious tradeoff):** agents push directly to `main` for speed during the 14-day build. No open PRs/issues as of 2026-10-19.
**Recommended post-v1:** feature branches + PRs with required CI checks; branch protection on `main` (Sai's decision to enable).
**Tracking:** one GitHub Issue per work batch (see Issue #1 — v1 build tracking hub). No silent work: every batch links its issue, commits, fix files, and test evidence.

## 6. Approval matrix

| Action | Who | Needs Sai's tap? |
|---|---|---|
| Read repo, review code, run QA agents | Muse | No |
| Post review findings to chat | Muse / watcher | No |
| Push docs/fix-lists to repo | Muse | **Yes** (approval card each time) |
| Push application code | Claude Code agents (via Sai's machine) | Via Sai's own git |
| Merge PRs, deploy, release to Play | Sai | **Always** |
| Production-affecting decisions (pricing, Tier-2 AI keys, scope cuts) | Sai | **Always** |

## 7. Documentation map

- `docs/SRS.md` — requirements, owner decisions, acceptance criteria
- `docs/ARCHITECTURE.md` — architecture + ADRs
- `Angadi_Architecture_Blueprint.md` (Muse workspace, mirrored in HQ) — the standard reviews are judged against
- `docs/AUDIT_FIXES.md`, `docs/AGENT_TEST_FIXES.md` — review findings → fixes
- `docs/AGENT_WORKFLOW.md` — this file
- `docs/CHANGELOG.md` — every meaningful change (Agent E maintains)

## 8. Conflict resolution

Muse and Claude optimize for the same approved outcome. Disagreements are settled by: explicit requirements > reproducible tests > security/maintainability > preference. Reviewers must cite file/line evidence. Settled decisions stay settled without new evidence. A reviewer approval is claimed only when the review actually happened.

## 9. The "Why" rule (owner directive, 2026-10-19)

Every change Muse pushes to this repo must explain itself so that a Claude Code agent reading the file understands it on sight — no tribal knowledge, no bare diffs. Every change file contains:

1. **WHAT changed** — file paths, and the change in plain words.
2. **WHAT THE ISSUE WAS** — the flaw, gap, or requirement that made the change necessary. Concrete: quote the failing behavior, the missing requirement, or the risk.
3. **WHY this fix** — the reasoning: why this approach, what alternatives were considered and rejected, what breaks if it's done differently.

Fix files (`docs/*_FIXES.md`) follow the **Flaw → Fix → Rationale** pattern per item. Workflow/process changes get the same treatment at the top of the file. `docs/CHANGELOG.md` carries the running log: date, change, issue addressed, and the one-line why. A change whose "why" can't be written clearly doesn't get pushed.
