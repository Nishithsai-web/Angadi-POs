using Android.Bluetooth;
using Android.Content;
using Angadi.Application.Common;
using Angadi.Application.Printing;
using Angadi.Infrastructure.Printing;
using Microsoft.Extensions.Logging;

namespace Angadi.Maui.Platform;

/// <summary>
/// v2 printing: classic Bluetooth SPP to a printer the owner already PAIRED in Android settings. No discovery, so no
/// location and no BLUETOOTH_SCAN permission. Android 12+: BLUETOOTH_CONNECT is a runtime permission, requested only
/// from Settings → Receipt printer (<see cref="ListPairedAsync"/>); a print never pops a permission dialog over the
/// counter. Android 9–11: BLUETOOTH/BLUETOOTH_ADMIN are install-time (manifest, maxSdkVersion 30).
/// Every blocking socket call runs on a worker thread; cancellation closes the socket, which aborts a hanging connect
/// or write; the connect has its own 10 s bound and the handler above bounds the whole job.
/// Hardware note: this class cannot run in CI (the emulator has no Bluetooth printer); it is exercised on a real printer.
/// </summary>
internal sealed class AndroidBluetoothPrinterTransport(ILogger<AndroidBluetoothPrinterTransport> log) : IBluetoothPrinterTransport
{
    private static readonly Java.Util.UUID Spp = Java.Util.UUID.FromString("00001101-0000-1000-8000-00805F9B34FB")!;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    private const int ChunkBytes = 1024;
    private const int StatusWaitMs = 600;

    public async Task<Result<IReadOnlyList<PairedPrinter>>> ListPairedAsync(CancellationToken ct = default)
    {
        try
        {
            var adapter = Adapter();
            if (adapter is null) return Result.Failure<IReadOnlyList<PairedPrinter>>(ErrorCodes.PrinterNoBluetooth);
            var permission = await EnsurePermissionAsync(request: true);
            if (permission.IsFailure) return Result.Failure<IReadOnlyList<PairedPrinter>>(permission.Error);
            if (!adapter.IsEnabled) return Result.Failure<IReadOnlyList<PairedPrinter>>(ErrorCodes.PrinterBluetoothOff);
            var paired = (adapter.BondedDevices ?? [])
                .Where(d => !string.IsNullOrEmpty(d.Address))
                .Select(d => new PairedPrinter(string.IsNullOrWhiteSpace(d.Name) ? d.Address! : d.Name!, d.Address!))
                .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            return paired.Count == 0
                ? Result.Failure<IReadOnlyList<PairedPrinter>>(ErrorCodes.PrinterNonePaired)
                : Result.Success<IReadOnlyList<PairedPrinter>>(paired);
        }
        catch (Exception ex) when (ex is Java.Lang.SecurityException or PermissionException)
        {
            log.LogWarning(ex, "Bluetooth permission problem listing paired printers");
            return Result.Failure<IReadOnlyList<PairedPrinter>>(ErrorCodes.PrinterPermissionDenied, ex.GetType().Name);
        }
    }

    public async Task<Result> SendAsync(string address, byte[] job, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        var adapter = Adapter();
        if (adapter is null) return Result.Failure(ErrorCodes.PrinterNoBluetooth);
        var permission = await EnsurePermissionAsync(request: false);
        if (permission.IsFailure) return permission;
        if (!adapter.IsEnabled) return Result.Failure(ErrorCodes.PrinterBluetoothOff);
        if (!BluetoothAdapter.CheckBluetoothAddress(address)) return Result.Failure(ErrorCodes.PrinterNotSet);
        return await Task.Run(() => SendBlocking(adapter, address, job, ct), CancellationToken.None);
    }

    private Result SendBlocking(BluetoothAdapter adapter, string address, byte[] job, CancellationToken ct)
    {
        BluetoothSocket? socket = null;
        var stage = "connect";
        try
        {
            var device = adapter.GetRemoteDevice(address);
            if (device is null) return Result.Failure(ErrorCodes.PrinterNotSet);
            socket = Connect(device, ct, secure: true) ?? Connect(device, ct, secure: false);
            if (socket is null)
                return ct.IsCancellationRequested ? Result.Failure(ErrorCodes.PrinterTimeout) : Result.Failure(ErrorCodes.PrinterConnectFailed);

            stage = "write";
            var output = socket.OutputStream ?? throw new Java.IO.IOException("no output stream");
            if (PaperIsOut(socket, output, ct)) return Result.Failure(ErrorCodes.PrinterNoPaper);
            for (var offset = 0; offset < job.Length; offset += ChunkBytes)
            {
                ct.ThrowIfCancellationRequested();
                output.Write(job, offset, Math.Min(ChunkBytes, job.Length - offset));
            }
            output.Flush();
            // Many cheap printers drop the tail of a job if the link closes the instant the last byte is written.
            Thread.Sleep(800);
            log.LogInformation("Print job sent ({Bytes} bytes)", job.Length);
            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            return Result.Failure(ErrorCodes.PrinterTimeout);
        }
        catch (Java.Lang.SecurityException ex)
        {
            log.LogWarning(ex, "Bluetooth permission revoked");
            return Result.Failure(ErrorCodes.PrinterPermissionDenied, ex.GetType().Name);
        }
        catch (Exception ex) when (ex is Java.Lang.Exception or IOException or ObjectDisposedException)
        {
            log.LogWarning(ex, "Print failed at {Stage}", stage);
            if (ct.IsCancellationRequested) return Result.Failure(ErrorCodes.PrinterTimeout);
            return Result.Failure(stage == "connect" ? ErrorCodes.PrinterConnectFailed : ErrorCodes.PrinterDisconnected, ex.GetType().Name);
        }
        finally
        {
            try { socket?.Close(); } catch (Java.IO.IOException) { /* already closed */ }
            socket?.Dispose();
        }
    }

    /// <summary>
    /// DLE EOT 4 (paper sensor). Printers that implement it answer one byte within a few ms, even while offline; a
    /// printer that stays silent (or answers garbage) is assumed to have paper, so the check never blocks a print.
    /// </summary>
    private bool PaperIsOut(BluetoothSocket socket, Stream output, CancellationToken ct)
    {
        if ((socket.InputStream as Android.Runtime.InputStreamInvoker)?.BaseInputStream is not { } input) return false;
        output.Write(EscPos.PaperStatusRequest());
        output.Flush();
        for (var waited = 0; waited < StatusWaitMs; waited += 50)
        {
            ct.ThrowIfCancellationRequested();
            if (input.Available() > 0)
            {
                var status = input.Read();
                if (status < 0) return false;
                var paperOut = EscPos.IsPaperOut((byte)status);
                if (paperOut) log.LogWarning("Printer reports paper end (status 0x{Status:X2})", status);
                return paperOut;
            }
            Thread.Sleep(50);
        }
        return false;
    }

    /// <summary>Some cheap printers only accept the insecure RFCOMM socket; try secure first. Bounded by <see cref="ConnectTimeout"/>.</summary>
    private BluetoothSocket? Connect(BluetoothDevice device, CancellationToken ct, bool secure)
    {
        if (ct.IsCancellationRequested) return null;
        var socket = secure ? device.CreateRfcommSocketToServiceRecord(Spp) : device.CreateInsecureRfcommSocketToServiceRecord(Spp);
        if (socket is null) return null;
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bound.CancelAfter(ConnectTimeout);
        using var abort = bound.Token.Register(() =>
        {
            try { socket.Close(); } catch (Java.IO.IOException) { /* closing aborts connect() */ }
        });
        try
        {
            socket.Connect();
            return socket;
        }
        catch (Exception ex) when (ex is Java.IO.IOException or IOException)
        {
            log.LogInformation("RFCOMM connect ({Mode}) failed: {Error}", secure ? "secure" : "insecure", ex.GetType().Name);
            try { socket.Close(); } catch (Java.IO.IOException) { /* already closed */ }
            socket.Dispose();
            return null;
        }
    }

    private static BluetoothAdapter? Adapter()
    {
        var manager = global::Android.App.Application.Context.GetSystemService(Context.BluetoothService) as BluetoothManager;
        return manager?.Adapter;
    }

    private static async Task<Result> EnsurePermissionAsync(bool request)
    {
        var status = await MainThread.InvokeOnMainThreadAsync(Permissions.CheckStatusAsync<BluetoothConnectPermission>);
        if (status != PermissionStatus.Granted && request)
            status = await MainThread.InvokeOnMainThreadAsync(Permissions.RequestAsync<BluetoothConnectPermission>);
        return status == PermissionStatus.Granted ? Result.Success() : Result.Failure(ErrorCodes.PrinterPermissionDenied);
    }

    /// <summary>
    /// Only BLUETOOTH_CONNECT on Android 12+. MAUI's built-in Permissions.Bluetooth would also demand location on
    /// older versions and BLUETOOTH_SCAN on newer ones, which a paired-printer connection does not need.
    /// </summary>
    private sealed class BluetoothConnectPermission : Permissions.BasePlatformPermission
    {
        public override (string androidPermission, bool isRuntime)[] RequiredPermissions
        {
            get
            {
                if (OperatingSystem.IsAndroidVersionAtLeast(31))
                    return [(global::Android.Manifest.Permission.BluetoothConnect, true)];
                return [];
            }
        }
    }
}
