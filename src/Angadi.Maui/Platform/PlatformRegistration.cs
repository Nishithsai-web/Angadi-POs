using Angadi.Application.Common;
using Angadi.Application.Platform;
using Angadi.Maui.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Angadi.Maui.Platform;

public static class PlatformRegistration
{
    public static IServiceCollection AddAndroidPlatform(this IServiceCollection s)
    {
        s.AddSingleton<AndroidSpeechToText>();
        // VOICE-9 (FIX-1, 2026-10-20): FallbackSpeechToText moved to Angadi.Application.Platform
        // (it now wraps ISpeechToText, so the fallback chain is unit-testable); the factory keeps
        // the concrete Android recogniser as its inner service. Logged in docs/AGENT_FIX_LOG.md.
        s.AddSingleton<ISpeechToText>(sp => new FallbackSpeechToText(
            sp.GetRequiredService<AndroidSpeechToText>(),
            sp.GetRequiredService<ILogger<FallbackSpeechToText>>()));
        s.AddSingleton<IReceiptShare, ReceiptShare>();
        s.AddSingleton<ManualBarcodeScanner>();
        s.AddSingleton<IBarcodeScanner, CameraBarcodeScanner>();
        // Registered after AddAngadiInfrastructure, so it replaces the C2 stub (last registration wins).
        s.AddSingleton<ISubscriptionService, PlaySubscriptionService>();
        // v2 printing: replaces Infrastructure's no-Bluetooth stub (last registration wins).
        s.AddSingleton<Angadi.Application.Printing.IBluetoothPrinterTransport, AndroidBluetoothPrinterTransport>();
        return s;
    }
}

/// <summary>P3: OS share sheet for receipt PDFs/backups/logs; wa.me deep links for WhatsApp (free, ADR-4).</summary>
internal sealed class ReceiptShare(ILogger<ReceiptShare> log) : IReceiptShare
{
    public async Task<Result> ShareFileAsync(string filePath, string title, CancellationToken ct = default)
    {
        try
        {
            var contentType = Path.GetExtension(filePath).ToLowerInvariant() switch
            {
                ".pdf" => "application/pdf",
                ".zip" => "application/zip",
                _ => "application/octet-stream",
            };
            await MainThread.InvokeOnMainThreadAsync(() => Share.Default.RequestAsync(new ShareFileRequest
            {
                Title = title,
                File = new ShareFile(filePath, contentType),
            }));
            return Result.Success();
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Share failed");
            return Result.Failure(ErrorCodes.ShareFailed, ex.GetType().Name);
        }
    }

    public async Task<Result> OpenSmsAsync(string phoneE164, string message, CancellationToken ct = default)
    {
        try
        {
            if (!Sms.Default.IsComposeSupported) return Result.Failure(ErrorCodes.ShareFailed, "no sms");
            await MainThread.InvokeOnMainThreadAsync(() => Sms.Default.ComposeAsync(new SmsMessage(message, ["+" + phoneE164])));
            return Result.Success();
        }
        catch (Exception ex)
        {
            log.LogError(ex, "SMS compose failed");
            return Result.Failure(ErrorCodes.ShareFailed, ex.GetType().Name);
        }
    }

    public async Task<Result> ShareTextAsync(string text, string title, CancellationToken ct = default)
    {
        try
        {
            await MainThread.InvokeOnMainThreadAsync(() => Share.Default.RequestAsync(new ShareTextRequest { Text = text, Title = title }));
            return Result.Success();
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Text share failed");
            return Result.Failure(ErrorCodes.ShareFailed, ex.GetType().Name);
        }
    }

    public async Task<Result> OpenWhatsAppAsync(string phoneE164, string message, CancellationToken ct = default)
    {
        try
        {
            var uri = new Uri("https://wa.me/" + phoneE164 + "?text=" + Uri.EscapeDataString(message));
            var opened = await MainThread.InvokeOnMainThreadAsync(() => Launcher.Default.OpenAsync(uri));
            return opened ? Result.Success() : Result.Failure(ErrorCodes.ShareFailed, "no handler");
        }
        catch (Exception ex)
        {
            log.LogError(ex, "WhatsApp link failed");
            return Result.Failure(ErrorCodes.ShareFailed, ex.GetType().Name);
        }
    }
}

/// <summary>
/// FR-09 manual entry: the camera scanner's fallback, and what a USB or Bluetooth barcode gun types into.
/// </summary>
internal sealed class ManualBarcodeScanner(IDialogService dialogs, ILocalizer text) : IBarcodeScanner
{
    public async Task<Result<string>> ScanAsync(CancellationToken ct = default)
    {
        var code = await dialogs.PromptAsync(text["pos.scan"], text["items.barcode"], text["common.ok"], text["common.cancel"], keyboard: Keyboard.Numeric);
        return string.IsNullOrWhiteSpace(code)
            ? Result.Failure<string>(ErrorCodes.ValidationFailed, "cancelled")
            : Result.Success(code.Trim());
    }
}
