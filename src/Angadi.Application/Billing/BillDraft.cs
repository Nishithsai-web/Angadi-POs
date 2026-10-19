using Angadi.Application.Voice;
using Angadi.Domain.Billing;
using Angadi.Domain.Common;
using Angadi.Domain.Items;

namespace Angadi.Application.Billing;

/// <summary>
/// The bill being built at the counter (ADR-13). Held by the POS ViewModel, never persisted until
/// FinalizeBillCommand. All edits go through here so the ViewModel stays thin and this stays tested.
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
            qty = UnitConversion.FromAmount(amount, item.Price);
        else if (parsed.Qty is { } spoken)
        {
            qty = UnitConversion.ToItemUnit(spoken, parsed.Unit, item.Unit, out var exact);
            needsConfirm = !exact;
        }
        else
            qty = Qty.Whole(1);

        if (qty.Milli <= 0) qty = Qty.Whole(1);
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

    public void SetTender(TenderType tender, Guid? customerId = null, Paise? cashReceived = null)
    {
        Bill.Tender = tender;
        Bill.CustomerId = tender == TenderType.Udhaar ? customerId : null;
        Bill.CashReceived = tender == TenderType.Cash ? cashReceived ?? Paise.Zero : Paise.Zero;
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
