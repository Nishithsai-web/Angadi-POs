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
/// ISpeechToText over Android's SpeechRecognizer (te-IN). Prefers the on-device recogniser
/// (API 31+) so voice billing works offline; otherwise Google's network recogniser, which needs
/// internet (NFR-01, see docs/PLATFORM_NOTES.md).
/// One listen at a time: a new call aborts the one in flight. Cancellation aborts (no result).
/// Never logs what was said (NFR-08), only error codes.
/// </summary>
public sealed class AndroidSpeechToText(ILogger<AndroidSpeechToText> log) : ISpeechToText
{
    /// <summary>Silence after speech that ends the utterance (FR-06: lines within 3 s of speech ending).</summary>
    private const long CompleteSilenceMs = 1800;
    private const long PossiblyCompleteSilenceMs = 1500;
    /// <summary>android.speech.extra.PREFER_OFFLINE (literal: no binding constant on older targets).</summary>
    private const string PreferOfflineExtra = "android.speech.extra.PREFER_OFFLINE";
    /// <summary>Safety net if the recognizer never calls back (seen on some OEM builds).</summary>
    private static readonly TimeSpan HardTimeout = TimeSpan.FromSeconds(20);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private CancellationTokenSource? _current;

    public async Task<Result<string>> ListenAsync(string culture, IProgress<string>? partials, CancellationToken ct = default)
    {
        // FIX(VOICE-9, 2026-10-20, FIX-1): Flaw: the internet check ran FIRST, so an off-grid shop
        // got "no internet" even when the phone had an on-device Telugu recogniser — the code never
        // tried CreateOnDeviceSpeechRecognizer (API 31+) and never asked for offline models.
        // Fix: probe on-device recognition first; the internet gate applies only when no on-device
        // recogniser exists, and BuildIntent asks for offline models.
        // Why: Angadi's promise is offline-first; voice billing must not die with the network.
        // Logged in docs/AGENT_FIX_LOG.md.
        var onDevice = await MainThread.InvokeOnMainThreadAsync(() =>
            OperatingSystem.IsAndroidVersionAtLeast(31)
            && SpeechRecognizer.IsOnDeviceRecognitionAvailable(global::Android.App.Application.Context));
        if (!onDevice && Connectivity.Current.NetworkAccess != NetworkAccess.Internet)
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
            return await ListenOnceAsync(string.IsNullOrWhiteSpace(culture) ? "te-IN" : culture, partials, linked.Token, ct, onDevice);
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

    private async Task<Result<string>> ListenOnceAsync(string culture, IProgress<string>? partials, CancellationToken token, CancellationToken callerToken, bool preferOnDevice)
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

            // VOICE-9: on-device recogniser when the probe found one (null = it vanished; use the service one).
            // FIX(BUILD-2, 2026-10-21, Claude Code): Flaw: CreateOnDeviceSpeechRecognizer is API 31+ but this runs on 28+
            // (CA1416; a NoSuchMethodError on Android 9-11 if the probe ever said yes). Fix: explicit version guard.
            // Why: the app's floor is Android 9. Logged in docs/AGENT_FIX_LOG.md.
            recognizer = preferOnDevice && OperatingSystem.IsAndroidVersionAtLeast(31)
                ? SpeechRecognizer.CreateOnDeviceSpeechRecognizer(context) ?? SpeechRecognizer.CreateSpeechRecognizer(context)
                : SpeechRecognizer.CreateSpeechRecognizer(context);
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
        // VOICE-9: prefer on-device models when the system has them (a hint; ignored where unsupported).
        intent.PutExtra(PreferOfflineExtra, true);
        // Google's recognizer treats these as hints and may ignore them; the 20 s hard timeout backs them up.
        intent.PutExtra(RecognizerIntent.ExtraSpeechInputCompleteSilenceLengthMillis, CompleteSilenceMs);
        intent.PutExtra(RecognizerIntent.ExtraSpeechInputPossiblyCompleteSilenceLengthMillis, PossiblyCompleteSilenceMs);
        return intent;
    }

    private static Result<string> Cancelled() => Result.Failure<string>(ErrorCodes.ValidationFailed, "cancelled");

    // VOICE-9: the ERROR_* → error-code table moved to Angadi.Application.Platform.SpeechErrors,
    // where the tests project can reach it; the Listener below calls it directly.

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

        public void OnError(SpeechRecognizerError error) => tcs.TrySetResult(Result<string>.Failure(SpeechErrors.Map((int)error)));

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
