#if ANDROID
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Android.Content;
using Android.OS;
using Android.Speech;
using Angadi.Application.Common;
using Angadi.Application.Platform;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Networking;

namespace Angadi.Maui.Platform;

/// <summary>
/// ISpeechToText over Android's SpeechRecognizer (Google, te-IN). Needs internet (NFR-01): Google's
/// recognizer streams audio to Google's servers (see docs/PLATFORM_NOTES.md).
/// One listen at a time: a new call aborts the one in flight. Cancellation aborts (no result).
/// Never logs what was said (NFR-08), only error codes.
/// </summary>
public sealed class AndroidSpeechToText(ILogger<AndroidSpeechToText> log) : ISpeechToText
{
    /// <summary>Silence after speech that ends the utterance (FR-06: lines within 3 s of speech ending).</summary>
    private const long CompleteSilenceMs = 1800;
    private const long PossiblyCompleteSilenceMs = 1500;
    /// <summary>Safety net if the recognizer never calls back (seen on some OEM builds).</summary>
    private static readonly TimeSpan HardTimeout = TimeSpan.FromSeconds(20);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private CancellationTokenSource? _current;

    public async Task<Result<string>> ListenAsync(string culture, IProgress<string>? partials, CancellationToken ct = default)
    {
        if (Connectivity.Current.NetworkAccess != NetworkAccess.Internet)
            return Result.Failure<string>(ErrorCodes.VoiceNoInternet, "offline");

        var permission = await MainThread.InvokeOnMainThreadAsync(async () =>
        {
            var status = await Permissions.CheckStatusAsync<Permissions.Microphone>();
            return status == PermissionStatus.Granted ? status : await Permissions.RequestAsync<Permissions.Microphone>();
        });
        if (permission != PermissionStatus.Granted)
            return Result.Failure<string>(ErrorCodes.VoicePermissionDenied, permission.ToString());

        // One listen at a time: abort whoever holds the mic, then take it.
        lock (_sync) { _current?.Cancel(); }
        try { await _gate.WaitAsync(ct); }
        catch (System.OperationCanceledException) { return Cancelled(); }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (_sync) { _current = linked; }
        try
        {
            linked.CancelAfter(HardTimeout);
            return await ListenOnceAsync(string.IsNullOrWhiteSpace(culture) ? "te-IN" : culture, partials, linked.Token, ct);
        }
        catch (Exception ex) when (ex is not System.OperationCanceledException)
        {
            log.LogError(ex, "Speech recognizer failed to start");
            return Result.Failure<string>(ErrorCodes.VoiceNothingHeard, "start_failed");
        }
        finally
        {
            lock (_sync) { if (ReferenceEquals(_current, linked)) _current = null; }
            _gate.Release();
        }
    }

    private async Task<Result<string>> ListenOnceAsync(string culture, IProgress<string>? partials, CancellationToken token, CancellationToken callerToken)
    {
        var tcs = new TaskCompletionSource<Result<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        SpeechRecognizer? recognizer = null;

        // SpeechRecognizer must be created and driven on the main (looper) thread.
        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            var context = global::Android.App.Application.Context;
            if (!SpeechRecognizer.IsRecognitionAvailable(context))
            {
                tcs.TrySetResult(Result.Failure<string>(ErrorCodes.VoiceNothingHeard, "recognizer_unavailable"));
                return;
            }

            recognizer = SpeechRecognizer.CreateSpeechRecognizer(context);
            if (recognizer is null)
            {
                tcs.TrySetResult(Result.Failure<string>(ErrorCodes.VoiceNothingHeard, "recognizer_unavailable"));
                return;
            }

            recognizer.SetRecognitionListener(new Listener(tcs, partials));
            recognizer.StartListening(BuildIntent(culture));
        });

        using var registration = token.Register(() =>
        {
            var timedOut = !callerToken.IsCancellationRequested;
            tcs.TrySetResult(timedOut
                ? Result.Failure<string>(ErrorCodes.VoiceNothingHeard, "timeout")
                : Cancelled());
        });

        var result = await tcs.Task;

        // Always release the mic and the service binding, whatever happened.
        var toDestroy = recognizer;
        if (toDestroy is not null)
        {
            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                try { toDestroy.Cancel(); } catch (Exception) { /* already stopped */ }
                try { toDestroy.Destroy(); } catch (Exception) { /* already destroyed */ }
            });
        }

        if (result.IsFailure)
            log.LogInformation("Speech listen ended: {Code} ({Detail})", result.Error.Code, result.Error.Detail);
        return result;
    }

    private static Intent BuildIntent(string culture)
    {
        var intent = new Intent(RecognizerIntent.ActionRecognizeSpeech);
        intent.PutExtra(RecognizerIntent.ExtraLanguageModel, RecognizerIntent.LanguageModelFreeForm);
        intent.PutExtra(RecognizerIntent.ExtraLanguage, culture);
        intent.PutExtra(RecognizerIntent.ExtraLanguagePreference, culture);
        intent.PutExtra(RecognizerIntent.ExtraPartialResults, true);
        intent.PutExtra(RecognizerIntent.ExtraMaxResults, 1);
        // Google's recognizer treats these as hints and may ignore them; the 20 s hard timeout backs them up.
        intent.PutExtra(RecognizerIntent.ExtraSpeechInputCompleteSilenceLengthMillis, CompleteSilenceMs);
        intent.PutExtra(RecognizerIntent.ExtraSpeechInputPossiblyCompleteSilenceLengthMillis, PossiblyCompleteSilenceMs);
        return intent;
    }

    private static Result<string> Cancelled() => Result.Failure<string>(ErrorCodes.ValidationFailed, "cancelled");

    /// <summary>
    /// Maps android.speech.SpeechRecognizer.ERROR_* (by numeric value, so newer constants added after
    /// API 28 need no binding lookups) to our error codes.
    /// </summary>
    internal static Error MapError(int code) => code switch
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
        _ => new Error(ErrorCodes.VoiceNothingHeard, "error_" + code),          // AUDIO, CLIENT, BUSY, …
    };

    /// <summary>Callbacks arrive on the main thread. Completes the TCS exactly once.</summary>
    private sealed class Listener(TaskCompletionSource<Result<string>> tcs, IProgress<string>? partials)
        : Java.Lang.Object, IRecognitionListener
    {
        public void OnResults(Bundle? results)
        {
            var top = Top(results);
            tcs.TrySetResult(string.IsNullOrWhiteSpace(top)
                ? Result.Failure<string>(ErrorCodes.VoiceNothingHeard, "empty_result")
                : Result.Success(top));
        }

        public void OnPartialResults(Bundle? partialResults)
        {
            var top = Top(partialResults);
            if (!string.IsNullOrWhiteSpace(top)) partials?.Report(top);
        }

        public void OnError(SpeechRecognizerError error) => tcs.TrySetResult(Result<string>.Failure(MapError((int)error)));

        public void OnReadyForSpeech(Bundle? @params) { }
        public void OnBeginningOfSpeech() { }
        public void OnRmsChanged(float rmsdB) { }
        public void OnBufferReceived(byte[]? buffer) { }
        public void OnEndOfSpeech() { }
        public void OnEvent(int eventType, Bundle? @params) { }

        private static string? Top(Bundle? bundle)
        {
            IList<string>? list = bundle?.GetStringArrayList(SpeechRecognizer.ResultsRecognition);
            return list is { Count: > 0 } ? list[0]?.Trim() : null;
        }
    }
}
#endif
