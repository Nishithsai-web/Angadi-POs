namespace Angadi.Infrastructure.Receipts;

/// <summary>
/// Opens the receipt TTFs (FR-17). Each factory returns a new readable stream; the renderer reads it once,
/// caches the typeface and disposes the stream. MAUI: FileSystem.OpenAppPackageFileAsync("fonts/…").
/// Telugu = NotoSansTelugu-*.ttf, Latin = NotoSans-*.ttf (digits, ₹, English and anything Telugu lacks).
/// </summary>
public sealed record ReceiptFonts(Func<Stream> Telugu, Func<Stream> TeluguBold, Func<Stream> Latin, Func<Stream> LatinBold);
