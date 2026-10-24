# Voice pipeline (FR-06, FR-07, NFR-12)

```
ISpeechToText (Android te-IN, needs internet)
  → utterance text
  → TeluguParser      rules from Resources/voice/*.json
  → ParsedLine[]      name + qty + unit, or name + ₹ amount
  → ItemMatcher       vs the shop's own items + aliases
  → AutoAdd ≥ 0.90 | Confirm 0.70–0.90 | NoMatch
  → AliasLearner      a confirmed correction becomes an alias → exact next time
```

No LLM and no paid API. Every bill's voice path is deterministic and costs ₹0.

## Parser rules
- **Normalise:** NFC, Telugu digits → ASCII, zero-width joiners removed, Latin lower-cased.
- **Split phrases** on `,` `.` and the words in `grammar.te.json` → `separators` (ఇంకా, మరియు, తర్వాత, and…).
- **Word order is free.** Numbers, units, multipliers (డజను), currency (రూపాయల) and fillers (ఇవ్వండి) are recognised. Everything else is the item name.
- **Numbers** add up: ఇరవై ఐదు = 25. Fractions: అర ½, పావు ¼, ముప్పావు ¾, -న్నర +½ (ఆరున్నర = 6½). A hundred-word after a number multiplies (రెండు వందల = 200). Fused hundreds are listed (రెండొందల = 200).
- **Fused tokens** are split: `2కేజీలు`, `రెండుకేజీలు`, `₹20`.
- **Digits in product names** ("dolo 650", "pan 40") stay in the name. A digit token is a quantity only when a unit or currency word follows it, a currency word precedes it, or it's under 100 with no other number in the phrase.
- **Amount mode:** a number with a currency word ("పది రూపాయల ఉల్లిపాయలు") becomes `Amount = ₹10`. The bill computes the quantity as amount ÷ price.

## Matcher rules
- **Match key:** normalised, spaces removed, a final `మ్` → `ం`, and the plural/oblique endings `లు`/`ల` (or English `s`) stripped.
- **Scores:**
  - An exact key match on the Telugu name, English name or any alias scores **1.0**.
  - When one name contains the other, the score is 0.80–0.90, so it always asks.
  - Otherwise the score is 1 − edit distance ÷ length.
- **Ties:** if the top two are within 0.05 of each other, an auto-add becomes Confirm.
- **Candidates:** up to `VoiceOptions.MaxCandidates` (3) with score ≥ 0.40.

## Fixing a misheard word
Add the word to the item's aliases in the app, or to `Resources/voice/*.json` for numbers and units. No code change is needed. Confirming on the counter does this automatically.

## Battery (tests/Angadi.Tests/Voice/VoiceBatteryTests.cs)
40 counter phrases against the 120 seed items (plus the 212-phrase regional battery above). Day 2 prototype: **30/30 correct, 29/30 auto-added** (target ≥ 27/30). The 40-phrase integration battery (≥ 35/40 with no confirmation) runs on a real device with real speech. See `docs/TEST_PLAN.md`.

## Regional Telugu (VD, 2026-10-22)
**Covered** (battery `tests/Angadi.Tests/Voice/DialectBatteryTests.cs`, 212 phrases, gate ≥ 95 % and zero wrong auto-adds):
- **Telangana / Hyderabad:** ఉల్లిగడ్డ, ఆలుగడ్డ, తెల్లగడ్డ (garlic), కొత్మీర, కరేపాకు, పాల **కవర్** (= packet), ఒక **కట్ట**, జర (please), ఇయ్యి/ఏసెయ్; Dakhni/Hindi-Urdu counting ఏక్ దో తీన్ చార్ పాంచ్, ఆధా ½, డేఢ్ 1½, ఢాయ్ 2½, సవా 1¼, పౌనా ¾, **పావ్ కిలో** ¼ kg (but "పావ్" alone = pav bread), దర్జన్; item words chawal, shakkar/cheeni, namak, haldi, imli, adrak, lehsun, pyaz, tamatar, dahi, doodh, anda, double roti, thanda, machis, mombatti; fillers దేదో, చాహియే, భాయ్; separator ఔర్.
- **Coastal Andhra:** బంగాళాదుంప, పంచదార, బియ్యము (old -ము ending), ఒకటింపావు 1¼, సీసా/డబ్బా containers, "ఆ తర్వాత" between items.
- **Rayalaseema:** ఎర్రగడ్డలు (onions), ఉర్లగడ్డలు (potatoes), ఎల్లిగడ్డలు (garlic), కందిబేడలు/పెసరబేడలు (dal), టెంకాయ (coconut), కావలె/కావల్ల, clipped నాలుగ్/మూడ్, మూట/బస్తా sacks.
- **Uttarandhra:** రొండు, ఒక్క/ఒక్కటి, ప్యాకెట్టు/పాకెట్టు.
- **Numbers everywhere:** ఒకటి/ఒక/ఒక్క, రెండు/రెండ్, అర, పావు, ముప్పావు, సగం, -న్నర (ఒకటిన్నర, రెండున్నర, and **కిలోన్నర / కేజీన్నర / లీటరున్నర** = 1½ of that unit), నూట యాభై (150), యాబై/నలబై/తొంబై spellings; English inside Telugu (వన్, టూ, హాఫ్, క్వార్టర్); Latin from the recogniser (rendu, ara, paavu, dedh, aadha, one, half); digits, "1/2", "1.5", "2,500", "₹50".
- **Units with plural/case endings:** కేజీ/కేజి/కిలో/కిలోలు/కేజీల, గ్రాం/గ్రాములు/gm, లీటర్/లీటర్లు/ltr, ప్యాకెట్/ప్యాకెట్లు/ప్యాకెట్స్, బాటిల్/బాటిల్స్, స్ట్రిప్/షీట్, డజను/డజన్లు.
- **Pharmacy:** brand words as people say them (dolo, crocin, okacet, glycomet, azee, pantop, telma, digene, moov, whisper…). **Always ask:** symptom/class phrases (జ్వరం మాత్రలు, బీపీ మాత్ర, గ్యాస్ మాత్ర, షుగర్ మాత్ర, మాత్ర, సిరప్, మందు) and bare "paracetamol" (500 or 650?). A strength spoken as a number ("pan 40", "650 పారాసెటమాల్") is never a quantity: the line asks with no count (VD-5).
- **Generic words always ask**, never auto-add: నూనె/ఆయిల్/oil/tel, పప్పు/దాల్/dal, సబ్బు/సోప్/sabun, పిండి, పొడి/powder, రవ్వ (Bombay or idli?), మిర్చి (green or powder?), మసాలా.

**How to add a word — JSON only, no code:**
- A number, fraction or Hindi/English number word → `Resources/voice/numbers.te.json` (`units`, `tens`, `fractions`; values in thousandths: 1½ = 1500). A word that is a number only before a unit (like పావ్) → `unitBoundNumbers`.
- A unit or container → `units.te.json` `units` (value = the `Unit` enum name); a "×12" word → `multipliers`.
- "Please / give / want" words → `grammar.te.json` `fillers`; "and / then" → `separators`; a category word that must always ask → `genericAliases`.
- A regional item name → its alias list in `tools/generate_seed.py`, then `python3 tools/generate_seed.py` (CI fails on alias collisions and on a hand-edited seed JSON). Shops add their own words in the item screen; confirming at the counter learns them.
- Then add a phrase row to `DialectBatteryTests.Cases` — expected item and quantity, or `Ask(...)` when the honest answer is to ask.

**Known limits:**
- Shop noise, two people speaking, and recogniser mishearings (a wrong number heard as a right-sounding one) happen before the parser and are outside its control; the matcher only rescues names.
- Hindi modifiers before a number ("సవా రెండు" 2¼, "పౌనే రెండు" 1¾) are read as a range and **ask** — never guessed.
- "గుడ్" (Hindi jaggery) collides with eggs through plural stripping (VD-6); use బెల్లం/gud (Latin).
- Sacks and balls (బస్తా, మూట, ఉండ) have no weight: on per-kg items the bill asks for the quantity.
- Bare counts of 100 or more without a unit ("బియ్యం వెయ్యి") always ask — వెయ్యి is also the verb "put".

## Known limits
- Speech recognition quality is Google's. The matcher only rescues names, not wrong numbers.
- Telugu voice needs internet (NFR-01). The error message `VOICE_NO_INTERNET` sends the shopkeeper to tap or type instead.
