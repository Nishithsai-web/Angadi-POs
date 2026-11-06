# SERVER.md — Angadi v2 sync server (phase 2)

**What it is:** a small ASP.NET Core service on PostgreSQL. Phones push their outbox events (bills, stock moves, payments, staff pay…) in batches; the server stores each event once per shop and serves them back for dashboards. Billing never waits on it: if the server is down, phones keep selling and send later.

**Run locally:** `docker compose up` (repo root) → `http://localhost:8080/health` says `ok`. Nothing is hosted and nothing costs money until the owner approves hosting (ENTERPRISE_PLAN phase 5).

## API (v1)
| Method | Path | Auth | Purpose |
|---|---|---|---|
| GET | `/health` | none | server and database are up |
| POST | `/v1/shops/register` | none, rate-limited per IP | `{shopId, name}` → `{shopId, shopKey}`; the key is shown once, only its SHA-256 is kept; a second registration of the same shop is refused (409) |
| POST | `/v1/shops/{shopId}/events` | `X-Shop-Key` | `{deviceId, events[≤500]}` → `{acked[], lastSeq}`; each event stored once per shop, so replays and overlaps are safe; a bad event refuses the whole batch |
| GET | `/v1/shops/{shopId}/events?after=&limit=` | `X-Shop-Key` | events after a sequence number, oldest first, `more` flag for paging |
| GET | `/v1/shops/{shopId}/summary` | `X-Shop-Key` | event counts by type, newest sequence |

## Data rules
- Every table row carries `shop_id`; every query filters by it. A shop's key can neither read nor write another shop.
- Unknown shop and wrong key look identical (401, constant-time compare), so shop ids cannot be probed.
- Schema changes: add `Sql/NNN_name.sql`; applied once, in order, in a transaction, under an advisory lock (`schema_migrations`).

## Not yet (later phases)
Phone-side sync client and "Cloud sync" settings (phase 3); owner accounts with phone OTP, organisations, live chain dashboard (phase 4); hosting, backups, monitoring, DPDP notices (phase 5, needs the owner's approval of spend).

**Tests:** `tests/Angadi.Server.Tests` against a real Postgres (CI service container): registration, replay/overlap idempotency, paging, cross-shop isolation, bad-batch rejection, summary.
