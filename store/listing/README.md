# Play Store listing: everything to paste into Play Console

| What | File | Play Console place |
|---|---|---|
| App name (≤ 30) | `en/title.txt`, `te/title.txt` | Grow → Store presence → Main store listing → App name |
| Short description (≤ 80) | `en/short_description.txt`, `te/short_description.txt` | same page → Short description |
| Full description (≤ 4000) | `en/full_description.txt`, `te/full_description.txt` | same page → Full description |
| Phone screenshots | `ci-e2e` branch → `<branch>/debug/store-screenshots/en/` and `/te/` | same page → Phone screenshots (2–8 per language) |
| Icon, feature graphic | `../play-icon-512.png`, `../feature-graphic-1024x500.png` | same page → Graphics |
| Data safety + other declarations | `DATA_SAFETY.md` | Policy → App content |
| Content rating | `CONTENT_RATING.md` | Policy → App content → Content rating |
| Subscriptions | `IN_APP_PRODUCTS.md` | Monetize with Play → Products → Subscriptions |

**Languages:** set the default language to **English (India) – en-IN**, then **Manage translations → Add your own
translation text → Telugu – te** and paste the `te/` texts. Store settings: category **Business**, contact email = the
support email in the privacy policy.

**Screenshots** are taken automatically by `tools/store_screenshots.py` on every CI run (debug E2E job), from the real
app, in both languages, 1080×1920. Download them from the `ci-e2e` branch of the latest green `main` run. Upload the `en/`
set to the English listing and the `te/` set to the Telugu translation.

**Keep it true.** The texts list only features that exist today. When a feature changes, change these files in the
same pull request. `tools/release_check.py` (run before every release build) checks the length limits.
