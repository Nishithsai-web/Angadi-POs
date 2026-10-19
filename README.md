# Angadi POS

Telugu-first, Square-like point-of-sale app for kirana shops and pharmacies — .NET MAUI, Android-first, on-device SQLite, zero backend.

> "మీ అంగడి, మీ హిసాబు — మీ భాషలో." — Your shop, your accounts, in your language.

## Status

**Day 1 — requirements + contracts.** `docs/SRS.md` is drafted with the Day 1 decisions (₹200/mo, Tier 2 LLM moved to v2, one role, tablet-first) and awaits sign-off. Domain contracts are in `src/Angadi.Domain` for review; they are not yet compiled.

## Documents

| File | What it is |
|---|---|
| [`docs/Angadi_HQ.html`](docs/Angadi_HQ.html) | Project HQ: product spec, architecture blueprint (canonical), 14-day agent briefs, original Galla kickoff pack |
| [`docs/SRS.md`](docs/SRS.md) | Software Requirements Specification — **draft, awaiting sign-off** |

## Planned solution layout (per blueprint §2)

```
Angadi.sln
├── Angadi.Core            # billing engine, GST math, dues ledger, Result<T>
├── Angadi.Voice           # ISpeechToText, Telugu parser, fuzzy matcher
├── Angadi.Comms           # PDF receipts, WhatsApp share, Play Billing gate
├── Angadi.AI              # on-device insights (Tier 2 LLM assistant is v2)
├── Angadi.Domain          # entities, zero dependencies
├── Angadi.Application     # Mediator commands/queries, pipeline behaviors
├── Angadi.Infrastructure  # sqlite-net-pcl, outbox, platform adapters
├── Angadi.Maui            # Shell UI, CommunityToolkit.Mvvm, AngadiTheme
└── Angadi.Tests
```
