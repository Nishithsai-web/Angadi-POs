using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Angadi.Application.Billing;
using Angadi.Application.Catalog;
using Angadi.Application.Common;
using Angadi.Application.Insights;
using Angadi.Application.Persistence;
using Angadi.Application.Platform;
using Angadi.Domain.Billing;
using Angadi.Domain.Common;
using Angadi.Domain.Events;
using Angadi.Domain.Inventory;
using Angadi.Domain.Items;
using Angadi.Domain.Purchasing;

namespace Angadi.Application.Stock;

// ---------- Validators ----------

internal sealed partial class SaveSupplierValidator : IValidator<SaveSupplierCommand>
{
    [GeneratedRegex("^[0-9]{2}[A-Z]{5}[0-9]{4}[A-Z][0-9A-Z]Z[0-9A-Z]$")]
    private static partial Regex Gstin();

    public Error? Validate(SaveSupplierCommand c)
    {
        var s = c.Supplier;
        if (string.IsNullOrWhiteSpace(s.Name) || s.Name.Trim().Length > 80) return new Error(ErrorCodes.SupplierNameRequired);
        if (!string.IsNullOrWhiteSpace(s.Phone) && PhoneNumbers.Normalize(s.Phone) is null) return new Error(ErrorCodes.PhoneInvalid);
        if (!string.IsNullOrWhiteSpace(s.Gstin) && !Gstin().IsMatch(s.Gstin.Trim().ToUpperInvariant())) return new Error(ErrorCodes.GstinInvalid);
        if ((s.Notes?.Trim().Length ?? 0) > Angadi.Domain.Notes.Note.MaxLength) return new Error(ErrorCodes.NoteTooLong);
        return null;
    }
}

internal sealed class RecordPurchaseValidator : IValidator<RecordPurchaseCommand>
{
    public Error? Validate(RecordPurchaseCommand c)
    {
        var e = c.Entry;
        if (string.IsNullOrWhiteSpace(e.SupplierBillNo) || e.SupplierBillNo.Trim().Length > 40) return new Error(ErrorCodes.PurchaseBillNoRequired);
        if (e.Lines.Count == 0) return new Error(ErrorCodes.PurchaseEmpty);
        if (e.Lines.Any(l => l.Qty.Milli <= 0 || l.Qty.Milli > FinalizeBillValidator.MaxLineQtyMilli
                             || l.CostPrice.Value < 0 || l.CostPrice.Value > SaveItemValidator.MaxPricePaise
                             || !Enum.IsDefined(l.GstSlab) || (l.BatchNo?.Trim().Length ?? 0) > 30))
            return new Error(ErrorCodes.PurchaseLineInvalid);
        return null;
    }
}

internal sealed class SavePurchaseOrderValidator : IValidator<SavePurchaseOrderCommand>
{
    public Error? Validate(SavePurchaseOrderCommand c)
    {
        if (c.Order.Lines.Count == 0) return new Error(ErrorCodes.PurchaseEmpty);
        if (c.Order.Lines.Any(l => l.Qty.Milli <= 0 || l.Qty.Milli > FinalizeBillValidator.MaxLineQtyMilli))
            return new Error(ErrorCodes.PurchaseLineInvalid);
        return null;
    }
}

internal sealed class AdjustBatchStockValidator : IValidator<AdjustBatchStockCommand>
{
    public Error? Validate(AdjustBatchStockCommand c) =>
        c.Delta.Milli == 0 ? new Error(ErrorCodes.BillQtyInvalid)
        : c.Reason is StockMoveReason.Sale or StockMoveReason.VoidRestore ? new Error(ErrorCodes.ValidationFailed, "sales move stock only via bills")
        : !Enum.IsDefined(c.Reason) ? new Error(ErrorCodes.ValidationFailed, "unknown reason")
        : null;
}

internal sealed class StockInBatchValidator : IValidator<StockInBatchCommand>
{
    public Error? Validate(StockInBatchCommand c) =>
        c.Qty.Milli <= 0 || c.Qty.Milli > FinalizeBillValidator.MaxLineQtyMilli ? new Error(ErrorCodes.BillQtyInvalid)
        : string.IsNullOrWhiteSpace(c.BatchNo) || c.BatchNo.Trim().Length > 30 ? Error.With(ErrorCodes.BatchDetailsRequired, "")
        : null;
}

// ---------- Suppliers ----------

internal sealed class SupplierHandlers(IPurchasingRepository repo, IOutbox outbox, IClock clock) :
    IRequestHandler<SaveSupplierCommand, Result<Guid>>,
    IRequestHandler<ListSuppliersQuery, IReadOnlyList<Supplier>>
{
    public async Task<Result<Guid>> Handle(SaveSupplierCommand command, CancellationToken ct)
    {
        var s = command.Supplier;
        s.Name = s.Name.Trim();
        s.Phone = PhoneNumbers.Normalize(s.Phone) is null ? null : s.Phone!.Trim();
        s.Gstin = string.IsNullOrWhiteSpace(s.Gstin) ? null : s.Gstin.Trim().ToUpperInvariant();
        s.Notes = string.IsNullOrWhiteSpace(s.Notes) ? null : s.Notes.Trim();
        await repo.SaveSupplierAsync(s, ct);
        await outbox.AppendAsync("Supplier", new SupplierChanged(s.Id, clock.Now), ct);
        return Result.Success(s.Id);
    }

    public Task<IReadOnlyList<Supplier>> Handle(ListSuppliersQuery query, CancellationToken ct) => repo.ListSuppliersAsync(ct);
}

// ---------- Purchase entry ----------

/// <summary>One transaction (Sender): every line's stock, every batch, every cost and the purchase row — or nothing.</summary>
internal sealed class RecordPurchaseHandler(
    IPurchasingRepository repo, IItemRepository items, IBatchLedger batches, IOutbox outbox, IClock clock, ILocalizer text)
    : IRequestHandler<RecordPurchaseCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(RecordPurchaseCommand command, CancellationToken ct)
    {
        var e = command.Entry;
        if (await repo.GetSupplierAsync(e.SupplierId, ct) is not { } supplier) return Result.Failure<Guid>(ErrorCodes.SupplierNotFound);
        var billNo = e.SupplierBillNo.Trim();
        if (await repo.PurchaseBillExistsAsync(supplier.Id, billNo, ct))
            return Result.Failure<Guid>(Error.With(ErrorCodes.PurchaseDuplicate, billNo));

        PurchaseOrder? order = null;
        if (e.PurchaseOrderId is { } orderId)
        {
            order = await repo.GetOrderAsync(orderId, ct);
            if (order is null) return Result.Failure<Guid>(ErrorCodes.OrderNotFound);
            if (order.Status == PurchaseOrderStatus.Received) return Result.Failure<Guid>(ErrorCodes.OrderNotEditable);
        }

        var purchase = new Purchase
        {
            SupplierId = supplier.Id, SupplierName = supplier.Name, SupplierBillNo = billNo, BillDate = e.BillDate, PurchaseOrderId = e.PurchaseOrderId,
        };
        var lineItems = new List<Item>();
        try
        {
            foreach (var l in e.Lines)
            {
                if (await items.GetAsync(l.ItemId, ct) is not { } item)
                    return Result.Failure<Guid>(ErrorCodes.ItemNotFound, l.ItemId.ToString("D", CultureInfo.InvariantCulture));
                var batchNo = string.IsNullOrWhiteSpace(l.BatchNo) ? null : l.BatchNo.Trim().ToUpperInvariant();
                if (item.TracksBatches && (batchNo is null || l.Expiry is null))
                    return Result.Failure<Guid>(Error.With(ErrorCodes.BatchDetailsRequired, text.Name(item)));
                purchase.Lines.Add(new PurchaseLine
                {
                    ItemId = item.Id, TeluguName = item.TeluguName, EnglishName = item.EnglishName, Unit = item.Unit,
                    Qty = l.Qty, CostPrice = l.CostPrice, GstSlab = l.GstSlab,
                    BatchNo = item.TracksBatches ? batchNo : null, Expiry = item.TracksBatches ? l.Expiry : null,
                    LineTotal = PurchaseLine.Total(l.Qty, l.CostPrice, l.GstSlab),
                });
                lineItems.Add(item);
            }
            purchase.Total = new Paise(purchase.Lines.Aggregate(0L, (sum, l) => checked(sum + l.LineTotal.Value)));
        }
        catch (OverflowException) { return Result.Failure<Guid>(ErrorCodes.BillAmountTooLarge); }

        var note = supplier.Name + " #" + billNo;
        for (var i = 0; i < purchase.Lines.Count; i++)
        {
            var line = purchase.Lines[i];
            line.BatchId = await batches.ReceiveAsync(line.ItemId, line.Qty, StockMoveReason.Purchase, purchase.Id,
                line.BatchNo, line.Expiry, line.CostPrice, note, ct);
            if (line.CostPrice.Value > 0) await repo.SetItemCostAsync(line.ItemId, line.CostPrice, ct);
        }
        await repo.InsertPurchaseAsync(purchase, ct);

        var now = clock.Now;
        if (order is not null)
        {
            order.Status = PurchaseOrderStatus.Received;
            order.PurchaseId = purchase.Id;
            await repo.SaveOrderAsync(order, ct);
            await outbox.AppendAsync("PurchaseOrder", new PurchaseOrderChanged(order.Id, now, (int)order.Status), ct);
        }
        await outbox.AppendAsync("Purchase", new PurchaseRecorded(purchase.Id, now, supplier.Id, purchase.Total.Value, purchase.Lines.Count), ct);
        return Result.Success(purchase.Id);
    }
}

internal sealed class PurchaseQueries(IPurchasingRepository repo) :
    IRequestHandler<ListPurchasesQuery, IReadOnlyList<Purchase>>,
    IRequestHandler<GetPurchaseQuery, Purchase?>,
    IRequestHandler<GetPurchasesBySupplierQuery, IReadOnlyList<SupplierPurchaseTotal>>
{
    public Task<IReadOnlyList<Purchase>> Handle(ListPurchasesQuery q, CancellationToken ct) => repo.ListPurchasesAsync(q.From, q.To, ct);

    public Task<Purchase?> Handle(GetPurchaseQuery q, CancellationToken ct) => repo.GetPurchaseAsync(q.PurchaseId, ct);

    public async Task<IReadOnlyList<SupplierPurchaseTotal>> Handle(GetPurchasesBySupplierQuery q, CancellationToken ct)
    {
        var purchases = await repo.ListPurchasesAsync(q.From, q.To, ct);
        return purchases.GroupBy(p => p.SupplierId)
            .Select(g => new SupplierPurchaseTotal(g.Key, g.OrderByDescending(p => p.CreatedAt).First().SupplierName, g.Count(),
                new Paise(g.Sum(p => p.Total.Value))))
            .OrderByDescending(t => t.Total.Value)
            .ToList();
    }
}

// ---------- Purchase orders ----------

internal sealed class PurchaseOrderHandlers(
    IPurchasingRepository repo, IItemRepository items, IInsightsService insights, IOutbox outbox, IClock clock) :
    IRequestHandler<SavePurchaseOrderCommand, Result<Guid>>,
    IRequestHandler<ListPurchaseOrdersQuery, IReadOnlyList<PurchaseOrder>>,
    IRequestHandler<GetPurchaseOrderQuery, PurchaseOrder?>,
    IRequestHandler<SuggestOrderFromLowStockQuery, IReadOnlyList<PurchaseOrderLine>>,
    IRequestHandler<MarkPurchaseOrderSentCommand, Result>,
    IRequestHandler<PrefillPurchaseFromOrderQuery, Result<PurchaseEntry>>
{
    /// <summary>Suggested order covers this many days of the recent average sales.</summary>
    internal const int CoverDays = 14;

    public async Task<Result<Guid>> Handle(SavePurchaseOrderCommand command, CancellationToken ct)
    {
        var order = command.Order;
        if (await repo.GetSupplierAsync(order.SupplierId, ct) is null) return Result.Failure<Guid>(ErrorCodes.SupplierNotFound);
        if (await repo.GetOrderAsync(order.Id, ct) is { } existing && existing.Status != PurchaseOrderStatus.Draft)
            return Result.Failure<Guid>(ErrorCodes.OrderNotEditable);
        foreach (var itemId in order.Lines.Select(l => l.ItemId).Distinct())
            if (await items.GetAsync(itemId, ct) is null) return Result.Failure<Guid>(ErrorCodes.ItemNotFound);
        order.Status = PurchaseOrderStatus.Draft;
        order.PurchaseId = null;
        // One line per item: the same item added twice is one order line with the summed quantity.
        order.Lines = order.Lines.GroupBy(l => l.ItemId).Select(g => new PurchaseOrderLine(g.Key, new Qty(g.Sum(l => l.Qty.Milli)))).ToList();
        await repo.SaveOrderAsync(order, ct);
        await outbox.AppendAsync("PurchaseOrder", new PurchaseOrderChanged(order.Id, clock.Now, (int)order.Status), ct);
        return Result.Success(order.Id);
    }

    public Task<IReadOnlyList<PurchaseOrder>> Handle(ListPurchaseOrdersQuery q, CancellationToken ct) => repo.ListOrdersAsync(ct);

    public Task<PurchaseOrder?> Handle(GetPurchaseOrderQuery q, CancellationToken ct) => repo.GetOrderAsync(q.OrderId, ct);

    public async Task<IReadOnlyList<PurchaseOrderLine>> Handle(SuggestOrderFromLowStockQuery q, CancellationToken ct)
    {
        var forecast = (await insights.GetReorderForecastAsync(q.WithinDays, ct)).ToDictionary(f => f.ItemId);
        var low = await items.GetLowStockAsync(ct);
        var candidates = new Dictionary<Guid, Item>();
        foreach (var i in low) candidates[i.Id] = i;
        foreach (var id in forecast.Keys.Where(id => !candidates.ContainsKey(id)))
            if (await items.GetAsync(id, ct) is { IsActive: true } item) candidates[id] = item;

        var lines = new List<PurchaseOrderLine>();
        foreach (var item in candidates.Values)
        {
            var avg = forecast.TryGetValue(item.Id, out var f) ? f.AvgDailySales.Milli : 0;
            var target = Math.Max(2 * item.LowStockThreshold.Milli, avg * CoverDays);
            var need = target - item.Stock.Milli;
            // Whole units, at least one: nobody orders 0.37 of a strip.
            var wholeUnits = Math.Max(1, (need + 999) / 1000);
            lines.Add(new PurchaseOrderLine(item.Id, Qty.Whole(wholeUnits)));
        }
        return lines;
    }

    public async Task<Result> Handle(MarkPurchaseOrderSentCommand command, CancellationToken ct)
    {
        if (await repo.GetOrderAsync(command.OrderId, ct) is not { } order) return Result.Failure(ErrorCodes.OrderNotFound);
        if (order.Status == PurchaseOrderStatus.Sent) return Result.Success();
        if (order.Status != PurchaseOrderStatus.Draft) return Result.Failure(ErrorCodes.OrderNotEditable);
        order.Status = PurchaseOrderStatus.Sent;
        await repo.SaveOrderAsync(order, ct);
        await outbox.AppendAsync("PurchaseOrder", new PurchaseOrderChanged(order.Id, clock.Now, (int)order.Status), ct);
        return Result.Success();
    }

    public async Task<Result<PurchaseEntry>> Handle(PrefillPurchaseFromOrderQuery q, CancellationToken ct)
    {
        if (await repo.GetOrderAsync(q.OrderId, ct) is not { } order) return Result.Failure<PurchaseEntry>(ErrorCodes.OrderNotFound);
        if (order.Status == PurchaseOrderStatus.Received) return Result.Failure<PurchaseEntry>(ErrorCodes.OrderNotEditable);
        var lines = new List<PurchaseEntryLine>();
        foreach (var l in order.Lines)
            if (await items.GetAsync(l.ItemId, ct) is { } item)
                lines.Add(new PurchaseEntryLine(item.Id, l.Qty, item.Cost, item.GstSlab));
        return Result.Success(new PurchaseEntry(order.SupplierId, "", clock.Today, lines, order.Id));
    }
}

/// <summary>No transaction: the share sheet waits on the user. Marking Sent is its own command (own transaction).</summary>
internal sealed class SharePurchaseOrderHandler(
    IPurchasingRepository repo, IItemRepository items, IShopProfileRepository shop, IReceiptShare share, ILocalizer text, ISender sender)
    : IRequestHandler<SharePurchaseOrderCommand, Result>
{
    public async Task<Result> Handle(SharePurchaseOrderCommand command, CancellationToken ct)
    {
        if (await repo.GetOrderAsync(command.OrderId, ct) is not { } order) return Result.Failure(ErrorCodes.OrderNotFound);
        var supplier = await repo.GetSupplierAsync(order.SupplierId, ct);
        var profile = await shop.GetAsync(ct);

        var sb = new StringBuilder();
        sb.AppendLine(text.Format("po.share.header", supplier?.Name ?? "", profile?.Name ?? ""));
        foreach (var line in order.Lines)
        {
            var item = await items.GetAsync(line.ItemId, ct);
            var name = item is null ? "?" : ItemNames.Pick(text.Language, item.TeluguName, item.EnglishName);
            sb.Append("• ").Append(name).Append(" — ").Append(IndianFormat.Quantity(line.Qty));
            if (item is not null) sb.Append(' ').Append(text.UnitLabel(item.Unit));
            sb.AppendLine();
        }
        if (profile is not null && !string.IsNullOrWhiteSpace(profile.Phone)) sb.AppendLine(text.Format("po.share.footer", profile.Phone));

        var shared = await share.ShareTextAsync(sb.ToString().TrimEnd(), text["po.share.title"], ct);
        if (shared.IsFailure) return shared;
        return order.Status == PurchaseOrderStatus.Draft ? await sender.Send(new MarkPurchaseOrderSentCommand(order.Id), ct) : Result.Success();
    }
}

// ---------- Batch & expiry ----------

/// <summary>Shared by the pre-finalize check (UI) and the finalize guard, so both see the same FEFO split.</summary>
internal sealed class ExpiredSaleChecker(IItemRepository items, IBatchLedger batches, IClock clock, ILocalizer text)
{
    public async Task<IReadOnlyList<ExpiredSaleLine>> FindAsync(IEnumerable<SaleQty> lines, CancellationToken ct)
    {
        var today = clock.Today;
        var result = new List<ExpiredSaleLine>();
        foreach (var g in lines.GroupBy(l => l.ItemId))
        {
            var qty = g.Sum(l => l.Qty.Milli);
            if (qty <= 0) continue;
            if (await items.GetAsync(g.Key, ct) is not { TracksBatches: true } item) continue;
            var itemBatches = await batches.GetBatchesAsync(item.Id, ct);
            var byId = itemBatches.ToDictionary(b => b.Id);
            foreach (var take in BatchAllocator.AllocateOut(itemBatches, qty, today).Takes.Where(t => t.Expired))
                result.Add(new ExpiredSaleLine(item.Id, text.Name(item), byId[take.BatchId].BatchNo, byId[take.BatchId].Expiry, new Qty(take.Milli)));
        }
        return result;
    }
}

/// <summary>
/// v2-stock: wraps FinalizeBillHandler (which stays untouched) — a bill that would sell from an expired batch is
/// refused with BATCH_EXPIRED unless the command says the owner confirmed (the UI asks through the owner-PIN gate).
/// Items without batches never reach the check's batch lookup, so their bills behave exactly as before.
/// </summary>
internal sealed class ExpiryGuardedFinalizeHandler(FinalizeBillHandler inner, IBillRepository bills, ExpiredSaleChecker checker)
    : IRequestHandler<FinalizeBillCommand, Result<FinalizedBill>>
{
    public async Task<Result<FinalizedBill>> Handle(FinalizeBillCommand command, CancellationToken ct)
    {
        var bill = command.Draft;
        if (!command.ExpiredBatchConfirmed && bill.Status == BillStatus.Draft
            && await bills.GetAsync(bill.Id, ct) is null or { Status: BillStatus.Draft })
        {
            var expired = await checker.FindAsync(bill.Lines.Select(l => new SaleQty(l.ItemId, l.Qty)), ct);
            if (expired.Count > 0)
                return Result.Failure<FinalizedBill>(Error.With(ErrorCodes.BatchExpired, ExpiryText.Describe(expired)));
        }
        return await inner.Handle(command, ct);
    }
}

internal static class ExpiryText
{
    public static string Describe(IEnumerable<ExpiredSaleLine> lines) =>
        string.Join(", ", lines.Select(l => l.BatchNo.Length == 0 ? l.Name : l.Name + " (" + l.BatchNo + ")").Distinct(StringComparer.Ordinal));
}

internal sealed class BatchHandlers(
    IItemRepository items, IBatchLedger batches, IOutbox outbox, IClock clock, ILocalizer text, ExpiredSaleChecker checker) :
    IRequestHandler<GetItemBatchesQuery, IReadOnlyList<Batch>>,
    IRequestHandler<AdjustBatchStockCommand, Result>,
    IRequestHandler<StockInBatchCommand, Result>,
    IRequestHandler<GetExpiringQuery, ExpiringReport>,
    IRequestHandler<CheckExpiredSaleQuery, IReadOnlyList<ExpiredSaleLine>>
{
    public async Task<IReadOnlyList<Batch>> Handle(GetItemBatchesQuery q, CancellationToken ct) =>
        BatchAllocator.SellingOrder(await batches.GetBatchesAsync(q.ItemId, ct), clock.Today).ToList();

    public async Task<Result> Handle(AdjustBatchStockCommand command, CancellationToken ct)
    {
        if (await items.GetAsync(command.ItemId, ct) is not { } item) return Result.Failure(ErrorCodes.ItemNotFound);
        if (!item.TracksBatches) return Result.Failure(ErrorCodes.ValidationFailed, "item does not track batches");
        if ((await batches.GetBatchesAsync(item.Id, ct)).All(b => b.Id != command.BatchId)) return Result.Failure(ErrorCodes.BatchNotFound);
        await batches.AdjustBatchAsync(item.Id, command.BatchId, command.Delta, command.Reason, command.Note?.Trim(), ct);
        await outbox.AppendAsync("Item", new BatchStockAdjusted(item.Id, clock.Now, command.BatchId, command.Delta.Milli, (int)command.Reason), ct);
        return Result.Success();
    }

    public async Task<Result> Handle(StockInBatchCommand command, CancellationToken ct)
    {
        if (await items.GetAsync(command.ItemId, ct) is not { } item) return Result.Failure(ErrorCodes.ItemNotFound);
        if (!item.TracksBatches) return Result.Failure(ErrorCodes.ValidationFailed, "item does not track batches");
        var batchId = await batches.ReceiveAsync(item.Id, command.Qty, StockMoveReason.Purchase, null,
            command.BatchNo.Trim().ToUpperInvariant(), command.Expiry, Paise.Zero, command.Note?.Trim(), ct);
        await outbox.AppendAsync("Item", new BatchStockAdjusted(item.Id, clock.Now, batchId ?? Guid.Empty, command.Qty.Milli, (int)StockMoveReason.Purchase), ct);
        return Result.Success();
    }

    public async Task<ExpiringReport> Handle(GetExpiringQuery q, CancellationToken ct)
    {
        var today = clock.Today;
        var tracked = (await items.GetActiveAsync(ct)).Where(i => i.TracksBatches).ToDictionary(i => i.Id);
        var lines = new List<ExpiringLine>();
        foreach (var b in await batches.GetStockedBatchesAsync(ct))
        {
            if (b.Qty.Milli <= 0 || b.Expiry is not { } expiry || !tracked.TryGetValue(b.ItemId, out var item)) continue;
            var daysLeft = expiry.DayNumber - today.DayNumber;
            if (daysLeft > q.WithinDays) continue;
            var value = new Paise((2 * b.Cost.Value * b.Qty.Milli + 1000) / 2000);   // cost × qty, half up
            lines.Add(new ExpiringLine(item.Id, b.Id, text.Name(item), item.Unit, b.BatchNo, expiry, b.Qty, value, daysLeft));
        }
        lines = lines.OrderBy(l => l.DaysLeft).ThenBy(l => l.Name, StringComparer.Ordinal).ToList();
        ExpiryBucket Bucket(Func<ExpiringLine, bool> pick)
        {
            var picked = lines.Where(pick).ToList();
            return new ExpiryBucket(picked.Count, new Paise(picked.Sum(l => l.Value.Value)));
        }
        return new ExpiringReport(
            Bucket(l => l.DaysLeft < 0),
            Bucket(l => l.DaysLeft is >= 0 and <= 30),
            Bucket(l => l.DaysLeft is >= 0 and <= 60),
            Bucket(l => l.DaysLeft is >= 0 and <= 90),
            lines);
    }

    public Task<IReadOnlyList<ExpiredSaleLine>> Handle(CheckExpiredSaleQuery q, CancellationToken ct) => checker.FindAsync(q.Lines, ct);
}

// ---------- Margin estimate ----------

internal sealed class MarginHandler(IBillRepository bills, IItemRepository items) : IRequestHandler<GetMarginEstimateQuery, MarginEstimate>
{
    public async Task<MarginEstimate> Handle(GetMarginEstimateQuery q, CancellationToken ct)
    {
        var sold = (await bills.ListByDayAsync(q.From, q.To, ct)).Where(b => b.Status == BillStatus.Finalized);
        var cache = new Dictionary<Guid, Item?>();
        long sales = 0, cost = 0;
        int with = 0, without = 0;
        foreach (var line in sold.SelectMany(b => b.Lines))
        {
            if (!cache.TryGetValue(line.ItemId, out var item)) cache[line.ItemId] = item = await items.GetAsync(line.ItemId, ct);
            if (item is null || item.Cost.Value <= 0) { without++; continue; }
            with++;
            var divisor = 10_000 + (long)line.GstSlab;
            sales += (line.Net.Value * 10_000 * 2 + divisor) / (2 * divisor);          // before GST, half up
            cost += (2 * item.Cost.Value * line.Qty.Milli + 1000) / 2000;             // qty × latest cost, half up
        }
        return new MarginEstimate(new Paise(sales), new Paise(cost), new Paise(sales - cost), with, without);
    }
}
