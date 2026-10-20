using Angadi.Application.Common;
using Angadi.Application.Voice;
using Angadi.Domain.Common;
using Angadi.Domain.Items;

namespace Angadi.Tests.Voice;

/// <summary>M2 learning safety, plus AUDIT 5/6/14 parser and key checks.</summary>
public class AliasLearnerAndAuditTests : IDisposable
{
    private readonly TempDb _t = new();
    public void Dispose() => _t.Dispose();

    private static readonly VoiceOptions Voice = new();
    private AliasLearner Learner() => new(_t.Items, _t.Clock, Microsoft.Extensions.Options.Options.Create(Voice), TempDb.Log<AliasLearner>());
    private VoiceBillingPipeline Pipeline() => new(new TeluguParser(), _t.Items, Microsoft.Extensions.Options.Options.Create(Voice), _t.Clock, TempDb.Log<VoiceBillingPipeline>());

    private async Task<(Item rice, Item sugar)> TwoItemsAsync()
    {
        var rice = new Item { TeluguName = "బియ్యం", EnglishName = "Rice", Aliases = ["biyyam"], Price = new Paise(6200) };
        var sugar = new Item { TeluguName = "చక్కెర", EnglishName = "Sugar", Aliases = ["పంచదార"], Price = new Paise(4800) };
        await _t.Items.SaveAsync(rice);
        await _t.Items.SaveAsync(sugar);
        return (rice, sugar);
    }

    private async Task<Item> ReloadAsync(Guid id) => (await _t.Items.GetAsync(id))!;

    [Fact]
    public async Task M2_LearnedOnce_AsksOnly_LearnedTwice_AutoAdds()
    {
        var (rice, _) = await TwoItemsAsync();
        await Learner().LearnAsync(rice.Id, "సోనా బియ్యం");
        var once = await ReloadAsync(rice.Id);
        Assert.False(Assert.Single(once.LearnedAliases).CanAutoAdd);
        Assert.True(ItemMatcher.ScoreItem(TeluguText.MatchKey("సోనా బియ్యం"), once) < new VoiceOptions().AutoAddThreshold);

        _t.Clock.Advance(TimeSpan.FromMinutes(1));
        await Learner().LearnAsync(rice.Id, "సోనా బియ్యం");
        var twice = await ReloadAsync(rice.Id);
        Assert.True(Assert.Single(twice.LearnedAliases).CanAutoAdd);
        Assert.Equal(1.0, ItemMatcher.ScoreItem(TeluguText.MatchKey("సోనా బియ్యం"), twice));
    }

    [Fact]
    public async Task T2_AutoAdd_TouchesLearnedAliasLastUsed()
    {
        // VOICE-7 regression: LastUsedMs moved only on LEARN, so the LRU cap evicted by age —
        // a daily-used alias learned long ago went first. An AutoAdd decided by a learned alias
        // must stamp that alias as used.
        var (rice, _) = await TwoItemsAsync();
        await Learner().LearnAsync(rice.Id, "సోనా బియ్యం");
        _t.Clock.Advance(TimeSpan.FromMinutes(1));
        await Learner().LearnAsync(rice.Id, "సోనా బియ్యం");      // confidence 2 → can auto-add
        var learnedAt = Assert.Single((await ReloadAsync(rice.Id)).LearnedAliases).LastUsedMs;

        _t.Clock.Advance(TimeSpan.FromDays(30));
        var result = await Pipeline().ProcessUtteranceAsync("సోనా బియ్యం రెండు కేజీలు");
        Assert.Equal(MatchKind.AutoAdd, Assert.Single(result.Value!).Kind);

        var after = Assert.Single((await ReloadAsync(rice.Id)).LearnedAliases);
        Assert.True(after.LastUsedMs > learnedAt, "AutoAdd must stamp the deciding alias as used");
    }

    [Fact]
    public async Task M2_AnotherItemsCuratedName_IsNeverLearned()
    {
        var (rice, _) = await TwoItemsAsync();
        await Learner().LearnAsync(rice.Id, "పంచదార");   // belongs to Sugar
        Assert.Empty((await ReloadAsync(rice.Id)).LearnedAliases);
    }

    [Fact]
    public async Task M2_ContradictingCorrection_DemotesThenRemoves()
    {
        var (rice, sugar) = await TwoItemsAsync();
        await Learner().LearnAsync(rice.Id, "తెల్లది");
        await Learner().LearnAsync(rice.Id, "తెల్లది");                 // confidence 2 on rice
        await Learner().LearnAsync(sugar.Id, "తెల్లది");                // shopkeeper says it meant sugar
        Assert.Equal(1, Assert.Single((await ReloadAsync(rice.Id)).LearnedAliases).Confidence);
        await Learner().LearnAsync(sugar.Id, "తెల్లది");
        Assert.Empty((await ReloadAsync(rice.Id)).LearnedAliases);
    }

    [Fact]
    public async Task M2_LruCap_NeverEvictsCuratedAliases()
    {
        var (rice, _) = await TwoItemsAsync();
        for (var i = 0; i < Voice.MaxLearnedAliasesPerItem + 5; i++)
        {
            _t.Clock.Advance(TimeSpan.FromSeconds(1));
            await Learner().LearnAsync(rice.Id, $"రకం{i:D2}");
        }
        var after = await ReloadAsync(rice.Id);
        Assert.Equal(Voice.MaxLearnedAliasesPerItem, after.LearnedAliases.Count);
        Assert.Equal(["biyyam"], after.Aliases);
        Assert.DoesNotContain(after.LearnedAliases, a => a.Text == "రకం00");   // oldest went first
    }

    // ---------- AUDIT 5 / 6 / 14 ----------

    [Fact]
    public void Audit5_QuantityOnlyUtterance_NeverMatchesAnItem()
    {
        var items = new[] { new Item { TeluguName = "బియ్యం", EnglishName = "Rice" } };
        foreach (var line in new TeluguParser().Parse("రెండు కేజీలు"))
            Assert.Equal(MatchKind.NoMatch, ItemMatcher.Match(line, items, new VoiceOptions()).Kind);
    }

    [Fact]
    public void Audit6_TeluguScriptDigits_AreQuantities()
    {
        var line = Assert.Single(new TeluguParser().Parse("బియ్యం ౨ కేజీలు"));
        Assert.Equal(2000, line.Qty?.Milli);
        Assert.Equal(Unit.Kilogram, line.Unit);
    }

    [Fact]
    public void Audit14_MilkAndDal_HaveDistinctNonEmptyKeys()
    {
        var milk = TeluguText.MatchKey("పాలు");
        Assert.NotEmpty(milk);
        Assert.NotEqual(milk, TeluguText.MatchKey("పప్పు"));
        Assert.Equal(milk, TeluguText.MatchKey(" పాలు "));
    }
}
