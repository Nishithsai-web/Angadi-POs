# V2_BUILD_PLAN.md — Angadi v2, file-level implementation plan

**WHAT:** The executable build plan for Angadi v2 — sync engine, `Angadi.AI` (Tier-2 LLM),
server-side purchase verification (client half), and automatic cloud backup — written at file
level: exact paths, new-vs-edit, interface signatures, named owner per file, tests by name,
CI deltas, and wave order. Agents (Muse dev/fix agents + Claude Code) build from this file.

**ISSUE:** v1 shipped deliberately without these four capabilities, each behind a seam that
exists in code today: `ISyncService` is declared in
`src/Angadi.Application/Platform/PlatformServices.cs` and **registered nowhere**; the Outbox is
append-only (`IOutbox` has only `AppendAsync`) so nothing can drain it; Tier-2 AI exists only as
Blueprint §11 design (architecture audit searched the whole tree: no `Angadi.AI`, no
`IAiService`); Play Billing entitlement is client-trusted — `IQuotaStore`'s own doc comment
says *"Reinstall/factory reset still wipes it (documented; v2 moves the entitlement to a
server)"*; backup is a manual share-sheet export only.

**WHY this plan, this shape:** The owner ordered v2 completed on 2026-10-20 ("Complete v2
too") with the standing constraints: no paid resources by default, maintainable in Visual
Studio by one person, enterprise bar (warnings-as-errors, named tests, CI green observed or
marked UNVERIFIED). So every workstream is split so that **Wave 0 builds and fully tests with
no server, no API keys, and no spend** — against in-process fakes — and only the thin transport
/ provider adapters in Wave 1 wait on the owner's backend + key decisions (see
`docs/AGENT_FIX_LOG.md` and DEV-1's `V2_SPEC.md` for backend options and ₹ costs).

**OWNER DECISION (2026-10-20 13:30 EDT): the v2 backend is FREE TIER FIRST — Supabase or
Firebase, ₹0 until real shops onboard.** Consequences for this plan: every client-side
transport/endpoint targets that backend's plain **HTTPS APIs** (Supabase PostgREST / Edge
Functions, or Firebase Firestore REST / Cloud Functions) with `HttpClient` — **no
Azure-specific service, SDK, or assumption appears anywhere in the client plan**; the
`ISyncTransport` / `IPurchaseVerifier` / AI-provider abstractions and the fake-server harness
stay exactly as designed, so the client builds and fully tests with zero spend and zero
accounts, and the free-tier project is only needed when Wave 1 adapters are switched on.
Supabase-vs-Firebase is DEV-1's call to justify in `V2_SPEC.md`; the client plan below is
written so either drops in behind the same ports.

**Grounding:** file reads at repo HEAD `94a9684f` on 2026-10-20 (`PlatformServices.cs`,
`Persistence/Repositories.cs`, `Persistence/QuotaStore.cs`, `Infrastructure/
DependencyInjection.cs` read in full; structure per the architecture audit of the same tree).
Items a builder must re-confirm before coding are marked **CONFIRM** inline; anything not read
this session is marked **UNVERIFIED**.

---

## 0. Ground rules (binding on every v2 change)

1. **Layer rules do not move.** Domain ← Application ← Infrastructure / Angadi.AI ← Maui.
   New projects/ports follow the v1 pattern: ports (interfaces) live in **Application**,
   implementations in Infrastructure or Angadi.AI, platform code in the Maui head.
   `tests/Angadi.Tests/Architecture/LayerRulesTests.cs` is extended, never weakened.
2. **DI is explicit.** No assembly scanning (ADR-2 revised). Every registration is a line in
   a `DependencyInjection.cs`. Platform/last registrations win — the C2 precedent
   (`PlaySubscriptionService` replaces the stub because `AddAndroidPlatform` runs last).
3. **One owner per file.** Shared/serialized files have exactly ONE named owner for all of v2
   (table in §1). Nobody else edits them; they request the change from the owner.
   `src/Angadi.Domain/Voice/TeluguParser.cs` (if that is its path — **CONFIRM**) is **not
   touched by v2 at all**: voice rescue hooks the pipeline, never the parser.
4. **Options pattern.** New settings are a new section in
   `src/Angadi.Maui/Resources/Raw/appsettings.json` bound to a new options class in
   `src/Angadi.Application/Common/Options.cs`, exactly like Voice/Billing/Insights/Support/Dues.
5. **Errors are catalog codes.** New failures get codes in `errors.json` (te + en) in the same
   push; a test already enforces every code exists in both languages.
6. **Telugu only in JSON resources.** No Telugu literals in C# — including prompts (prompt
   templates that contain Telugu live in JSON, §3).
7. **Every fix-site and new seam carries the `// FIX(<ID>, date, agent): Flaw/Fix/Why`
   comment** and an entry in `docs/AGENT_FIX_LOG.md` (protocol in that file).

## 1. File-ownership map (serialized files)

| File | Sole v2 owner | Who needs changes in it |
|---|---|---|
| `src/Angadi.Maui/MauiProgram.cs` | **INTEGRATOR** | W1 (Sync options binding), W2 (`AddAngadiAi` call + Ai binding), W4 (cloud-backup trigger registration) |
| `src/Angadi.Infrastructure/DependencyInjection.cs` | **INTEGRATOR** | W1 (sync registrations), W2 (IMetaStore), W3 (verifier), W4 (cloud backup) |
| `src/Angadi.Application/DependencyInjection.cs` | **DEV-AI** | W2 only (DisabledAiService default registration) |
| `src/Angadi.Maui/Resources/Raw/appsettings.json` | **INTEGRATOR** | W1 `Sync`, W2 `Ai`, W4 `CloudBackup` sections — workstreams supply the JSON, INTEGRATOR applies |
| `src/Angadi.Application/Resources/errors.json` (**CONFIRM** exact path) | **INTEGRATOR** | W1 `SYNC_*`, W2 `AI_*`, W3 `PURCHASE_VERIFY_*`, W4 `CLOUD_BACKUP_*` — appended per workstream, applied by INTEGRATOR |
| `src/Angadi.Maui/Areas/SetupAndSettingsPages.cs` | **INTEGRATOR** | W3 (verification-pending state), W4 (cloud-backup settings UI) |
| `src/Angadi.Maui/App.cs` | **INTEGRATOR** | W1 (drain on start/sleep), W4 (backup check on sleep) — both ride the existing `TakeSafetySnapshotCommand` background pattern |
| `src/Angadi.Infrastructure/Persistence/AngadiDb.cs` | **DEV-SYNC** | W1 only (schema 3 → 4, §2) |
| `Angadi.sln`, `Directory.Packages.props` | **DEV-AI** | W2 (new project + provider package pins, central package management) |
| `tools/coverage_gate.py`, `.github/workflows/ci.yml` | **INTEGRATOR** | §6 |

Workstream owners: **DEV-SYNC** (W1), **DEV-AI** (W2), **DEV-VERIFY** (W3), **DEV-BACKUP**
(W4), **INTEGRATOR** (composition root + shared files + CI). Claude Code may take any
workstream; the owner column is about the *file*, not the tool — claim the entry in
`docs/AGENT_FIX_LOG.md` (`IN-FIX` + agent name) before starting.

---

## 2. W1 — SYNC ENGINE (DEV-SYNC)

**Flaw → Fix → Why.** *Flaw:* Outbox rows accumulate forever (`SyncState` 0 = pending);
`ISyncService` is registered nowhere, so a lost/stolen tablet is total business-data loss and
multi-device is impossible — the market analysis shows every serious rival syncs.
*Fix:* drain the Outbox through a transport port, pull remote events back, apply them with
per-aggregate conflict rules — all behind the existing `ISyncService` signature
(`Task<Result<int>> DrainAsync`), so no v1 caller changes. *Why this shape:* the seam was
designed for exactly this (M4); building the engine against an in-process fake server first
means the whole engine is test-proven before the owner spends ₹1 on hosting.

### 2.1 New ports — `src/Angadi.Application/Persistence/SyncPorts.cs` (NEW, DEV-SYNC)

`IOutbox` stays append-only (v1 contract); reading/marking is a new port.

```csharp
namespace Angadi.Application.Persistence;

/// <summary>One pending Outbox row, in drain order. Id is the Outbox row's primary key (CONFIRM in Rows.cs).</summary>
public sealed record OutboxEntry(
    long Id, string AggregateType, Guid AggregateId, string EventType,
    string PayloadJson, int PayloadVersion, long CreatedMs);

/// <summary>v2: the read side of the Outbox. Append stays on IOutbox (v1 contract unchanged).</summary>
public interface IOutboxStore
{
    /// <summary>Pending rows (SyncState = 0), oldest first, at most <paramref name="max"/>.</summary>
    Task<IReadOnlyList<OutboxEntry>> PendingAsync(int max, CancellationToken ct = default);
    /// <summary>Marks exactly these rows synced (SyncState = 1). Called only after server ack.</summary>
    Task MarkSyncedAsync(IReadOnlyList<long> ids, CancellationToken ct = default);
}

/// <summary>One event received from the server. ServerSeq is the server's global order for this shop.</summary>
public sealed record RemoteEvent(
    long ServerSeq, Guid EventId, Guid OriginDeviceId, string AggregateType, Guid AggregateId,
    string EventType, string PayloadJson, int PayloadVersion, long OccurredMs);

public sealed record PushReceipt(IReadOnlyList<long> AckedIds, IReadOnlyList<RemoteEvent> AlsoHave);
public sealed record PullPage(IReadOnlyList<RemoteEvent> Events, long HighWaterSeq, bool HasMore);

/// <summary>
/// The ONLY sync dependency on the outside world. Wave 0 tests use FakeSyncServer;
/// Wave 1 ships HttpSyncTransport against the owner's backend (DEV-1 spec).
/// </summary>
public interface ISyncTransport
{
    Task<Result<PushReceipt>> PushAsync(Guid deviceId, IReadOnlyList<OutboxEntry> batch, CancellationToken ct = default);
    Task<Result<PullPage>> PullAsync(Guid deviceId, long afterSeq, int max, CancellationToken ct = default);
}
```

Also in this file: `SyncOptions` goes to `Options.cs` (edit, DEV-SYNC):
`Enabled` (default **false** in v2.0 — sync is opt-in per shop until the backend exists),
`PushBatchSize` (100), `PullPageSize` (200), `BaseBackoffSeconds` (30), `MaxBackoffMinutes` (30).

### 2.2 Infrastructure — `src/Angadi.Infrastructure/Sync/` (all NEW, DEV-SYNC)

| File | Contents |
|---|---|
| `SyncService.cs` | `ISyncService` implementation. `DrainAsync`: loop { read batch → `PushAsync` → `MarkSyncedAsync(acked)` } until empty or failure; then pull pages from cursor → `ConflictApplier.ApplyPageAsync` → advance cursor. Returns total delivered (the interface contract). One drain at a time (SemaphoreSlim) — triggers may overlap. |
| `SyncState.cs` | Meta-key state, mirroring `QuotaStore` exactly (same `db.RunAsync` + `MetaRow` style): `sync.device_id` (Guid minted once), `sync.pull.server_seq`, `sync.last_success_ms`, `sync.fail.count`, `sync.fail.last_ms`. |
| `SyncBackoff.cs` | Pure, unit-testable: `DelayFor(failCount)` = min(30s × 2^n, 30 min) + jitter 0–20% (jitter source injected so tests are deterministic). `DrainAsync` refuses to run before `last_ms + delay` and returns 0 — a refused drain is success-with-zero, never an error. |
| `ConflictApplier.cs` | Applies one `PullPage` inside ONE `IUnitOfWork.ExecuteAsync` transaction (commit only on success; cursor advances in the same transaction — a crashed apply never skips events). Dedupe first: skip `EventId`s already in `AppliedEvent` (schema change below) and skip events whose `OriginDeviceId` == own device id. |
| `HttpSyncTransport.cs` | **Wave 1 only.** `HttpClient` implementation of `ISyncTransport` against the free-tier backend's HTTPS endpoints (`SyncOptions.BaseUrl` = the Supabase/Firebase project URL from DEV-1's spec + anon/device token header). No vendor SDK — PostgREST/REST + JSON only, so swapping Supabase↔Firebase is a config + payload-shape change inside this one file. Written last, thinnest file in the workstream. |

**Conflict rules (in `ConflictApplier`, one method per aggregate — this is the contract the
fake server and the real server must both honour):**
- **Bill (append-only history):** finalized/voided bills are immutable. Apply = insert if
  absent; if present, only a `BillVoided` event may change `Status`. Never LWW on money fields.
- **StockMove (append-only):** insert if `EventId`/move Guid unseen. **`Item.Stock` is never
  merged — after applying moves it is recomputed as Σ ledger deltas** (the v1 invariant the
  inventory report already checks).
- **DuesEntry (append-only):** same rule; `Customer.DuesBalance` recomputed as ledger Σ.
- **Item / Customer / Note (mutable):** last-writer-wins on `(UpdatedAt ms, tie-break:
  lexicographically larger OriginDeviceId)`. Loser's values are dropped, not merged.
- **ShopProfile:** LWW as above, except bill-number sequence: on apply, local
  `NextBillSequence` = max(local, remote) — numbers must never re-issue (per-device prefix
  remains the real guard; v2 keeps v1 prefixes).

### 2.3 Edits to existing files

| File | Edit | Owner |
|---|---|---|
| `src/Angadi.Infrastructure/Persistence/Rows.cs` | **CONFIRM** `OutboxRow` PK shape first. No new columns needed (SyncState exists). | DEV-SYNC |
| `src/Angadi.Infrastructure/Persistence/AngadiDb.cs` | `SchemaVersion` 3 → 4: new table `AppliedEvent(EventId Guid PK, ServerSeq long)` via the existing `CreateTable` migration path (additive — the `.pre-update` safety copy already covers it). | DEV-SYNC (sole owner) |
| `src/Angadi.Infrastructure/Persistence/Outbox.cs` (class `Outbox`, registered as `IOutbox` in Infra DI — confirmed by read) | Add `OutboxStore : IOutboxStore` over the same table. | DEV-SYNC |
| `src/Angadi.Infrastructure/DependencyInjection.cs` | Register `IOutboxStore`, `ISyncService → SyncService`, `ISyncTransport` (fake in tests; Http in Wave 1 via `SyncOptions.Enabled` + BaseUrl — when disabled, register `NullSyncTransport` in the same file so `DrainAsync` returns 0 cleanly). | INTEGRATOR |
| `src/Angadi.Maui/App.cs` | Fire-and-forget `DrainAsync` beside the existing safety-snapshot calls in `OnStart`/`OnSleep`, guarded by `SyncOptions.Enabled`. | INTEGRATOR |
| `src/Angadi.Maui/Platforms/Android/SyncWorker.cs` (**CONFIRM** Platforms/Android layout) | NEW: WorkManager `Worker` that resolves `ISyncService` from the MAUI DI and drains; scheduled periodic (6 h, network-connected constraint) + expedited on connectivity regain. | DEV-SYNC |
| `src/Angadi.Maui/Angadi.Maui.csproj` | WorkManager package pin (Maui head pins its own packages — v1 precedent). | INTEGRATOR |

### 2.4 Tests (NEW folder `tests/Angadi.Tests/Sync/`, DEV-SYNC)

Fake first: `FakeSyncServer.cs` — an in-process `ISyncTransport` with an in-memory event log
implementing the §2.2 conflict contract server-side, plus chaos knobs (`FailNextPushes(n)`,
`DuplicateDeliveries`, `ShufflePages`). Two-device tests build **two AppHost instances over
two temp AngadiDb files sharing one FakeSyncServer** — device A and device B.

- `SyncOutboxDrainTests` — drain marks **only acked** ids; partial ack leaves the rest
  pending; order preserved; empty outbox → returns 0, no transport call.
- `SyncBackoffTests` — delay sequence 30/60/120… capped at 30 min; jitter within bounds;
  success resets fail count; drain inside backoff window is a no-op returning 0.
- `SyncTwoDeviceTests` — bill finalized on A appears on B with identical totals; stock on B
  equals Σ moves from both devices after cross-sales; udhaar charge on A + payment on B →
  both devices' dues equal ledger Σ; item price edited on both → LWW winner on both.
- `SyncPullApplyTests` — mid-page failure rolls back the whole page and the cursor does not
  advance; redelivered page (duplicates) changes nothing (AppliedEvent dedupe); own-device
  events are skipped.
- `SyncDisabledTests` — `SyncOptions.Enabled = false`: AppHost resolves, `DrainAsync` returns
  0, zero transport calls, zero Meta writes (v1 behaviour unchanged).

### 2.5 Wave order inside W1

Wave 0: ports → OutboxStore → SyncState/Backoff → FakeSyncServer → SyncService +
ConflictApplier → all tests green → Android worker + App hooks (compile-tested; on-device run
is UAT). Wave 1: `HttpSyncTransport` only, after DEV-1 backend exists.

---

## 3. W2 — `Angadi.AI` PROJECT / TIER-2 LLM (DEV-AI)

**Flaw → Fix → Why.** *Flaw:* Tier-2 AI (ask-your-data, supplier-bill scan, low-confidence
voice rescue) exists only as Blueprint §11 design; there is no project, no port, no flag.
*Fix:* a new outer-ring project `src/Angadi.AI` implementing an `IAiService` port declared in
Application, behind `AiOptions.Enabled` (default **false**), with a disabled default that
makes flag-off behaviour **byte-identical to v1**. *Why:* the per-bill hot path must stay
deterministic and free (locked decision) — AI is an optional sidecar the shop can never
depend on for billing, capped at 50 calls/shop/month (~₹2–5/shop at expected usage, <3% of
the ₹200 price).

### 3.1 Where the project sits (layer rules)

```
Domain  ←  Application  ←  Infrastructure  ←  Maui
                    ↑  ←  Angadi.AI (NEW)  ←  ┘ (Maui references both outer projects)
```

- `src/Angadi.AI/Angadi.AI.csproj` (NEW, DEV-AI): `net10.0`; references **Application only**
  (Domain comes transitively). It is a sibling of Infrastructure in the outer ring — it may
  never be referenced by Domain or Application, and it never references Infrastructure
  (persistence goes through Application ports, §3.4) or Maui.
- `Angadi.sln` (edit, DEV-AI): add the project beside Infrastructure.
- `tests/Angadi.Tests/Architecture/LayerRulesTests.cs` (edit, DEV-AI): add rules —
  *Angadi.AI is referenced by nothing except Maui + Tests; Angadi.AI references only
  Application.* The v1 rules must keep passing unchanged.

### 3.2 The port — `src/Angadi.Application/Platform/AiServices.cs` (NEW, DEV-AI)

Ports live in Application (the `ISubscriptionService` precedent — implementations get swapped
at composition). DTOs here are deliberately aggregate-only (§3.5).

```csharp
namespace Angadi.Application.Platform;

/// <summary>
/// Tier-2 AI seam (Blueprint §11). v1 behaviour = DisabledAiService. Contract for every
/// implementation: NEVER called from the finalize path; failures are ordinary Results
/// (AI_DISABLED / AI_CAP_REACHED / AI_UNAVAILABLE / AI_BAD_RESPONSE), never exceptions
/// across the boundary; every call is counted by the cap decorator before it is sent.
/// </summary>
public interface IAiService
{
    /// <summary>"How much udhaar is stuck over 30 days?" over aggregates only — no customer rows ever leave the device.</summary>
    Task<Result<AiAnswer>> AskYourDataAsync(AggregateSnapshot snapshot, string question, CancellationToken ct = default);

    /// <summary>Photo of a supplier bill → draft items (name/qty/price) for the owner to confirm. Nothing auto-saves.</summary>
    Task<Result<SupplierBillDraft>> ScanSupplierBillAsync(ReadOnlyMemory<byte> imageJpeg, CancellationToken ct = default);

    /// <summary>Low-confidence voice rescue: transcript + the shop's own item names → ranked candidates. Advisory only.</summary>
    Task<Result<VoiceRescue>> RescueVoiceAsync(string transcript, IReadOnlyList<string> candidateItemNames, CancellationToken ct = default);
}

public sealed record AiAnswer(string Text, IReadOnlyList<string> FollowUps);
public sealed record SupplierBillDraft(IReadOnlyList<SupplierBillLine> Lines, string? SupplierName);
public sealed record SupplierBillLine(string Name, long QtyMilli, long PricePaise, decimal Confidence);
public sealed record VoiceRescue(IReadOnlyList<string> CandidateNames, string? Note);
```

### 3.3 The PII guard — `AggregateSnapshot` (the type IS the guard)

Ask-your-data never builds a prompt from entities. The only input type is:

```csharp
/// <summary>
/// The ONLY thing ask-your-data may send. Constructed by AggregateSnapshotBuilder from
/// repositories — totals, counts and age buckets; there is deliberately NO field that can
/// hold a customer name, a phone number, or a per-person due. Item display names are the
/// shop's own catalogue (not personal data) and are included so answers can name items.
/// </summary>
public sealed record AggregateSnapshot(
    DateOnly FromDay, DateOnly ToDay,
    long TotalSalesPaise, long CashPaise, long UpiPaise, long UdhaarPaise,
    int BillCount, int VoidCount,
    IReadOnlyList<SlabTotal> BySlab,                 // GST slab → taxable + tax (paise)
    IReadOnlyList<ItemTotal> TopItems,                // item name + qty milli + value paise
    long DuesOutstandingPaise, int CustomersWithDues, // counts only — never who
    IReadOnlyList<DuesAgeBucket> DuesAging,           // 0–7 / 8–30 / 31–90 / 90+ days → paise
    IReadOnlyList<StockFlag> LowStock);               // item name + stock milli + threshold
public sealed record SlabTotal(int SlabBasisPoints, long TaxablePaise, long TaxPaise);
public sealed record ItemTotal(string ItemName, long QtyMilli, long ValuePaise);
public sealed record DuesAgeBucket(int MinDays, int? MaxDays, long Paise);
public sealed record StockFlag(string ItemName, long StockMilli, long ThresholdMilli);
```

Enforcement is mechanical, three layers (all Wave 0):
1. **Type shape** — the record graph above contains no customer type at any depth.
2. **Reflection test** — `PiiGuardTests` walks the `AggregateSnapshot` type graph and fails
   the build if any property name matches a denylist (`Phone`, `Customer`, `Name` outside
   `ItemName`/`SupplierName`-style allowlist, `Address`, `Note`) or any reachable type is a
   Domain customer entity.
3. **Scrubber** — free-text fields that DO leave (`question`, voice `transcript`) pass
   through `PiiScrubber` (in Angadi.AI) which strips phone-like digit runs (10-digit Indian
   mobile patterns, `91`-prefixed) before anything is sent. Supplier-bill **images** may
   contain the *supplier's* business details — that is the feature working, not customer PII;
   it is gated by its own settings toggle + consent line (owner-facing copy in strings JSON).

### 3.4 Project layout — `src/Angadi.AI/` (all NEW, DEV-AI unless noted)

| File | Contents |
|---|---|
| `Angadi.AI.csproj` | Outer-ring project (see §3.1). Provider SDK (if any) pinned in `Directory.Packages.props` — **prefer raw `HttpClient` + JSON** (free-tier LLM APIs are plain HTTPS; no SDK dependency to maintain). |
| `DependencyInjection.cs` | `AddAngadiAi()`: registers `QuotaGuardAiService` decorating the provider service when `AiOptions.Enabled`, else registers nothing extra (Application's disabled default stands). Called from MauiProgram (INTEGRATOR) after `AddAngadiApplication`. |
| `DisabledAiService.cs` — **in Application**, beside the port (`AiServices.cs`) | The inert default: every method returns `Result.Fail(AI_DISABLED)`; no I/O, no counters. Registered by `AddAngadiApplication` (edit `src/Angadi.Application/DependencyInjection.cs`, DEV-AI sole owner) — the exact C2 stub pattern, so with the flag off (or the project absent) the DI graph is v1's. |
| `ProviderAiService.cs` | The real `IAiService`: builds payloads **only** via `AiPayloadBuilder`, calls `IAiProviderClient`, parses defensively (bad JSON → `AI_BAD_RESPONSE`, never a throw across the boundary). |
| `IAiProviderClient.cs` + `HttpAiProviderClient.cs` | Provider adapter seam + the one shipped adapter (provider chosen by DEV-1's cost spec; the interface is one method: `Task<Result<string>> CompleteAsync(string systemPrompt, string userPayload, CancellationToken ct)`). Swapping providers = one new adapter file + config. |
| `AiPayloadBuilder.cs` | The ONLY constructor of provider payloads. Takes `AggregateSnapshot` / image / transcript(+scrubbed) — there is no overload that accepts a `Customer`, `Bill`, or repository. Internal to the project. |
| `PiiScrubber.cs` | Phone-pattern scrubbing for free text (§3.3 layer 3). Pure, heavily unit-tested. |
| `AiQuotaStore.cs` | Monthly cap, reusing the **QuotaStore Meta pattern verbatim**: keys `ai.count.YYYYMM` + `ai.last_seen_ms`, effective month = month of max(now, last_seen) — a rolled-back clock cannot reopen a spent month (the H2 defence, inherited). Persistence via a new generic port (below) because Angadi.AI may not reference Infrastructure. |
| `QuotaGuardAiService.cs` | Decorator over any `IAiService`: cap-check (count → `AI_CAP_REACHED`) → call inner → increment **only on a sent call**. Wraps the provider service; never wraps `DisabledAiService` (disabled calls are free and uncounted). |
| `AggregateSnapshotBuilder.cs` | Builds `AggregateSnapshot` from Application repository ports (`IBillRepository.ListByDayAsync`, `ICustomerRepository.GetWithDuesAsync` — count + totals only, `IItemRepository.GetLowStockAsync`, `IStockLedger` daily sales) + the FIFO `DuesAging` buckets. Lives in Angadi.AI; takes ports via constructor. |
| `Resources/prompts.json` | System prompts + per-feature templates as JSON resources (rule 6 — any Telugu in prompts lives here, never in C#). **CONFIRM** resource-embedding pattern against `Angadi.Application` resources before writing. |

New port for persistence — `src/Angadi.Application/Persistence/MetaPorts.cs` (NEW, DEV-AI):

```csharp
namespace Angadi.Application.Persistence;

/// <summary>Generic Meta-table access (v1's QuotaStore pattern, exposed as a port for outer-ring projects).</summary>
public interface IMetaStore
{
    Task<string?> GetAsync(string key, CancellationToken ct = default);
    Task SetAsync(string key, string value, CancellationToken ct = default);
}
```

Implemented by `src/Angadi.Infrastructure/Persistence/MetaStore.cs` (NEW, DEV-AI; registered
by INTEGRATOR) over the same `MetaRow`/`db.RunAsync` mechanics `QuotaStore` uses.

`AiOptions` (edit `Options.cs`, DEV-AI): section `Ai` — `Enabled` (false),
`MonthlyCallCap` (50), `Provider` ("" ), `Model` (""), `SupplierScanEnabled` (false, separate
consent toggle).

### 3.5 Callers (edits)

| File | Edit | Owner |
|---|---|---|
| `src/Angadi.Application/Voice/VoiceBillingPipeline.cs` (**CONFIRM** path) | Optional rescue: when the top match scores below the confirm threshold, call `IAiService.RescueVoiceAsync` **only if** a non-disabled service is registered; result only re-ranks the confirm sheet — auto-add thresholds are unchanged, and any AI failure falls through to today's behaviour silently. `TeluguParser.cs` is NOT touched. | DEV-AI |
| `src/Angadi.Maui/Areas/LedgerPages.cs` (**CONFIRM** page file) | "Ask your data" entry on the Reports page: question box → `AskYourDataQuery` (new handler in `src/Angadi.Application/Insights/`, DEV-AI — builds snapshot via Angadi.AI's builder resolved through DI, calls `IAiService`) → answer sheet. Hidden entirely when `AiOptions.Enabled` is false. | DEV-AI (page edit by DEV-AI; this page is not on the serialized list) |
| `src/Angadi.Maui/MauiProgram.cs` | Bind `Ai` section; call `AddAngadiAi()`. | INTEGRATOR |

### 3.6 Tests (NEW folder `tests/Angadi.Tests/Ai/`, DEV-AI)

- `DisabledAiServiceTests` — flag off: AppHost DI resolves `IAiService` to the disabled
  default; all three methods return `AI_DISABLED`; **zero Meta writes**; voice pipeline and
  finalize behaviour byte-identical to v1 fixtures.
- `AiQuotaTests` — 50 calls pass, 51st returns `AI_CAP_REACHED` and is never sent (fake
  provider asserts call count = 50); failed sends don't increment; month rollover frees the
  cap; **clock rolled back stays on the spent month** (H2 mirror).
- `PiiGuardTests` — reflection denylist walk over `AggregateSnapshot` (§3.3); snapshot built
  from a seeded shop (with customers + dues) serializes to JSON containing **no** seeded phone
  string and no customer name; scrubber strips `98480 12345`, `+91…`, `91…` forms from
  questions/transcripts and leaves item names intact.
- `AskYourDataTests` — snapshot math against seeded bills (slab totals, dues aging buckets,
  top items) equals hand-computed values, using `FakeAiProviderClient` (in the test project)
  that captures the payload — assert the payload is exactly the snapshot JSON.
- `SupplierScanTests` — fake provider returns canned JSON → draft lines parsed; malformed
  JSON → `AI_BAD_RESPONSE`; draft never reaches any repository (assert item count unchanged).
- `VoiceRescueTests` — fake `IAiService` re-ranks candidates → confirm sheet order changes;
  disabled service → v1 order unchanged; AI throwing/failing → v1 behaviour, no exception
  escapes the pipeline.

---

## 4. W3 — PURCHASE VERIFICATION, CLIENT SIDE (DEV-VERIFY)

**Flaw → Fix → Why.** *Flaw:* v1 entitlement is client-trusted: `PlaySubscriptionService`
caches Play's answer on-device, the free-tier counter lives in the same DB it protects, and
`IQuotaStore` documents that reinstall wipes it. A patched APK or a restored backup can fake
Pro. *Fix (client half — the server half is DEV-1's spec):* the app captures the Play purchase
token at purchase/restore time, stores it in a pending-verification record, and has it verified
by the free-tier backend (an HTTPS endpoint — Supabase Edge Function / Firebase Cloud Function
that calls Google's Play Developer API); the verified verdict + expiry is cached on-device and
drives `GetStatusAsync`. *Why:* verification must never enter the billing hot path — v1's
contract (`GetStatusAsync` answers from local state immediately, never the network) is what
fixed the counter freeze; this design keeps that contract and verifies in the background.

### 4.1 New port — edit `src/Angadi.Application/Platform/PlatformServices.cs` (DEV-VERIFY)

```csharp
/// <summary>What Play hands the app for one purchase. The token is the proof — it is stored
/// on-device only until the server has verified it, and never logged (NFR-08 spirit).</summary>
public sealed record PurchaseProof(string ProductId, string PurchaseToken, string? OrderId, long PurchasedMs);

public enum PurchaseVerdict { Verified, Invalid, Unknown }

/// <summary>Server verdict for one token. ExpiresMs is the subscription expiry the SERVER
/// read from Google — the client never trusts its own clock-math for entitlement.</summary>
public sealed record PurchaseVerification(PurchaseVerdict Verdict, long? ExpiresMs, SubscriptionPlan? Plan);

/// <summary>v2: server-side purchase verification. Wave 0 tests use a fake;
/// Wave 1 ships the HTTPS adapter against the free-tier backend endpoint.</summary>
public interface IPurchaseVerifier
{
    Task<Result<PurchaseVerification>> VerifyAsync(PurchaseProof proof, CancellationToken ct = default);
}
```

Next to it, extend the `ISubscriptionService` doc comment (no signature change): status is
`Verified-cache → Pending-grace → Free`, per §4.3.

### 4.2 What the app sends and stores

**Sends** (only on purchase, restore, and the daily background re-check; always via
`IPurchaseVerifier`): `ProductId` (`angadi_pro_monthly` / `angadi_pro_yearly` from
`BillingOptions`), `PurchaseToken`, `OrderId`, plus the shop's `sync.device_id` once W1
exists (until then a `purchase.device_id` Meta Guid minted the same way). Nothing else — no
shop name, no phone, no sales data.

**Stores** — `src/Angadi.Infrastructure/Billing/PurchaseVerificationStore.cs` (NEW,
DEV-VERIFY), Meta keys in the QuotaStore style:
- `purchase.pending` — JSON `PurchaseProof` awaiting a verdict (written **before** any verify
  call, so a crash mid-purchase loses nothing).
- `purchase.verified` — JSON { productId, plan, expiresMs, verifiedMs } from the last
  `Verified` verdict.
- `purchase.state` — `None | Pending | Verified | Invalid`.
- The token is **never** written to logs (add to the NFR-08 test corpus, §4.5) and the pending
  record is cleared on an `Invalid` verdict.

### 4.3 Grace behaviour (the state machine `PlaySubscriptionService` must implement)

| State | `GetStatusAsync` (cache-only, as v1) | Background behaviour |
|---|---|---|
| `Verified`, now < server expiry | **Pro** | Re-verify at most daily (existing rate limit), refresh expiry. |
| `Pending` (bought/restored, no verdict yet) and purchase age ≤ **7 days** | **Pro (grace)** | Retry verify on connectivity regain + daily; each failure keeps grace until day 7. |
| `Pending`, purchase age > 7 days, still no verdict | **Free** + settings shows "verification pending — connect to the internet" | Keeps retrying daily; a later `Verified` restores Pro immediately. |
| `Invalid` verdict | **Free immediately** (no grace — Google said no) | Pending record cleared; purchase flow must be re-run. |
| No purchase records (v1 upgrade path) | Exactly v1 behaviour: Play cache + 7-day offline Pro grace as today | First successful Play status read with a token available seeds `purchase.pending` and starts verification. |

Clock defence mirrors H2: grace age is measured against `max(now, purchase.last_seen_ms)`
bookkeeping in the same store, so rolling the clock back cannot stretch grace.

### 4.4 Files

| File | Change | Owner |
|---|---|---|
| `src/Angadi.Application/Platform/PlatformServices.cs` | Add `PurchaseProof` / `PurchaseVerification` / `IPurchaseVerifier` (§4.1). | DEV-VERIFY |
| `src/Angadi.Maui/Platform/PlaySubscriptionService.cs` | On purchase/restore success: extract token + order id from the Plugin.InAppBilling result, write `purchase.pending` **before** acknowledging... (acknowledgement order: store token → verify attempt → acknowledge; a failed store must fail the purchase visibly, never acknowledge-and-lose). `GetStatusAsync` reads the §4.3 state machine from the store + existing Play cache; background re-check also drives verification retries. The never-block contract is untouched: no verify call on the status path. | DEV-VERIFY (sole owner of this file for v2) |
| `src/Angadi.Infrastructure/Billing/PurchaseVerificationStore.cs` | NEW (§4.2). | DEV-VERIFY |
| `src/Angadi.Infrastructure/Billing/HttpPurchaseVerifier.cs` | **Wave 1.** `HttpClient` POST of the proof JSON to the free-tier backend verify endpoint (`VerifyOptions.BaseUrl`); response = verdict + server expiry. No vendor SDK. | DEV-VERIFY |
| `src/Angadi.Infrastructure/Billing/StubSubscriptionService.cs` | Unchanged (still the non-Android/test default). | — |
| `src/Angadi.Infrastructure/DependencyInjection.cs` | Register store + verifier (fake-friendly: when `VerifyOptions.Enabled` is false, register `UnconfiguredPurchaseVerifier` in the same file returning `Unknown` — grace rules then apply, v1 behaviour preserved). | INTEGRATOR |
| `src/Angadi.Application/Common/Options.cs` | `VerifyOptions`: `Enabled` (false), `BaseUrl` (""), `GraceDays` (7). | DEV-VERIFY |
| `src/Angadi.Maui/Areas/SetupAndSettingsPages.cs` | Subscription page shows the pending/failed-verification state (strings JSON). | INTEGRATOR |

### 4.5 Tests (NEW folder `tests/Angadi.Tests/Billing/`, DEV-VERIFY)

`FakePurchaseVerifier` (scripted verdicts + call log) lives in the test project.
- `PurchaseVerificationFlowTests` — purchase → pending stored with the exact token →
  `Verified` → status Pro with server expiry; `Invalid` → Free immediately + pending cleared.
- `PurchaseGraceTests` — verifier unreachable: Pro through day 7 (fake clock), Free on day 8,
  Pro again the moment a `Verified` lands; clock rolled back mid-grace does not extend it.
- `PurchaseHotPathTests` — finalize a bill with a verifier that **blocks forever**: finalize
  completes (the C2 regression guard — zero verifier calls on the status/finalize path,
  asserted on the fake's call log).
- `PurchaseTokenPrivacyTests` — run purchase + verify + a failing verify; assert the token
  string appears in **no** log file content (extends the NFR-08 corpus approach).

---

## 5. W4 — AUTOMATIC CLOUD BACKUP (DEV-BACKUP)

**Flaw → Fix → Why.** *Flaw:* v1 backup is manual (export → share sheet) and purely local
snapshots die with the phone; W1 sync fixes multi-device but a shop that never enables sync
still loses everything when the tablet dies — the market analysis flags "lose your phone,
lose nothing" as the rivals' strongest pitch. *Fix:* automatic, **encrypted** backup of
`angadi.db` to storage the **owner chooses** — Android Storage Access Framework (any folder,
incl. Google Drive via the system picker) with **zero accounts, zero cost, zero new
permissions** as the default; a Drive app-folder adapter is an optional Wave-1 extra.
*Why SAF first:* it needs no Google Cloud project, no OAuth consent screen, no API key — the
owner points the app at a folder once and Android mediates access forever.

### 5.1 Ports — `src/Angadi.Application/Platform/CloudBackupPorts.cs` (NEW, DEV-BACKUP)

```csharp
namespace Angadi.Application.Platform;

/// <summary>A place a backup file can be written to and read back from (SAF folder, Drive
/// app-folder, or a plain folder in tests). Names are logical; implementations map them.</summary>
public interface ICloudStore
{
    Task<Result> PutAsync(string name, ReadOnlyMemory<byte> data, CancellationToken ct = default);
    Task<Result<byte[]>> GetAsync(string name, CancellationToken ct = default);
    Task<Result<DateTimeOffset?>> LastModifiedAsync(string name, CancellationToken ct = default);
}

/// <summary>Device key custody for backup encryption (Android Keystore on device;
/// in-memory in tests). The key never leaves the device and is never backed up itself.</summary>
public interface IBackupKeyStore
{
    Task<Result<byte[]>> GetOrCreateKeyAsync(CancellationToken ct = default);
}

/// <summary>v2: automatic encrypted cloud backup of the whole database.</summary>
public interface ICloudBackupService
{
    /// <summary>Exports, encrypts and uploads if the DB changed since the last success. Cheap no-op otherwise.</summary>
    Task<Result<CloudBackupOutcome>> BackupIfChangedAsync(CancellationToken ct = default);
    /// <summary>Downloads the newest cloud backup, decrypts, and hands it to the EXISTING staged restore.</summary>
    Task<Result> RestoreLatestAsync(CancellationToken ct = default);
}
public sealed record CloudBackupOutcome(bool Uploaded, long Bytes, DateTimeOffset? At);
```

### 5.2 Implementation — `src/Angadi.Infrastructure/Backup/` (NEW, DEV-BACKUP)

| File | Contents |
|---|---|
| `CloudBackupService.cs` | Orchestrates: `IBackupService.ExportAsync` (existing — already checkpoints and produces the self-contained file) → SHA-256 of the export → compare `cloudbackup.last_hash` → encrypt → `ICloudStore.PutAsync("angadi-cloud-backup.db.enc")` → only on success write Meta `cloudbackup.last_success_ms` + hash. **Restore:** `GetAsync` → decrypt → write to cache temp → `IBackupService.RestoreAsync(tempPath)` — the existing staged path (integrity check + invariant sweep + atomic swap) validates it exactly like a manual restore. No new restore logic exists in this workstream. |
| `BackupCrypto.cs` | AES-256-GCM envelope: `[magic "AGBK1"][12B nonce][ciphertext+tag]`. Wrong key / tampered byte → failure Result, live DB untouched (the staged restore never even sees it). |
| `CloudBackupOptions` (in `Options.cs`) | Section `CloudBackup`: `Enabled` (false — owner opts in from Settings), `KeepDailyDays` (7 — dated copies `angadi-cloud-backup-yyyyMMdd.db.enc` pruned beyond this; the `latest` file always kept). |

Meta keys (QuotaStore style): `cloudbackup.enabled`, `cloudbackup.target` (`saf` | `drive` |
`folder`), `cloudbackup.last_success_ms`, `cloudbackup.last_hash`.

### 5.3 Platform + triggers (Maui head)

| File | Change | Owner |
|---|---|---|
| `src/Angadi.Maui/Platform/AndroidSafCloudStore.cs` (**CONFIRM** Platform folder layout) | NEW: `ICloudStore` over a persisted SAF tree URI (`ACTION_OPEN_DOCUMENT_TREE`, `takePersistableUriPermission`); document create/read via `ContentResolver`. This is the default store — works with local folders, SD cards, and Drive-through-the-picker with no Google account setup in the app. | DEV-BACKUP |
| `src/Angadi.Maui/Platform/AndroidBackupKeyStore.cs` | NEW: `IBackupKeyStore` over Android Keystore (AES key, non-exportable). **Documented tradeoff, stated in Settings copy:** if the phone dies, the key dies with it — so Settings also offers *passphrase mode* (PBKDF2-HMAC-SHA256, 210k iterations, salt in the envelope header) for owners who want phone-loss recovery; passphrase mode is the recommended default for exactly that reason. | DEV-BACKUP |
| `src/Angadi.Maui/Platform/DriveAppFolderStore.cs` | **Wave 1, optional.** Drive REST app-folder `ICloudStore` (owner's own Google account OAuth). Only if DEV-1/owner want it — SAF already reaches Drive. | DEV-BACKUP |
| `src/Angadi.Maui/Platforms/Android/CloudBackupWorker.cs` | NEW: WorkManager daily worker (charging-or-idle preferred, network not required for SAF-local targets) calling `BackupIfChangedAsync`. Plus the cheap trigger: App `OnSleep` fires it fire-and-forget beside the safety snapshot (INTEGRATOR applies the `App.cs` edit). | DEV-BACKUP / INTEGRATOR |
| `src/Angadi.Maui/Areas/SetupAndSettingsPages.cs` | Settings section: enable toggle, "choose backup folder" (SAF picker), passphrase set/change, "Back up now", "Restore from cloud" (with the existing data-recovery confirm copy), last-success line. | INTEGRATOR |

### 5.4 Tests (NEW folder `tests/Angadi.Tests/Backup/`, DEV-BACKUP)

`FolderCloudStore` + `InMemoryKeyStore` fakes live in the test project.
- `CloudBackupCryptoTests` — round-trip; single flipped byte → failure; wrong key →
  failure; passphrase mode round-trip + wrong-passphrase failure.
- `CloudBackupRoundTripTests` — seed shop → backup → wipe DB → `RestoreLatestAsync` →
  bills/stock/dues identical, and the restore demonstrably went through the staged path
  (a tampered cloud file is rejected by the invariant sweep, live data untouched).
- `CloudBackupScheduleTests` — unchanged DB → no upload (store call count 0); changed DB →
  one upload; `last_success_ms` written only on success; failed upload leaves hash stale so
  the next run retries.

---

## 6. Wave order (the whole of v2)

| Wave | Contents | Needs |
|---|---|---|
| **Wave 0 — everything buildable at ₹0** | W2 complete (project, port, disabled default, quota, PII guard, fake provider, all §3.6 tests). W1 engine complete against `FakeSyncServer` (all §2.4 tests). W3 client complete against `FakePurchaseVerifier` (all §4.5 tests). W4 complete against `FolderCloudStore` (all §5.4 tests) + SAF/Keystore adapters (compile + on-device UAT). | Nothing. No accounts, no keys, no server. |
| **Wave 1 — switch-ons** | `HttpSyncTransport` against the free-tier backend (DEV-1 spec: Supabase/Firebase project + tables/functions). `HttpPurchaseVerifier` against the backend verify endpoint. `HttpAiProviderClient` adapter for the one chosen provider. Optional `DriveAppFolderStore`. | The §7 owner actions. |
| **Wave 2 — hardening** | Background reliability pass (WorkManager constraints on real devices), conflict edge cases from UAT, sync status UI (last-synced line in Settings), two-emulator E2E in CI (non-gating). | UAT feedback only. |

Definition of done per workstream = the §10 enterprise bar: warnings-as-errors build, the
named tests above green **in CI (observed, or the log entry says UNVERIFIED-CI)**, fix-log
entry `VERIFIED` by Muse (main), CHANGELOG in the same push.

## 7. CI changes (INTEGRATOR)

- `.github/workflows/ci.yml` — no structural change: job 1 builds the solution in Release
  with warnings-as-errors and runs the full test project, so `Angadi.AI` + all new tests are
  picked up automatically once the project is in `Angadi.sln`. **CONFIRM** the build step is
  solution-level (audit read it as such); if it lists projects explicitly, add `Angadi.AI`.
  The two-device sync tests and AI tests run in-process — no new services, no secrets.
- `tools/coverage_gate.py` — add the `Angadi.AI` assembly to the gate at the existing ≥70%
  line (Domain + Application stay ≥70%, BillCalculator ≥95% — unchanged).
- No CI secrets are added in Wave 0 or Wave 1 (the free-tier anon key, if used, is a public
  client key shipped in appsettings — row-level security / function-side checks are the
  backend's job per DEV-1's spec; service-role keys and LLM keys **never** enter the repo or
  the app — they live only in the backend's function environment).

## 8. OWNER-ACTION checklist (Sai — the only things agents cannot do)

| # | Action | Cost | Needed for |
|---|---|---|---|
| 1 | **Create ONE free-tier backend project** (Supabase or Firebase, per DEV-1's justified pick) and paste its project URL + anon key to Muse. **This is the only account v2 needs.** | ₹0 (free tier; paid only after real shops onboard, owner's later call) | Wave 1 switch-ons (sync transport, verify endpoint) |
| 2 | Play Console (already a v1 gate, unchanged): create products `angadi_pro_monthly` / `angadi_pro_yearly`; complete the 12+-tester / 14-day closed test. | $25 one-time (already known) | Selling Pro at all |
| 3 | In Play Console: enable the Play Developer API and create the service account the backend verify function uses; paste its JSON into the **backend function's** secret settings (never chat, never repo). | ₹0 | W3 Wave 1 (real verdicts) |
| 4 | Pick the ONE Tier-2 AI provider (DEV-1 spec has the ₹ comparison) and create its API key; paste it into the **backend function's** secrets (the app never holds an LLM key — ask-your-data/scan/rescue call the backend, which calls the provider; this also keeps the 50/mo cap enforceable server-side later). Cheapest viable: the provider with a free tier / lowest per-call rate in DEV-1's table. | ₹0 free tier to start; ~₹2–5/shop/mo at cap when live | W2 Wave 1 |
| 5 | On the shop tablet at UAT: pick the cloud-backup folder once (SAF picker — a Drive folder if wanted) and set the backup passphrase. | ₹0 | W4 live use |
| 6 | Release keystore (already a v1 gate, unchanged): create/keep the upload keystore; production AAB signing stays Sai's tap. | ₹0 | Any Play release |

**Explicitly NOT needed:** Azure anything · a paid server · a WhatsApp Business API account ·
a second Google Cloud project (Drive app-folder via SAF needs none; the optional REST adapter
would reuse the account from #1/#3 flows) · any spend before real shops onboard.

---

*Plan status: Wave 0 is buildable today from this file alone. CONFIRM items are one-line
checks a builder does before coding its file (OutboxRow PK, exact resource paths, Platforms
folder layout, CI build scope) — they are marked where they sit, not hidden.*
