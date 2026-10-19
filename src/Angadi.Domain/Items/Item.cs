using Angadi.Domain.Common;

namespace Angadi.Domain.Items;

/// <summary>FR-03. One item library for kirana goods and medicines alike — no per-shop-type forks.</summary>
public sealed class Item : Entity
{
    public string TeluguName { get; set; } = "";
    public string EnglishName { get; set; } = "";

    /// <summary>Spoken/written variants ("బియ్యం", "biyyam", "rice"). FR-07 corrections append here.</summary>
    public List<string> Aliases { get; set; } = [];

    public string Category { get; set; } = "";
    public Unit Unit { get; set; } = Unit.Piece;

    /// <summary>Price per 1 whole unit, GST-inclusive (MRP norm, SRS A1).</summary>
    public Paise Price { get; set; }
    public GstSlab GstSlab { get; set; } = GstSlab.Zero;
    public string? Hsn { get; set; }

    /// <summary>Indexed and unique when present (FR-08).</summary>
    public string? Barcode { get; set; }

    /// <summary>Denormalised current stock. Source of truth is the StockMove ledger (FR-24/26).</summary>
    public Qty Stock { get; set; }
    public Qty LowStockThreshold { get; set; }

    public bool IsQuickSale { get; set; }   // FR-10
    public bool IsActive { get; set; } = true;

    public bool IsLowStock => IsActive && Stock <= LowStockThreshold;
}

public enum Unit { Piece = 0, Kilogram = 1, Gram = 2, Litre = 3, Millilitre = 4, Pack = 5, Strip = 6 }

/// <summary>Stored as basis points so the slab value is the enum value (500 = 5%).</summary>
public enum GstSlab { Zero = 0, Five = 500, Twelve = 1200, Eighteen = 1800, TwentyEight = 2800 }
