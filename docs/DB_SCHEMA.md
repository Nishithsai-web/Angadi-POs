# Database schema (SQLite, on device)

File: `<AppData>/angadi.db`, WAL mode. Code: `src/Angadi.Infrastructure/Persistence/Rows.cs`. Schema version is kept in `Meta.schema_version` (currently **4**: 2 customer accounts + notes, 3 shop UPI QR, 4 split tender + returns).
File: `<AppData>/angadi.db`, WAL mode. Code: `src/Angadi.Infrastructure/Persistence/Rows.cs`. Schema version is kept in `Meta.schema_version` (currently **4**; v2-stock = base 3 + 1).

**Conventions:**
- All ids are GUIDs.
- Money is in paise, quantity in thousandths of a unit, time in Unix ms UTC, and `Day` is a `yyyymmdd` int in the shop's local time.
- Domain entities carry no SQLite attributes (ADR-12).

| Table | Key columns | Notes |
|---|---|---|
| **Item** | Id, TeluguName, EnglishName, AliasesJson, Unit, PricePaise, GstBasisPoints, Barcode (unique, nullable), StockMilli, LowStockMilli, IsQuickSale, IsActive | StockMilli changes **only** via `StockLedger` (relative `UPDATE … + ?`). Item edits never overwrite it. |
| **Bill** | Id, Number (unique), Status, Tender, CustomerId, totals in paise, TaxSplitsJson, IsTaxInvoice, FinalizedAtMs + offset, Day, Void fields | Frozen at finalize. Correction = void. |
| **BillLine** | Id, BillId, Position, ItemId, name/price/slab snapshots, QtyMilli, discounts, Gross/LineTotal/BillDiscountShare | Snapshots mean later item edits never change old bills. |
| **Customer** | Id, Name, Phone, DuesBalancePaise | Balance changes **only** via `AddDuesEntryAsync`. |
| **DuesEntry** | Id, CustomerId, Kind (Charge/Payment/Reversal), AmountPaise, BillId | The udhaar ledger (FR-22). |
| **StockMove** | Id, ItemId, DeltaMilli, Reason, BillId, Day | Every stock change. Σ DeltaMilli = Item.StockMilli. Daily sales for insights come from Sale + VoidRestore + Return rows. Reason 6 = Return (v2). |
| **Outbox** | Id, AggregateType, AggregateId, EventType, PayloadJson, SyncState | v2 sync seam. v1 only appends. |
| **ShopProfile** | one row: Name, Address, Phone, UpiId, Gstin, BillPrefix, NextBillSequence, Language | NextBillSequence is incremented atomically by `NextBillNumberAsync`. |
| **Meta** | Key, Value | `app=angadi`, `schema_version`, `seq.return` (next return number; carried forward on restore like the bill sequence). |
| **BillPayment** *(v2, schema 4)* | Id, BillId, Position, Tender, AmountPaise, UpiRef, CashReceivedPaise | How a bill was paid, part by part. Σ AmountPaise = Bill.GrandTotalPaise (checked on restore). Written for every bill finalized from schema 4; older bills have none (read as one part of `Bill.Tender`). `Bill.Tender = 3` = Split. |
| **SalesReturn** *(v2, schema 4)* | Id, Number (unique, `R-000001`), BillId, BillNumber, CustomerId, LinesTotalPaise, RoundingPaise, RefundTotalPaise, IsTaxInvoice, TaxSplitsJson, TaxTotalPaise, Method, CashRefundPaise, UpiRefundPaise, DuesCreditPaise, UpiRef, Reason, ReturnedAtMs + offset, Day | A credit note against a finalized bill; insert-only. RefundTotal = Cash + UPI + DuesCredit (checked on restore). Reported on its own `Day`. |
| **ReturnLine** *(v2, schema 4)* | Id, ReturnId, Position, BillLineId, ItemId, name/unit/slab snapshots, QtyMilli, AmountPaise | Σ QtyMilli per BillLineId ≤ the bill line's QtyMilli. |
| **Meta** | Key, Value | `app=angadi`, `schema_version`. |
| **Item** (schema 4 additions) | TracksBatches, CostPaise | Nullable on old rows (NULL = off / unknown cost). CostPaise = latest purchase cost per unit before GST, owned by purchase entry (item edits never overwrite it). |
| **StockMove** (schema 4 additions) | BatchId, PurchaseId | Every move of a batch-tracked item names its batch; Purchase moves (and purchase corrections) name their purchase. |
| **Batch** (schema 4) | Id, ItemId, BatchNo (upper-case; "" = the "no batch" row), ExpiryDay (yyyymmdd; last day of the printed MM/YY), QtyMilli, CostPaise | **Invariants for TracksBatches items: Σ Batch.QtyMilli = Item.StockMilli, and each Batch.QtyMilli = Σ StockMove.DeltaMilli WHERE BatchId = it.** Sales split FEFO in `StockLedger`; voids go back to the batches the bill took from; shortfalls (stock going negative, legal as in v1) land on the "no batch" row. Switching tracking on writes a zero-sum pair of CountCorrection moves that puts existing stock on the "no batch" row. |
| **Supplier** (schema 4) | Id, Name, Phone, Gstin, Notes | Phone is never logged. |
| **Purchase** (schema 4) | Id, SupplierId, SupplierName (snapshot), SupplierBillNo, BillDay, PurchaseOrderId, TotalPaise | Insert-only (immutable). Supplier + bill no is unique (checked case-insensitively). |
| **PurchaseLine** (schema 4) | Id, PurchaseId, Position, ItemId, name/unit snapshots, QtyMilli, CostPaise (before GST), GstBasisPoints, BatchNo, ExpiryDay, BatchId, LineTotalPaise | LineTotal = round(qty × cost × (1 + GST)), half up. |
| **PurchaseOrder** (schema 4) | Id, SupplierId, Status (0 Draft, 1 Sent, 2 Received), LinesJson (`[["itemId","qtyMilli"],…]`), PurchaseId | Only drafts are editable; Received is set by the purchase entry made from it. |

Restore sweep (`BackupService.PassesInvariants` → `BatchesReconcile`) checks both batch invariants and orphan batches; a pre-schema-4 backup has no Batch table and skips it. The backup HMAC dump includes the new tables only when they hold rows, and the new Item/StockMove columns only when set, so a pre-schema-4 backup still verifies.

## Transactions
`AngadiDb.ExecuteAsync(work, shouldCommit)` is the unit of work. It serialises access through one gate and runs `work` in a single SQLite transaction. It commits only when `shouldCommit(result)` is true, and rolls back on a failed `Result` or any exception. Repository calls inside it join the open transaction.

## Migrations
sqlite-net `CreateTable<T>` adds missing columns on startup, so **adding** a column or table needs no migration code. Renames and type changes need an explicit step that bumps `SchemaVersion`. Never drop a column in v1.

## Backup / restore (FR-37)
- **Export:** WAL checkpoint, copy the file, switch the copy to `journal_mode=DELETE` so it's self-contained, then share it.
- **Restore:** check `Meta.app = angadi` and `schema_version ≤` the app's version, take a safety backup of the current data, then swap the file and reopen.

## Several shops on one phone (v2)
No schema change. Each shop is a separate database file with this same schema; the list is a small JSON file outside every database.
| Shop | Database | Safety copies |
|---|---|---|
| First (`main`) | `files/angadi.db` (unchanged v1 path) | `files/snapshots/` |
| Others (32-hex id) | `files/shops/<id>.db` | `files/shop-snapshots/<id>/` |

`files/shops.json` = `{ "current": "<id>", "shops": [ { "id", "name" } ] }`; paths are derived from the id, never read from the file. A shop file on disk missing from the list is listed again. Switching (`AngadiDb.SwitchToAsync`) takes the write lock and then the read lock (PERF-1), checkpoints and closes the open file, opens the other verified (damage → recovery from that shop's own copies); on failure it reopens the previous file; both locks are always released. The free quota adds every other shop's month count (`quota.count.YYYYMM` or its bills, plus its M-3 unabsorbed lifetime bills) — read from those files, never written.

## Staff and activity (schema 6, RBAC-1)
- `Staff`: Id (Guid PK), Name, Role (1 Manager, 2 Cashier), PinHash ("salt:hash" PBKDF2-SHA256, 100k), IsActive, CreatedAtMs. One table per shop file.
- `Activity`: Id (Guid PK), AtMs (indexed), Action (request name), Who, Role, ApprovedBy, ApprovedByRole. Rows older than 180 days are pruned. It never holds customer data or PINs.
- Meta `access.cashier_grants`: comma list of the permissions cashiers have without a manager. Absent means void + refund.
- Both tables are in the backup MAC (AppendIfAny, so older backups still verify).

## Seed data
`Seed/kirana.json` (80) and `Seed/pharmacy.json` (40) are generated by `tools/generate_seed.py`, which fails on alias collisions. Opening stock is written as `StockMove(Opening)`.
