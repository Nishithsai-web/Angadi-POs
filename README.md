# Angadi POS — అంగడి

> **మీ అంగడి, మీ హిసాబు — మీ భాషలో.** Your shop, your accounts, in your language.

Angadi is a Telugu-first point-of-sale app for kirana shops and small pharmacies in Andhra Pradesh and Telangana. The shopkeeper says the items out loud in Telugu (బియ్యం రెండు కేజీలు, పప్పు అర కేజీ), scans barcodes or taps favourites. Angadi builds a GST-correct bill, takes cash, UPI or udhaar, and sends the receipt on WhatsApp. It also tracks stock, low-stock alerts, customer dues, day-end and GST reports, and gives free on-device insights (reorder forecast, dead stock, morning summary). It runs on Android 9+ as a .NET 10 MAUI app. All data stays in on-device SQLite and there is no backend. It is free for 50 bills a month, then ₹200/month or ₹1,999/year through Google Play Billing.

## Status — honest

> **The code has never been compiled.** It was written in a sandbox with no .NET SDK, so no `dotnet build` or `dotnet test` has run yet. Expect a round of compile fixes on the first build. The steps are below.

| Day | Done |
|---|---|
| 1 | SRS v1.0 draft (`docs/SRS.md`, awaiting sign-off), Day 1 decisions (₹200/₹1,999, Tier 2 LLM moved to v2, one role, tablet-first), Domain contracts |
| 1 PM | Solution scaffold (5 projects, ADR-7), Application contracts, Telugu/English resources, ADRs |
| 2–3 | Billing engine (`BillCalculator`, GST 2.0 slabs), Telugu voice pipeline (parser, matcher, alias learner), seed data (80 kirana + 40 pharmacy items) |
| 4 | SQLite data layer, unit of work, backup/restore, Serilog file logging, seeding |
| 5 | Application handlers, in-house dispatcher (ADR-2 revised), insights, reports, UI groundwork (`src/Angadi.Maui/Core`) |
| 6 | **In progress, in parallel:** MAUI UI areas (POS, items, customers/bills, reports/settings), Android platform adapters (speech, camera, share, Play Billing), deployment/runbook/privacy/user-guide docs |
| 7–14 | Integration, device testing, UAT, Play closed testing — not started |

About 50 xUnit tests are written: billing math, the voice battery, the data layer, resources and layer rules. None has run yet. The voice battery numbers in `docs/VOICE_PIPELINE.md` come from the Day 2 prototype, not from the C# test run.

## Repo map

```
Angadi.sln
Directory.Build.props        shared C# settings (nullable, warnings, central package management on)
Directory.Packages.props     every NuGet version, pinned in one place
src/
  Angadi.Domain/             entities, Paise/Qty, BillCalculator, GST slabs — zero dependencies
  Angadi.Application/        commands/queries + handlers, dispatcher, Result<T>, voice parser/matcher,
                             insights, reports; Resources/*.json (UI strings, errors, voice rules)
  Angadi.Infrastructure/     sqlite-net repositories, unit of work, outbox, backup/restore, Serilog,
                             receipt PDF, seed JSON — platform-neutral, tests run it on Linux/Windows
  Angadi.Maui/               the Android app: Shell, Areas/<feature> pages + ViewModels, Platforms/Android
tests/
  Angadi.Tests/              xUnit, one folder per layer; test names carry FR/NFR numbers
tools/
  generate_seed.py           regenerates Seed/kirana.json + pharmacy.json; fails on alias collisions
docs/                        see the index below
```

Dependency rule: `Maui → Application → Domain ← Infrastructure`. Details are in `docs/ARCHITECTURE.md`.

## First build

### 1. Install
- **.NET 10 SDK** (10.0.100 or later): `dotnet --version`.
- **IDE.** Visual Studio 2022 17.14+ with the *.NET Multi-platform App UI* workload, or Visual Studio 2026, or the CLI alone.
  - Microsoft ships .NET 10 support in Visual Studio 2026. If VS 2022 refuses to load the `net10.0` projects, use VS 2026 or the CLI below. Rider also works.
- **MAUI Android workload:**
  ```bash
  dotnet workload install maui-android
  ```
- **Android SDK (API 36) and JDK 17+.** Visual Studio installs both. On the CLI you can let the build fetch them:
  ```bash
  dotnet build src/Angadi.Maui -t:InstallAndroidDependencies -f net10.0-android \
    -p:AndroidSdkDirectory="$HOME/android-sdk" -p:JavaSdkDirectory="<path to JDK 17+>" -p:AcceptAndroidSDKLicenses=True
  ```

### 2. Build the core first (no MAUI needed)
Domain, Application, Infrastructure and Tests target plain `net10.0`, so they build and test on any OS. Get these green before touching the app head.
```bash
dotnet restore
dotnet test tests/Angadi.Tests
```

### 3. Build the whole solution and run the app
```bash
dotnet build                                                   # whole solution, including Angadi.Maui
dotnet build src/Angadi.Maui -t:Run -f net10.0-android         # deploys to the running emulator / USB device
```
- **Emulator.** Create a Pixel tablet or phone image with API 34+ in Android Device Manager and start it first.
- **Physical device.** Turn on Developer options → USB debugging and check `adb devices`.
- **Voice.** Voice needs a real device or a Google Play emulator image with internet, plus the Telugu speech pack: Google app → Settings → Voice → Languages → తెలుగు (భారతదేశం).

### 4. If a package version fails to restore
Every version lives in **`Directory.Packages.props`**. They were pinned on Day 1 without NuGet access, so one may not exist.
1. Find the latest stable in the **same major**, e.g. `dotnet package search Plugin.InAppBilling --exact-match`, or check nuget.org.
2. Change only that `<PackageVersion>` line. Never add `Version=` to a `PackageReference` in a .csproj (central package management rejects it).
3. Record the change in `docs/ARCHITECTURE.md` §6 Dependencies.
4. Skia packages must move together (`SkiaSharp`, `SkiaSharp.HarfBuzz`, `SkiaSharp.NativeAssets.Linux.NoDependencies` on the same version).

After the first green build, set `TreatWarningsAsErrors` to `true` in `Directory.Build.props`.

### 5. Things the first build needs that are not in the repo yet
- **Telugu font.** `NotoSansTelugu-Regular.ttf` (and Bold) goes in `src/Angadi.Maui/Resources/Fonts/` and in `tests/Angadi.Tests/Fonts/`. Get it from Google Fonts (SIL Open Font License).
- **Release keystore.** Never in git (`*.keystore` and `*.jks` are ignored). See `docs/DEPLOYMENT_GUIDE.md`.

## Documents

| File | For | What |
|---|---|---|
| [`docs/Angadi_HQ.html`](docs/Angadi_HQ.html) | everyone | Product spec, blueprint, 14-day plan |
| [`docs/SRS.md`](docs/SRS.md) | everyone | Requirements FR-01…37, NFR-01…12 (draft, awaiting sign-off) |
| [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) | devs | As-built architecture, ADRs, dependencies |
| [`docs/DB_SCHEMA.md`](docs/DB_SCHEMA.md) | devs | SQLite tables, transactions, migrations, backup |
| [`docs/BILLING_RULES.md`](docs/BILLING_RULES.md) | devs | GST math, rounding, discounts, worked examples |
| [`docs/VOICE_PIPELINE.md`](docs/VOICE_PIPELINE.md) | devs | Telugu parser and matcher rules |
| [`docs/UI_CONVENTIONS.md`](docs/UI_CONVENTIONS.md) | devs | MAUI folder ownership, strings, theme |
| [`docs/DEPLOYMENT_GUIDE.md`](docs/DEPLOYMENT_GUIDE.md) | release owner | Play Console, signing, keystore safekeeping, subscriptions, closed testing, release checklist |
| [`docs/MAINTENANCE_RUNBOOK.md`](docs/MAINTENANCE_RUNBOOK.md) | maintainer | Day-to-day changes: aliases, voice words, strings, GST, prices, logs, restore, releases, schema |
| [`docs/PRIVACY_POLICY.md`](docs/PRIVACY_POLICY.md) / [`_TE`](docs/PRIVACY_POLICY_TE.md) | public | Privacy policy (English / తెలుగు) — host it and link it in Play Console |
| [`docs/USER_GUIDE_TE.md`](docs/USER_GUIDE_TE.md) | shopkeeper | తెలుగు యూజర్ గైడ్ |
| [`docs/QUICK_START_TE.md`](docs/QUICK_START_TE.md) | shopkeeper | ఒక పేజీ క్విక్ స్టార్ట్ కార్డ్ |

Planned and not written yet: `docs/UI_MAP.md`, `docs/TEST_PLAN.md`, test report, defect log.
