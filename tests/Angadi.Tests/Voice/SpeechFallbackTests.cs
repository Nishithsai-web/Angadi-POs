using Angadi.Application.Common;
using Angadi.Application.Platform;
using Microsoft.Extensions.Logging.Abstractions;

namespace Angadi.Tests.Voice;

/// <summary>
/// SPEC-6/7 (VOICE-8/VOICE-9): the speech fallback chain and the recogniser error map.
/// These had ZERO tests while FallbackSpeechToText wrapped the concrete Android class in the
/// MAUI project; the chain now lives in Angadi.Application.Platform over ISpeechToText.
/// </summary>
public class SpeechFallbackTests
{
    private sealed class FakeSpeech(params Result<string>[] script) : ISpeechToText
    {
        public List<string> Cultures { get; } = [];

        public Task<Result<string>> ListenAsync(string culture, IProgress<string>? partials, CancellationToken ct = default)
        {
            Cultures.Add(culture);
            return Task.FromResult(script[Math.Min(Cultures.Count - 1, script.Length - 1)]);
        }
    }

    private static FallbackSpeechToText Chain(ISpeechToText inner, string deviceCulture = "en-IN") =>
        new(inner, NullLogger<FallbackSpeechToText>.Instance, () => deviceCulture);

    [Fact]
    public async Task T2_LanguageMissing_RetriesOnceInDeviceLanguage()
    {
        var fake = new FakeSpeech(
            Result.Failure<string>(ErrorCodes.VoiceNothingHeard, "language_not_supported"),
            Result.Success("బియ్యం"));
        var result = await Chain(fake).ListenAsync("te-IN", null);
        Assert.True(result.IsSuccess);
        Assert.Equal("బియ్యం", result.Value);
        Assert.Equal(["te-IN", "en-IN"], fake.Cultures);
    }

    [Fact]
    public async Task T2_NetworkFailure_IsNotRetried()
    {
        var fake = new FakeSpeech(Result.Failure<string>(ErrorCodes.VoiceNoInternet, "network"));
        var result = await Chain(fake).ListenAsync("te-IN", null);
        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.VoiceNoInternet, result.Error.Code);
        Assert.Equal("network", result.Error.Detail);
        Assert.Single(fake.Cultures);
    }

    [Fact]
    public async Task T2_RecognizerUnavailable_BecomesManual()
    {
        // "manual" is the POS signal to open typed search (PosViewModel focuses the search box).
        var fake = new FakeSpeech(Result.Failure<string>(ErrorCodes.VoiceNothingHeard, "recognizer_unavailable"));
        var result = await Chain(fake).ListenAsync("te-IN", null);
        Assert.True(result.IsFailure);
        Assert.Equal("manual", result.Error.Detail);
        Assert.Single(fake.Cultures);
    }

    [Fact]
    public async Task T2_DeviceCultureEqualsRequested_NoRetry_ButManual()
    {
        var fake = new FakeSpeech(Result.Failure<string>(ErrorCodes.VoiceNothingHeard, "language_unavailable"));
        var result = await Chain(fake, deviceCulture: "te-IN").ListenAsync("te-IN", null);
        Assert.True(result.IsFailure);
        Assert.Equal("manual", result.Error.Detail);
        Assert.Single(fake.Cultures);
    }

    [Theory]
    [InlineData(1, "network_timeout")]
    [InlineData(2, "network")]
    [InlineData(4, "server")]
    [InlineData(9, "insufficient_permissions")]
    [InlineData(6, "speech_timeout")]
    [InlineData(7, "no_match")]
    [InlineData(12, "language_not_supported")]
    [InlineData(13, "language_unavailable")]
    [InlineData(999, "error_999")]
    public void T2_SpeechErrors_MapRecognizerCodes(int code, string expectedDetail)
    {
        // SPEC-7: pin the table — the fallback chain keys off these Detail strings.
        Assert.Equal(expectedDetail, SpeechErrors.Map(code).Detail);
    }

    [Fact]
    public void T2_SpeechErrors_MapToTheRightCodeFamilies()
    {
        Assert.Equal(ErrorCodes.VoiceNoInternet, SpeechErrors.Map(2).Code);
        Assert.Equal(ErrorCodes.VoiceNoInternet, SpeechErrors.Map(10).Code);
        Assert.Equal(ErrorCodes.VoicePermissionDenied, SpeechErrors.Map(9).Code);
        Assert.Equal(ErrorCodes.VoiceNothingHeard, SpeechErrors.Map(7).Code);
        Assert.Equal(ErrorCodes.VoiceNothingHeard, SpeechErrors.Map(999).Code);
    }
}
