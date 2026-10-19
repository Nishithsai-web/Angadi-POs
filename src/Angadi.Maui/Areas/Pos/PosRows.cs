using Angadi.Domain.Items;
using QRCoder;

namespace Angadi.Maui.Areas.Pos;

// Immutable display rows. Records give value equality, so the ViewModel can sync collections
// in place (only changed rows are replaced) instead of clearing and re-adding on every edit.

/// <summary>Quick-sale tile or search result.</summary>
public sealed record ItemTile(Item Item, string Name, string EnglishName, string PriceText);

/// <summary>One bill line as shown at the counter.</summary>
public sealed record BillLineRow(
    Guid Id, string Name, string EnglishName, string Detail, string AmountText, string DiscountText, bool HasDiscount);

/// <summary>A voice match candidate. The score is deliberately not shown.</summary>
public sealed record CandidateRow(Guid ItemId, string DisplayName);

/// <summary>Udhaar customer picker row.</summary>
public sealed record CustomerRow(Guid Id, string Name, string Phone, string DuesText);

/// <summary>Receipt screen line summary.</summary>
public sealed record ReceiptLineRow(string Name, string Detail, string AmountText);

/// <summary>UPI tender QR (FR-14). QRCoder's PngByteQRCode is pure managed: no System.Drawing.</summary>
internal static class PosQr
{
    public static byte[] Png(string payload, int pixelsPerModule = 10)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data);
        return png.GetGraphic(pixelsPerModule);
    }
}
