using Angadi.Application.Common;
using Angadi.Application.Voice;
using Angadi.Domain.Items;
using Angadi.Infrastructure.Seed;

namespace Angadi.Tests.Voice;

/// <summary>
/// Regional Telugu battery (VD, 2026-10-22): how kirana and pharmacy customers actually speak in
/// Telangana (incl. Hyderabadi Dakhni/Hindi-Urdu), Coastal Andhra, Rayalaseema and Uttarandhra,
/// English-Telugu code-mixing, and what the recogniser returns (Telugu script, digits, Latin).
/// 212 phrases against the 120 seed items, parser → matcher → the bill's unit check.
///
/// A phrase is CORRECT when it auto-adds exactly the expected item and quantity, or — when the
/// expectation is "ask" (a generic word, a range, a sack of unknown weight, a symptom) — when the
/// counter asks with the expected item among the candidates. Gate: ≥ 95 % correct and ZERO wrong
/// auto-adds (a wrong auto-add is a money error; asking costs one tap).
/// To add a phrase: add a row below. To make one pass: add the word to Resources/voice/*.json or an
/// alias in tools/generate_seed.py — never special-case the parser for a single phrase.
/// </summary>
public class DialectBatteryTests
{
    public sealed record Expect(string? Item, long? Qty, Unit? Unit, long? Amount, bool Ask);
    public sealed record Case(string Region, string Phrase, Expect[] Lines);

    private static Expect Add(string item, long? qty = null, Unit? unit = null, long? amount = null) => new(item, qty, unit, amount, false);
    private static Expect Ask(string? item = null, long? qty = null, Unit? unit = null, long? amount = null) => new(item, qty, unit, amount, true);

    private static readonly IReadOnlyList<Item> Items =
        SeedCatalog.Kirana().Concat(SeedCatalog.Pharmacy()).Select(s => s.Item).ToList();

    private static readonly VoiceOptions Options = new();
    private static readonly TeluguParser Parser = new();

    // Qty in milli of the SPOKEN unit; Amount in paise. Region tag = where/how it is said.
    public static readonly Case[] Cases =
    [
        new("Telangana", "ఉల్లిగడ్డలు రెండు కిలోలు", [Add("Onions", 2000L, Unit.Kilogram)]),
        new("Telangana", "ఆలుగడ్డ అర కిలో", [Add("Potatoes", 500L, Unit.Kilogram)]),
        new("Telangana", "తెల్లగడ్డ పావు కిలో", [Add("Garlic", 250L, Unit.Kilogram)]),
        new("Telangana", "కొత్మీర ఒక కట్ట", [Add("Coriander leaves", 1000L, Unit.Pack)]),
        new("Telangana", "కరేపాకు రెండు కట్టలు", [Add("Curry leaves", 2000L, Unit.Pack)]),
        new("Telangana", "జర బియ్యం ఐదు కిలోలు ఇయ్యి", [Add("Rice (Sona Masoori)", 5000L, Unit.Kilogram)]),
        new("Telangana", "పాల కవర్ రెండు", [Add("Milk (500 ml)", 2000L, Unit.Pack)]),
        new("Telangana", "రెండు పాల కవర్లు", [Add("Milk (500 ml)", 2000L, Unit.Pack)]),
        new("Telangana", "పల్లీలు అర కిలో", [Add("Groundnuts", 500L, Unit.Kilogram)]),
        new("Telangana", "పల్లి నూనె ఒక లీటర్", [Add("Groundnut oil", 1000L, Unit.Litre)]),
        new("Telangana", "చక్కెర కిలోన్నర", [Add("Sugar", 1500L, Unit.Kilogram)]),
        new("Telangana", "కందిపప్పు కేజీన్నర", [Add("Toor dal", 1500L, Unit.Kilogram)]),
        new("Telangana", "చాయ్ పొడి ఒక ప్యాకెట్", [Add("Tea powder (250 g)", 1000L, Unit.Pack)]),
        new("Telangana", "పెరుగు ఒక కవర్", [Add("Curd (500 g)", 1000L, Unit.Pack)]),
        new("Telangana", "బెల్లం ఒక ఉండ", [Ask("Jaggery", 1000L, Unit.Piece)]),
        new("Telangana", "బియ్యం ఒక బస్తా", [Ask("Rice (Sona Masoori)", 1000L, Unit.Piece)]),
        new("Telangana", "అగ్గిపెట్టెలు పది", [Add("Matchbox", 10000L)]),
        new("Telangana", "గుడ్లు అర డజను", [Add("Eggs", 6000L, Unit.Piece)]),
        new("Telangana", "కోడిగుడ్లు రెండు డజన్లు", [Add("Eggs", 24000L, Unit.Piece)]),
        new("Telangana", "జీలకర్ర యాబై గ్రాములు", [Add("Cumin seeds", 50000L, Unit.Gram)]),
        new("Telangana", "పసుపు నూట యాభై గ్రాములు", [Add("Turmeric powder", 150000L, Unit.Gram)]),
        new("Telangana", "కారం రెండొందల గ్రాములు", [Add("Chilli powder", 200000L, Unit.Gram)]),
        new("Telangana", "ఇర్వై రూపాయల పచ్చిమిర్చి", [Add("Green chillies", null, null, 2000L)]),
        new("Telangana", "బియ్యం రెండు కేజీలు వెయ్యి", [Ask("Rice (Sona Masoori)", 2000L, Unit.Kilogram)]),
        new("Telangana", "బియ్యం వెయ్యి", [Ask("Rice (Sona Masoori)", 1000000L)]),
        new("Telangana", "సర్ఫ్ ఒక ప్యాకెట్ ఏసెయ్", [Add("Detergent powder (1 kg)", 1000L, Unit.Pack)]),
        new("Telangana", "నాల్గు సబ్బులు", [Ask(null, 4000L)]),
        new("Telangana", "చింతపండు పావు కిలో", [Add("Tamarind", 250L, Unit.Kilogram)]),
        new("Telangana", "ఆవాలు వంద గ్రాములు", [Add("Mustard seeds", 100000L, Unit.Gram)]),
        new("Telangana", "సగ్గుబియ్యం అర కిలో", [Add("Sabudana", 500L, Unit.Kilogram)]),
        new("Telangana", "రవ్వ ఒక కిలో", [Ask("Rava (sooji)", 1000L, Unit.Kilogram)]),
        new("Telangana", "ఇడ్లీ రవ్వ రెండు కిలోలు", [Add("Idli rava", 2000L, Unit.Kilogram)]),
        new("Telangana", "అల్లం యాబై గ్రాములు", [Add("Ginger", 50000L, Unit.Gram)]),
        new("Hyderabadi", "ఏక్ కిలో చావల్", [Add("Rice (Sona Masoori)", 1000L, Unit.Kilogram)]),
        new("Hyderabadi", "దో కిలో షక్కర్", [Add("Sugar", 2000L, Unit.Kilogram)]),
        new("Hyderabadi", "ఆధా కిలో తూర్ దాల్", [Add("Toor dal", 500L, Unit.Kilogram)]),
        new("Hyderabadi", "పావ్ కిలో అద్రక్", [Add("Ginger", 250L, Unit.Kilogram)]),
        new("Hyderabadi", "పావ్ రెండు", [Add("Bread", 2000L)]),
        new("Hyderabadi", "డేఢ్ కిలో ఆటా", [Add("Wheat atta", 1500L, Unit.Kilogram)]),
        new("Hyderabadi", "ఢాయ్ కిలో చావల్", [Add("Rice (Sona Masoori)", 2500L, Unit.Kilogram)]),
        new("Hyderabadi", "సవా కిలో షక్కర్", [Add("Sugar", 1250L, Unit.Kilogram)]),
        new("Hyderabadi", "పౌనా కిలో చనా దాల్", [Add("Chana dal", 750L, Unit.Kilogram)]),
        new("Hyderabadi", "సవా రెండు కిలో చావల్", [Ask("Rice (Sona Masoori)", 1250L, Unit.Kilogram)]),
        new("Hyderabadi", "తీన్ అండే", [Add("Eggs", 3000L)]),
        new("Hyderabadi", "ఏక్ దర్జన్ అండే", [Add("Eggs", 12000L, Unit.Piece)]),
        new("Hyderabadi", "చార్ నింబు", [Add("Lemons", 4000L)]),
        new("Hyderabadi", "పాంచ్ కిలో చావల్ దేదో", [Add("Rice (Sona Masoori)", 5000L, Unit.Kilogram)]),
        new("Hyderabadi", "ఏక్ డబల్ రొట్టె", [Add("Bread", 1000L)]),
        new("Hyderabadi", "దో ఠండా", [Add("Soft drink (750 ml)", 2000L)]),
        new("Hyderabadi", "హల్దీ వంద గ్రాములు", [Add("Turmeric powder", 100000L, Unit.Gram)]),
        new("Hyderabadi", "లాల్ మిర్చ్ పావ్ కిలో", [Add("Chilli powder", 250L, Unit.Kilogram)]),
        new("Hyderabadi", "నమక్ ఏక్ ప్యాకెట్", [Add("Salt (1 kg)", 1000L, Unit.Pack)]),
        new("Hyderabadi", "ఇమ్లీ ఆధా కిలో", [Add("Tamarind", 500L, Unit.Kilogram)]),
        new("Hyderabadi", "ఏక్ మాచిస్", [Add("Matchbox", 1000L)]),
        new("Hyderabadi", "దో మోంబత్తి", [Add("Candle", 2000L)]),
        new("Hyderabadi", "కోత్మీర్ ఏక్ కట్ట", [Add("Coriander leaves", 1000L, Unit.Pack)]),
        new("Hyderabadi", "ప్యాజ్ దో కిలో", [Add("Onions", 2000L, Unit.Kilogram)]),
        new("Hyderabadi", "టమాటర్ ఏక్ కిలో", [Add("Tomatoes", 1000L, Unit.Kilogram)]),
        new("Hyderabadi", "దహీ ఏక్ ప్యాకెట్", [Add("Curd (500 g)", 1000L, Unit.Pack)]),
        new("Hyderabadi", "దూద్ దో ప్యాకెట్", [Add("Milk (500 ml)", 2000L, Unit.Pack)]),
        new("Hyderabadi", "చాయ్ పత్తీ ఏక్ ప్యాకెట్", [Add("Tea powder (250 g)", 1000L, Unit.Pack)]),
        new("Hyderabadi", "ఏక్ కిలో తేల్", [Ask(null, 1000L, Unit.Kilogram)]),
        new("Hyderabadi", "దాల్ ఏక్ కిలో చాహియే", [Ask(null, 1000L, Unit.Kilogram)]),
        new("Hyderabadi", "సాబున్ దో", [Ask(null, 2000L)]),
        new("Hyderabadi", "లెహసున్ పావ్ కిలో", [Add("Garlic", 250L, Unit.Kilogram)]),
        new("Hyderabadi", "చీనీ ఏక్ కిలో", [Add("Sugar", 1000L, Unit.Kilogram)]),
        new("Hyderabadi", "భాయ్ ఏక్ కిలో ప్యాజ్ ఔర్ ఆధా కిలో టమాటర్", [Add("Onions", 1000L, Unit.Kilogram), Add("Tomatoes", 500L, Unit.Kilogram)]),
        new("Coastal", "బంగాళాదుంపలు రెండు కేజీలు", [Add("Potatoes", 2000L, Unit.Kilogram)]),
        new("Coastal", "పంచదార ఒకటిన్నర కేజీ", [Add("Sugar", 1500L, Unit.Kilogram)]),
        new("Coastal", "కందిపప్పు ఒక కేజీ ఇవ్వండి", [Add("Toor dal", 1000L, Unit.Kilogram)]),
        new("Coastal", "శనగపప్పు అర కేజీ", [Add("Chana dal", 500L, Unit.Kilogram)]),
        new("Coastal", "సెనగపప్పు పావు కేజీ", [Add("Chana dal", 250L, Unit.Kilogram)]),
        new("Coastal", "పెసరపప్పు ముప్పావు కేజీ", [Add("Moong dal", 750L, Unit.Kilogram)]),
        new("Coastal", "మినపప్పు రెండున్నర కేజీలు", [Add("Urad dal", 2500L, Unit.Kilogram)]),
        new("Coastal", "కొబ్బరికాయలు రెండు", [Add("Coconut", 2000L)]),
        new("Coastal", "కొబ్బరి నూనె ఒక సీసా", [Add("Coconut oil (200 ml)", 1000L, Unit.Piece)]),
        new("Coastal", "నెయ్యి ఒక డబ్బా", [Add("Ghee (500 ml)", 1000L, Unit.Piece)]),
        new("Coastal", "బియ్యము పది కేజీలు", [Add("Rice (Sona Masoori)", 10000L, Unit.Kilogram)]),
        new("Coastal", "బియ్యం ఒకటింపావు కేజీ", [Add("Rice (Sona Masoori)", 1250L, Unit.Kilogram)]),
        new("Coastal", "అటుకులు అర కేజీ ఇవ్వండి", [Add("Poha", 500L, Unit.Kilogram)]),
        new("Coastal", "చక్కెర ఒక కేజీ ఆ తర్వాత ఉప్పు ఒక ప్యాకెట్", [Add("Sugar", 1000L, Unit.Kilogram), Add("Salt (1 kg)", 1000L, Unit.Pack)]),
        new("Coastal", "నూనె రెండు లీటర్లు", [Ask(null, 2000L, Unit.Litre)]),
        new("Coastal", "పప్పు ఒక కేజీ", [Ask(null, 1000L, Unit.Kilogram)]),
        new("Coastal", "పిండి ఒక కేజీ", [Ask(null, 1000L, Unit.Kilogram)]),
        new("Coastal", "సబ్బు ఒకటి", [Ask(null, 1000L)]),
        new("Coastal", "పొడి వంద గ్రాములు", [Ask(null, 100000L, Unit.Gram)]),
        new("Coastal", "మసాలా ఒక ప్యాకెట్", [Ask("Garam masala (50 g)", 1000L, Unit.Pack)]),
        new("Coastal", "మిర్చి పావు కేజీ", [Ask(null, 250L, Unit.Kilogram)]),
        new("Coastal", "గోధుమ పిండి ఐదు కేజీలు", [Add("Wheat atta", 5000L, Unit.Kilogram)]),
        new("Coastal", "మైదా పిండి ఒక కేజీ", [Add("Maida", 1000L, Unit.Kilogram)]),
        new("Coastal", "బొంబాయి రవ్వ అర కేజీ", [Add("Rava (sooji)", 500L, Unit.Kilogram)]),
        new("Coastal", "అగరబత్తి ఒక ప్యాకెట్", [Add("Agarbatti", 1000L, Unit.Pack)]),
        new("Coastal", "యాలకులు ఒక ప్యాకెట్", [Add("Cardamom (10 g)", 1000L, Unit.Pack)]),
        new("Coastal", "లవంగాలు ఒక ప్యాకెట్", [Add("Cloves (10 g)", 1000L, Unit.Pack)]),
        new("Coastal", "మిరియాలు యాభై గ్రాములు", [Add("Black pepper", 50000L, Unit.Gram)]),
        new("Coastal", "కిస్మిస్ వంద గ్రాములు", [Add("Raisins", 100000L, Unit.Gram)]),
        new("Coastal", "జీడిపప్పు పావు కేజీ", [Add("Cashew", 250L, Unit.Kilogram)]),
        new("Coastal", "సేమియా రెండు ప్యాకెట్లు", [Add("Vermicelli (200 g)", 2000L, Unit.Pack)]),
        new("Coastal", "అప్పడాలు ఒక ప్యాకెట్", [Add("Papad", 1000L, Unit.Pack)]),
        new("Coastal", "ఆవకాయ ఒక సీసా", [Add("Mango pickle (500 g)", 1000L, Unit.Piece)]),
        new("Rayalaseema", "ఎర్రగడ్డలు రెండు కేజీలు", [Add("Onions", 2000L, Unit.Kilogram)]),
        new("Rayalaseema", "ఉర్లగడ్డలు ఒక కేజీ", [Add("Potatoes", 1000L, Unit.Kilogram)]),
        new("Rayalaseema", "ఎల్లిగడ్డలు పావు కేజీ", [Add("Garlic", 250L, Unit.Kilogram)]),
        new("Rayalaseema", "కందిబేడలు ఒక కేజీ కావల్ల", [Add("Toor dal", 1000L, Unit.Kilogram)]),
        new("Rayalaseema", "పెసరబేడలు అర కేజీ", [Add("Moong dal", 500L, Unit.Kilogram)]),
        new("Rayalaseema", "టెంకాయ ఒకటి", [Add("Coconut", 1000L)]),
        new("Rayalaseema", "రాగి పిండి రెండు కేజీలు", [Add("Ragi flour", 2000L, Unit.Kilogram)]),
        new("Rayalaseema", "జొన్నలు ఐదు కేజీలు", [Add("Jowar", 5000L, Unit.Kilogram)]),
        new("Rayalaseema", "బొబ్బర్లు అర కేజీ", [Add("Black-eyed beans", 500L, Unit.Kilogram)]),
        new("Rayalaseema", "అలసందలు పావు కేజీ", [Add("Black-eyed beans", 250L, Unit.Kilogram)]),
        new("Rayalaseema", "శనగలు ఒక కేజీ కావలె", [Add("Chickpeas", 1000L, Unit.Kilogram)]),
        new("Rayalaseema", "బియ్యం రెండు మూటలు", [Ask("Rice (Sona Masoori)", 2000L, Unit.Piece)]),
        new("Rayalaseema", "నాలుగ్ కేజీల బియ్యం", [Add("Rice (Sona Masoori)", 4000L, Unit.Kilogram)]),
        new("Rayalaseema", "మూడ్ కేజీలు చక్కెర", [Add("Sugar", 3000L, Unit.Kilogram)]),
        new("Rayalaseema", "పుట్నాల పప్పు అర కేజీ", [Add("Roasted gram", 500L, Unit.Kilogram)]),
        new("Rayalaseema", "అయ్దు గుడ్లు", [Add("Eggs", 5000L)]),
        new("Rayalaseema", "కారం ఒక కేజీ", [Add("Chilli powder", 1000L, Unit.Kilogram)]),
        new("Rayalaseema", "చింతపండు రెండు కేజీలు", [Add("Tamarind", 2000L, Unit.Kilogram)]),
        new("Rayalaseema", "అరకేజీ బెల్లం", [Add("Jaggery", 500L, Unit.Kilogram)]),
        new("Uttarandhra", "రొండు కేజీలు బియ్యం", [Add("Rice (Sona Masoori)", 2000L, Unit.Kilogram)]),
        new("Uttarandhra", "రొండు లీటర్లు పల్లి నూనె", [Add("Groundnut oil", 2000L, Unit.Litre)]),
        new("Uttarandhra", "పంచదార ఒక్క కేజీ", [Add("Sugar", 1000L, Unit.Kilogram)]),
        new("Uttarandhra", "ఒక్కటి అగ్గిపెట్టె", [Add("Matchbox", 1000L)]),
        new("Uttarandhra", "ఉల్లిపాయలు ఒక్క కేజీ", [Add("Onions", 1000L, Unit.Kilogram)]),
        new("Uttarandhra", "పాలు రెండు ప్యాకెట్టు", [Add("Milk (500 ml)", 2000L, Unit.Pack)]),
        new("Uttarandhra", "పెరుగు ఒక పాకెట్టు", [Add("Curd (500 g)", 1000L, Unit.Pack)]),
        new("Uttarandhra", "టమాటాలు రెండ్ కేజీలు", [Add("Tomatoes", 2000L, Unit.Kilogram)]),
        new("Uttarandhra", "కొబ్బరికాయ ఒక్కటి", [Add("Coconut", 1000L)]),
        new("Uttarandhra", "కరివేపాకు ఒక కట్ట", [Add("Curry leaves", 1000L, Unit.Pack)]),
        new("Code-mix", "రైస్ టూ కేజీ", [Add("Rice (Sona Masoori)", 2000L, Unit.Kilogram)]),
        new("Code-mix", "షుగర్ వన్ కేజీ", [Add("Sugar", 1000L, Unit.Kilogram)]),
        new("Code-mix", "హాఫ్ కేజీ టమాటా", [Add("Tomatoes", 500L, Unit.Kilogram)]),
        new("Code-mix", "క్వార్టర్ కేజీ అల్లం", [Add("Ginger", 250L, Unit.Kilogram)]),
        new("Code-mix", "సన్ ఫ్లవర్ ఆయిల్ వన్ లీటర్", [Add("Sunflower oil", 1000L, Unit.Litre)]),
        new("Code-mix", "ఆయిల్ ఒక లీటర్", [Ask(null, 1000L, Unit.Litre)]),
        new("Code-mix", "బాత్ సోప్ త్రీ", [Add("Bath soap", 3000L)]),
        new("Code-mix", "సోప్ టూ", [Ask(null, 2000L)]),
        new("Code-mix", "టూత్ పేస్ట్ వన్", [Add("Toothpaste (100 g)", 1000L)]),
        new("Code-mix", "కోల్గేట్ ఒకటి", [Add("Toothpaste (100 g)", 1000L)]),
        new("Code-mix", "బిస్కెట్ ప్యాకెట్స్ ఫైవ్", [Add("Biscuits", 5000L, Unit.Pack)]),
        new("Code-mix", "కూల్ డ్రింక్ టూ బాటిల్స్", [Add("Soft drink (750 ml)", 2000L, Unit.Piece)]),
        new("Code-mix", "వాటర్ బాటిల్ టెన్", [Add("Water bottle (1 L)", 10000L, Unit.Piece)]),
        new("Code-mix", "ఎగ్స్ వన్ డజన్", [Add("Eggs", 12000L, Unit.Piece)]),
        new("Code-mix", "చిప్స్ ఫోర్ ప్యాకెట్స్", [Add("Chips", 4000L, Unit.Pack)]),
        new("Code-mix", "నూడుల్స్ త్రీ ప్యాకెట్లు", [Add("Noodles", 3000L, Unit.Pack)]),
        new("Code-mix", "దాల్ ఒక కేజీ", [Ask(null, 1000L, Unit.Kilogram)]),
        new("Code-mix", "బటర్ ఒకటి", [Add("Butter (100 g)", 1000L)]),
        new("Code-mix", "పనీర్ ఒకటి", [Add("Paneer (200 g)", 1000L)]),
        new("Code-mix", "షాంపూ పది", [Add("Shampoo sachet", 10000L)]),
        new("Latin", "rendu kilo biyyam", [Add("Rice (Sona Masoori)", 2000L, Unit.Kilogram)]),
        new("Latin", "oka kilo chakkera", [Add("Sugar", 1000L, Unit.Kilogram)]),
        new("Latin", "ara kilo kandi pappu", [Add("Toor dal", 500L, Unit.Kilogram)]),
        new("Latin", "paavu kilo allam", [Add("Ginger", 250L, Unit.Kilogram)]),
        new("Latin", "two kg sugar", [Add("Sugar", 2000L, Unit.Kilogram)]),
        new("Latin", "half kg tomato", [Add("Tomatoes", 500L, Unit.Kilogram)]),
        new("Latin", "1 litre sunflower oil", [Add("Sunflower oil", 1000L, Unit.Litre)]),
        new("Latin", "ek kilo chawal", [Add("Rice (Sona Masoori)", 1000L, Unit.Kilogram)]),
        new("Latin", "dedh kilo atta", [Add("Wheat atta", 1500L, Unit.Kilogram)]),
        new("Latin", "dhai kilo chawal", [Add("Rice (Sona Masoori)", 2500L, Unit.Kilogram)]),
        new("Latin", "paav kilo haldi", [Add("Turmeric powder", 250L, Unit.Kilogram)]),
        new("Latin", "aadha kilo gud", [Add("Jaggery", 500L, Unit.Kilogram)]),
        new("Latin", "moodu packets milk", [Add("Milk (500 ml)", 3000L, Unit.Pack)]),
        new("Latin", "eggs one dozen", [Add("Eggs", 12000L, Unit.Piece)]),
        new("Latin", "darjan eggs", [Add("Eggs", 12000L, Unit.Piece)]),
        new("Latin", "rendunnara kilo biyyam", [Add("Rice (Sona Masoori)", 2500L, Unit.Kilogram)]),
        new("Latin", "oil 1 litre", [Ask(null, 1000L, Unit.Litre)]),
        new("Latin", "dal 1 kg", [Ask(null, 1000L, Unit.Kilogram)]),
        new("Latin", "toor dal 2 kg", [Add("Toor dal", 2000L, Unit.Kilogram)]),
        new("Latin", "moong dal half kg", [Add("Moong dal", 500L, Unit.Kilogram)]),
        new("Digits", "2 కేజీలు బియ్యం", [Add("Rice (Sona Masoori)", 2000L, Unit.Kilogram)]),
        new("Digits", "1/2 కేజీ చక్కెర", [Add("Sugar", 500L, Unit.Kilogram)]),
        new("Digits", "1/4 కేజీ జీలకర్ర", [Add("Cumin seeds", 250L, Unit.Kilogram)]),
        new("Digits", "1.5 కేజీ కందిపప్పు", [Add("Toor dal", 1500L, Unit.Kilogram)]),
        new("Digits", "500 గ్రాములు పెసరపప్పు", [Add("Moong dal", 500000L, Unit.Gram)]),
        new("Digits", "250 గ్రాం కారం", [Add("Chilli powder", 250000L, Unit.Gram)]),
        new("Digits", "100 gm పసుపు", [Add("Turmeric powder", 100000L, Unit.Gram)]),
        new("Digits", "₹50 ఉల్లిపాయలు", [Add("Onions", null, null, 5000L)]),
        new("Digits", "20 రూపాయల కొత్తిమీర", [Add("Coriander leaves", null, null, 2000L)]),
        new("Digits", "3 లీటర్లు పల్లి నూనె", [Add("Groundnut oil", 3000L, Unit.Litre)]),
        new("Digits", "2 బ్రెడ్", [Add("Bread", 2000L)]),
        new("Digits", "బియ్యం 25 కేజీలు", [Add("Rice (Sona Masoori)", 25000L, Unit.Kilogram)]),
        new("Pharmacy", "డోలో 650 రెండు స్ట్రిప్పులు", [Add("Paracetamol 650 mg", 2000L, Unit.Strip)]),
        new("Pharmacy", "dolo 650 one strip", [Add("Paracetamol 650 mg", 1000L, Unit.Strip)]),
        new("Pharmacy", "క్రోసిన్ ఒక స్ట్రిప్", [Add("Paracetamol 500 mg", 1000L, Unit.Strip)]),
        new("Pharmacy", "పారాసెటమాల్ ఒక స్ట్రిప్", [Ask("Paracetamol 650 mg", 1000L, Unit.Strip)]),
        new("Pharmacy", "paracetamol 2 strips", [Ask("Paracetamol 500 mg", 2000L, Unit.Strip)]),
        new("Pharmacy", "జ్వరం మాత్రలు ఒక షీట్", [Ask("Paracetamol 650 mg", 1000L, Unit.Strip)]),
        new("Pharmacy", "బీపీ మాత్ర ఒక స్ట్రిప్", [Ask("Amlodipine 5 mg", 1000L, Unit.Strip)]),
        new("Pharmacy", "గ్యాస్ మాత్ర రెండు స్ట్రిప్పులు", [Ask("Pantoprazole 40 mg", 2000L, Unit.Strip)]),
        new("Pharmacy", "షుగర్ మాత్ర ఒక స్ట్రిప్", [Ask("Metformin 500 mg", 1000L, Unit.Strip)]),
        new("Pharmacy", "నొప్పి మాత్ర ఒక స్ట్రిప్", [Ask("Ibuprofen 400 mg", 1000L, Unit.Strip)]),
        new("Pharmacy", "దగ్గు మందు ఒకటి", [Ask("Cough syrup (100 ml)", 1000L)]),
        new("Pharmacy", "మాత్రలు రెండు స్ట్రిప్పులు", [Ask(null, 2000L, Unit.Strip)]),
        new("Pharmacy", "సిరప్ ఒకటి", [Ask(null, 1000L)]),
        new("Pharmacy", "pan 40", [Ask("Pantoprazole 40 mg")]),
        new("Pharmacy", "pan 40 రెండు స్ట్రిప్పులు", [Add("Pantoprazole 40 mg", 2000L, Unit.Strip)]),
        new("Pharmacy", "టెల్మా 40 ఒక స్ట్రిప్", [Ask("Telmisartan 40 mg", 1000L, Unit.Strip)]),
        new("Pharmacy", "okacet ఒక స్ట్రిప్", [Add("Cetirizine 10 mg", 1000L, Unit.Strip)]),
        new("Pharmacy", "సిట్రిజిన్ ఒక స్ట్రిప్", [Add("Cetirizine 10 mg", 1000L, Unit.Strip)]),
        new("Pharmacy", "glycomet రెండు స్ట్రిప్పులు", [Add("Metformin 500 mg", 2000L, Unit.Strip)]),
        new("Pharmacy", "azee ఒక స్ట్రిప్", [Add("Azithromycin 500 mg", 1000L, Unit.Strip)]),
        new("Pharmacy", "ఓఆర్ఎస్ ఐదు", [Add("ORS sachet", 5000L)]),
        new("Pharmacy", "ఎలక్ట్రాల్ మూడు", [Add("ORS sachet", 3000L)]),
        new("Pharmacy", "డైజీన్ ఒక సీసా", [Add("Antacid syrup (170 ml)", 1000L, Unit.Piece)]),
        new("Pharmacy", "డెట్టాల్ ఒకటి", [Add("Antiseptic liquid (100 ml)", 1000L)]),
        new("Pharmacy", "మూవ్ ఒకటి", [Add("Pain relief ointment (30 g)", 1000L)]),
        new("Pharmacy", "విస్పర్ ఒక ప్యాకెట్", [Add("Sanitary pads", 1000L, Unit.Pack)]),
        new("Pharmacy", "మాస్కులు పది", [Add("Face mask", 10000L)]),
        new("Pharmacy", "షెల్కాల్ ఒక స్ట్రిప్", [Add("Calcium + D3", 1000L, Unit.Strip)]),
        new("Pharmacy", "సైనరెస్ట్ ఒక స్ట్రిప్", [Add("Cold relief tablets", 1000L, Unit.Strip)]),
        new("Pharmacy", "650 పారాసెటమాల్", [Ask("Paracetamol 650 mg")]),
        new("Pharmacy", "ప్లాస్టర్ ఒక ప్యాకెట్", [Add("Adhesive bandages", 1000L, Unit.Pack)]),
    ];

    public enum Outcome { Correct, Miss, WrongAutoAdd }

    /// <summary>Runs one phrase the way the counter does and grades every line.</summary>
    internal static (Outcome Outcome, string Detail) Grade(Case c)
    {
        var lines = Parser.Parse(c.Phrase);
        if (lines.Count != c.Lines.Length) return (Outcome.Miss, $"{lines.Count} lines, expected {c.Lines.Length}");
        var outcome = Outcome.Correct;
        var detail = new List<string>();
        for (var k = 0; k < lines.Count; k++)
        {
            var exp = c.Lines[k];
            var m = ItemMatcher.Match(lines[k], Items, Options);
            var line = m.Line;   // VD-5 may drop a quantity that was really the strength
            var best = m.Best is null ? null : Items.First(i => i.Id == m.Best.ItemId);
            // The bill asks too when the spoken unit can't be converted to the item's unit (a sack of a per-kg item).
            var unitAsks = false;
            if (m.Kind == MatchKind.AutoAdd && best is not null && line.Amount is null && line.Qty is { } spoken)
            {
                UnitConversion.ToItemUnit(spoken, line.Unit, best.Unit, out var exact);
                unitAsks = !exact;
            }
            var asks = m.Kind == MatchKind.Confirm || unitAsks;
            var parseOk = line.Qty?.Milli == exp.Qty && line.Unit == exp.Unit && line.Amount?.Value == exp.Amount;
            var got = $"{m.Kind}{(unitAsks ? "(unit asks)" : "")} {best?.EnglishName} qty={line.Qty?.Milli} {line.Unit} amt={line.Amount?.Value}";

            if (m.Kind == MatchKind.AutoAdd && !asks)
            {
                if (exp.Ask || best?.EnglishName != exp.Item || !parseOk) { outcome = Outcome.WrongAutoAdd; detail.Add("WRONG AUTO-ADD " + got); }
            }
            else if (asks)
            {
                var inCandidates = exp.Item is null || m.Candidates.Any(x => Items.First(i => i.Id == x.ItemId).EnglishName == exp.Item);
                if (!(exp.Ask && inCandidates && parseOk) && outcome == Outcome.Correct) { outcome = Outcome.Miss; detail.Add("asked: " + got); }
            }
            else if (outcome == Outcome.Correct)
            {
                outcome = Outcome.Miss;
                detail.Add("no match: " + got);
            }
        }
        return (outcome, string.Join("; ", detail));
    }

    public static TheoryData<string, string> Phrases()
    {
        var data = new TheoryData<string, string>();
        foreach (var c in Cases) data.Add(c.Region, c.Phrase);
        return data;
    }

    [Theory]
    [MemberData(nameof(Phrases))]
    public void VD_RegionalPhrase_IsNeverAWrongAutoAdd(string region, string phrase)
    {
        var c = Cases.Single(x => x.Region == region && x.Phrase == phrase);
        var (outcome, detail) = Grade(c);
        Assert.True(outcome != Outcome.WrongAutoAdd, $"[{region}] {phrase}: {detail}");
    }

    [Fact]
    public void VD_DialectBattery_MeetsGate()
    {
        Assert.True(Cases.Length >= 150, $"only {Cases.Length} phrases");
        Assert.Equal(Cases.Length, Cases.Select(c => c.Phrase).Distinct(StringComparer.Ordinal).Count());
        foreach (var region in new[] { "Telangana", "Hyderabadi", "Coastal", "Rayalaseema", "Uttarandhra", "Code-mix", "Latin", "Digits", "Pharmacy" })
            Assert.True(Cases.Count(c => c.Region == region) >= 10, $"region {region} has under 10 phrases");

        var graded = Cases.Select(c => (c, g: Grade(c))).ToList();
        var wrong = graded.Where(x => x.g.Outcome == Outcome.WrongAutoAdd).Select(x => $"{x.c.Phrase}: {x.g.Detail}").ToList();
        var correct = graded.Count(x => x.g.Outcome == Outcome.Correct);
        var misses = string.Join(" | ", graded.Where(x => x.g.Outcome == Outcome.Miss).Select(x => $"{x.c.Phrase}: {x.g.Detail}"));

        Assert.True(wrong.Count == 0, "wrong auto-adds: " + string.Join(" | ", wrong));
        Assert.True(correct * 100 >= Cases.Length * 95, $"correct {correct}/{Cases.Length}; misses: {misses}");
    }

    // ---------- the parser fixes the battery depends on (VD-1 … VD-5) ----------

    [Theory]
    [InlineData("చక్కెర కిలోన్నర", "చక్కెర", 1500L, Unit.Kilogram)]
    [InlineData("కందిపప్పు కేజీన్నర", "కందిపప్పు", 1500L, Unit.Kilogram)]
    [InlineData("పాలు లీటరున్నర", "పాలు", 1500L, Unit.Litre)]
    public void VD1_UnitAndAHalf_IsOneAndAHalfOfThatUnit(string phrase, string name, long qty, Unit unit)
    {
        var line = Assert.Single(Parser.Parse(phrase));
        Assert.Equal(name, line.Name);
        Assert.Equal(qty, line.Qty?.Milli);
        Assert.Equal(unit, line.Unit);
    }

    [Fact]
    public void VD2_ThousandAfterAUnit_IsNotAMultiplier()
    {
        // వెయ్యి is also 'put it': "రెండు కేజీలు వెయ్యి" must never become 2,000 kg.
        var line = Assert.Single(Parser.Parse("బియ్యం రెండు కేజీలు వెయ్యి"));
        Assert.Equal(2000L, line.Qty?.Milli);
        Assert.True(line.IsAmbiguous);
        // Adjacent compounds still multiply.
        Assert.Equal(500_000L, Assert.Single(Parser.Parse("ఐదు వెయ్యి రూపాయల బియ్యం")).Amount?.Value);
        Assert.False(Assert.Single(Parser.Parse("ఐదు వెయ్యి రూపాయల బియ్యం")).IsAmbiguous);
    }

    [Fact]
    public void VD3_Paav_IsAQuarterBeforeAUnit_AndBreadOtherwise()
    {
        Assert.Equal(250L, VoiceRules.Default.UnitBoundNumbers["పావ్"]);
        var quarter = Assert.Single(Parser.Parse("పావ్ కిలో చక్కెర"));
        Assert.Equal("చక్కెర", quarter.Name);
        Assert.Equal(250L, quarter.Qty?.Milli);
        Assert.Equal(Unit.Kilogram, quarter.Unit);

        var bread = Assert.Single(Parser.Parse("పావ్ రెండు"));
        Assert.Equal("పావ్", bread.Name);
        Assert.Equal(2000L, bread.Qty?.Milli);
    }

    [Theory]
    [InlineData("బియ్యం వెయ్యి")]
    [InlineData("వంద గుడ్లు")]
    [InlineData("బిస్కెట్లు నూట యాభై")]
    public void VD4_BareCountOfAHundredOrMore_Asks(string phrase)
    {
        Assert.True(Assert.Single(Parser.Parse(phrase)).IsAmbiguous);
    }

    [Theory]
    [InlineData("రెండొందల గ్రాముల జీలకర్ర")]
    [InlineData("పది రూపాయల పచ్చిమిర్చి")]
    [InlineData("బిస్కెట్లు ఐదు")]
    public void VD4_UnitsAmountsAndSmallCounts_StayCertain(string phrase)
    {
        Assert.False(Assert.Single(Parser.Parse(phrase)).IsAmbiguous);
    }

    [Fact]
    public void VD5_StrengthSpokenAsANumber_IsNotAQuantity()
    {
        var m = ItemMatcher.Match(Assert.Single(Parser.Parse("pan 40")), Items, Options);
        Assert.Equal(MatchKind.Confirm, m.Kind);
        Assert.Equal("Pantoprazole 40 mg", Items.First(i => i.Id == m.Best!.ItemId).EnglishName);
        Assert.Null(m.Line.Qty);   // was 40 strips

        // A real count on a plain name is untouched.
        var soap = ItemMatcher.Match(Assert.Single(Parser.Parse("బాత్ సోప్ 2")), Items, Options);
        Assert.Equal(MatchKind.AutoAdd, soap.Kind);
        Assert.Equal(2000L, soap.Line.Qty?.Milli);
    }
}
