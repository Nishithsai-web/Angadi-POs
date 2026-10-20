# V2_SPEC.md — Angadi v2: Sync, Tier-2 AI, Verified Purchases, Cloud Backup

**Status:** Build-ready specification · **Author:** DEV-1 (new-development agent) · **Date:** 2026-10-20
**Owner decision recorded:** 2026-10-20 13:30 EDT — **v2 backend = FREE TIER FIRST (Supabase). ₹0 hosting until real shops onboard; paid hosting only after revenue.** Azure/VPS remain documented alternatives (§9), not the recommendation.
**Companion doc:** `V2_BUILD_PLAN.md` (DEV-2: file-level build plan + fake-server harness, so the client side builds and tests at zero spend before any backend exists).

---

## 0. WHAT / ISSUE / WHY

**WHAT this is.** The complete specification for Angadi v2: (a) multi-device sync that drains the v1 Outbox through the existing `ISyncService` seam; (b) Tier-2 AI in a new `Angadi.AI` library behind an `AiFeatures` flag, via a swappable `IAiService`; (c) server-side Google Play purchase verification replacing v1's client trust; (d) automatic cloud backup of the shop database.

**ISSUE (why v2 exists, why now).** The owner ordered "Complete v2 too" (2026-10-20) instead of deferring it. The gaps v2 closes are the ones the v1 reviews and market analysis already named:

1. **Phone-change = business loss.** v1 keeps everything on one device (SRS FR-37 exists precisely because "without it, a lost phone loses every udhaar record"). Khatabook's "lose your phone, lose nothing" pitch is Angadi's single biggest competitive hole; manual file backup (FR-37) only helps shopkeepers who remember to do it.
2. **No second device.** The owner cannot see his own shop from home; a second counter device cannot bill.
3. **Tier-2 AI was designed but deferred** (Blueprint §11; SRS decision D2 moved it to v2 because "zero backend means the API key would ship inside the APK and could be stolen — v2 adds a small proxy that holds the key and enforces caps"). This spec is that proxy.
4. **Client-trusted purchases.** v1 believes the device about Pro status. A patched APK gets Pro free, and the on-device free-tier quota resets on reinstall (documented v1 limit).

**WHY this shape.** v1 was deliberately built so v2 is an addition, not a rewrite: Guid primary keys everywhere ("chosen for future multi-device sync merge"), an append-only Outbox written **inside the same transaction** as every change (SRS FR-15, NFR-05), an `ISyncService` port that v1 deliberately registers nowhere, money/stock/dues all represented as **ledgers** (StockMove, DuesEntry) whose balances are pure functions of the entries. v2 keeps every one of those invariants and spends **₹0 on hosting** until shops are actually paying (owner decision above).

---

## 1. Goals / Non-goals

### 1.1 Goals (v2.0)

| # | Goal | Traces to |
|---|---|---|
| G1 | A shop's data syncs across the owner's devices: install on a new phone, sign in, the books are there (phone-change safety + owner second-device view + optional second counter device). | §4 |
| G2 | Automatic cloud backup of the full shop DB, on by default after sync opt-in, plus optional copy in the **owner's own Google Drive**. | §7 |
| G3 | Tier-2 AI: ask-your-data assistant, supplier-bill photo scan, low-confidence voice rescue — behind `AiFeatures`, capped at 50 calls/shop/month, **aggregates only, no customer PII in prompts**, never in the billing hot path. | §5 |
| G4 | Server-side Play purchase verification (Play Developer API + RTDN) as the entitlement source of truth; reinstall can no longer reset a synced shop's free-tier usage. | §6 |
| G5 | v1 behaviour is the floor: every v2 feature degrades to exact v1 behaviour when offline, over cap, unconfigured, or down. Billing never waits on the network (the counter-freeze lesson, C2). | §4.6, §5.5 |

### 1.2 Non-goals (explicitly NOT v2.0)

- **No staff roles / PIN / per-user permissions.** v1 decision D3 (one role, no PIN) still holds for the counter; v2 adds an *account* (the shop), not staff identities. Staff roles stay a later version.
- **No real-time collaborative billing** (two devices editing the same open draft). Drafts stay device-local (as in v1); only finalized data syncs.
- **No iOS, no web shopfront, no online store, no loans, no soundbox, no card hardware** — locked scope is unchanged.
- **No GSTR filing / e-invoice / e-way bill / Tally export** — the GST-depth ceiling is accepted for the target segment (composition & small kirana/pharmacy).
- **No AI in the transaction path.** The per-bill voice pipeline stays the deterministic parser + matcher. AI rescue is advisory and user-confirmed, exactly like v1's Confirm band.
- **No migration off SQLite on device.** The phone remains the system of engagement; the server is a replica + services, never the counter's dependency.

---

## 2. Current state (verified against repo `main`, 2026-10-20)

Facts the design builds on — read from code/docs, not assumed:

- **Projects:** `Angadi.Domain`, `Angadi.Application`, `Angadi.Infrastructure`, `Angadi.Maui`, `Angadi.Tests`; net10.0, Android only. Dependency direction strictly inward; architecture tests enforce it.
- **`ISyncService` exists, registered nowhere** (`src/Angadi.Application/Platform/PlatformServices.cs`): `Task<Result<int>> DrainAsync(CancellationToken)` — doc comment (M4): *"A v2 implementation drains unsent Outbox rows (in order, by PayloadVersion) and returns how many it delivered."* v2 implements this contract; it does not change it.
- **Outbox** (Infrastructure `Rows.cs`, docs/DB_SCHEMA.md): `Id, AggregateType, AggregateId, EventType, PayloadJson, PayloadVersion (=1), SyncState (0 = pending)`. Rows are appended inside the same `AngadiDb.ExecuteAsync` transaction as the change they describe (FR-15). v1 never drains, never deletes.
- **Ledgers:** `StockMove` (Σ DeltaMilli = Item.StockMilli, enforced invariant), `DuesEntry` (Kind = Charge/Payment/Reversal; balance changes only via `AddDuesEntryAsync`), `Bill` is frozen at finalize (correction = void; BillLine carries name/price/slab snapshots). FIFO dues aging is a pure function over DuesEntry.
- **Bill numbers** come from `ShopProfile.NextBillSequence` (atomic `NextBillNumberAsync`), unique **per device only** — see §4.4, this matters for multi-device.
- **Quota:** on-device Meta counters (`quota.count.YYYYMM`, max(now, last_seen) clock defence), checked + incremented inside the finalize transaction. Documented v1 limit: reinstall/factory reset wipes it.
- **Subscriptions (v1):** `ISubscriptionService.GetStatusAsync` is cache-only + background recheck, 7-day offline grace, clock-back forces recheck; the server plays no part. `SubscriptionPlan { Monthly, Yearly }`, products `angadi_pro_monthly` (₹200), `angadi_pro_yearly` (₹1,999).
- **Backup (v1, FR-37):** `IBackupService.ExportAsync/RestoreAsync` — WAL checkpoint → self-contained file copy (`journal_mode=DELETE`) → share sheet; restore validates `Meta.app = angadi` and `schema_version ≤ current`, takes a safety backup, swaps atomically. v2 cloud backup **reuses this exact pipeline** (§7).
- **Tier-1 insights ship in v1** (reorder forecast, dead-stock radar, morning summary) — deterministic, on-device. **No `Angadi.AI` project and no `IAiService` exist.** Tier-2 (Blueprint §11) is design only.
- **Privacy posture (v1):** no backend, no analytics; the only network actors are Google's speech recogniser (audio), Play Billing, and the user's own WhatsApp/SMS/UPI apps. **v2 changes this — §8 states exactly how, in the open.**

---

## 3. Architecture

### 3.1 Shape

```
┌──────────────────────────── Shop devices (Android) ────────────────────────────┐
│ Angadi.Maui ──► Angadi.Application ──► Angadi.Domain                            │
│      │               │  ISyncService (exists)  IAiService (new, in Angadi.AI)  │
│      │               ▼                                                           │
│      │        Angadi.Infrastructure                                              │
│      │          ├─ SQLite (source of truth at the counter)                       │
│      │          ├─ SyncService      → HTTPS ──┐                                  │
│      │          ├─ AiProxyClient    → HTTPS ──┤  (both via ISyncTransport/        │
│      │          ├─ EntitlementClient→ HTTPS ──┤   IAngadiServerApi abstraction)   │
│      │          └─ CloudBackup      → HTTPS ──┘                                  │
└──────────────────────────────────────────────┼──────────────────────────────────┘
                                               ▼
                        ┌──────────── Supabase (ap-south-1, Mumbai) ────────────┐
                        │ Postgres: shops, devices, sync_events, entitlements, │
                        │           ai_usage, backups (metadata)               │
                        │ Row Level Security: a shop sees ONLY its own rows     │
                        │ Edge Functions (Deno/TS):                            │
                        │   sync-push · sync-pull · verify-purchase ·          │
                        │   rtdn-webhook · ai-ask · ai-billscan ·              │
                        │   ai-voice-rescue · backup-url                       │
                        │ Storage: bucket `shop-backups` (private)             │
                        │ Auth: phone OTP (MSG91) → JWT + refresh token        │
                        └──────┬───────────────────────┬───────────────────────┘
                               ▼                       ▼
                  Google Play Developer API     LLM provider (key server-side only;
                  + RTDN (Cloud Pub/Sub topic    swappable: Gemini Flash / GPT-mini
                    → webhook into function)    class; held in function secrets)
```

**Design rules**

1. **The server is a replica and a service provider — never the counter's master.** Every counter operation (finalize, void, stock, dues) completes against local SQLite with the network absent, exactly as v1. Sync, AI, verification, and backup are background/optional lanes hanging off the same seams v1 left (`ISyncService`, `IBackupService`, `ISubscriptionService`).
2. **One transport abstraction.** Client code never calls Supabase types directly. Infrastructure defines `IAngadiServerApi` (typed HttpClient wrapper: base URL + JWT from secure storage, Result<T> in/out, 15 s timeouts, no retries inside a transaction). DEV-2's fake-server harness implements the same interface, so the entire client builds, runs, and is tested **before the Supabase project exists** and at ₹0.
3. **Plain Postgres tables mirroring the SQLite schema** (§4.3) — no exotic features. Exit path is `pg_dump`; the client speaks HTTPS/JSON only. Vendor lock is limited to Auth + Edge Functions rewiring, an accepted, documented trade (§10).
4. **All server secrets live server-side** (Play service-account JSON, LLM API key, MSG91 key) in Edge Function secrets. The APK ships only the Supabase **anon key + project URL**, which are public by design because RLS — not key secrecy — is the security boundary. This closes SRS D2's stated reason for deferring Tier 2.

### 3.2 Project changes (client)

| Project | Change |
|---|---|
| `Angadi.Domain` | **None.** No new entities are needed; sync state lives in Meta, AI DTOs live in Angadi.AI. |
| `Angadi.Application` | Register the real `ISyncService` (was: nothing registered — deliberate v1 stance). New Meta keys (§4.5). New error codes (§5.5, §6.4). `Options`: `ServerOptions` (BaseUrl, SyncEnabled default **off** until opt-in), `AiOptions` (Enabled = the `AiFeatures` flag source of truth on device; MonthlyCap = 50). |
| `Angadi.AI` (**new**, net10.0 class lib, refs Application + Domain only) | `IAiService` + DTOs (§5.2), the aggregate snapshot builder (the privacy enforcement point, §5.3), `NullAiService` (flag-off default), `RemoteAiService` (calls the proxy via `IAiProxyTransport`). |
| `Angadi.Infrastructure` | `Sync/SyncService.cs` (implements `ISyncService`), `Sync/ServerApi.cs` (`IAngadiServerApi`), `Sync/SyncApplier.cs` (pull application through repositories), `Ai/RemoteAiService` transport half, `Subscriptions/EntitlementService.cs` (verified entitlement cache behind the existing `ISubscriptionService` contract), `Backup/CloudBackupService.cs`. |
| `Angadi.Maui` | Opt-in wizard page ("Backup & Sync" in Settings → shop code/OTP flow), Sync health row (last sync, pending count), AI surfaces (Ask card on Reports, Scan button on stock-in, rescue inside the voice Confirm sheet), subscription page shows verified status. DI: register sync/AI/entitlement **last**, same "last registration wins" pattern that put `PlaySubscriptionService` over the stub. |
| `Angadi.Tests` | Sync conflict suite (§4.4 cases), applier invariant tests, AI snapshot PII tests (§5.3), cap tests, entitlement grace tests. All against the fake server — CI never touches the real backend. |
| *(new, outside the .sln)* `server/supabase/` | Migrations (SQL), Edge Functions (TypeScript), `config.toml`, README with the runbook (create project → run migrations → set secrets → deploy functions). Kept in the same repo so the whole system opens in one clone. |

### 3.3 Auth model — phone OTP for the owner, approval code for extra devices

**Decision: the shop account is keyed to the owner's mobile number, authenticated by SMS OTP (Supabase Auth + MSG91). Additional devices join by an approval code shown on an already-signed-in device (or a fresh OTP to the same number). No passwords. No email. No PIN-only accounts.**

Justification, against the alternatives:

| Option | Verdict | Why |
|---|---|---|
| **Phone OTP (chosen)** | ✅ | The phone number is already the shop's public identity (UPI, WhatsApp, the v1 ShopProfile.Phone). OTP is the one login ritual every kirana owner already performs for UPI/banking apps. It gives **account recovery for free** — a lost phone is re-authenticated by SIM, which is exactly the phone-change scenario G1 exists for. Logins are rare; sync afterwards runs on long-lived refresh tokens, so OTP cost is a few rupees per shop per *year*, not per day. |
| Shop code + PIN | ❌ as the account credential | Zero recovery path (forgotten PIN + lost phone = the books are safe in the cloud and unreachable forever — the worst possible outcome for this product). PINs get shared with staff and written on the counter; a 4-digit secret is brute-forceable against an internet endpoint. Acceptable for a future *device lock*, never as the root credential. |
| Email + password | ❌ | Much of the segment has no email they check; passwords get reused and forgotten; recovery flows assume desktop habits. |
| Google sign-in | ❌ as the only method | Many counter tablets use a helper's or a shared Google account; tying the shop's books to it is fragile. May be added later as an *additional* provider, never the root. |

**Flow**

1. **Opt-in (primary device):** Settings → "Backup & Sync" → enter owner mobile → OTP (6-digit SMS) → Supabase Auth issues JWT + refresh token (stored in Android Keystore-backed secure storage). Server creates/links `shops` row keyed by the verified phone. Device registers itself (`devices` row: device Guid, label e.g. "Counter tablet", platform, app version).
2. **First sync** runs the bootstrap of §4.7.
3. **Second device:** install → "Join my shop" → enter the **same owner mobile** → either (a) OTP to that number, or (b) a 6-digit **device-approval code** displayed on the primary device (Settings → Devices → "Add a device"; code lives 10 minutes, single-use). Both paths land the device in the same `shop_id`. The owner can revoke a device (server marks it revoked; its refresh token dies; the device falls back to v1 local-only mode with its local data intact).
4. **Session:** access token short-lived (1 h), refresh token rotated by Supabase Auth, silent refresh in background. Sync/AI/backup calls carry the JWT; RLS + function-side checks scope everything to `shop_id` from the token — a client can never name another shop.
5. **SMS cost control:** OTP sends rate-limited per number server-side (Supabase Auth limits + MSG91), and the pilot runs on MSG91's pay-as-you-go (~₹0.15–0.30/SMS). OTP spend is a monitored tripwire (§9.4), not a surprise.

---

## 4. Sync engine

### 4.1 What syncs

Everything a second device needs to *be* the shop: ShopProfile, Items (catalog), Customers, Bills + BillLines, DuesEntries, StockMoves, Notes, and learned aliases (part of Item state). **What does not sync:** the open bill draft (device-local, as v1), logs, the local Outbox itself, quota Meta counters (replaced by server counting, §6.3), `UpiQrImage` bytes ride inside ShopProfile state — they already travel in v1 backups the same way.

### 4.2 Push / pull cycle (`ISyncService.DrainAsync`)

`DrainAsync` keeps its exact v1 contract — *drain unsent Outbox rows, in order, return how many were delivered* — and a full sync cycle is **push, then pull, then apply**:

1. **Push.** Read up to **100** pending Outbox rows (`SyncState = 0`) in insertion order. POST them to `sync-push` (§4.3). The server inserts each into `sync_events` with `ON CONFLICT (shop_id, event_id) DO NOTHING` and returns the ids it accepted (including already-seen duplicates — acknowledgement is idempotent). Client marks exactly the acknowledged rows `SyncState = 1` **inside one local transaction**. Unacknowledged rows stay pending for the next cycle. `DrainAsync` returns the delivered count, per contract.
2. **Pull.** POST `sync-pull` with the device's cursor (`Meta: sync.cursor` = highest server sequence applied, §4.5). Server returns up to 500 events with `server_seq > cursor` **excluding events this device originated** (it already has them), plus the new high-water mark.
3. **Apply.** `SyncApplier` applies the batch in `server_seq` order, **through the Application repositories inside one `AngadiDb.ExecuteAsync` transaction** — never raw row writes (§4.4). On success, advance `sync.cursor` in the same transaction. On any failure: roll back, cursor unmoved, error logged with correlation id; the batch will be retried next cycle (application is idempotent, §4.4).
4. **Triggers:** after every finalize/void/dues/catalog mutation (fire-and-forget background task, debounced 5 s), on app start, on connectivity regain, and a periodic 30-minute WorkManager-style nudge via MAUI background fetch equivalent. Sync **never runs inside** a billing transaction and never blocks one; offline simply means the Outbox grows (it already does in v1 at zero cost).
5. **Conflict surfacing:** sync is designed so money conflicts cannot occur (§4.4). The one visible merge a human may notice — catalog edits from two devices — resolves by rule, and the Sync health screen shows "last synced … · N changes waiting" so state is never mysterious.

### 4.3 API contract (Edge Functions; all `POST`, JSON, `Authorization: Bearer <JWT>`)

```
POST /functions/v1/sync-push
  req:  { "deviceId": "guid",
          "events": [ { "eventId": "guid",            // = Outbox.Id (idempotency key)
                        "aggregateType": "Bill|Item|Customer|DuesEntry|StockMove|ShopProfile|Note",
                        "aggregateId": "guid",
                        "eventType": "BillFinalized|BillVoided|ItemChanged|…",   // existing DomainEvents names
                        "payloadVersion": 1,
                        "occurredAtMs": 1730…,
                        "payload": { … } } ] }              // the v1 PayloadJson, parsed
  res:  { "accepted": ["guid", …], "serverSeqHigh": 12345 }
  err:  401 (bad/expired token — client refreshes once, then treats as signed-out)
        413 (batch > 100 — client bug; shrink batch)

POST /functions/v1/sync-pull
  req:  { "deviceId": "guid", "afterSeq": 12300, "limit": 500 }
  res:  { "events": [ { "serverSeq": 12301, "eventId": "guid", "originDeviceId": "guid",
                        "aggregateType": "…", "aggregateId": "guid", "eventType": "…",
                        "payloadVersion": 1, "occurredAtMs": …, "payload": { … } } ],
          "highWater": 12480, "hasMore": true }

POST /functions/v1/verify-purchase        (§6)
POST /functions/v1/ai-ask | ai-billscan | ai-voice-rescue   (§5.4)
POST /functions/v1/backup-url             (§7)  → { "uploadUrl": "…", "path": "shopId/…", "expiresInSec": 600 }
```

Server tables (Postgres, RLS on every one, policy: `shop_id = (auth.jwt() ->> 'shop_id')` via a `shop_members` lookup):

```sql
shops          (id uuid pk, owner_phone text unique, name text, created_at timestamptz)
devices        (id uuid pk, shop_id fk, label text, revoked bool, last_seen_at timestamptz)
sync_events    (shop_id fk, event_id uuid, server_seq bigint identity,
                origin_device_id uuid, aggregate_type text, aggregate_id uuid,
                event_type text, payload_version int, occurred_at_ms bigint,
                payload jsonb, received_at timestamptz,
                PRIMARY KEY (shop_id, event_id),
                UNIQUE (shop_id, server_seq))
entitlements   (shop_id pk, is_pro bool, plan text, state text,        -- §6
                current_period_end timestamptz, purchase_token_hash text, updated_at timestamptz)
ai_usage       (shop_id, month text, calls int, PRIMARY KEY (shop_id, month))
backup_objects (shop_id, kind text, storage_path text, size_bytes bigint, created_at timestamptz)
```

### 4.4 Conflict rules (per aggregate)

The governing principle: **anything that is a ledger or a frozen record never conflicts; only descriptive state can, and it resolves deterministically.** Every rule below is testable and has a named test in §11.

| Aggregate | Rule | Reasoning |
|---|---|---|
| **Bill / BillLine** | **Immutable, insert-or-ignore by Guid.** A finalized bill is never edited anywhere (v1: correction = void). A `BillVoided` event flips status on replicas that hold the bill; if the void arrives before the bill (out-of-order pull across devices), the applier stores a pending-void marker and applies it when the bill lands. | Frozen-at-finalize is already the domain law (BILLING_RULES); sync inherits it for free. |
| **Bill numbers** | Numbers stay **unique per device** (v1 `NextBillSequence` is device state and is NOT synced). The server's identity for a bill is its Guid. On a second device, both devices' "A-0042" can exist — the Bills list on a joined device groups/labels by origin device short-tag (e.g. `A-0042 · Counter`) once a shop has >1 billing device; receipts always print the number of the device that made the bill (that's the copy the customer saw). | Renumbering history would falsify printed receipts. Collisions are a display concern, not a data conflict — the Guid never collides. |
| **Stock** | **Ledger replay only.** The denormalised `Item.StockMilli` is *never* synced. Replicas apply `StockMove` events (insert-or-ignore by Guid) and recompute stock as Σ Delta — the same invariant v1's restore sweep already enforces. Concurrent sales of the last unit on two devices both apply; stock goes negative, which v1 already treats as legal ("sold before stock-in"), and the inventory report flags it. | Stock derived from an append-only ledger is order-independent and commutative — no conflict is possible by construction. |
| **Dues** | **Ledger replay only.** `DuesEntry` events (Charge/Payment/Reversal) apply insert-or-ignore; every replica's balance = DuesAging over the full entry set (FIFO aging is a pure function, identical inputs → identical aging on every device). A payment recorded on device B against a charge not yet pulled from device A still applies: entries carry their own CustomerId + BillId references; aging resolves once both are present. | Same construction as stock. H1's void-blocked-by-payments rule is evaluated **locally at void time** against local state; if a payment exists only on another device, the void event still wins by rule order (void is a Bill event; the dues Reversal it carries is part of the same event) — worst case the customer ledger shows the reversal before a late payment, and FIFO aging absorbs it. |
| **Customers** | Descriptive fields (name, phone, address, notes, promised date): **last-writer-wins** by `(UpdatedAtMs, originDeviceId)` total order. Dues balance is *not* a field — it is ledger-derived (above) and excluded from LWW. | A customer's identity data has one human author at a time in practice; LWW with a deterministic tiebreak converges everywhere. |
| **Items / catalog** | Descriptive fields (names, aliases, price, slab, unit, thresholds, barcode, IsActive): **LWW** by `(UpdatedAtMs, originDeviceId)`. Deactivation is a field, not a delete — tombstones propagate naturally. **Learned aliases merge as a union** keyed by normalised text, keeping max confidence per key (they are the shop's voice moat; LWW would throw away one device's learning). Stock excluded (ledger). Two devices assigning the **same barcode** to different items: both events apply locally; the second device to *scan* that code surfaces the existing v1 duplicate-barcode warning flow, and the Sync health screen lists the collision for the owner to resolve — data is never silently overwritten. | Catalog convergence without losing learning; barcode uniqueness is a physical-world constraint the software must surface, not hide. |
| **ShopProfile** | LWW whole-row, **primary device wins ties** (the device that created the shop is flagged primary; only it can change GSTIN/UPI ID — server rejects profile events touching those fields from non-primary devices). | Shop identity fields are money-routing data (UPI ID) and tax identity (GSTIN); they get an owner, not a merge rule. |
| **Notes** | LWW per note by `(UpdatedAtMs, deviceId)`; done-flag is a field. | Trivial stakes; convergence is all that matters. |
| **Quota counters** | Not synced as data. Server derives monthly finalized-bill counts from `BillFinalized` events (§6.3). | Counters are derived state; syncing derived state invites divergence. |

**Idempotency, end to end.** Push is at-least-once; the server dedupes on `(shop_id, event_id)`. Pull application dedupes on event Guid (applier checks a local `applied_events` Meta-backed set / existence of the aggregate row before inserting). Retried pushes, retried pulls, and re-applied batches are all no-ops. There is no exactly-once machinery anywhere, and none is needed.

### 4.5 Local sync state (new Meta keys)

`sync.shop_id`, `sync.device_id` (Guid, minted at first opt-in, persisted), `sync.cursor` (highest applied server_seq), `sync.primary` (bool), `sync.last_ok_ms`, `sync.signed_out` (bool). Plus local Outbox `SyncState` finally gets its second value: 1 = delivered. (Rows are retained 30 days after delivery, then pruned by a housekeeping pass — a v2 addition; v1's "never delete" was a v1 stance, and unbounded growth on a 3 GB phone is the greater evil. Pruning only removes *delivered* rows whose aggregates are fully reflected server-side.)

### 4.6 Offline & failure behaviour

No connectivity → sync silently defers; the Outbox count on the Sync health row is the only sign. Server 5xx → exponential backoff (1 min → 1 h cap), no user-facing error unless sign-in itself fails. A revoked/expired credential → sync pauses, banner offers re-OTP; **billing is never affected** (G5). This mirrors the C2 counter-freeze defence stack: cache-only reads in transactions, bounded waits, fail-closed-to-local.

### 4.7 Bootstrap (first sync of an existing v1 shop)

The v1 Outbox already contains an event for every mutation since the shop's first day (NFR-05) — but not for seed/opening state in a replayable order guarantee. Bootstrap therefore does **not** trust history replay alone:

1. Client uploads a **snapshot bootstrap**: current ShopProfile, all Items (with opening computed from ledger), all Customers, all DuesEntries, all StockMoves, all Bills+Lines from the last **24 months**, as synthetic `Bootstrap…` events with `payloadVersion = 1`, in dependency order (profile → items/customers → bills → entries/moves), chunked through the normal push path (batches of 100, resumable — progress is just the Outbox draining).
2. Server marks the shop `bootstrapped` when the client's `BootstrapComplete` event arrives.
3. A joining device **pulls a snapshot first** (latest backup object, §7, if present — fastest) **or** replays from `server_seq = 0`; either way it then applies the live event tail. Snapshot-then-tail and pure replay are both tested to converge to identical state (§11).

---

## 5. Tier-2 AI (new `Angadi.AI` library)

### 5.1 Shape and the flag

- New project **`Angadi.AI`** (net10.0; references `Angadi.Application` + `Angadi.Domain` only — same inward dependency direction; architecture tests gain a rule: Maui may reference AI, Domain/Application may **not**).
- The whole tier sits behind the **`AiFeatures`** flag: `AiOptions.Enabled` in appsettings, overridable per shop from the server flags row (§10) so it can be killed remotely without an app release. Flag off (or `Angadi.AI` unregistered) → `NullAiService` is injected and **no AI UI renders at all** — the app is byte-for-byte v1 behaviour.
- The LLM provider is chosen **server-side** (Edge Function secret), never in the app. Swapping Gemini ↔ OpenAI ↔ a self-hosted model changes one function; the app contract below never changes. `IAiService` is the swap seam on the client; the proxy is the swap seam for the provider — exactly the arrangement SRS D2 demanded ("a small proxy that holds the key and enforces caps").

### 5.2 `IAiService` contract

```csharp
namespace Angadi.AI;

public interface IAiService
{
    /// <summary>Ask-your-data. Input is an AGGREGATE snapshot built on-device (§5.3) — never raw rows.</summary>
    Task<Result<AiAnswer>> AskYourDataAsync(AiShopSnapshot snapshot, string question, CancellationToken ct = default);

    /// <summary>Supplier-bill photo → DRAFT stock-in lines for owner confirmation. Never posts anything.</summary>
    Task<Result<ScannedSupplierBill>> ScanSupplierBillAsync(ReadOnlyMemory<byte> jpeg, string idempotencyKey, CancellationToken ct = default);

    /// <summary>Voice rescue for a sub-Confirm-band transcript. Sends transcript + ITEM NAMES only.</summary>
    Task<Result<VoiceRescue>> RescueVoiceMatchAsync(string transcript, IReadOnlyList<AiItemCandidate> catalog, CancellationToken ct = default);
}

public sealed record AiAnswer(string Text, AiUsage Usage);                       // Text in the shop's UI language
public sealed record ScannedSupplierBill(IReadOnlyList<ScannedLine> Lines, AiUsage Usage);
public sealed record ScannedLine(string RawText, string? MatchedItemName, decimal? Qty, string? Unit, long? PricePaise, double Confidence);
public sealed record VoiceRescue(IReadOnlyList<AiItemCandidate> Ranked, AiUsage Usage);  // ids + scores, re-checked locally
public sealed record AiItemCandidate(Guid ItemId, string Name);                  // names only — the catalog, not customers
public sealed record AiUsage(int CallsUsedThisMonth, int CallsCap);              // echoed so the UI can show "12 / 50"
```

`AiShopSnapshot` is a closed record of aggregates (§5.3) — there is deliberately **no** free-form `object`/`Dictionary` parameter anywhere in this interface, so a future developer cannot casually stuff raw data into a prompt. The interface is the privacy boundary, enforced by its shape.

### 5.3 Prompt data rules — the no-PII law, enforced in code

**LAW: No customer PII ever enters a prompt. No customer names, no phone numbers, no per-customer dues, no bill-level detail that identifies a person.** Shop business aggregates are fine; people are not.

Enforcement is mechanical, not by reviewer vigilance:

1. **The snapshot is built by one class** — `AiSnapshotBuilder` in `Angadi.AI` — from repository *aggregate queries only*: sales totals by day (30/90 d), top items by qty/value, category totals, stock value + dead-stock list (item names + qty + days), dues **totals by age bucket** (0–7/8–30/30+ days: count of customers + sum — never who), tender mix %. Its input types are aggregate DTOs; it has no reference to `Customer` at all (a project-level test asserts the builder's assembly references exclude the customer repository).
2. **A PII tripwire test** seeds a customer named with a distinctive string + phone, runs every AI path, and asserts neither string appears in the captured request body (against the fake proxy). This test is a release gate.
3. **Voice rescue** sends the transcript (the *shopkeeper's* speech about *products*) plus item names. Transcripts are never customer data; they are product utterances, and they are not stored server-side (function is stateless; provider logging policy in §8).
4. **Supplier-bill scan** is the one image path. A supplier bill is a *business document addressed to the shop*, not customer PII; it may contain the supplier's name/GSTIN. Rules: the image goes device → our function → vision model, is **not persisted** anywhere (no Storage write), and the extracted draft is returned for **mandatory owner confirmation** — AI output never writes stock by itself. The owner can always type the bill instead (v1 flow).
5. Server functions **log counts and error codes only** — never prompt bodies, never images, never answers (same discipline as v1's "pipeline logs counts, never utterance text", NFR-08).

### 5.4 Server proxy (Edge Functions)

```
POST /functions/v1/ai-ask          { snapshot, question, lang }        → { text, usage }
POST /functions/v1/ai-billscan     (image bytes, x-idempotency-key)    → { lines[], usage }
POST /functions/v1/ai-voice-rescue { transcript, catalog[{id,name}] }  → { ranked[{id,score}], usage }
```

Each function: verify JWT → resolve `shop_id` → **atomic cap check-and-increment** (`ai_usage` upsert … `calls = calls + 1 … WHERE calls < 50`, one statement; over cap → `409 AI_CAP_REACHED` and the LLM is never called) → build prompt from a **server-side template** (client data is inserted as data, fenced, never as instructions — prompt-injection posture: a scanned bill or question that says "ignore your instructions" is content, and the output schema is validated JSON before anything returns) → call provider → validate/parse → return. Timeouts: 20 s function-side; provider errors → `502 AI_UNAVAILABLE`.

### 5.5 Cap: 50 calls/shop/month, enforced twice

- **Counting unit:** one user action = one call — one question, one photo, one rescue. A retry carrying the same idempotency key within 24 h returns the cached outcome and does **not** double-count (billscan keys are client-minted per photo; ask/rescue are cheap enough to count per send, with a client-side 2 s debounce).
- **Server is authoritative** (atomic increment above — a modified APK cannot exceed it, same reasoning as §6).
- **Client pre-checks** a local mirror counter for instant UX ("AI: 47 of 50 left this month" on the AI surfaces) and hides AI affordances at the cap instead of letting the user compose a question that will fail.
- **Sub-caps + a rupee ceiling protect the budget.** Photo scans are the expensive call (≈ ₹1–3/image at current vision-mini prices vs ≈ ₹0.1–0.4 for a text call), so scans are sub-capped at **20/month** inside the 50. Even so, a shop maxing every cap (30 text × ₹0.3 + 20 scans × ₹2 ≈ ₹49) would bust the ₹2–5/shop target (Blueprint §11) — so enforcement is also **₹-aware**: the function accumulates estimated spend per shop from provider usage metadata and refuses at a **₹10/shop/month hard ceiling** regardless of call count (₹10 ≈ 6% of the ~₹170 net per shop). Expected real-world use (~10–15 calls/month, mostly text) lands at the ₹2–5 target; the ceiling exists for the abusive/heavy tail. The ceiling is a server config value, reviewed monthly (§10).
- **Fallbacks (G5):** over cap → `AI_CAP_REACHED` error code with te/en strings; the Ask card shows "AI limit reached — your reports still work" and links to the normal reports. Provider down/timeout → `AI_UNAVAILABLE`; voice rescue falls back to v1's manual search; bill scan falls back to manual stock-in. **No AI failure can touch finalize** — AI is invoked only from Reports, stock-in, and the voice Confirm sheet, all outside any transaction.

### 5.6 The three features, concretely

1. **Ask-your-data** (Reports tab card, flag-gated): free-text question (Telugu or English, typed or via the existing speech recogniser) over the §5.3 snapshot — "గత నెలలో ఏ వస్తువు ఎక్కువ అమ్ముడైంది?" / "How much udhaar is older than a month?" Answers are text in the UI language, always ending with the figures' as-of time. It answers from the snapshot only; questions needing data outside it get an honest "I don't have that" (template-enforced), never a hallucinated number — the snapshot's numbers are the only numbers in the prompt.
2. **Supplier-bill scan** (Items → Stock-in → "Scan supplier bill"): photo → draft lines matched against the catalog (fuzzy, on-device after return) with qty/price/confidence → owner edits/confirms → posts through the **existing** `AdjustStockCommand` path (Purchase reason, ledger-written, identical to manual stock-in). Unmatched lines create-item inline, as with unknown barcodes.
3. **Voice rescue**: when the deterministic matcher lands in the **below-Confirm** band (today: "not found" + manual search), and only then, the transcript + catalog names go to the proxy; returned ranked candidates re-enter the **existing Confirm sheet** (owner still taps to confirm — rescue can suggest, never add). If rescue also fails or is off, v1 behaviour is unchanged. The 40-phrase battery gate is untouched: rescue is a fallback for failure cases, not a change to the hot path.

---

## 6. Server-side Play purchase verification

### 6.1 Why (the flaw being closed)

v1 trusts the client: a tampered APK can claim Pro, and the free-tier counter lives and dies with the install. Verification moves the truth to the server: **Google tells our server what the shop bought; the app believes the server's entitlement row (with an offline grace), not itself.**

### 6.2 Flow

1. Purchase happens exactly as v1 (Play Billing via the existing `PlaySubscriptionService`; product ids unchanged).
2. On purchase/restore success, the client POSTs to the function:
   ```
   POST /functions/v1/verify-purchase
     req: { "productId": "angadi_pro_monthly", "purchaseToken": "…", "deviceId": "guid" }
     res: { "isPro": true, "plan": "Monthly", "state": "ACTIVE",
            "currentPeriodEnd": "2026-11-20T…Z", "graceUntil": "…" }
   ```
3. The function calls the **Play Developer API** (`purchases.subscriptionsv2.get` with the service-account credential from function secrets), validates the token belongs to package `com.angadi.pos` and the claimed product, and upserts `entitlements` for the shop (keyed by the JWT's shop, and storing only a **hash** of the purchase token for RTDN matching — raw tokens are not retained).
4. The client caches the returned entitlement (signed payload: the function includes an HMAC over `{shopId, isPro, periodEnd, issuedAt}` with a server secret also embedded… no — the cache is protected differently: the cached record carries the server-issued expiry and is re-fetched on every app start when online; a tampered cache buys at most the days until the next online start, and §6.3's server-side counting removes the durable value of tampering. Deliberately no client-side crypto theatre.)
5. **RTDN (Real-time Developer Notifications):** a Cloud Pub/Sub topic receives Google's notifications (renewed, cancelled, on-hold, in-grace, revoked, expired); a push subscription forwards them to the `rtdn-webhook` Edge Function, which re-fetches the subscription state from the Play API (notifications are hints, never truth) and updates `entitlements`. This catches refunds/revocations that the client would never report.
6. **Entitlement reads:** the app's existing `ISubscriptionService.GetStatusAsync` contract is preserved — cache-only, instant, never blocking the finalize transaction. Its cache is now fed by the entitlement client instead of Play alone: last verified server state + Play local state, server wins when both exist and disagree (`EntitlementService` in Infrastructure, registered after the v1 pieces — last-registration-wins again).

### 6.3 Free-tier counting moves server-side (for synced shops)

For a shop with sync on, the server counts `BillFinalized` events per calendar month from `sync_events`; the entitlement response carries `freeBillsUsed`. On finalize, the client uses **max(local counter, last known server count)** — the local per-month Meta counter (H2/B2, unchanged, still incremented inside the transaction) remains the offline mechanism, and sync reconciles it upward. Effect: reinstall on the same shop no longer resets the 50 for any shop that has ever synced. A shop that never opts into sync keeps the documented v1 behaviour (accepted, disclosed limit — sync is also the fix, and opting in is free).

### 6.4 Grace, states, errors

- **Offline grace:** a verified-Pro shop keeps Pro for **7 days** past the last successful verification (mirrors v1's existing grace), and past `currentPeriodEnd` only while Play reports `GRACE_PERIOD`/`ON_HOLD` maps to **not** Pro for new bills beyond the existing grace (state mapping table is implemented from `subscriptionsv2` `subscriptionState` values verbatim, in the function README).
- **Fail-closed, fail-local:** verification endpoint down → the app keeps its last verified state (Pro stays Pro within grace; free stays free with local counting). Verification failure never blocks a bill that local state allows (the C2 lesson, again).
- New error codes (errors.json, te + en): `VERIFY_FAILED`, `AI_CAP_REACHED`, `AI_UNAVAILABLE`, `SYNC_SIGNED_OUT`, `SYNC_PARTIAL` — each with the shortfall-style args pattern v1 established.

---

## 7. Cloud backup (the cheap win, shipped first)

Sync (§4) already makes a lost phone recoverable *in principle*; backup makes it recoverable *in one tap*, covers shops that joined a second device never, and is the safety net under sync itself during the beta. It reuses v1's FR-37 machinery end-to-end:

1. **What:** the same self-contained DB file v1 export produces (checkpoint → copy → `journal_mode=DELETE`), produced by the existing `IBackupService` pipeline — v2 adds a destination, not a new format.
2. **When:** automatic daily (on app start if the last success is > 24 h old), after every 50 finalized bills, and on demand (Settings → "Back up now"). Runs as an `INoTransaction` command (v1 pattern — user-waiting/long work never holds the DB gate).
3. **Where — two destinations, either or both:**
   - **Supabase Storage** (bucket `shop-backups`, private, RLS-scoped path `<shop_id>/…`): client asks `backup-url` for a signed upload URL (600 s), uploads directly, function records `backup_objects`. Retention: **latest 3 per shop** server-side (older pruned by a scheduled function) — this cap is what keeps free-tier storage viable (§9).
   - **Owner's own Google Drive** ("owner's own storage" — the owner's choice, in their control): the app uploads the same file to a `Angadi Backups` folder in the **shopkeeper's own Drive** via the share-sheet-free Drive API flow (Google account picked by the owner on-device; the file is theirs, on their quota — Google gives them 15 GB free, our cost is ₹0 forever, and "your books live in *your* Drive" is a trust line no competitor's cloud pitch matches). This path works even before/without a Supabase account for backup-only users.
4. **Restore:** new phone → sign in (OTP) → "Restore my shop" → newest Storage backup downloads → **the exact v1 staged restore runs** (integrity_check + invariant sweep incl. stock = Σ ledger and dues = Σ ledger, then atomic swap; tampered/invalid files rejected with live data untouched — H3 machinery, unchanged). If sync is also on, restore-then-sync-tail (§4.7) converges the last hours too.
5. **Precedence rule:** backup is a *snapshot*, sync is the *truth stream*. Restore is offered only on a fresh install / empty shop or after an explicit "replace this device's data" confirmation with the v1 safety-export of current data taken first. The two never fight: after a restore, the device re-registers and pulls events newer than the snapshot.

---

## 8. Data-safety & privacy — what changes in v2, stated plainly

v1's story was absolute: nothing leaves the device except Google speech audio, Play, and the user's own messaging apps. **v2 ends that absoluteness, by the owner's informed opt-in, and this section is the record of exactly how.** Nothing in this section is optional reading for whoever writes the v2 privacy policy — it must be rewritten from this table before v2 ships, and the Play **Data Safety** section changes from "no data collected" to declaring the rows below (a Play policy requirement, and a launch gate in §10).

| Data | Leaves the device in v2? | To where | Why | Protection |
|---|---|---|---|---|
| Shop catalog, bills, stock moves, notes, shop profile | ✅ (sync opt-in) | Supabase Postgres, **ap-south-1 (Mumbai)** | Multi-device + phone-change safety (G1) | TLS in transit, encryption at rest (platform), RLS per shop, JWT-scoped |
| **Customer names, phones, dues entries** | ✅ (sync opt-in) | Same | A dues ledger that doesn't sync is useless on a new phone — this is the shop's own customer data, synced under the shopkeeper's instruction to *his* account | Same as above. **Never** sent to the AI (§5.3), never sold, never used for any other shop, never for ads/lending |
| Full DB file (contains all of the above) | ✅ (if cloud backup on) | Supabase Storage **and/or the owner's own Google Drive** | One-tap restore (G2) | Private bucket + signed URLs; Drive copy lives under the owner's own Google account |
| Supplier-bill photos | ✅ (per AI use) | Our function → vision LLM | Stock-in scan (§5.6) | **Not persisted** on our side; transmitted per call only |
| Voice-rescue transcripts (product utterances) | ✅ (per AI use) | Our function → LLM | §5.6 | Not persisted; contains product speech, not customer data |
| Ask-your-data snapshot | ✅ (per AI use) | Our function → LLM | §5.6 | **Aggregates only, PII-free by construction (§5.3)** |
| Purchase token | ✅ (per purchase) | Our function → Google Play API | Verification (§6) | Only a hash retained server-side |
| Owner's mobile number | ✅ (account) | Supabase Auth + MSG91 (SMS) | OTP login (§3.3) | It's the account key; never displayed to other shops |

**Posture & obligations**

- **Opt-in, off by default.** A v1→v2 upgrade changes nothing until the owner turns on Backup & Sync and completes OTP. The consent screen (Telugu + English) says, in plain words, what leaves the phone — mirroring the table above — before the first byte moves.
- **DPDP Act 2023 framing:** for customer data, the **shopkeeper is the data fiduciary** (it's his customer relationship); Angadi acts as processor on his instruction. The app gives him the tools that framing requires: per-shop isolation, export (he already has full DB export), and deletion (next point).
- **Account deletion is a Play requirement** for apps with accounts: v2 ships (a) in-app "Delete my cloud data" (Settings → Backup & Sync → Danger zone): wipes server rows + storage objects for the shop, signs out all devices, local data untouched unless separately chosen; and (b) a public web deletion-request page (one static page + the same function). **No v2 launch without both.**
- **Server-side data minimisation:** function logs carry counts/codes only (§5.3.5); sync payloads at rest are the shop's data itself (that is the service), access-logged by the platform; no analytics SDK is added to the app in v2 (the v1 "no analytics" stance survives).
- **LLM provider terms:** choose a provider/plan whose terms state prompts are not used for training and are retained ≤ 30 days (or zero-retention where offered); the choice + its terms are recorded in the server README when the key is provisioned (owner action, §10). This matters most for bill-scan images.
- **Region:** Supabase project in **ap-south-1 (Mumbai)** — Indian shop data in India; also the lowest-latency option for the entire user base.

---

## 9. Backend options & costs (₹/month, estimated 2026-10)

The owner decision (§0) fixes the primary: **free tier first**. The table exists so the decision — and its exit ramps — are on the record with numbers. Estimates are planning-grade (state assumptions: busy shop ≈ 40 finalized bills/day; event payload ≈ 1 KB; sync sessions batched per §4.2; backup file ≈ 2–5 MB/shop, latest-3 retention).

### Option A — Supabase (RECOMMENDED, free-tier first)

Postgres + Auth + Edge Functions + Storage, one platform, RLS as the isolation story, PostgREST/HTTPS from the existing .NET HttpClient stack, `supabase-csharp` not even required. The SQLite schema maps to Postgres tables almost 1:1 (Guid PKs, bigint paise/milli, jsonb payloads) — no impedance translation layer to build or test.

| Scale | Plan | ₹/mo | Notes |
|---|---|---|---|
| 100 shops | **Free** | **₹0** | ~50 MB DB, well inside 500 MB; ~0.2M function calls/mo, inside 500k; storage: latest-3 backups ≈ 1–1.5 GB — **at/over the 1 GB free cap → free-tier shops default to latest-1 backup, or Drive (§7) carries backups and Storage carries only the newest** |
| 1,000 shops | Free → **Pro ($25)** | **₹0 → ~₹2,100** | Free DB (500 MB) is the first wall: raw events ≈ 40–60 MB/mo growth before pruning; with §4.5's 30-day delivered-event pruning on devices and server-side event compaction (materialise + drop events older than 90 days once all devices ack) free can stretch surprisingly far — but plan the Pro upgrade at the §9.4 tripwire, don't heroically avoid a ₹2,100 bill with paying shops waiting |
| 10,000 shops | Pro + usage | **~₹8,000–25,000** | DB tens of GB with compaction, function invocations ~6–12M/mo, egress for restores. Per-shop ₹0.8–2.5 against ~₹170 net revenue — margin is never the problem at this scale |

**Free-tier limits that bind, and the exact tripwires (→ first paid upgrade = Pro, $25 ≈ ₹2,100/mo):**

| Resource | Free limit | Tripwire (act at) | Mitigation before paying |
|---|---|---|---|
| Postgres size | 500 MB | **350 MB** (dashboard alert) | Event compaction job; bootstrap snapshots instead of full history for new devices |
| Edge Function invocations | 500k/mo | **350k/mo run-rate** | Batch (already 100/push, 500/pull); sync debounce; don't sync on every keystroke — only on mutations + schedule |
| Storage | 1 GB | **700 MB** | Latest-1 backup on free tier; push Drive (§7) as the default backup home |
| Egress | 5 GB/mo | **3.5 GB/mo** | Restores are rare; snapshot deltas not needed at this scale |
| Auth MAU | 50,000 | not binding (owners, not customers, are users) | — |
| **Project pausing** | Free projects **pause after ~7 days of inactivity** and need a manual/API unpause | any week with zero syncs (pre-launch dev, pilot lull) | Any active shop syncing daily keeps it awake; the runbook documents the 1-click unpause; **this is the #1 operational annoyance of free tier and the honest reason the first ₹2,100 is well spent the moment real shops depend on it** |
| OTP SMS (MSG91, separate) | pay-as-you-go | **> ₹500/mo spend** | Rate limits per number; logins are rare by design (§3.3) |

**Why Supabase over Firebase (the decision the owner delegated):** (1) **Data model** — Angadi is ledgers and relational aggregates; ask-your-data snapshots and server-side bill counting are SQL-shaped. Firestore would force denormalised counters and per-read billing on exactly the chatty sync traffic v2 creates. (2) **Cost shape** — Firebase Blaze is per-operation: a sync bug or a chatty client becomes a billing incident; Supabase Pro is a flat $25 with headroom — for a solo owner, *predictable beats theoretically-cheaper*. (3) **.NET client story** — both are "just HTTPS" from MAUI, but PostgREST + SQL semantics mirror the app's existing repository thinking; Firebase has no first-party .NET SDK for Firestore in a MAUI app (REST/gRPC community paths only). (4) Firebase's genuine strengths — realtime listeners, the best phone-auth free quota — solve problems v2 doesn't have (sync is batched by design; OTP volume is tiny). Firebase remains the documented fallback if Supabase's free tier or pricing changes adversely (the transport abstraction in §3.1 is what makes that a migration, not a rewrite).

### Option B — Firebase (documented alternative)

Spark free → Blaze pay-as-you-go. ₹0 at 100 shops; 1,000 shops ≈ ₹1,500–6,000/mo (Firestore ops + Functions + Auth, traffic-dependent); 10,000 shops ≈ ₹15,000–60,000/mo with real variance risk. Rejected as primary for the reasons above; not blocked by anything in the client design.

### Option C — Azure App Service + Azure SQL (documented alternative)

The most .NET-native option (backend in C#, Play verification in the owner's strongest language): App Service B1 ≈ ₹1,100/mo + Azure SQL (Basic/S0) ≈ ₹1,200–2,500/mo → **₹2,500–4,000/mo from day one, at 100 shops**, ~₹15,000–40,000/mo at 10,000. Zero free tier → violates the owner's free-first decision now; becomes attractive if the team ever wants a single-language stack and is already revenue-positive. Also the natural home if Supabase is ever outgrown (the Postgres data moves; the API contract in §4.3 is implementable 1:1 in ASP.NET Core).

### Option D — Self-hosted VPS (documented alternative)

Hetzner/Vultr-class VPS (4 GB) ≈ ₹900–1,800/mo running self-hosted Supabase or Postgres + a small API; cheapest per rupee at 10,000 shops (≈ ₹3,000–6,000/mo all-in) — but paid from day one, and **the owner becomes the pager**: patching, TLS, backups of the backup system, uptime at 8 AM when shops open. Rejected as primary on operational burden for a solo owner; viable later with revenue and an ops habit.

**Recommendation restated:** Option A. ₹0 until shops pay; first upgrade ₹2,100/mo flat at a named tripwire; every alternative stays reachable because the client depends on an HTTPS contract, not on Supabase SDKs.

---

## 10. Rollout

1. **Flags (all default OFF, remotely flippable per shop):** `SyncFeatures`, `CloudBackup`, `AiFeatures`, `ServerEntitlement`. Flags are served from a `shop_flags` row fetched at app start and cached; a flag can be turned off server-side for a shop (or globally) without an app release — the kill-switch discipline for a solo-operated backend.
2. **Order of shipping (each step independently valuable):**
   - **Step 1 — Cloud backup** (§7): smallest surface, kills the scariest v1 risk, exercises auth + storage + restore. (Drive path can ship even before Supabase exists.)
   - **Step 2 — Purchase verification** (§6): server truth for money; quiet, invisible, de-risks revenue before sync widens the user base.
   - **Step 3 — Sync** (§4): the big one; beta-gated hardest.
   - **Step 4 — Tier-2 AI** (§5): last, because it spends money per call and needs the cap machinery proven under Steps 1–3's auth.
3. **Beta cohorts:** internal devices → **5 friendly shops** (owner-recruited, WhatsApp support channel, 2 weeks) → **50 shops** → general availability per feature. A feature graduates only when its §11 criteria pass **in the cohort**, not just in tests.
4. **Owner actions (Sai's taps — standing boundary: accounts, keys, and spend are his):** create the Supabase project (Mumbai region), run the migrations from `server/supabase/`, create Play service account + grant the API access, provision Pub/Sub topic for RTDN, MSG91 account + sender id, LLM provider key, set function secrets, host the account-deletion page, and approve the Pro upgrade when a §9.4 tripwire fires. **None of these are delegable to agents, and the build plan (DEV-2) is sequenced so every client feature is built and tested against the fake server before any of them are needed.**
5. **Docs that must move with the code (repo rule — WHAT/ISSUE/WHY, same push):** `docs/PRIVACY_POLICY.md` rewritten from §8; `docs/RELEASE.md` gains the Data Safety + account-deletion gates; `docs/RUNBOOK.md` gains backend ops (unpause, tripwire checks, secret rotation); `docs/CLAUDE_STATUS.md` new rows V2-1…V2-4; `docs/AGENT_FIX_LOG.md` entries opened for each v2 workstream before code lands; `docs/CHANGELOG.md` per push.

---

## 11. Acceptance criteria (testable; each maps to a named test or a cohort check)

**Sync (§4)**
- AC-S1: Two devices, one shop: finalize a cash bill on A → after one sync cycle it appears on B with identical lines, totals, and tax splits; stock on B reflects the sale (Σ ledger equal on both).
- AC-S2: Concurrent edits: A and B both sell the last unit of an item offline; after sync both bills exist, stock = −1 on both devices, no event lost or duplicated (server event count = union of both Outboxes).
- AC-S3: Push retry after a simulated server 500 mid-batch: re-push delivers the same events; server holds each `event_id` exactly once; local pending count reaches 0.
- AC-S4: Dues: charge on A, payment on B (offline, before seeing the charge); after sync, both devices' FIFO aging and balances are paise-identical.
- AC-S5: Catalog: price edit on A and a learned alias earned on B converge on both (price = LWW winner; learned aliases = union with max confidence).
- AC-S6: Bill void on A propagates to B; a void pulled before its bill still results in a voided bill once both events are applied.
- AC-S7: Bootstrap equivalence: a fresh device reaches state identical (per-table row counts + ledger sums + dues balances) via snapshot+tail and via pure replay from seq 0.
- AC-S8: Offline week: device bills offline 7 days (≥ 200 bills), then syncs; zero lost events, finalize latency during the week unchanged vs v1 (NFR-02 spot-check on a 3 GB device).

**Tier-2 AI (§5)**
- AC-A1: PII tripwire (§5.3.2) passes in CI for all three AI paths; the seeded customer name/phone appears in no captured request.
- AC-A2: Cap: the 50th call in a month succeeds; the 51st returns `AI_CAP_REACHED` and the provider mock records **no** 51st invocation; scans stop at 20; the ₹ ceiling refuses even below call caps when spend metadata says so.
- AC-A3: Provider down (mock 500/timeout): Ask shows `AI_UNAVAILABLE`; voice rescue degrades to v1 manual search; bill scan degrades to manual stock-in; a finalize run during the outage is byte-identical in behaviour to v1.
- AC-A4: Bill scan of the fixture supplier bills produces draft lines with ≥ the agreed field accuracy on the test set, and **zero** stock writes occur before owner confirmation (ledger row count unchanged until confirm).
- AC-A5: With `AiFeatures` off, no AI UI is reachable and no AI network call is attempted (fake-server log empty) across a full UAT pass.

**Purchase verification (§6)**
- AC-P1: A real purchase in Play's closed test verifies server-side within 60 s of purchase; entitlement row matches Play's `subscriptionsv2` state.
- AC-P2: Refund/revoke in Play Console (test track) → RTDN updates the entitlement; the app shows not-Pro on next online start, and keeps Pro offline only within the 7-day grace.
- AC-P3: Tampered local cache (Pro flag flipped in the DB on a rooted test device) does not survive the next online app start; server state wins.
- AC-P4: Reinstall on a synced shop: free-tier usage reflects the server's month count — the 51st bill of a month already at 50 server-side is blocked after sign-in + first sync.
- AC-P5: Verification endpoint down for 24 h: Pro shops inside grace bill normally; no finalize is blocked by any verification call (C2-pattern test).

**Cloud backup (§7)**
- AC-B1: Fresh install → OTP sign-in → Restore: resulting DB passes the v1 invariant sweep (stock = Σ ledger, dues = Σ ledger) and row counts match the source device.
- AC-B2: A corrupted/truncated backup file is rejected by staged restore with the fresh install's (empty) data untouched.
- AC-B3: Automatic backup fires after 50 bills / 24 h without any user action and without delaying a finalize by more than the v1 export's own time (background, `INoTransaction`).

**Privacy / platform gates (§8, §10)**
- AC-G1: Privacy policy (hosted) and Play Data Safety match §8's table line-for-line; in-app deletion removes server rows + storage objects within 24 h (verified by direct DB check) — **all three are GA blockers, not follow-ups.**
- AC-G2: With sync never opted-in, a network capture of a full UAT pass shows zero calls to the v2 backend.

---

## 12. Risks

| # | Risk | Likelihood / impact | Mitigation (in this spec) |
|---|---|---|---|
| R1 | **Free-tier project pauses** during a pilot lull → sync/backup silently stale when a shop needs restore | Medium / High | §9.4: daily-sync shops keep it awake; runbook unpause; Sync health screen shows staleness honestly; **Pro upgrade is the real fix and is tripwire-triggered, not discretionary** |
| R2 | Sync merge bug corrupts a shop's books (worst-case trust event for this product) | Low / Critical | Ledger-replay design (§4.4) makes money paths deterministic; AC-S1…S8; restore-from-backup is always one step behind any bad merge; beta cohorts (§10) before GA |
| R3 | OTP friction/cost: owners who can't receive SMS (DND, number changed) locked out of *cloud* features | Medium / Medium | Local app never locks (§4.6 — only cloud features pause); device-approval-code path (§3.3) covers number-change with an existing device; support runbook for manual re-link after identity proof |
| R4 | LLM provider price/terms change, or prompts retained against policy | Medium / Medium | Provider is a server-side secret swap (§5.1); ₹ ceiling (§5.5) caps blast radius; provider terms recorded at provisioning (§8); `AiFeatures` kill-switch (§10) |
| R5 | Play policy: Data Safety mismatch or missing account deletion → rejection/suspension | Medium / High | §8 + AC-G1 as GA blockers; deletion ships with Step 1, not "later" |
| R6 | DPDP Act obligations grow (breach notification, consent records) | Low / Medium | Data minimisation (§8), region pinning, consent captured at opt-in with timestamp server-side; revisit at 1,000 shops |
| R7 | Solo-operator bus factor on a now-*operated* backend (v1 needed no ops; v2 does) | Medium / Medium | Managed platform (Supabase) over VPS (§9 Option D rejected exactly for this); runbook; flags/kill-switches; alerts on tripwires |
| R8 | Bill-number display collisions confuse a two-device shop's customers | Medium / Low | §4.4 device-tag display; receipts unchanged from the issuing device; explained in the Telugu user guide |
| R9 | A "free" tier changes terms (Supabase pricing/policy shift) | Low / Medium | §3.1 rule 3 (plain Postgres + HTTPS contracts) keeps exit = migration; Firebase/Azure documented (§9) |
| R10 | AI answers with wrong numbers erode trust faster than no AI | Medium / Medium | Snapshot-only facts + template honesty (§5.6.1); answers show as-of time; AI never writes anything (scan drafts need confirmation; rescue needs a tap) |

---

*End of V2_SPEC.md. Companion: `V2_BUILD_PLAN.md` (DEV-2) sequences the file-level work and the fake-server harness. This spec changes no v1 behaviour until the owner provisions the backend (§10.4) and the flags turn on (§10.1).*
