using Angadi.Application.Common;
using Angadi.Application.Voice;
using Angadi.Domain.Items;
using Angadi.Infrastructure.Seed;

namespace Angadi.Tests.Voice;

/// <summary>
/// NFR-12 unit battery: 30 realistic counter phrases against the 120 seed items.
/// Target ≥ 27/30 correct. The Day 2 prototype scored 30/30 correct, 29/30 auto-added.
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
        { "నూనె ఒక లీటరు", "Sunflower oil" },
        { "పల్లీ నూనె ఒక లీటరు", "Groundnut oil" },
        { "గుడ్లు ఒక డజను", "Eggs" },
        { "సబ్బు రెండు", "Bath soap" },
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
        Assert.True(correct >= 27, $"correct {correct}/30");
        Assert.True(auto >= 25, $"auto-added {auto}/30");
    }

    [Fact]
    public void FR07_PartialName_AsksInsteadOfAutoAdding()
    {
        // "పప్పు" alone fits several dals — never silently pick one.
        var (result, _) = Run("పప్పు అర కేజీ");
        Assert.NotEqual(MatchKind.AutoAdd, result.Kind);
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
