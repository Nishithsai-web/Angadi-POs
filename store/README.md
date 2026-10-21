# Play Store listing assets

| File | Play Console field | Size |
|---|---|---|
| `play-icon-512.png` | App icon | 512 × 512 PNG |
| `feature-graphic-1024x500.png` | Feature graphic | 1024 × 500 PNG |
| `launcher-preview.png` | Not uploaded: how the icon looks under round, squircle and teardrop launcher masks | — |

**The mark:** a kirana shop's striped awning over **అ**, the first letter of **అంగడి** ("shop"). It says "shop" to
anyone, and "Telugu" to the people it is built for. Deep green and turmeric amber, the app's own colours.
The Telugu letter is a path traced from Noto Sans Telugu Bold (SIL Open Font Licence), so no font is needed to draw it.

**Regenerate** (repo root): `python3 tools/brand/make_logo.py` writes the app icon layers, splash and `store/play-icon.svg`.
Then render the PNGs with Chromium: `node tools/brand/render.js store/play-icon.svg store/play-icon-512.png 512 512`
and `node tools/brand/render.js tools/brand/feature.html store/feature-graphic-1024x500.png 1024 500`.

**Listing text, screenshots, Data safety, content rating, subscriptions:** see `listing/README.md`.
