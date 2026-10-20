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
