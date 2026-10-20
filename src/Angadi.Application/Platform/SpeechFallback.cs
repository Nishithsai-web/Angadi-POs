using System.Globalization;
using Angadi.Application.Common;
using Microsoft.Extensions.Logging;

namespace Angadi.Application.Platform;

/// <summary>
/// Maps android.speech.SpeechRecognizer.ERROR_* values (numeric, so constants added after API 28
/// need no binding lookups) to our error codes.
/// FIX(VOICE-9, 2026-10-20, FIX-1): moved here from the Android implementation — the table had zero
/// tests because it lived inside the MAUI platform class. Logged in docs/AGENT_FIX_LOG.md.
/// </summary>
public static class SpeechErrors
{
    public static Error Map(int code) => code switch
    {
        1 => new Error(ErrorCodes.VoiceNoInternet, "network_timeout"),          // ERROR_NETWORK_TIMEOUT
        2 => new Error(ErrorCodes.VoiceNoInternet, "network"),                  // ERROR_NETWORK
        4 => new Error(ErrorCodes.VoiceNoInternet, "server"),                   // ERROR_SERVER
        10 => new Error(ErrorCodes.VoiceNoInternet, "too_many_requests"),       // ERROR_TOO_MANY_REQUESTS (API 31)
        11 => new Error(ErrorCodes.VoiceNoInternet, "server_disconnected"),     // ERROR_SERVER_DISCONNECTED (API 31)
        9 => new Error(ErrorCodes.VoicePermissionDenied, "insufficient_permissions"), // ERROR_INSUFFICIENT_PERMISSIONS
        6 => new Error(ErrorCodes.VoiceNothingHeard, "speech_timeout"),         // ERROR_SPEECH_TIMEOUT
        7 => new Error(ErrorCodes.VoiceNothingHeard, "no_match"),               // ERROR_NO_MATCH
        12 => new Error(ErrorCodes.VoiceNothingHeard, "language_not_supported"),// ERROR_LANGUAGE_NOT_SUPPORTED (API 31)
        13 => new Error(ErrorCodes.VoiceNothingHeard, "language_unavailable"),  // ERROR_LANGUAGE_UNAVAILABLE (API 31)
        _ => new Error(ErrorCodes.VoiceNothingHeard, "error_" + code.ToString(CultureInfo.InvariantCulture)), // AUDIO, CLIENT, BUSY, …
    };
}

/// <summary>
/// P4: never a dead mic. te-IN first; if the device has no Telugu recogniser, retry once in the
/// device language; if speech is unavailable altogether the failure says "manual" and the POS
/// opens typed search.
/// FIX(VOICE-9, 2026-10-20, FIX-1): this chain used to live in the MAUI project wrapped around the
/// CONCRETE AndroidSpeechToText, so the tests project (which does not reference MAUI) could not
/// test it at all. It now wraps ISpeechToText in the Application layer; the device culture is
/// injectable for deterministic tests. Logged in docs/AGENT_FIX_LOG.md.
/// </summary>
public sealed class FallbackSpeechToText(ISpeechToText inner, ILogger<FallbackSpeechToText> log, Func<string>? deviceCulture = null) : ISpeechToText
{
    private static readonly string[] LanguageMissing = ["language_not_supported", "language_unavailable"];

    public async Task<Result<string>> ListenAsync(string culture, IProgress<string>? partials, CancellationToken ct = default)
    {
        var first = await inner.ListenAsync(culture, partials, ct);
        if (first.IsSuccess || !LanguageMissing.Contains(first.Error.Detail)) return Manual(first);

        var fallbackCulture = deviceCulture?.Invoke() ?? CultureInfo.CurrentUICulture.Name;
        if (string.IsNullOrEmpty(fallbackCulture) || fallbackCulture.Equals(culture, StringComparison.OrdinalIgnoreCase)) return Manual(first);

        log.LogWarning("Telugu recogniser unavailable; falling back to device language");
        return Manual(await inner.ListenAsync(fallbackCulture, partials, ct));
    }

    private static Result<string> Manual(Result<string> r) =>
        r.IsFailure && r.Error.Detail is "recognizer_unavailable" or "language_not_supported" or "language_unavailable"
            ? Result.Failure<string>(ErrorCodes.VoiceNothingHeard, "manual")
            : r;
}
