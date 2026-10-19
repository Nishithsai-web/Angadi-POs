# UI_SPEC.md — Modern, flexible, responsive UI for Angadi POS v1

## Design principles
- **Telugu-first:** every label in Telugu, with small English sublabels where space allows. A shopkeeper who can't read English must use every screen alone.
- **Built for thumbs and bright shops:** minimum 48dp touch targets, high-contrast text, no pale-grey-on-white. Readable in harsh tube-light.
- **Offline-first:** every screen works with zero connectivity. Sync is v2; the UI never waits on a network.
- **Forgiving:** every destructive action (void, delete, stock adjust) confirms in plain Telugu and can be undone where money is involved.

## Responsive: 7" and 10" tablets, portrait and landscape
Use MAUI `VisualStateManager` adaptive triggers — one codebase, layouts that reflow:
- **Billing:** portrait / 7" → stacked (item grid on top, draft + tender below, mic always visible). Landscape / 10" → side-by-side (catalog left, draft right).
- **Inventory / Udhaar / Reports:** list-detail pattern — list full-width on 7" portrait; master-detail columns on 10" and landscape.
- Text scales with the OS font-size setting (older shopkeepers crank it up — the layout must not break).

## Screens
1. **Billing** — the home screen. Big mic button (voice everywhere, per VoiceKhata's lesson), search bar, category chips, item grid with photos/emoji, draft panel with running total, tender selector (Cash / UPI / Udhaar), change-due display.
2. **Inventory** — searchable stock list, low-stock badges, quick adjust (+/- with reason), barcode scan-to-find.
3. **Udhaar** — dues by customer, record payment, reminder via WhatsApp share-sheet, overpayment blocked with the actual balance shown.
4. **Reports** — day-end summary, GST slab report, top items (by value *and* quantity), udhaar outstanding.
5. **Settings** — shop profile (name, address, GSTIN), business type (Kirana / Pharmacy / Both), language, export logs (FR-36), subscription status, backup/restore.

## Business-type modes (kirana + pharmacy)
A **business type** toggle in shop profile: Kirana / Pharmacy / Both.
- **Pharmacy mode:** seeds the 40-item pharmacy catalog; unit labels switch to strip/tablet/bottle/syrup; bill header reads as a medical retail invoice; GST slabs identical.
- **Kirana mode:** the 80-item kirana catalog; kg/g/packet units.
- **Both:** merged catalog with category chips.
- **Explicitly OUT (owner decision, 2026-10-19):** expiry-date tracking. Do not build it. Pharmacy billing + inventory work fully without it.

## Flexibility
- Light/dark theme toggle (dark for evening counters).
- Bilingual labels everywhere (Telugu primary, English secondary).
- All user strings from `strings.te.json` / `errors.json` — zero hardcoded Telugu in code (architecture test enforces this).

## Why this exists
The app is used 10 hours a day by people who didn't grow up with software. Beauty here isn't decoration — big type, clear Telugu, and a mic button you can't miss are what make a 55-year-old shopkeeper trust it on day one.
