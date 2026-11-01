using System.Globalization;
using Angadi.Domain.Common;

namespace Angadi.Domain.Inventory;

/// <summary>
/// v2-stock: one batch of a batch-tracked item (pharmacies). Invariant for such items:
/// Σ Batch.Qty == Item.Stock == Σ StockMove.Delta, and every StockMove written while tracking is on carries its
/// BatchId. The "no batch" placeholder (BatchNo "" and no expiry) holds stock that existed before tracking was
/// switched on, and any quantity sold beyond what the batches hold (stock may go negative, exactly as in v1).
/// </summary>
public sealed class Batch : Entity
{
    public Guid ItemId { get; set; }
    public string BatchNo { get; set; } = "";
    /// <summary>Last day the batch may be sold (a printed "MM/YY" expiry means that month's last day). Null = unknown.</summary>
    public DateOnly? Expiry { get; set; }
    public Qty Qty { get; set; }
    /// <summary>Cost per whole unit, before GST, from the latest purchase into this batch. Zero = unknown.</summary>
    public Paise Cost { get; set; }

    public bool IsPlaceholder => BatchNo.Length == 0 && Expiry is null;
    public bool IsExpired(DateOnly today) => BatchAllocator.IsExpired(Expiry, today);
}

/// <summary>Quantity taken from (or put back into) one batch.</summary>
public sealed record BatchTake(Guid BatchId, long Milli, bool Expired);

/// <summary>A split of one stock change across batches; <see cref="ShortfallMilli"/> is what no batch could cover.</summary>
public sealed record BatchAllocation(IReadOnlyList<BatchTake> Takes, long ShortfallMilli)
{
    public long ExpiredMilli => Takes.Where(t => t.Expired).Sum(t => t.Milli);
}

/// <summary>First-Expiry-First-Out, pure and deterministic, so the stock ledger and the expired-sale check agree.</summary>
public static class BatchAllocator
{
    public static bool IsExpired(DateOnly? expiry, DateOnly today) => expiry is { } e && e < today;

    /// <summary>
    /// Selling order: unexpired batches first, soonest expiry first (unknown expiry last), then expired batches.
    /// Expired stock is only ever reached when unexpired stock cannot cover the quantity — and the finalize
    /// guard refuses that unless the owner confirmed through the PIN gate.
    /// </summary>
    public static IEnumerable<Batch> SellingOrder(IEnumerable<Batch> batches, DateOnly today) =>
        batches.OrderBy(b => IsExpired(b.Expiry, today) ? 1 : 0)
            .ThenBy(b => b.Expiry ?? DateOnly.MaxValue)
            .ThenBy(b => b.CreatedAt)
            .ThenBy(b => b.Id);

    /// <summary>Takes <paramref name="milli"/> out of the batches that hold stock, FEFO.</summary>
    public static BatchAllocation AllocateOut(IEnumerable<Batch> batches, long milli, DateOnly today)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(milli);
        var takes = new List<BatchTake>();
        var left = milli;
        foreach (var b in SellingOrder(batches, today))
        {
            if (left == 0) break;
            if (b.Qty.Milli <= 0) continue;
            var take = Math.Min(left, b.Qty.Milli);
            takes.Add(new BatchTake(b.Id, take, IsExpired(b.Expiry, today)));
            left -= take;
        }
        return new BatchAllocation(takes, left);
    }

    /// <summary>
    /// Puts <paramref name="milli"/> back into the batches a bill took it from (void / return), up to what each
    /// batch gave that bill net of earlier returns. Whatever is left over is the shortfall.
    /// </summary>
    public static BatchAllocation AllocateBack(IEnumerable<(Guid BatchId, long SoldMilli)> soldByBatch, long milli)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(milli);
        var takes = new List<BatchTake>();
        var left = milli;
        foreach (var (batchId, sold) in soldByBatch)
        {
            if (left == 0) break;
            if (sold <= 0) continue;
            var take = Math.Min(left, sold);
            takes.Add(new BatchTake(batchId, take, false));
            left -= take;
        }
        return new BatchAllocation(takes, left);
    }

    /// <summary>"MM/YY" or "MM/YYYY" (→ that month's last day), or "DD/MM/YYYY". Null when unreadable.</summary>
    public static DateOnly? ParseExpiry(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.Trim().Split(new[] { '/', '-', '.', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 2 or > 3 || parts.Any(p => p.Length > 4 || !p.All(char.IsAsciiDigit))) return null;
        var n = parts.Select(p => int.Parse(p, NumberStyles.None, CultureInfo.InvariantCulture)).ToArray();
        static int Year(int y) => y < 100 ? 2000 + y : y;
        int day, month, year;
        if (n.Length == 2) { month = n[0]; year = Year(n[1]); day = 0; }
        else { day = n[0]; month = n[1]; year = Year(n[2]); }
        if (year is < 2000 or > 2099 || month is < 1 or > 12) return null;
        var last = DateTime.DaysInMonth(year, month);
        if (day == 0) day = last;
        if (day < 1 || day > last) return null;
        return new DateOnly(year, month, day);
    }
}
