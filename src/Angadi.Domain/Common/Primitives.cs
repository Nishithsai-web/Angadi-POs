namespace Angadi.Domain.Common;

/// <summary>
/// Base for all persisted entities. Guid ids (not autoincrement ints) so v2 cloud sync
/// can merge rows from several devices without key collisions.
/// </summary>
public abstract class Entity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// Money in paise (₹1 = 100). Never decimal/double in storage: sqlite-net maps decimal to REAL,
/// which loses precision. All arithmetic stays in whole paise.
/// </summary>
public readonly record struct Paise(long Value) : IComparable<Paise>
{
    public static readonly Paise Zero = new(0);
    public static Paise FromRupees(long rupees) => new(checked(rupees * 100));
    public static Paise operator +(Paise a, Paise b) => new(checked(a.Value + b.Value));
    public static Paise operator -(Paise a, Paise b) => new(checked(a.Value - b.Value));
    public static Paise operator -(Paise a) => new(checked(-a.Value));
    public static bool operator <(Paise a, Paise b) => a.Value < b.Value;
    public static bool operator >(Paise a, Paise b) => a.Value > b.Value;
    public static bool operator <=(Paise a, Paise b) => a.Value <= b.Value;
    public static bool operator >=(Paise a, Paise b) => a.Value >= b.Value;
    public int CompareTo(Paise other) => Value.CompareTo(other.Value);
    public override string ToString() => $"₹{Value / 100m:0.00}";
}

/// <summary>
/// Quantity in thousandths of the item's unit (1 kg = 1000, "అర కేజీ" = 500, 3 pcs = 3000).
/// Integer storage keeps stock reconciliation exact (NFR-06).
/// </summary>
public readonly record struct Qty(long Milli) : IComparable<Qty>
{
    public static readonly Qty Zero = new(0);
    public static Qty Whole(long units) => new(checked(units * 1000));
    public static Qty operator +(Qty a, Qty b) => new(checked(a.Milli + b.Milli));
    public static Qty operator -(Qty a, Qty b) => new(checked(a.Milli - b.Milli));
    public static Qty operator -(Qty a) => new(checked(-a.Milli));
    public static bool operator <=(Qty a, Qty b) => a.Milli <= b.Milli;
    public static bool operator >=(Qty a, Qty b) => a.Milli >= b.Milli;
    public static bool operator <(Qty a, Qty b) => a.Milli < b.Milli;
    public static bool operator >(Qty a, Qty b) => a.Milli > b.Milli;
    public int CompareTo(Qty other) => Milli.CompareTo(other.Milli);
}

/// <summary>A discount: percent (basis points, 1000 = 10%) or flat paise. Never both.</summary>
public readonly record struct Discount(DiscountKind Kind, long Amount)
{
    public static readonly Discount None = new(DiscountKind.None, 0);
    public static Discount Percent(int basisPoints) => new(DiscountKind.PercentBasisPoints, basisPoints);
    public static Discount Flat(Paise amount) => new(DiscountKind.FlatPaise, amount.Value);
}

public enum DiscountKind { None = 0, PercentBasisPoints = 1, FlatPaise = 2 }
