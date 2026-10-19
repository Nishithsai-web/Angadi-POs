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
30 counter phrases against the 120 seed items. Day 2 prototype: **30/30 correct, 29/30 auto-added** (target ≥ 27/30). The 40-phrase integration battery (≥ 35/40 with no confirmation) runs on a real device with real speech. See `docs/TEST_PLAN.md`.

## Known limits
- Speech recognition quality is Google's. The matcher only rescues names, not wrong numbers.
- Telugu voice needs internet (NFR-01). The error message `VOICE_NO_INTERNET` sends the shopkeeper to tap or type instead.
