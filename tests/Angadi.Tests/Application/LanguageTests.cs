using Angadi.Application.Common;
using Angadi.Application.Reports;
using Angadi.Application.Voice;
using Angadi.Domain.Billing;
using Angadi.Domain.Items;
using Angadi.Domain.Shop;
using Microsoft.Extensions.DependencyInjection;

namespace Angadi.Tests.Application;

/// <summary>One app-wide language: UI text, item names, reports and voice candidates all follow it.</summary>
public class LanguageTests
{
    [Theory]
    [InlineData(UiLanguage.Telugu, "బియ్యం", "Rice", "బియ్యం")]
    [InlineData(UiLanguage.English, "బియ్యం", "Rice", "Rice")]
    [InlineData(UiLanguage.English, "బియ్యం", " ", "బియ్యం")]   // no English name: never blank
    [InlineData(UiLanguage.Telugu, "", "Rice", "Rice")]
    public void ItemName_FollowsLanguage_WithFallback(UiLanguage lang, string te, string en, string expected) =>
        Assert.Equal(expected, ItemNames.Pick(lang, te, en));

    [Fact]
    public void EveryUiKey_ExistsInBothLanguages()
    {
        var te = JsonLocalizer.LoadAll("te", strict: true);
        var en = JsonLocalizer.LoadAll("en", strict: true);
        Assert.Empty(te.Keys.Except(en.Keys));
        Assert.Empty(en.Keys.Except(te.Keys));
        Assert.All(en, kv => Assert.False(string.IsNullOrWhiteSpace(kv.Value), kv.Key));
    }

    [Fact]
    public void VoiceCandidates_UseTheAppLanguage()
    {
        var items = new[]
        {
            new Item { TeluguName = "సన్ ఫ్లవర్ నూనె", EnglishName = "Sunflower oil" },
            new Item { TeluguName = "పల్లి నూనె", EnglishName = "Groundnut oil" },
        };
        var line = new ParsedLine("నూనె", null, null, null, "నూనె");
        var en = ItemMatcher.Match(line, items, new VoiceOptions(), UiLanguage.English);
        Assert.All(en.Candidates, c => Assert.Contains("oil", c.DisplayName, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Reports_ShowItemNamesInTheChosenLanguage()
    {
        using var h = new AppHost();
        await h.SeedShopAsync();
        var rice = await h.AddItemAsync("బియ్యం", 6000);
        rice.EnglishName = "Rice";
        await h.Sender.Send(new Angadi.Application.Catalog.SaveItemCommand(rice));
        var draft = AppHost.Draft(TenderType.Cash, (rice, 1000));
        await h.Sender.Send(new Angadi.Application.Billing.FinalizeBillCommand(draft));

        h.Services.GetRequiredService<Angadi.Application.Platform.ILocalizer>().Language = UiLanguage.English;
        var top = await h.Sender.Send(new GetTopItemsQuery(h.Clock.Today, h.Clock.Today));
        Assert.Equal("Rice", Assert.Single(top).DisplayName);
    }
}

public class LocalDateTests
{
    [Fact]
    public void ReminderDates_UseTheAppLanguage()
    {
        var te = new JsonLocalizer { Language = UiLanguage.Telugu };
        var en = new JsonLocalizer { Language = UiLanguage.English };
        Assert.Equal("25 అక్టోబర్", te.Short(new DateOnly(2026, 10, 25)));
        Assert.Equal("25 Oct", en.Short(new DateOnly(2026, 10, 25)));
    }
}
