using Angadi.Application.Voice;
using Angadi.Domain.Billing;
using Angadi.Domain.Common;
using Angadi.Domain.Items;

namespace Angadi.Application.Billing;

/// <summary>
/// The bill being built at the counter (ADR-13). Held by the POS ViewModel; a snapshot is saved after every change
/// (SaveBillDraftCommand) so a phone call or Android closing the app never loses a half-built bill. All edits go through here so the ViewModel stays thin and this stays tested.
/// Totals are recomputed after every change with the same BillCalculator used at finalize.
/// </summary>
public sealed class BillDraft
{
    private readonly bool _shopHasGstin;

    public BillDraft(bool shopHasGstin)
    {
        _shopHasGstin = shopHasGstin;
        Bill = new Bill();
    }

    public Bill Bill { get; private set; }
    public IReadOnlyList<BillLine> Lines => Bill.Lines;
    public bool IsEmpty => Bill.Lines.Count == 0;
    public event EventHandler? Changed;

    /// <summary>
    /// FR-08/10/11: adds an item. Scanning or tapping the same item again adds to the existing line
    /// (+1 of its unit) instead of creating a duplicate. Returns the line that changed.
    /// </summary>
    public BillLine Add(Item item, Qty? qty = null, LineSource source = LineSource.Manual)
    {
        var amount = qty ?? Qty.Whole(1);
        if (amount.Milli <= 0) throw new ArgumentOutOfRangeException(nameof(qty), "Quantity must be positive.");

        var existing = Bill.Lines.FirstOrDefault(l => l.ItemId == item.Id && l.UnitPrice == item.Price && l.LineDiscount == Discount.None);
        if (existing is not null)
        {
            existing.Qty += amount;
            Recompute();
            return existing;
        }

        var line = new BillLine
        {
            ItemId = item.Id,
            TeluguName = item.TeluguName,
            EnglishName = item.EnglishName,
            Unit = item.Unit,
            UnitPrice = item.Price,
            GstSlab = item.GstSlab,
            Qty = amount,
            Source = source,
        };
        Bill.Lines.Add(line);
        Recompute();
        return line;
    }

    /// <summary>
    /// FR-06: adds a voice match. Converts the spoken unit to the item's unit (200 గ్రా of a per-kg
    /// item → 0.2 kg) or the spoken amount to a quantity (₹10 of onions at ₹35/kg → 0.286 kg).
    /// <paramref name="needsConfirm"/> is true when the units didn't line up.
    /// </summary>
    public BillLine AddFromVoice(Item item, ParsedLine parsed, out bool needsConfirm)
    {
        needsConfirm = false;
        Qty qty;
        if (parsed.Amount is { } amount)
        {
            qty = UnitConversion.FromAmount(amount, item.Price);
            needsConfirm = item.Price.Value <= 0;   // "₹10 of" a zero-priced item has no quantity: ask
        }
        else if (parsed.Qty is { } spoken)
        {
            qty = UnitConversion.ToItemUnit(spoken, parsed.Unit, item.Unit, out var exact);
            needsConfirm = !exact;
        }
        else
            qty = Qty.Whole(1);

        // LOW (audit): never silently turn a nonsense quantity into 1 — keep 1 as a placeholder but make the
        // shopkeeper confirm it, so a mishearing can't slip onto the bill.
        if (qty.Milli <= 0) { qty = Qty.Whole(1); needsConfirm = true; }
        return Add(item, qty, LineSource.Voice);
    }

    public void SetQty(Guid lineId, Qty qty)
    {
        if (qty.Milli <= 0) { Remove(lineId); return; }
        Line(lineId).Qty = qty;
        Recompute();
    }

    /// <summary>FR-11: price override for this bill only (the item's price is unchanged).</summary>
    public void SetPrice(Guid lineId, Paise price)
    {
        if (price.Value < 0) throw new ArgumentOutOfRangeException(nameof(price));
        Line(lineId).UnitPrice = price;
        Recompute();
    }

    public void SetLineDiscount(Guid lineId, Discount discount)
    {
        Line(lineId).LineDiscount = discount;
        Recompute();
    }

    public void SetBillDiscount(Discount discount)
    {
        Bill.BillDiscount = discount;
        Recompute();
    }

    public void Remove(Guid lineId)
    {
        Bill.Lines.RemoveAll(l => l.Id == lineId);
        Recompute();
    }

    public void SetTender(TenderType tender, Guid? customerId = null, Paise? cashReceived = null, string? upiRef = null)
    {
        Bill.UpiRef = tender == TenderType.Upi && !string.IsNullOrWhiteSpace(upiRef) ? upiRef.Trim() : null;
        Bill.Tender = tender;
        // A customer can be attached to any bill (account history); udhaar requires one.
        if (customerId is not null) Bill.CustomerId = customerId;
        Bill.CashReceived = tender == TenderType.Cash ? cashReceived ?? Paise.Zero : Paise.Zero;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Attach (or with null, detach) the customer whose account this bill belongs to.</summary>
    public void SetCustomer(Guid? customerId)
    {
        Bill.CustomerId = customerId;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public Paise ChangeDue => Bill.Tender == TenderType.Cash && Bill.CashReceived.Value > 0
        ? BillCalculator.ChangeDue(Bill)
        : Paise.Zero;

    /// <summary>Starts a fresh bill (after finalize, or "New bill").</summary>
    public void Clear()
    {
        Bill = new Bill();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// What survives the app being closed mid-bill: lines, discounts and the attached customer, under the same
    /// Bill.Id (so finalize stays idempotent). The tender is chosen at pay time and is not kept.
    /// </summary>
    public BillDraftSnapshot ToSnapshot() => new(
        Bill.Id,
        [.. Bill.Lines.Select(l => new BillDraftLine(l.Id, l.ItemId, l.TeluguName, l.EnglishName, l.Unit, l.UnitPrice.Value,
            l.GstSlab, l.Qty.Milli, l.LineDiscount.Kind, l.LineDiscount.Amount, l.Source))],
        Bill.BillDiscount.Kind, Bill.BillDiscount.Amount, Bill.CustomerId);

    /// <summary>Rebuilds a draft from a snapshot. Lines with a non-positive quantity or negative price are dropped.</summary>
    public static BillDraft Restore(BillDraftSnapshot snapshot, bool shopHasGstin)
    {
        var draft = new BillDraft(shopHasGstin);
        draft.Bill.Id = snapshot.BillId;
        draft.Bill.CustomerId = snapshot.CustomerId;
        draft.Bill.BillDiscount = new Discount(snapshot.BillDiscountKind, snapshot.BillDiscountAmount);
        foreach (var l in snapshot.Lines)
        {
            if (l.QtyMilli <= 0 || l.UnitPricePaise < 0) continue;
            draft.Bill.Lines.Add(new BillLine
            {
                Id = l.LineId, ItemId = l.ItemId, TeluguName = l.TeluguName, EnglishName = l.EnglishName, Unit = l.Unit,
                UnitPrice = new Paise(l.UnitPricePaise), GstSlab = l.GstSlab, Qty = new Qty(l.QtyMilli),
                LineDiscount = new Discount(l.DiscountKind, l.DiscountAmount), Source = l.Source,
            });
        }
        try { draft.Recompute(); }
        catch (OverflowException) { return new BillDraft(shopHasGstin); }   // a tampered snapshot never crashes the counter
        return draft;
    }

    private BillLine Line(Guid id) => Bill.Lines.First(l => l.Id == id);

    private void Recompute()
    {
        if (Bill.Lines.Count == 0)
        {
            Bill.SubTotal = Bill.TaxTotal = Bill.GrandTotal = Bill.RoundingAdjustment = Bill.BillDiscountAmount = Paise.Zero;
            Bill.TaxSplits = [];
        }
        else
        {
            BillCalculator.Compute(Bill, _shopHasGstin);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

public sealed record BillDraftLine(
    Guid LineId, Guid ItemId, string TeluguName, string EnglishName, Unit Unit, long UnitPricePaise,
    GstSlab GstSlab, long QtyMilli, DiscountKind DiscountKind, long DiscountAmount, LineSource Source);

public sealed record BillDraftSnapshot(
    Guid BillId, IReadOnlyList<BillDraftLine> Lines, DiscountKind BillDiscountKind, long BillDiscountAmount, Guid? CustomerId)
{
    public bool IsEmpty => Lines.Count == 0;
}
