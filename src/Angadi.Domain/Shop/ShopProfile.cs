using Angadi.Domain.Common;

namespace Angadi.Domain.Shop;

/// <summary>FR-01. Exactly one row.</summary>
public sealed class ShopProfile : Entity
{
    public string Name { get; set; } = "";
    public string Address { get; set; } = "";
    public string Phone { get; set; } = "";
    public string UpiId { get; set; } = "";   // e.g. ramu@okaxis, for the receipt/tender QR
    public string? Gstin { get; set; }        // null → Bill of Supply (FR-18)
    public string BillPrefix { get; set; } = "A";
    public long NextBillSequence { get; set; } = 1;
    public UiLanguage Language { get; set; } = UiLanguage.Telugu;   // FR-02
}

public enum UiLanguage { Telugu = 0, English = 1 }
