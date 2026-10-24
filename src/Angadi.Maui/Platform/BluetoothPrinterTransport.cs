using Android.Bluetooth;
using Android.Content;
using Angadi.Application.Common;
using Angadi.Application.Printing;
using Microsoft.Extensions.Logging;

namespace Angadi.Maui.Platform;

/// <summary>
/// v2 printing: classic Bluetooth SPP to a printer the owner already PAIRED in Android settings. No discovery, so no
/// location or BLUETOOTH_SCAN permission. Android 12+: BLUETOOTH_CONNECT is a runtime permission, requested only
/// from Settings → Receipt printer (<see cref="ListPairedAsync"/>); a print never pops a permission dialog over the
/// counter. Android 9–11: BLUETOOTH/BLUETOOTH_ADMIN are install-time (manifest, maxSdkVersion 30).
/// Every blocking socket call runs on a worker thread; cancellation closes the socket, which aborts a hanging
/// connect or write. The handler above bounds the whole job with a timeout as well.
/// Hardware note: this class cannot run in CI (no Bluetooth in the emulator); it is exercised on a real printer.
/// </summary>
internal sealed class AndroidBluetoothPrinterTransport(ILogger<AndroidBluetoothPrinterTransport> log) : IBluetoothPrinterTransport
{
    private static readonly Java.Util.UUID Spp = Java.Util.UUID.FromString("00001101-0000-1000-8000-00805F9B34FB")!;
    private const int ChunkBytes = 1024;

    public async Task<Result<IReadOnlyList<PairedPrinter>>> ListPairedAsync(CancellationToken ct = default)
    {
        try
        {
            var permission = await EnsurePermissionAsync(request: true);
            if (permission.IsFailure) return Result.Failure<IReadOnlyList<PairedPrinter>>(permission.Error);
            var adapter = Adapter();
            if (adapter is null) return Result.Failure<IReadOnlyList<PairedPrinter>>(ErrorCodes.PrinterNoBluetooth);
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

    public async Task<Result> SendAsync(string address, byte[] data, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        var permission = await EnsurePermissionAsync(request: false);
        if (permission.IsFailure) return permission;
        var adapter = Adapter();
        if (adapter is null) return Result.Failure(ErrorCodes.PrinterNoBluetooth);
        if (!adapter.IsEnabled) return Result.Failure(ErrorCodes.PrinterBluetoothOff);
        if (!BluetoothAdapter.CheckBluetoothAddress(address)) return Result.Failure(ErrorCodes.PrinterNotSet);
        return await Task.Run(() => SendBlocking(adapter, address, data, ct), CancellationToken.None);
    }

    private Result SendBlocking(BluetoothAdapter adapter, string address, byte[] data, CancellationToken ct)
    {
        BluetoothSocket? socket = null;
        var stage = "connect";
        try
        {
            var device = adapter.GetRemoteDevice(address);
            if (device is null) return Result.Failure(ErrorCodes.PrinterNotSet);
            if (!OperatingSystem.IsAndroidVersionAtLeast(31))
            {
                try { adapter.CancelDiscovery(); } catch (Java.Lang.SecurityException) { /* not discovering anyway */ }
            }
            socket = Connect(device, ct, secure: true) ?? Connect(device, ct, secure: false);
            if (socket is null)
                return ct.IsCancellationRequested ? Result.Failure(ErrorCodes.PrinterTimeout) : Result.Failure(ErrorCodes.PrinterConnectFailed);

            stage = "write";
            var output = socket.OutputStream ?? throw new Java.IO.IOException("no output stream");
            for (var offset = 0; offset < data.Length; offset += ChunkBytes)
            {
                ct.ThrowIfCancellationRequested();
                output.Write(data, offset, Math.Min(ChunkBytes, data.Length - offset));
            }
            output.Flush();
            // Many cheap printers drop the tail of a job if the link closes the instant the last byte is written.
            Thread.Sleep(800);
            log.LogInformation("Print job sent ({Bytes} bytes)", data.Length);
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
        catch (Exception ex) when (ex is Java.Lang.Exception or IOException)
        {
            log.LogWarning(ex, "Print failed at {Stage}", stage);
            if (ct.IsCancellationRequested) return Result.Failure(ErrorCodes.PrinterTimeout);
            return Result.Failure(stage == "connect" ? ErrorCodes.PrinterConnectFailed : ErrorCodes.PrinterSendFailed, ex.GetType().Name);
        }
        finally
        {
            try { socket?.Close(); } catch (Java.IO.IOException) { /* already closed */ }
            socket?.Dispose();
        }
    }

    /// <summary>Some cheap printers only accept the insecure RFCOMM socket; try secure first.</summary>
    private BluetoothSocket? Connect(BluetoothDevice device, CancellationToken ct, bool secure)
    {
        if (ct.IsCancellationRequested) return null;
        var socket = secure ? device.CreateRfcommSocketToServiceRecord(Spp) : device.CreateInsecureRfcommSocketToServiceRecord(Spp);
        if (socket is null) return null;
        using var abort = ct.Register(() =>
        {
            try { socket.Close(); } catch (Java.IO.IOException) { /* closing aborts connect() */ }
        });
        try
        {
            socket.Connect();
            return socket;
        }
        catch (Java.IO.IOException ex)
        {
            log.LogInformation("RFCOMM connect ({Mode}) failed: {Error}", secure ? "secure" : "insecure", ex.GetType().Name);
            try { socket.Close(); } catch (Java.IO.IOException) { }
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
    /// Only BLUETOOTH_CONNECT on Android 12+ — MAUI's built-in Permissions.Bluetooth would also demand location on
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
