using System.Reflection;
using System.Text.Json;
using Angadi.Application.Common;

namespace Angadi.Tests.Contracts;

/// <summary>NFR-04 / FR-02: every message exists in Telugu and English.</summary>
public class ResourceTests
{
    private static readonly Assembly App = typeof(Result).Assembly;

    internal static JsonDocument Load(string name)
    {
        using var s = App.GetManifestResourceStream($"Angadi.Resources.{name}")
            ?? throw new InvalidOperationException($"Missing embedded resource {name}");
        return JsonDocument.Parse(s, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
    }

    [Fact]
    public void NFR04_EveryErrorCode_HasTeluguAndEnglish()
    {
        using var doc = Load("errors.json");
        var codes = typeof(ErrorCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(f => (string)f.GetRawConstantValue()!);

        foreach (var code in codes)
        {
            Assert.True(doc.RootElement.TryGetProperty(code, out var entry), $"errors.json missing {code}");
            Assert.False(string.IsNullOrWhiteSpace(entry.GetProperty("te").GetString()), $"{code}: empty te");
            Assert.False(string.IsNullOrWhiteSpace(entry.GetProperty("en").GetString()), $"{code}: empty en");
        }
    }

    [Fact]
    public void NFR04_TeluguAndEnglishStrings_HaveSameKeys()
    {
        // Merges strings.te.json + every strings.<area>.te.json; throws on a duplicate key.
        var teKeys = JsonLocalizer.LoadAll("te").Keys.ToHashSet();
        var enKeys = JsonLocalizer.LoadAll("en").Keys.ToHashSet();

        Assert.Empty(teKeys.Except(enKeys));
        Assert.Empty(enKeys.Except(teKeys));
    }

    [Theory]
    [InlineData("voice.numbers.te.json")]
    [InlineData("voice.units.te.json")]
    [InlineData("voice.grammar.te.json")]
    public void FR06_VoiceRuleFiles_AreEmbeddedAndValid(string name)
    {
        using var doc = Load(name);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
    }
}
