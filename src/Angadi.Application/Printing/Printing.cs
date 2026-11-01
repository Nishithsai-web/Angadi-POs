using Angadi.Application.Common;
using Angadi.Application.Persistence;
using Angadi.Domain.Billing;
using Angadi.Domain.Customers;
using Angadi.Domain.Shop;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Angadi.Application.Printing;

// v2 printing: Bluetooth thermal receipts (58/80 mm ESC/POS). The receipt is drawn with the SAME layout and fonts as
// the PDF (Infrastructure/Receipts), turned into a 1-bit raster and sent as ESC/POS bytes to a printer the owner has
// already PAIRED in Android's Bluetooth settings. Printing is never part of billing: no transaction, the whole job is
// bounded by PrintingOptions.Timeout, and every failure is a Result the screen shows — the bill is already saved
// whatever the printer does.

/// <summary>Paper roll width. 58 mm printers print 384 dots across, 80 mm printers 576 (203 dpi, 8 dots/mm).</summary>
public enum PaperWidth { Mm58 = 58, Mm80 = 80 }

/// <summary>The phone's receipt printer. Hardware, not books: stored on the phone, not in backups.</summary>
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

/// <summary>Bounds for one print job.</summary>
public sealed class PrintingOptions
{
    /// <summary>
    /// Whole job (wait for an earlier job, render, connect, send). A switched-off printer must never hang the screen:
    /// the transport gives up connecting after 2 × 10 s; the rest is for a long 80 mm bill over slow SPP (~10 KB/s).
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(60);
}

public interface IPrinterSettingsStore
{
    Task<PrinterSettings> LoadAsync(CancellationToken ct = default);
    Task SaveAsync(PrinterSettings settings, CancellationToken ct = default);
}

/// <summary>
/// Classic Bluetooth SPP (serial port profile) to a paired printer. The Android head implements it; elsewhere a stub
/// fails with PRINTER_NO_BLUETOOTH. Implementations run blocking I/O off the UI thread and honour cancellation.
/// </summary>
public interface IBluetoothPrinterTransport
{
    /// <summary>Asks for the Bluetooth permission (Android 12+) — called only from the printer setup screen.</summary>
    Task<Result<IReadOnlyList<PairedPrinter>>> ListPairedAsync(CancellationToken ct = default);

    /// <summary>Connects, checks paper when the printer reports it, writes <paramref name="job"/>, closes. Never asks for permission.</summary>
    Task<Result> SendAsync(string address, byte[] job, CancellationToken ct = default);
}

/// <summary>Renders a document as a complete ESC/POS print job (reset, raster bands, feed, cut) for the paper width.</summary>
public interface IThermalReceiptRenderer
{
    Task<Result<byte[]>> RenderReceiptAsync(Bill bill, ShopProfile shop, Customer? customer, PaperWidth paper, CancellationToken ct = default);
    Task<Result<byte[]>> RenderReturnAsync(SalesReturn ret, Bill bill, ShopProfile shop, Customer? customer, PaperWidth paper, CancellationToken ct = default);
    Task<Result<byte[]>> RenderTestPageAsync(string shopName, PaperWidth paper, CancellationToken ct = default);
}

public sealed record GetPrinterSettingsQuery : IQuery<PrinterSettings>;
public sealed record SavePrinterSettingsCommand(PrinterSettings Settings) : ICommand<Result>, INoTransaction;
/// <summary>Paired devices to choose from. May show the Android 12+ Bluetooth permission dialog.</summary>
public sealed record ListPairedPrintersQuery : IQuery<Result<IReadOnlyList<PairedPrinter>>>;
/// <summary>Prints a finalized (or voided) bill. Fails PRINTER_NOT_SET when no printer is chosen.</summary>
public sealed record PrintReceiptCommand(Guid BillId) : ICommand<Result>, INoTransaction;
/// <summary>Prints a return note (R-…).</summary>
public sealed record PrintReturnNoteCommand(Guid ReturnId) : ICommand<Result>, INoTransaction;
public sealed record PrintTestPageCommand : ICommand<Result>, INoTransaction;

internal sealed class PrinterSettingsHandlers(IPrinterSettingsStore store, IBluetoothPrinterTransport transport)
    : IRequestHandler<GetPrinterSettingsQuery, PrinterSettings>,
      IRequestHandler<SavePrinterSettingsCommand, Result>,
      IRequestHandler<ListPairedPrintersQuery, Result<IReadOnlyList<PairedPrinter>>>
{
    public Task<PrinterSettings> Handle(GetPrinterSettingsQuery query, CancellationToken ct) => store.LoadAsync(ct);

    public async Task<Result> Handle(SavePrinterSettingsCommand command, CancellationToken ct)
    {
        var s = command.Settings;
        if (s is null || !Enum.IsDefined(s.Paper)) return Result.Failure(ErrorCodes.ValidationFailed, "paper");
        // No printer → nothing to print automatically.
        await store.SaveAsync(s.IsSet ? s : s with { Address = null, Name = null, AutoPrint = false }, ct);
        return Result.Success();
    }

    public Task<Result<IReadOnlyList<PairedPrinter>>> Handle(ListPairedPrintersQuery query, CancellationToken ct) =>
        transport.ListPairedAsync(ct);
}

/// <summary>
/// Render → send, one job at a time (auto-print and a Print tap must not fight over the printer's single SPP link),
/// bounded by ONE deadline of <see cref="PrintingOptions.Timeout"/> — enforced with WaitAsync even if the transport
/// ignores cancellation, so the caller always gets an answer. No transaction: the DB is touched only to read.
/// </summary>
internal sealed class PrintHandlers(
    IBillRepository bills, IReturnRepository returns, IShopProfileRepository shop, ICustomerRepository customers,
    IPrinterSettingsStore store, IThermalReceiptRenderer renderer, IBluetoothPrinterTransport transport,
    IOptions<PrintingOptions> options, ILogger<PrintHandlers> log)
    : IRequestHandler<PrintReceiptCommand, Result>,
      IRequestHandler<PrintReturnNoteCommand, Result>,
      IRequestHandler<PrintTestPageCommand, Result>
{
    private static readonly SemaphoreSlim OneJob = new(1, 1);

    public async Task<Result> Handle(PrintReceiptCommand command, CancellationToken ct)
    {
        var settings = await store.LoadAsync(ct);
        if (!settings.IsSet) return Result.Failure(ErrorCodes.PrinterNotSet);
        if (await bills.GetAsync(command.BillId, ct) is not { } bill || bill.Status == BillStatus.Draft)
            return Result.Failure(ErrorCodes.BillNotFound);
        if (await shop.GetAsync(ct) is not { } profile) return Result.Failure(ErrorCodes.ValidationFailed, "shop profile missing");
        var customer = bill.CustomerId is { } customerId ? await customers.GetAsync(customerId, ct) : null;

        var result = await RunAsync(settings, t => renderer.RenderReceiptAsync(bill, profile, customer, settings.Paper, t), ct);
        Log(result, "bill", bill.Id, settings.Paper);
        return result;
    }

    public async Task<Result> Handle(PrintReturnNoteCommand command, CancellationToken ct)
    {
        var settings = await store.LoadAsync(ct);
        if (!settings.IsSet) return Result.Failure(ErrorCodes.PrinterNotSet);
        if (await returns.GetAsync(command.ReturnId, ct) is not { } ret) return Result.Failure(ErrorCodes.ValidationFailed, "return not found");
        if (await bills.GetAsync(ret.BillId, ct) is not { } bill) return Result.Failure(ErrorCodes.BillNotFound);
        if (await shop.GetAsync(ct) is not { } profile) return Result.Failure(ErrorCodes.ValidationFailed, "shop profile missing");
        var customer = ret.CustomerId is { } customerId ? await customers.GetAsync(customerId, ct) : null;

        var result = await RunAsync(settings, t => renderer.RenderReturnAsync(ret, bill, profile, customer, settings.Paper, t), ct);
        Log(result, "return", ret.Id, settings.Paper);
        return result;
    }

    public async Task<Result> Handle(PrintTestPageCommand command, CancellationToken ct)
    {
        var settings = await store.LoadAsync(ct);
        if (!settings.IsSet) return Result.Failure(ErrorCodes.PrinterNotSet);
        var name = (await shop.GetAsync(ct))?.Name ?? "";
        var result = await RunAsync(settings, t => renderer.RenderTestPageAsync(name, settings.Paper, t), ct);
        Log(result, "test page", Guid.Empty, settings.Paper);
        return result;
    }

    private async Task<Result> RunAsync(PrinterSettings settings, Func<CancellationToken, Task<Result<byte[]>>> render, CancellationToken ct)
    {
        var timeout = options.Value.Timeout;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var entered = false;
        try
        {
            entered = await OneJob.WaitAsync(timeout, ct).ConfigureAwait(false);
            if (!entered) return Result.Failure(ErrorCodes.PrinterTimeout, "busy");
            var job = await render(cts.Token).WaitAsync(cts.Token).ConfigureAwait(false);
            if (job.IsFailure) return Result.Failure(job.Error);
            return await transport.SendAsync(settings.Address!, job.Value, cts.Token).WaitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Result.Failure(ErrorCodes.PrinterTimeout);
        }
        finally
        {
            if (entered) OneJob.Release();
        }
    }

    private void Log(Result result, string what, Guid id, PaperWidth paper)
    {
        // Warning, not Error: a printer that is off or out of paper is an everyday event, not an app fault.
        if (result.IsFailure) log.LogWarning("Print of {What} {Id} failed: {Code} {Detail}", what, id, result.Error.Code, result.Error.Detail);
        else log.LogInformation("Printed {What} {Id} ({Paper})", what, id, paper);
    }
}
