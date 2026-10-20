using Angadi.Application.Common;
using Angadi.Application.Platform;
using Angadi.Maui.Core;
using BarcodeScanning;
using Microsoft.Extensions.Logging;

namespace Angadi.Maui.Platform;

/// <summary>
/// FR-08 camera scanner (in locked scope; restored after the v1 cut). On-device ML Kit, no network.
/// Never a dead end: no camera, permission denied, or the shopkeeper taps "type it" → the manual box
/// (which a USB/Bluetooth barcode gun also types into).
/// </summary>
internal sealed class CameraBarcodeScanner(ManualBarcodeScanner manual, ILocalizer text, ILogger<CameraBarcodeScanner> log) : IBarcodeScanner
{
    public async Task<Result<string>> ScanAsync(CancellationToken ct = default)
    {
        try
        {
            if (!MediaPicker.Default.IsCaptureSupported) return await manual.ScanAsync(ct);

            var status = await MainThread.InvokeOnMainThreadAsync(Permissions.RequestAsync<Permissions.Camera>);
            if (status != PermissionStatus.Granted)
            {
                log.LogInformation("Camera permission not granted; manual entry");
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

/// <summary>Full-screen camera with one hint line and two big buttons. First readable code wins.</summary>
internal sealed class ScanPage : ContentPage
{
    public const string TypeInstead = "\u0000type";
    private readonly TaskCompletionSource<string?> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CameraView _camera;

    public Task<string?> Result => _done.Task;

    public ScanPage(ILocalizer text)
    {
        BackgroundColor = Colors.Black;
        _camera = new CameraView { CameraEnabled = true, VibrationOnDetected = true };
        _camera.OnDetectionFinished += (_, e) =>
        {
            foreach (var barcode in e.BarcodeResults)
            {
                var value = barcode.DisplayValue?.Trim();
                if (!string.IsNullOrEmpty(value)) { Finish(value); return; }
            }
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

        var buttons = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Star) }, ColumnSpacing = 12, Padding = 16 };
        buttons.Add(type, 0, 0);
        buttons.Add(cancel, 1, 0);

        var grid = new Grid { RowDefinitions = { new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto) } };
        grid.Add(hint, 0, 0);
        grid.Add(_camera, 0, 1);
        grid.Add(buttons, 0, 2);
        Content = grid;
    }

    public void Finish(string? value)
    {
        if (!_done.TrySetResult(value)) return;
        MainThread.BeginInvokeOnMainThread(() => _camera.CameraEnabled = false);
    }

    protected override bool OnBackButtonPressed()
    {
        Finish(null);
        return true;
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        Finish(null);
    }
}
