using Angadi.Application.Common;
using Angadi.Application.Platform;
using Angadi.Domain.Shop;
using Microsoft.Extensions.DependencyInjection;

namespace Angadi.Tests.Application;

/// <summary>Every screen with a "?" button and every walkthrough step has real text in both languages.</summary>
public sealed class HelpTextTests : IDisposable
{
    private readonly AppHost _h = new();
    public void Dispose() => _h.Dispose();

    [Theory]
    [InlineData(UiLanguage.Telugu)]
    [InlineData(UiLanguage.English)]
    public void EveryHelpPageAndWalkthroughStep_HasTitleAndBody(UiLanguage language)
    {
        var text = _h.Services.GetRequiredService<ILocalizer>();
        var before = text.Language;
        text.Language = language;
        try
        {
            var keys = HelpPages.Names.SelectMany(n => new[] { $"help.{n}.title", $"help.{n}.body" })
                .Concat(Enumerable.Range(1, HelpPages.WalkthroughSteps).SelectMany(i => new[] { $"walk.{i}.title", $"walk.{i}.body" }))
                .Concat(["help.button", "walk.next", "walk.skip", "walk.done", "settings.walkthrough"]);
            foreach (var key in keys)
            {
                var value = text[key];
                Assert.False(string.IsNullOrWhiteSpace(value), key);
                Assert.NotEqual(key, value);
            }
        }
        finally { text.Language = before; }
    }

    [Fact]
    public void PlanStep_TakesFreeLimitAndPricesFromTheNumbersPassedIn()
    {
        var text = _h.Services.GetRequiredService<ILocalizer>();
        var body = text.Format($"walk.{HelpPages.WalkthroughSteps}.body", 50, 200, 1999);
        Assert.Contains("50", body, StringComparison.Ordinal);
        Assert.Contains("200", body, StringComparison.Ordinal);
        Assert.Contains("1999", body, StringComparison.Ordinal);
        Assert.DoesNotContain("{", body, StringComparison.Ordinal);
    }

    [Fact]
    public void EachHelpPageIsListedOnce() => Assert.Equal(HelpPages.Names.Count, HelpPages.Names.Distinct().Count());
}
