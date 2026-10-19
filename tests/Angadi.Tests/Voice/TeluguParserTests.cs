using Angadi.Application.Voice;
using Angadi.Domain.Items;

namespace Angadi.Tests.Voice;

/// <summary>FR-06 parser. Expected values match the Python prototype run on Day 2.</summary>
public class TeluguParserTests
{
    private readonly TeluguParser _parser = new();

    [Theory]
    // phrase,                               name,          qtyMilli, unit,         amountPaise
    [InlineData("రెండు కేజీల బియ్యం",            "బియ్యం",        2000L,   Unit.Kilogram, null)]
    [InlineData("చక్కెర ఒకటిన్నర కేజీ",           "చక్కెర",        1500L,   Unit.Kilogram, null)]
    [InlineData("పాలు రెండు ప్యాకెట్లు",          "పాలు",          2000L,   Unit.Pack,     null)]
    [InlineData("రెండు వందల గ్రాముల కారం",       "కారం",          200000L, Unit.Gram,     null)]
    [InlineData("రెండొందల గ్రాముల జీలకర్ర",      "జీలకర్ర",       200000L, Unit.Gram,     null)]
    [InlineData("నూనె ఒక లీటరు ఇవ్వండి",         "నూనె",          1000L,   Unit.Litre,    null)]
    [InlineData("గుడ్లు ఒక డజను",              "గుడ్లు",        12000L,  Unit.Piece,    null)]
    [InlineData("డోలో రెండు స్ట్రిప్పులు",         "డోలో",          2000L,   Unit.Strip,    null)]
    [InlineData("బిస్కెట్లు ఐదు",               "బిస్కెట్లు",     5000L,   null,          null)]
    [InlineData("టమాటా పావు కేజీ",             "టమాటా",         250L,    Unit.Kilogram, null)]
    [InlineData("పెరుగు ముప్పావు లీటరు",         "పెరుగు",        750L,    Unit.Litre,    null)]
    [InlineData("ఆరున్నర కేజీ బియ్యం",           "బియ్యం",        6500L,   Unit.Kilogram, null)]
    [InlineData("బియ్యం 2కేజీలు",              "బియ్యం",        2000L,   Unit.Kilogram, null)]
    [InlineData("౫ సబ్బులు",                   "సబ్బులు",       5000L,   null,          null)]
    [InlineData("సబ్బు 2",                     "సబ్బు",         2000L,   null,          null)]
    [InlineData("250 గ్రాములు కారం",            "కారం",          250000L, Unit.Gram,     null)]
    [InlineData("dolo 650 ఒక strip",           "dolo 650",     1000L,   Unit.Strip,    null)]
    [InlineData("pan 40 రెండు స్ట్రిప్పులు",       "pan 40",       2000L,   Unit.Strip,    null)]
    [InlineData("ఇరవై ఐదు కేజీలు బియ్యం",         "బియ్యం",        25000L,  Unit.Kilogram, null)]
    [InlineData("పది రూపాయల ఉల్లిపాయలు",        "ఉల్లిపాయలు",    null,    null,          1000L)]
    [InlineData("ఇరవై ఐదు రూపాయల పచ్చిమిర్చి",   "పచ్చిమిర్చి",    null,    null,          2500L)]
    [InlineData("₹20 టమాటా",                  "టమాటా",         null,    null,          2000L)]
    public void FR06_ParsesQuantityUnitAndName(string phrase, string name, long? qty, Unit? unit, long? amount)
    {
        var line = Assert.Single(_parser.Parse(phrase));
        Assert.Equal(name, line.Name);
        Assert.Equal(qty, line.Qty?.Milli);
        Assert.Equal(unit, line.Unit);
        Assert.Equal(amount, line.Amount?.Value);
    }

    [Fact]
    public void H4_DecimalQuantity_IsNotSplitAtThePoint()
    {
        var line = Assert.Single(_parser.Parse("2.5 కేజీల బియ్యం"));
        Assert.Equal("బియ్యం", line.Name);
        Assert.Equal(2500L, line.Qty?.Milli);   // was two phrases → 7 kg
        Assert.Equal(Unit.Kilogram, line.Unit);
    }

    [Fact]
    public void H4_SentenceFullStop_StillSplitsPhrases()
    {
        var lines = _parser.Parse("బియ్యం రెండు కేజీలు. చక్కెర ఒక కేజీ");
        Assert.Equal(new[] { "బియ్యం", "చక్కెర" }, lines.Select(l => l.Name));
    }

    [Fact]
    public void V1_Thousand_IsANumber_NotAFiller()
    {
        var line = Assert.Single(_parser.Parse("వెయ్యి రూపాయల బియ్యం"));
        Assert.Equal("బియ్యం", line.Name);
        Assert.Equal(100_000L, line.Amount?.Value);   // ₹1,000 — was silently dropped
    }

    [Fact]
    public void FR06_NameOnly_HasNoQuantity()
    {
        var line = Assert.Single(_parser.Parse("అగ్గిపెట్టె"));
        Assert.Equal("అగ్గిపెట్టె", line.Name);
        Assert.Null(line.Qty);
        Assert.Null(line.Unit);
    }

    [Fact]
    public void FR06_SplitsOnCommaAndConnectors()
    {
        var lines = _parser.Parse("బియ్యం రెండు కేజీలు, పప్పు అర కేజీ ఇంకా సబ్బు మూడు");
        Assert.Equal(new[] { "బియ్యం", "పప్పు", "సబ్బు" }, lines.Select(l => l.Name));
        Assert.Equal(new long?[] { 2000, 500, 3000 }, lines.Select(l => l.Qty?.Milli));
    }

    [Fact]
    public void FR06_MixedEnglish_SplitsOnAnd()
    {
        var lines = _parser.Parse("2 kg rice and sugar 1 kg");
        Assert.Equal(new[] { "rice", "sugar" }, lines.Select(l => l.Name));
        Assert.All(lines, l => Assert.Equal(Unit.Kilogram, l.Unit));
    }

    [Fact]
    public void FR06_ZeroWidthJoiners_AreIgnored()
    {
        var line = Assert.Single(_parser.Parse("సన్‌ఫ్లవర్ నూనె రెండు లీటర్లు"));
        Assert.Equal("సన్ఫ్లవర్ నూనె", line.Name);
    }

    [Fact]
    public void FR06_NumbersOnly_YieldsNothing()
    {
        Assert.Empty(_parser.Parse("రెండు కేజీలు"));
        Assert.Empty(_parser.Parse("   "));
    }

    [Fact]
    public void Parser_IsStateless_AcrossCalls()
    {
        Assert.Single(_parser.Parse("బియ్యం రెండు కేజీలు"));
        Assert.Single(_parser.Parse("చక్కెర ఒక కేజీ"));
    }
}
