using Angadi.Application.Common;
using Angadi.Application.Platform;
using Angadi.Maui.Core;
using BarcodeScanning;
using Microsoft.Extensions.Logging;

namespace Angadi.Maui.Platform;

/// <summary>
/// FR-08 camera scanner (in locked scope; restored after the v1 cut). Codes are decoded on the device by Google ML Kit
/// (no image leaves the phone); ML Kit itself sends usage diagnostics to Google — disclosed in docs/PRIVACY_POLICY.md (A2-04).
/// Never a dead end: no camera, permission denied, or the shopkeeper taps "type it" → the manual box
/// (which a USB/Bluetooth barcode gun also types into).
/// </summary>
internal sealed class CameraBarcodeScanner(ManualBarcodeScanner manual, IDialogService dialogs, ILocalizer text, ILogger<CameraBarcodeScanner> log) : IBarcodeScanner
{
    public async Task<Result<string>> ScanAsync(CancellationToken ct = default)
    {
        try
        {
            if (!MediaPicker.Default.IsCaptureSupported) return await manual.ScanAsync(ct);

            var (status, permanent) = await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                var s = await Permissions.RequestAsync<Permissions.Camera>();
                return (s, s != PermissionStatus.Granted && !Permissions.ShouldShowRationale<Permissions.Camera>());
            });
            if (status != PermissionStatus.Granted)
            {
                log.LogInformation("Camera permission not granted (permanent: {Permanent}); manual entry", permanent);
                // A2-18: Android will not ask again — say where to turn it on, with a one-tap way there.
                if (permanent && await dialogs.ConfirmAsync(text["perm.title"], text["perm.camera.off"], text["perm.open.settings"], text["scan.manual"]))
                {
                    AppInfo.Current.ShowSettingsUI();
                    return Result.Failure<string>(ErrorCodes.ValidationFailed, "cancelled");
                }
                return await manual.ScanAsync(ct);
            }

            var page = new ScanPage(text);
            var nav = Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault()?.Page?.Navigation;
            if (nav is null) return await manual.ScanAsync(ct);

            await MainThread.InvokeOnMainThreadAsync(() => nav.PushModalAsync(page));
            using var reg = ct.Register(() => page.Finish(null));
            var code = await page.Result;
            await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                if (nav.ModalStack.Contains(page)) await nav.PopModalAsync();
            });

            return code switch
            {
                ScanPage.TypeInstead => await manual.ScanAsync(ct),
                null => Result.Failure<string>(ErrorCodes.ValidationFailed, "cancelled"),
                _ => Result.Success(code),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Camera scan failed; manual entry");
            return await manual.ScanAsync(ct);
        }
    }
}

/// <summary>
/// Full-screen camera, a hint, a torch and two big buttons. Reads retail formats only (EAN-13/8, UPC-A/E, Code 128,
/// Code 39, QR). A code is accepted only when the SAME value is read in two consecutive frames and its EAN check
/// digit is right — so a blurred or partial read can never put the wrong item on a bill.
/// </summary>
internal sealed class ScanPage : ContentPage
{
    public const string TypeInstead = "\u0000type";
    private readonly TaskCompletionSource<string?> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CameraView _camera;
    private string? _candidate;

    public Task<string?> Result => _done.Task;

    public ScanPage(ILocalizer text)
    {
        BackgroundColor = Colors.Black;
        _camera = new CameraView
        {
            CameraEnabled = true,
            VibrationOnDetected = false,   // vibrate once, on the confirmed read (below)
            BarcodeSymbologies = BarcodeFormats.Ean13 | BarcodeFormats.Ean8 | BarcodeFormats.Upca | BarcodeFormats.Upce
                                 | BarcodeFormats.Code128 | BarcodeFormats.Code39 | BarcodeFormats.QRCode,
        };
        _camera.OnDetectionFinished += (_, e) =>
        {
            string? frame = null;
            foreach (var barcode in e.BarcodeResults)
            {
                // A QR that carries a link (a shop's UPI QR: "upi://pay?pa=…") is kept exactly as encoded;
                // product barcodes are normalized and check-digit verified.
                var raw = (barcode.RawValue ?? barcode.DisplayValue)?.Trim();
                if (raw is not null && raw.Contains("://", StringComparison.Ordinal)) { frame = raw; break; }
                var value = Angadi.Domain.Items.Barcodes.Normalize(barcode.DisplayValue);
                if (value is not null && Angadi.Domain.Items.Barcodes.IsValid(value)) { frame = value; break; }
            }
            if (frame is null) return;
            if (frame != _candidate) { _candidate = frame; return; }   // wait for a second identical frame
            try { Vibration.Default.Vibrate(TimeSpan.FromMilliseconds(80)); } catch (Exception) { /* no vibrator */ }
            Finish(frame);
        };

        var hint = new Label
        {
            Text = text["scan.hint"], TextColor = Colors.White, FontSize = 18, FontFamily = Theme.Font,
            HorizontalTextAlignment = TextAlignment.Center, Margin = new Thickness(16, 24, 16, 0),
        };
        var type = Ui.Secondary(text["scan.manual"]);
        type.Clicked += (_, _) => Finish(TypeInstead);
        var cancel = Ui.Secondary(text["common.cancel"]);
        cancel.Clicked += (_, _) => Finish(null);

        var torch = Ui.Secondary(text["scan.torch"]);
        torch.Clicked += (_, _) =>
        {
            try { _camera.TorchOn = !_camera.TorchOn; } catch (Exception) { /* no flash on this device */ }
        };

        var buttons = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Star), new(GridLength.Star) }, ColumnSpacing = 12, Padding = 16 };
        buttons.Add(torch, 0, 0);
        buttons.Add(type, 1, 0);
        buttons.Add(cancel, 2, 0);

        var grid = new Grid { RowDefinitions = { new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto) } };
        grid.Add(hint, 0, 0);
        grid.Add(_camera, 0, 1);
        grid.Add(buttons, 0, 2);
        Content = grid;
    }

    public void Finish(string? value)
    {
        if (!_done.TrySetResult(value)) return;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_camera.Handler is not null) _camera.CameraEnabled = false;   // A2-08: never on a torn-down view
        });
    }

    protected override bool OnBackButtonPressed()
    {
        Finish(null);
        return true;
    }

    // FIX(A2-08b, 2026-10-22, S2): Flaw: on Android 16 predictive Back the modal is popped natively, and the
    // OnDisappearing → Finish → CameraEnabled=false that followed made BarcodeScanning post a runnable that touched
    // the already-disposed LifecycleCameraController — an unhandled ObjectDisposedException (E2E scanner-back,
    // app-log-clean). Fix: leaving the page only completes the result; the camera is stopped by its handler's own
    // teardown. Why: a crash-class error on every Back from the scanner. Logged in docs/AGENT_FIX_LOG.md.
    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _done.TrySetResult(null);
    }
}
