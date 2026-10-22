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

    /// <summary>
    /// A2-01: registered under the composition scheme (CGST s.10). Such a shop has a GSTIN but may not collect tax:
    /// it issues a Bill of Supply (Rule 49) that still shows its GSTIN and the composition declaration.
    /// </summary>
    public bool IsComposition { get; set; }

    /// <summary>True only for a regular GST registration: the shop issues Tax Invoices with a tax split.</summary>
    public bool ChargesGst => !string.IsNullOrWhiteSpace(Gstin) && !IsComposition;
    public string BillPrefix { get; set; } = "A";
    public long NextBillSequence { get; set; } = 1;
    public UiLanguage Language { get; set; } = UiLanguage.Telugu;   // FR-02

    /// <summary>The owner's own UPI QR as scanned ("upi://pay?pa=…&amp;pn=…&amp;mc=…"). Payments go to exactly this payee.</summary>
    public string? UpiQrPayload { get; set; }

    /// <summary>A photo of the owner's printed UPI QR (PNG/JPEG bytes, ≤ 2 MB), shown as-is when they prefer it.</summary>
    public byte[]? UpiQrImage { get; set; }
}

public enum UiLanguage { Telugu = 0, English = 1 }
