using System.Globalization;
using Angadi.Application.Billing;
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
        s.AddSingleton<ISpeechToText, FallbackSpeechToText>();
        s.AddSingleton<IReceiptShare, ReceiptShare>();
        s.AddSingleton<ManualBarcodeScanner>();
        s.AddSingleton<IBarcodeScanner, CameraBarcodeScanner>();
        // Registered after AddAngadiInfrastructure, so it replaces the C2 stub (last registration wins).
        s.AddSingleton<ISubscriptionService, PlaySubscriptionService>();
        return s;
    }
}

/// <summary>
/// P4: never a dead mic. te-IN first; if the device has no Telugu recogniser, retry once in the device language;
/// if speech is unavailable altogether the failure says "manual" and the POS opens typed search.
/// </summary>
internal sealed class FallbackSpeechToText(AndroidSpeechToText inner, ILogger<FallbackSpeechToText> log) : ISpeechToText
{
    private static readonly string[] LanguageMissing = ["language_not_supported", "language_unavailable"];

    public async Task<Result<string>> ListenAsync(string culture, IProgress<string>? partials, CancellationToken ct = default)
    {
        var first = await inner.ListenAsync(culture, partials, ct);
        if (first.IsSuccess || !LanguageMissing.Contains(first.Error.Detail)) return Manual(first);

        var deviceCulture = CultureInfo.CurrentUICulture.Name;
        if (string.IsNullOrEmpty(deviceCulture) || deviceCulture.Equals(culture, StringComparison.OrdinalIgnoreCase)) return Manual(first);

        log.LogWarning("Telugu recogniser unavailable; falling back to device language");
        return Manual(await inner.ListenAsync(deviceCulture, partials, ct));
    }

    private static Result<string> Manual(Result<string> r) =>
        r.IsFailure && r.Error.Detail is "recognizer_unavailable" or "language_not_supported" or "language_unavailable"
            ? Result.Failure<string>(ErrorCodes.VoiceNothingHeard, "manual")
            : r;
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
            // FIX(SEND-1, 2026-10-20, FIX-4): Flaw: the number was used exactly as stored — a row arriving via
            // an old-backup restore or import can hold an un-normalized number, and the message (a full bill,
            // or a dues balance) would go to a malformed or wrong-country number. Fix: normalize at this choke
            // point — every WhatsApp/SMS send passes through here — and fail closed when it cannot be a valid
            // Indian mobile. Why here: the send handlers live upstream; this is the last place the number can
            // be checked before it leaves the device. Logged in docs/AGENT_FIX_LOG.md.
            if (PhoneNumbers.Normalize(phoneE164) is not { } phone) return Result.Failure(ErrorCodes.CustomerNoPhone);
            if (!Sms.Default.IsComposeSupported) return Result.Failure(ErrorCodes.ShareFailed, "no sms");
            await MainThread.InvokeOnMainThreadAsync(() => Sms.Default.ComposeAsync(new SmsMessage(message, ["+" + phone])));
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
            // FIX(SEND-1, 2026-10-20, FIX-4): same choke-point normalize-or-fail-closed as OpenSmsAsync above —
            // the wa.me URL is built only from a validated, normalized number, never from the raw stored one.
            if (PhoneNumbers.Normalize(phoneE164) is not { } phone) return Result.Failure(ErrorCodes.CustomerNoPhone);
            var uri = new Uri("https://wa.me/" + phone + "?text=" + Uri.EscapeDataString(message));
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
