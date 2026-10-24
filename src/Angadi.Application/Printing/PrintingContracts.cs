using Angadi.Application.Common;
using Angadi.Domain.Billing;
using Angadi.Domain.Shop;

namespace Angadi.Application.Printing;

// v2 (v2-print-shops): Bluetooth thermal receipt printing. The receipt is drawn with the SAME layout and fonts as
// the PDF (Infrastructure/Receipts), turned into a 1-bit raster and sent as ESC/POS bytes to a PAIRED printer.
// Printing is never part of billing: no transaction, bounded by PrintingOptions.Timeout, and every failure is a
// Result the screen shows as a toast — the bill is already saved whatever the printer does.

/// <summary>Paper roll width. 58 mm printers have 384 printable dots, 80 mm printers 576 (8 dots/mm).</summary>
public enum PaperWidth { Mm58 = 58, Mm80 = 80 }

/// <summary>The phone's receipt printer (device-wide: every shop on this phone uses the same printer).</summary>
/// <param name="Address">Bluetooth address of the paired printer; null = no printer chosen.</param>
/// <param name="Name">The printer's Bluetooth name, shown in Settings.</param>
/// <param name="AutoPrint">Print automatically after each bill.</param>
public sealed record PrinterSettings(string? Address, string? Name, PaperWidth Paper, bool AutoPrint)
{
    public static PrinterSettings None { get; } = new(null, null, PaperWidth.Mm58, false);
    public bool IsSet => !string.IsNullOrWhiteSpace(Address);
}

/// <summary>A printer already paired in Android's Bluetooth settings (Angadi never scans for devices).</summary>
public sealed record PairedPrinter(string Name, string Address);

/// <summary>Bounds for one print job. Bound from appsettings.json "Printing" when present.</summary>
public sealed class PrintingOptions
{
    /// <summary>Whole job (render + connect + send). A switched-off printer must never hang the screen.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(25);
}

public interface IPrinterSettingsStore
{
    Task<PrinterSettings> LoadAsync(CancellationToken ct = default);
    Task SaveAsync(PrinterSettings settings, CancellationToken ct = default);
}

/// <summary>
/// Classic Bluetooth SPP (serial port profile) to a paired printer. Android head implements it; elsewhere a stub
/// fails with PRINTER_NO_BLUETOOTH. Implementations run off the UI thread and honour cancellation.
/// </summary>
public interface IBluetoothPrinterTransport
{
    /// <summary>Asks for the Bluetooth permission (Android 12+) — only ever from the printer setup screen.</summary>
    Task<Result<IReadOnlyList<PairedPrinter>>> ListPairedAsync(CancellationToken ct = default);
    Task<Result> SendAsync(string address, byte[] data, CancellationToken ct = default);
}

/// <summary>Renders a receipt (or a test page) as a complete ESC/POS print job for the paper width.</summary>
public interface IReceiptRasterizer
{
    Task<Result<byte[]>> RenderReceiptAsync(Bill bill, ShopProfile shop, PaperWidth paper, CancellationToken ct = default);
    Task<Result<byte[]>> RenderTestPageAsync(string shopName, PaperWidth paper, CancellationToken ct = default);
}

public sealed record GetPrinterSettingsQuery : IQuery<PrinterSettings>;
public sealed record SavePrinterSettingsCommand(PrinterSettings Settings) : ICommand<Result>, INoTransaction;
public sealed record ListPairedPrintersQuery : IQuery<Result<IReadOnlyList<PairedPrinter>>>;
/// <summary>Prints a finalized (or voided) bill on the chosen printer. Fails PRINTER_NOT_SET when none is chosen.</summary>
public sealed record PrintReceiptCommand(Guid BillId) : ICommand<Result>, INoTransaction;
public sealed record PrintTestPageCommand : ICommand<Result>, INoTransaction;
