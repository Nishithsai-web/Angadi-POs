# ENTERPRISE_PLAN.md — Angadi for chains and large user bases

**Status:** plan for owner review (2026-11-06). **Rule:** the phone stays the source of truth; billing never waits on a server.

## Why
v1 keeps each shop's data on its own phone. That is right for one shop and fast offline, but a chain owner sees a branch only when a report file is sent (CHAIN-1), and nothing scales to thousands of shops. Enterprise needs accounts, sync and live reports on a server, without making the counter depend on the network.

## Architecture (target)
- **Phone (unchanged):** SQLite, outbox of changes (`SyncState` already exists). Bills, stock and udhaar are written locally first, always.
- **Sync API (new, `src/Angadi.Server`, ASP.NET Core):** receives the outbox as idempotent batches (change id + shop id + monotonic version), returns acknowledgements and newer changes for shared records (staff, price lists).
- **Database:** PostgreSQL. One organisation owns many shops; every row carries `org_id` and `shop_id`, and every query is filtered by them (row-level security as a second lock).
- **Accounts:** phone OTP sign-in for owners, managers and cashiers; roles map to today's Owner / Manager / Cashier plus a new **Chain owner** who sees all shops of the organisation, read-only by default.
- **Live chain dashboard:** reads server data; falls back to CHAIN-1 report packs when offline.
- **Conflicts:** bills and stock moves are append-only (never conflict). Editable records (items, customers, staff) use last-writer-wins by version with the loser kept in the activity log.

## Phases (each ships alone)
| Phase | Delivers | Needs |
|---|---|---|
| **0 (done)** | RBAC-1, CHAIN-1 report packs, PERF-1/2 | — |
| **1 Staff money (on phone)** | Staff attendance, daily wages/salary, advances and deductions, month payslip PDF to WhatsApp; owner/manager only | nothing external |
| **2 Server skeleton** | `Angadi.Server` + Postgres schema + local `docker compose`, health check, auth stub, sync endpoints, contract tests | nothing external (runs locally) |
| **3 Sync client** | Outbox drain, retry with backoff, "last synced" status, plain-language offline states | Phase 2 |
| **4 Accounts + chain dashboard** | OTP sign-in, organisations/branches, live reports, branch comparison | SMS/OTP provider (cost) |
| **5 Hosting + compliance** | Managed Postgres + app hosting, backups, monitoring, DPDP notices, data export/delete, load test | **owner approval of spend** |

## What this plan does not do
- Payroll **tax filing** (TDS/PF/ESI). Phase 1 records pay and advances only; statutory filing needs a compliance partner.
- Replace the on-phone database. Billing stays offline-first.

## Cost notes (to verify before any commitment)
Hosting and OTP prices change; get current quotes first. Small managed Postgres plus a small app host is typically a modest monthly cost against ₹200 per shop. Nothing is spent until the owner says so (Phase 5).

## Risks
- We become custodian of shopkeepers' customer and udhaar data: DPDP duties, breach response, support.
- Sync bugs are data-loss bugs: every phase needs replay/duplicate/out-of-order tests before release.
