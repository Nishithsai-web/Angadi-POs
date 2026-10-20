using Angadi.Application.Common;
using Angadi.Application.Voice;
using Angadi.Domain.Items;
using Angadi.Infrastructure.Seed;

namespace Angadi.Tests.Voice;

/// <summary>
/// NFR-12 / M15 battery: 40 realistic counter phrases against the 120 seed items — the voice exit gate.
/// Target ≥ 36/40 correct and ≥ 35/40 auto-added (M15). Any parser or seed change that drops below fails CI.
/// </summary>
public class VoiceBatteryTests
{
    private static readonly IReadOnlyList<Item> Items =
        SeedCatalog.Kirana().Concat(SeedCatalog.Pharmacy()).Select(s => s.Item).ToList();

    private static readonly VoiceOptions Options = new();
    private readonly TeluguParser _parser = new();

    public static TheoryData<string, string> Battery => new()
    {
        { "బియ్యం రెండు కేజీలు", "Rice (Sona Masoori)" },
        { "అర కేజీ కంది పప్పు", "Toor dal" },
        { "చక్కెర ఒక కేజీ", "Sugar" },
        { "పంచదార రెండు కేజీలు", "Sugar" },
        { "పాలు మూడు ప్యాకెట్లు", "Milk (500 ml)" },
        { "ఉల్లిపాయలు రెండు కేజీలు", "Onions" },
        { "ఉల్లిగడ్డ ఒక కేజీ", "Onions" },
        { "టమాటా అర కేజీ", "Tomatoes" },
        { "ఆలుగడ్డలు ఒక కేజీ", "Potatoes" },
        { "పది రూపాయల పచ్చిమిర్చి", "Green chillies" },
        { "కరివేపాకు", "Curry leaves" },
        { "రెండొందల గ్రాముల జీలకర్ర", "Cumin seeds" },
        { "పసుపు వంద గ్రాములు", "Turmeric powder" },
        { "సన్ ఫ్లవర్ నూనె ఒక లీటరు", "Sunflower oil" },   // H5: bare "నూనె" now asks (see below)
        { "పల్లీ నూనె ఒక లీటరు", "Groundnut oil" },
        { "గుడ్లు ఒక డజను", "Eggs" },
        { "స్నానం సబ్బు రెండు", "Bath soap" },
        { "సర్ఫ్ ఒక ప్యాకెట్", "Detergent powder (1 kg)" },
        { "టూత్ పేస్ట్ ఒకటి", "Toothpaste (100 g)" },
        { "అగ్గిపెట్టె", "Matchbox" },
        { "బిస్కెట్లు ఐదు", "Biscuits" },
        { "కూల్ డ్రింక్ రెండు", "Soft drink (750 ml)" },
        { "డోలో రెండు స్ట్రిప్పులు", "Paracetamol 650 mg" },
        { "dolo 650 ఒక strip", "Paracetamol 650 mg" },
        { "పాంటోప్రజోల్ ఒక స్ట్రిప్", "Pantoprazole 40 mg" },
        { "ఓఆర్ఎస్ మూడు", "ORS sachet" },
        { "దగ్గు సిరప్ ఒకటి", "Cough syrup (100 ml)" },
        { "బియ్యమ్ ఐదు కేజీలు", "Rice (Sona Masoori)" },
        { "మినపప్పు అర కేజీ", "Urad dal" },
        { "గోధుమపిండి రెండు కేజీలు", "Wheat atta" },
        // M15: ten more, spread across dairy, vegetables, staples and pharmacy.
        { "నెయ్యి ఒకటి", "Ghee (500 ml)" },
        { "పెరుగు రెండు ప్యాకెట్లు", "Curd (500 g)" },
        { "బెల్లం అర కేజీ", "Jaggery" },
        { "అల్లం వంద గ్రాములు", "Ginger" },
        { "చింతపండు ఒక కేజీ", "Tamarind" },
        { "అటుకులు అర కేజీ", "Poha" },
        { "కొబ్బరికాయ రెండు", "Coconut" },
        { "బ్రెడ్ ఒకటి", "Bread" },
        { "సెటిరిజిన్ ఒక స్ట్రిప్", "Cetirizine 10 mg" },
        { "శానిటైజర్ ఒకటి", "Hand sanitizer (100 ml)" },
    };

    /// <summary>
    /// SPEC-9 (VOICE-8): the same 40 phrases with their expected parse — quantity (milli, spoken
    /// unit), unit and amount (paise). The item battery alone could pass with a WRONG quantity.
    /// </summary>
    public static TheoryData<string, long?, Unit?, long?> QuantityBattery => new()
    {
        { "బియ్యం రెండు కేజీలు", 2000L, Unit.Kilogram, null },
        { "అర కేజీ కంది పప్పు", 500L, Unit.Kilogram, null },
        { "చక్కెర ఒక కేజీ", 1000L, Unit.Kilogram, null },
        { "పంచదార రెండు కేజీలు", 2000L, Unit.Kilogram, null },
        { "పాలు మూడు ప్యాకెట్లు", 3000L, Unit.Pack, null },
        { "ఉల్లిపాయలు రెండు కేజీలు", 2000L, Unit.Kilogram, null },
        { "ఉల్లిగడ్డ ఒక కేజీ", 1000L, Unit.Kilogram, null },
        { "టమాటా అర కేజీ", 500L, Unit.Kilogram, null },
        { "ఆలుగడ్డలు ఒక కేజీ", 1000L, Unit.Kilogram, null },
        { "పది రూపాయల పచ్చిమిర్చి", null, null, 1000L },
        { "కరివేపాకు", null, null, null },
        { "రెండొందల గ్రాముల జీలకర్ర", 200000L, Unit.Gram, null },
        { "పసుపు వంద గ్రాములు", 100000L, Unit.Gram, null },
        { "సన్ ఫ్లవర్ నూనె ఒక లీటరు", 1000L, Unit.Litre, null },
        { "పల్లీ నూనె ఒక లీటరు", 1000L, Unit.Litre, null },
        { "గుడ్లు ఒక డజను", 12000L, Unit.Piece, null },
        { "స్నానం సబ్బు రెండు", 2000L, null, null },
        { "సర్ఫ్ ఒక ప్యాకెట్", 1000L, Unit.Pack, null },
        { "టూత్ పేస్ట్ ఒకటి", 1000L, null, null },
        { "అగ్గిపెట్టె", null, null, null },
        { "బిస్కెట్లు ఐదు", 5000L, null, null },
        { "కూల్ డ్రింక్ రెండు", 2000L, null, null },
        { "డోలో రెండు స్ట్రిప్పులు", 2000L, Unit.Strip, null },
        { "dolo 650 ఒక strip", 1000L, Unit.Strip, null },
        { "పాంటోప్రజోల్ ఒక స్ట్రిప్", 1000L, Unit.Strip, null },
        { "ఓఆర్ఎస్ మూడు", 3000L, null, null },
        { "దగ్గు సిరప్ ఒకటి", 1000L, null, null },
        { "బియ్యమ్ ఐదు కేజీలు", 5000L, Unit.Kilogram, null },
        { "మినపప్పు అర కేజీ", 500L, Unit.Kilogram, null },
        { "గోధుమపిండి రెండు కేజీలు", 2000L, Unit.Kilogram, null },
        { "నెయ్యి ఒకటి", 1000L, null, null },
        { "పెరుగు రెండు ప్యాకెట్లు", 2000L, Unit.Pack, null },
        { "బెల్లం అర కేజీ", 500L, Unit.Kilogram, null },
        { "అల్లం వంద గ్రాములు", 100000L, Unit.Gram, null },
        { "చింతపండు ఒక కేజీ", 1000L, Unit.Kilogram, null },
        { "అటుకులు అర కేజీ", 500L, Unit.Kilogram, null },
        { "కొబ్బరికాయ రెండు", 2000L, null, null },
        { "బ్రెడ్ ఒకటి", 1000L, null, null },
        { "సెటిరిజిన్ ఒక స్ట్రిప్", 1000L, Unit.Strip, null },
        { "శానిటైజర్ ఒకటి", 1000L, null, null },
    };

    private (MatchResult result, string? bestEnglish) Run(string phrase)
    {
        var line = Assert.Single(_parser.Parse(phrase));
        var result = ItemMatcher.Match(line, Items, Options);
        var best = result.Best is null ? null : Items.First(i => i.Id == result.Best.ItemId).EnglishName;
        return (result, best);
    }

    [Theory]
    [MemberData(nameof(Battery))]
    public void FR07_BatteryPhrase_MatchesExpectedItem(string phrase, string expectedEnglish)
    {
        var (result, best) = Run(phrase);
        Assert.NotEqual(MatchKind.NoMatch, result.Kind);
        Assert.Equal(expectedEnglish, best);
    }

    [Theory]
    [MemberData(nameof(QuantityBattery))]
    public void T2_BatteryPhrase_ParsesExpectedQuantity(string phrase, long? qtyMilli, Unit? unit, long? amountPaise)
    {
        // SPEC-9 (VOICE-8): pin the parse, not just the matched item — right item, wrong
        // quantity is still a wrong bill.
        var line = Assert.Single(_parser.Parse(phrase));
        Assert.Equal(qtyMilli, line.Qty?.Milli);
        Assert.Equal(unit, line.Unit);
        Assert.Equal(amountPaise, line.Amount?.Value);
    }

    [Fact]
    public void T2_BareDosage_AsksWithParacetamolBest()
    {
        // SPEC-11 (VOICE-8): "650 పారాసెటమాల్" — a bare leading dosage is not a count of 650.
        // The parser flags it (M1 guard) and the matcher asks, with the medicine on top.
        var line = Assert.Single(_parser.Parse("650 పారాసెటమాల్"));
        Assert.True(line.IsAmbiguous);
        var (result, best) = Run("650 పారాసెటమాల్");
        Assert.Equal(MatchKind.Confirm, result.Kind);
        Assert.Equal("Paracetamol 650 mg", best);
    }

    [Fact]
    public void NFR12_Battery_MeetsTarget()
    {
        var correct = 0;
        var auto = 0;
        foreach (var row in Battery)
        {
            var phrase = (string)row[0];
            var expected = (string)row[1];
            var (result, best) = Run(phrase);
            if (result.Kind != MatchKind.NoMatch && best == expected)
            {
                correct++;
                if (result.Kind == MatchKind.AutoAdd) auto++;
            }
        }
        Assert.Equal(40, Battery.Count);
        Assert.True(correct >= 36, $"correct {correct}/40");
        Assert.True(auto >= 35, $"auto-added {auto}/40");

        // SPEC-9 gate: the quantities must be right too, not just the items.
        var qtyCorrect = 0;
        foreach (var row in QuantityBattery)
        {
            var line = Assert.Single(_parser.Parse((string)row[0]));
            if (line.Qty?.Milli == (long?)row[1] && line.Unit == (Unit?)row[2] && line.Amount?.Value == (long?)row[3])
                qtyCorrect++;
        }
        Assert.Equal(40, QuantityBattery.Count);
        Assert.True(qtyCorrect >= 36, $"qty-correct {qtyCorrect}/40");
    }

    [Fact]
    public void FR07_PartialName_AsksInsteadOfAutoAdding()
    {
        // "పప్పు" alone fits several dals — never silently pick one.
        var (result, _) = Run("పప్పు అర కేజీ");
        Assert.NotEqual(MatchKind.AutoAdd, result.Kind);
    }

    [Theory]
    [InlineData("నూనె ఒక లీటరు")]
    [InlineData("సబ్బు రెండు")]
    public void H5_GenericWord_AsksInsteadOfGuessing(string phrase)
    {
        var (result, _) = Run(phrase);
        Assert.Equal(MatchKind.Confirm, result.Kind);
        Assert.True(result.Candidates.Count >= 2);
    }

    [Fact]
    public void T2_GenericAlias_SingleOilCatalog_StillAsks()
    {
        // VOICE-6 regression: with ONE oil in the shop whose curated alias is exactly the generic
        // word "నూనె", the alias used to score 1.0 and auto-add — in the full catalog only the
        // multi-item tie made generic words ask. The generic-alias cap must hold at any size.
        var singleOil = new[]
        {
            new Item { TeluguName = "పల్లి నూనె", EnglishName = "Groundnut oil", Aliases = ["నూనె"], Unit = Unit.Litre },
        };
        var line = Assert.Single(_parser.Parse("నూనె ఒక లీటరు"));
        var result = ItemMatcher.Match(line, singleOil, Options);
        Assert.Equal(MatchKind.Confirm, result.Kind);
        Assert.Equal(singleOil[0].Id, result.Best?.ItemId);
    }

    [Theory]
    [InlineData("రైస్ రెండు కేజీలు", "Rice (Sona Masoori)")]
    [InlineData("షుగర్ ఒక కేజీ", "Sugar")]
    [InlineData("బియ్యము ఐదు కేజీలు", "Rice (Sona Masoori)")]          // M13: ము → ం
    [InlineData("650 పారాసెటమాల్ రెండు స్ట్రిప్", "Paracetamol 650 mg")] // M1: dosage first, rotated by the matcher
    public void H5_M1_M13_HowShopkeepersActuallySpeak(string phrase, string expected)
    {
        var (result, best) = Run(phrase);
        Assert.Equal(MatchKind.AutoAdd, result.Kind);
        Assert.Equal(expected, best);
    }

    [Fact]
    public void H6_Range_IsAskedNotAutoAdded()
    {
        var (result, best) = Run("రెండు మూడు కేజీల బియ్యం");
        Assert.Equal(MatchKind.Confirm, result.Kind);
        Assert.Equal("Rice (Sona Masoori)", best);
    }

    [Fact]
    public void FR07_UnknownWord_IsNoMatch()
    {
        var (result, _) = Run("హెలికాప్టర్ రెండు");
        Assert.Equal(MatchKind.NoMatch, result.Kind);
    }

    [Fact]
    public void FR04_Seed_Has80Kirana40Pharmacy_AndNoAliasCollisions()
    {
        Assert.Equal(80, SeedCatalog.Kirana().Count);
        Assert.Equal(40, SeedCatalog.Pharmacy().Count);

        var owner = new Dictionary<string, string>();
        foreach (var item in Items)
            foreach (var key in new[] { item.TeluguName, item.EnglishName }.Concat(item.Aliases).Select(TeluguText.MatchKey).Distinct())
            {
                if (owner.TryGetValue(key, out var other)) Assert.Fail($"'{key}' used by {other} and {item.EnglishName}");
                owner[key] = item.EnglishName;
            }
    }

    [Fact]
    public void FR04_SeedSlabs_AreCurrentGstSlabs()
    {
        Assert.All(Items, i => Assert.Contains(i.GstSlab, GstSlabs.Current));
    }
}
