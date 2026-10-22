using Angadi.Application.Common;
using Angadi.Application.Voice;
using Angadi.Domain.Items;
using Angadi.Infrastructure.Seed;
using Microsoft.Extensions.DependencyInjection;

namespace Angadi.Tests.Voice;

/// <summary>
/// T4: what shopkeepers actually say at the counter, beyond TeluguParserTests / VoiceBatteryTests —
/// thousands, fractions, decimals, Telugu-script digits, unit spellings, "₹10 of" amounts, several items in one
/// breath, polite noise words, and English product names inside Telugu sentences.
/// Tests marked Skip = "BUG T4-nn" are real parser bugs (see the T4 report); a fixer unskips them.
/// </summary>
public class TeluguVoiceEdgeTests
{
    private static readonly IReadOnlyList<Item> Seed =
        SeedCatalog.Kirana().Concat(SeedCatalog.Pharmacy()).Select(s => s.Item).ToList();

    private readonly TeluguParser _parser = new();

    private ParsedLine One(string phrase) => Assert.Single(_parser.Parse(phrase));

    // ------------------------------------------------------------------ quantities

    [Theory]
    // phrase,                         name,             qtyMilli,   unit
    [InlineData("వెయ్యి గ్రాముల బియ్యం",     "బియ్యం",        1_000_000L, Unit.Gram)]       // వెయ్యి = 1000
    [InlineData("వెయ్యి ml నూనె",           "నూనె",          1_000_000L, Unit.Millilitre)]
    [InlineData("అర కేజీ బియ్యం",           "బియ్యం",        500L,       Unit.Kilogram)]   // అర = ½
    [InlineData("అర లీటరు నూనె",            "నూనె",          500L,       Unit.Litre)]
    [InlineData("పావు కేజీ కారం",           "కారం",          250L,       Unit.Kilogram)]   // పావు = ¼
    [InlineData("ముప్పావు కేజీ చక్కెర",       "చక్కెర",        750L,       Unit.Kilogram)]   // ముప్పావు = ¾
    [InlineData("అరకిలో బియ్యం",            "బియ్యం",        500L,       Unit.Kilogram)]   // fused number + unit
    [InlineData("పావుకిలో కారం",            "కారం",          250L,       Unit.Kilogram)]
    [InlineData("ముప్పావుకేజీ చక్కెర",        "చక్కెర",        750L,       Unit.Kilogram)]
    [InlineData("మూడున్నర కేజీల బియ్యం",      "బియ్యం",        3500L,      Unit.Kilogram)]
    [InlineData("ఏడున్నర కేజీల బియ్యం",       "బియ్యం",        7500L,      Unit.Kilogram)]   // -న్నర suffix rule (not listed)
    [InlineData("పదిన్నర కేజీల బియ్యం",       "బియ్యం",        10500L,     Unit.Kilogram)]
    [InlineData("1.25 కేజీల చక్కెర",         "చక్కెర",        1250L,      Unit.Kilogram)]   // decimal
    [InlineData("౨ కేజీల బియ్యం",           "బియ్యం",        2000L,      Unit.Kilogram)]   // Telugu-script digits
    [InlineData("౧.౫ కేజీల చక్కెర",          "చక్కెర",        1500L,      Unit.Kilogram)]
    [InlineData("బియ్యం ౫ కేజీలు",           "బియ్యం",        5000L,      Unit.Kilogram)]
    [InlineData("500 గ్రాముల కారం",          "కారం",          500_000L,   Unit.Gram)]
    [InlineData("500 ml నూనె",              "నూనె",          500_000L,   Unit.Millilitre)]
    [InlineData("రెండు కిలోల బియ్యం",         "బియ్యం",        2000L,      Unit.Kilogram)]
    [InlineData("రెండుకేజీలు బియ్యం",         "బియ్యం",        2000L,      Unit.Kilogram)]
    [InlineData("దగ్గు సిరప్ రెండు సీసాలు",    "దగ్గు సిరప్",    2000L,      Unit.Piece)]      // bottles
    [InlineData("రెండు డజన్ల గుడ్లు",          "గుడ్లు",        24000L,     Unit.Piece)]
    [InlineData("అర డజను అరటిపండ్లు",        "అరటిపండ్లు",    6000L,      Unit.Piece)]
    [InlineData("రెండు వందల యాభై గ్రాముల కారం", "కారం",         250_000L,   Unit.Gram)]
    [InlineData("మూడొందల గ్రాముల కారం",       "కారం",          300_000L,   Unit.Gram)]
    [InlineData("half kg sugar",            "sugar",         500L,       Unit.Kilogram)]
    [InlineData("rice 2 kgs",               "rice",          2000L,      Unit.Kilogram)]
    public void Quantities_NumbersFractionsDigitsAndUnits(string phrase, string name, long qty, Unit unit)
    {
        var line = One(phrase);
        Assert.Equal(name, line.Name);
        Assert.Equal(qty, line.Qty?.Milli);
        Assert.Equal(unit, line.Unit);
        Assert.Null(line.Amount);
        Assert.False(line.IsAmbiguous);
    }

    [Theory]
    [InlineData("౧౦ సబ్బులు", "సబ్బులు", 10_000L)]
    [InlineData("బిస్కెట్లు ఐదు", "బిస్కెట్లు", 5_000L)]
    [InlineData("ఒకటి సబ్బు", "సబ్బు", 1_000L)]
    public void Quantities_WithoutAUnit_KeepTheCount(string phrase, string name, long qty)
    {
        var line = One(phrase);
        Assert.Equal(name, line.Name);
        Assert.Equal(qty, line.Qty?.Milli);
        Assert.Null(line.Unit);
    }

    // ------------------------------------------------------------------ "₹10 of" amounts

    [Theory]
    // phrase,                               name,          amountPaise
    [InlineData("rs 50 చక్కెర",                 "చక్కెర",       5_000L)]
    [InlineData("₹10 కరివేపాకు",               "కరివేపాకు",     1_000L)]
    [InlineData("₹ 30 పచ్చిమిర్చి",             "పచ్చిమిర్చి",    3_000L)]
    [InlineData("₹10.50 పాలు",                 "పాలు",         1_050L)]
    [InlineData("10 rupees tomato",            "tomato",       1_000L)]
    [InlineData("50 రూపాయలు పెట్రోల్",           "పెట్రోల్",      5_000L)]
    [InlineData("యాభై రూపాయలకు చక్కెర",         "చక్కెర",       5_000L)]
    [InlineData("వంద రూపాయల నూనె",             "నూనె",         10_000L)]
    [InlineData("రెండు వందల రూపాయల బియ్యం",      "బియ్యం",       20_000L)]
    [InlineData("అరవై ఐదు రూపాయల చక్కెర",        "చక్కెర",       6_500L)]
    [InlineData("తొంభై తొమ్మిది రూపాయల నూనె",     "నూనె",         9_900L)]
    [InlineData("పదిహేను వందల రూపాయల బియ్యం",    "బియ్యం",       150_000L)]   // fifteen hundred
    [InlineData("వెయ్యి ఐదొందల రూపాయల బియ్యం",    "బియ్యం",       150_000L)]   // thousand + five-hundred (fused)
    public void Amounts_AreRupeesWorth_NotAQuantity(string phrase, string name, long paise)
    {
        var line = One(phrase);
        Assert.Equal(name, line.Name);
        Assert.Equal(paise, line.Amount?.Value);
        Assert.Null(line.Qty);
        Assert.False(line.IsAmbiguous);
    }

    // ------------------------------------------------------------------ several items, noise words

    [Theory]
    [InlineData("బియ్యం రెండు కేజీలు, చక్కెర ఒక కేజీ, పప్పు అర కేజీ", new[] { "బియ్యం", "చక్కెర", "పప్పు" }, new[] { 2000L, 1000L, 500L })]
    [InlineData("బియ్యం రెండు కేజీలు మరియు నూనె ఒక లీటరు", new[] { "బియ్యం", "నూనె" }, new[] { 2000L, 1000L })]
    [InlineData("బియ్యం రెండు కేజీలు ఆ తర్వాత నూనె ఒక లీటరు", new[] { "బియ్యం", "నూనె" }, new[] { 2000L, 1000L })]   // two-word separator
    [InlineData("పాలు రెండు ప్యాకెట్లు తర్వాత పెరుగు ఒకటి", new[] { "పాలు", "పెరుగు" }, new[] { 2000L, 1000L })]
    [InlineData("సబ్బు మూడు అప్పుడు పేస్ట్ ఒకటి", new[] { "సబ్బు", "పేస్ట్" }, new[] { 3000L, 1000L })]
    [InlineData("బియ్యం రెండు కేజీలు. నూనె ఒక లీటరు.", new[] { "బియ్యం", "నూనె" }, new[] { 2000L, 1000L })]
    [InlineData("2 kg rice plus 1 kg sugar", new[] { "rice", "sugar" }, new[] { 2000L, 1000L })]
    public void MultiItemUtterance_SplitsIntoLines(string utterance, string[] names, long[] qtys)
    {
        var lines = _parser.Parse(utterance);
        Assert.Equal(names, lines.Select(l => l.Name));
        Assert.Equal(qtys, lines.Select(l => l.Qty?.Milli ?? -1));
    }

    [Theory]
    [InlineData("అన్నా బియ్యం రెండు కేజీలు ఇవ్వండి", "బియ్యం", 2000L)]
    [InlineData("సార్ ఒక సబ్బు కావాలి", "సబ్బు", 1000L)]
    [InlineData("అక్కా చక్కెర ఒక కేజీ కూడా వేయండి", "చక్కెర", 1000L)]
    [InlineData("please రెండు కేజీలు బియ్యం ఇవ్వండి", "బియ్యం", 2000L)]
    [InlineData("  బియ్యం   రెండు   కేజీలు  ", "బియ్యం", 2000L)]
    public void NoiseWords_AreDropped(string utterance, string name, long qty)
    {
        var line = One(utterance);
        Assert.Equal(name, line.Name);
        Assert.Equal(qty, line.Qty?.Milli);
    }

    [Theory]
    [InlineData("ఇవ్వండి")]
    [InlineData("అన్నా ఇవ్వండి")]
    [InlineData("రెండు కేజీలు")]
    [InlineData(",,, . ,")]
    public void OnlyNoise_GivesNoLine(string utterance) => Assert.Empty(_parser.Parse(utterance));

    [Fact]
    public async Task Pipeline_OnlyNoise_IsNoMatch_NotACrash()
    {
        using var h = new Angadi.Tests.Application.AppHost();
        var pipeline = h.Services.GetRequiredService<IVoiceBillingPipeline>();
        var r = await pipeline.ProcessUtteranceAsync("అన్నా ఇవ్వండి");
        Assert.True(r.IsFailure);
        Assert.Equal(ErrorCodes.VoiceNoMatch, r.Error.Code);
    }

    // ------------------------------------------------------------------ English item names in Telugu sentences

    [Theory]
    [InlineData("రెండు kg sugar ఇవ్వండి", "sugar", 2000L, Unit.Kilogram)]
    [InlineData("ఒక packet milk", "milk", 1000L, Unit.Pack)]
    [InlineData("toothpaste ఒకటి", "toothpaste", 1000L, null)]
    [InlineData("dettol సబ్బు ఒకటి", "dettol సబ్బు", 1000L, null)]
    [InlineData("dairy milk చాక్లెట్ రెండు", "dairy milk చాక్లెట్", 2000L, null)]
    [InlineData("crocin 500 ఒకటి", "crocin 500", 1000L, null)]                // dosage stays in the name
    [InlineData("dolo 650 రెండు strip", "dolo 650", 2000L, Unit.Strip)]
    public void EnglishNames_InsideTeluguSentences(string utterance, string name, long qty, Unit? unit)
    {
        var line = One(utterance);
        Assert.Equal(name, line.Name);
        Assert.Equal(qty, line.Qty?.Milli);
        Assert.Equal(unit, line.Unit);
    }

    [Theory]
    [InlineData("రెండు kg sugar ఇవ్వండి", "Sugar")]
    [InlineData("ఒక packet milk", "Milk (500 ml)")]
    [InlineData("toothpaste ఒకటి", "Toothpaste (100 g)")]
    [InlineData("rice 2 kgs", "Rice (Sona Masoori)")]
    [InlineData("biscuits ఐదు", "Biscuits")]
    [InlineData("అన్నా బియ్యం రెండు కేజీలు ఇవ్వండి", "Rice (Sona Masoori)")]
    public void EnglishAndPoliteUtterances_AutoAddTheRightSeedItem(string utterance, string english)
    {
        var result = ItemMatcher.Match(One(utterance), Seed, new VoiceOptions());
        Assert.Equal(MatchKind.AutoAdd, result.Kind);
        Assert.Equal(english, Seed.First(i => i.Id == result.Best!.ItemId).EnglishName);
    }

    // ------------------------------------------------------------------ real bugs (skipped until fixed)

    [Fact(Skip = "BUG T4-01: 'వెయ్యి ఐదు వందల రూపాయల' (₹1,500) parses as ₹1,00,500 and is auto-added")]
    public void Thousand_Then_Hundreds_IsFifteenHundred()
    {
        var line = One("వెయ్యి ఐదు వందల రూపాయల బియ్యం");
        Assert.Equal("బియ్యం", line.Name);
        Assert.Equal(150_000L, line.Amount?.Value);   // was 1,00,50,000 paise: 1000 + 5 = 1005, then × 100
    }

    [Theory(Skip = "BUG T4-02: 'వేల' (thousands) is not a number word; 'ఒక వెయ్యి' becomes ₹1")]
    [InlineData("రెండు వేల రూపాయల బియ్యం", 200_000L)]
    [InlineData("ఒక వెయ్యి రూపాయల బియ్యం", 100_000L)]
    public void Thousands_Plural_And_OneThousand(string phrase, long paise)
    {
        var line = One(phrase);
        Assert.Equal("బియ్యం", line.Name);
        Assert.Equal(paise, line.Amount?.Value);
        Assert.False(line.IsAmbiguous);
    }

    [Fact(Skip = "BUG T4-03: 'నూట యాభై' (150) is read as 50 with 'నూట' left in the item name")]
    public void NootaYabhai_IsOneHundredFifty()
    {
        var line = One("నూట యాభై గ్రాముల కారం");
        Assert.Equal("కారం", line.Name);
        Assert.Equal(150_000L, line.Qty?.Milli);
        Assert.Equal(Unit.Gram, line.Unit);
    }

    [Theory(Skip = "BUG T4-04: 'రూ.10' / 'Rs.20' — the '.' splits the phrase, so ₹10 of tomatoes becomes 10 kg")]
    [InlineData("రూ.10 టమాటా", "టమాటా", 1_000L)]
    [InlineData("Rs.20 చక్కెర", "చక్కెర", 2_000L)]
    public void RupeeAbbreviationWithDot_IsAnAmount(string phrase, string name, long paise)
    {
        var line = One(phrase);
        Assert.Equal(name, line.Name);
        Assert.Equal(paise, line.Amount?.Value);
        Assert.Null(line.Qty);
    }

    // Was BUG T4-05 ('1/2' exploded into '1' + '/2'); fixed by VOICE-1 (90b05d9) — now its regression test.
    [Fact]
    public void SlashFraction_IsHalf()
    {
        var line = One("1/2 కేజీ బియ్యం");
        Assert.Equal("బియ్యం", line.Name);
        Assert.Equal(500L, line.Qty?.Milli);
        Assert.Equal(Unit.Kilogram, line.Unit);
    }

    [Fact(Skip = "BUG T4-06: the 'g' of the brand 'Parle G' is taken as grams: 3 grams of 'parle'")]
    public void BrandEndingInG_IsNotGrams()
    {
        var line = One("3 parle g");
        Assert.Equal("parle g", line.Name);
        Assert.Equal(3000L, line.Qty?.Milli);
        Assert.Null(line.Unit);
    }

    [Theory(Skip = "BUG T4-07: English plural units 'strips' / 'packets' are not unit words; they end up in the item name")]
    [InlineData("paracetamol రెండు strips", "paracetamol", Unit.Strip)]
    [InlineData("maggi రెండు packets", "maggi", Unit.Pack)]
    public void EnglishPluralUnits(string phrase, string name, Unit unit)
    {
        var line = One(phrase);
        Assert.Equal(name, line.Name);
        Assert.Equal(2000L, line.Qty?.Milli);
        Assert.Equal(unit, line.Unit);
    }

    [Fact(Skip = "BUG T4-08: 'అమ్మా' (common address to a woman customer) is not a filler; it stays in the item name")]
    public void Amma_IsAFiller()
    {
        Assert.Equal("సబ్బు", One("అమ్మా ఒక సబ్బు").Name);
    }
}
