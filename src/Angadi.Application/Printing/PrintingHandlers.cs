using Angadi.Application.Common;
using Angadi.Application.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Angadi.Application.Printing;

internal sealed class PrinterSettingsHandlers(IPrinterSettingsStore store, IBluetoothPrinterTransport transport)
    : IRequestHandler<GetPrinterSettingsQuery, PrinterSettings>,
      IRequestHandler<SavePrinterSettingsCommand, Result>,
      IRequestHandler<ListPairedPrintersQuery, Result<IReadOnlyList<PairedPrinter>>>
{
    public Task<PrinterSettings> Handle(GetPrinterSettingsQuery query, CancellationToken ct) => store.LoadAsync(ct);

    public async Task<Result> Handle(SavePrinterSettingsCommand command, CancellationToken ct)
    {
        var s = command.Settings;
        if (!Enum.IsDefined(s.Paper)) return Result.Failure(ErrorCodes.ValidationFailed, "paper");
        await store.SaveAsync(s, ct);
        return Result.Success();
    }

    public Task<Result<IReadOnlyList<PairedPrinter>>> Handle(ListPairedPrintersQuery query, CancellationToken ct) =>
        transport.ListPairedAsync(ct);
}

/// <summary>
/// Render → send, bounded by <see cref="PrintingOptions.Timeout"/> even if the transport ignores cancellation
/// (WaitAsync), so the caller always gets an answer. No transaction: the DB lock is held only while the bill is read.
/// </summary>
internal sealed class PrintHandlers(
    IBillRepository bills, IShopProfileRepository shop, IPrinterSettingsStore store, IReceiptRasterizer rasterizer,
    IBluetoothPrinterTransport transport, IOptions<PrintingOptions> options, ILogger<PrintHandlers> log)
    : IRequestHandler<PrintReceiptCommand, Result>, IRequestHandler<PrintTestPageCommand, Result>
{
    public async Task<Result> Handle(PrintReceiptCommand command, CancellationToken ct)
    {
        var settings = await store.LoadAsync(ct);
        if (!settings.IsSet) return Result.Failure(ErrorCodes.PrinterNotSet);
        var bill = await bills.GetAsync(command.BillId, ct);
        if (bill is null) return Result.Failure(ErrorCodes.BillNotFound);
        var profile = await shop.GetAsync(ct);
        if (profile is null) return Result.Failure(ErrorCodes.ValidationFailed, "shop profile missing");

        var result = await RunBoundedAsync(settings, t => rasterizer.RenderReceiptAsync(bill, profile, settings.Paper, t), ct);
        if (result.IsFailure) log.LogWarning("Receipt print failed for bill {BillId}: {Code} {Detail}", bill.Id, result.Error.Code, result.Error.Detail);
        else log.LogInformation("Receipt printed for bill {BillId} ({Paper})", bill.Id, settings.Paper);
        return result;
    }

    public async Task<Result> Handle(PrintTestPageCommand command, CancellationToken ct)
    {
        var settings = await store.LoadAsync(ct);
        if (!settings.IsSet) return Result.Failure(ErrorCodes.PrinterNotSet);
        var name = (await shop.GetAsync(ct))?.Name ?? "";
        var result = await RunBoundedAsync(settings, t => rasterizer.RenderTestPageAsync(name, settings.Paper, t), ct);
        if (result.IsFailure) log.LogWarning("Test page failed: {Code} {Detail}", result.Error.Code, result.Error.Detail);
        return result;
    }

    private async Task<Result> RunBoundedAsync(PrinterSettings settings, Func<CancellationToken, Task<Result<byte[]>>> render, CancellationToken ct)
    {
        var timeout = options.Value.Timeout;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            var job = await render(cts.Token).WaitAsync(timeout, ct).ConfigureAwait(false);
            if (job.IsFailure) return Result.Failure(job.Error);
            return await transport.SendAsync(settings.Address!, job.Value, cts.Token).WaitAsync(timeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            await cts.CancelAsync().ConfigureAwait(false);   // tell a still-running transport to give up
            return Result.Failure(ErrorCodes.PrinterTimeout);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Result.Failure(ErrorCodes.PrinterTimeout);
        }
    }
}
