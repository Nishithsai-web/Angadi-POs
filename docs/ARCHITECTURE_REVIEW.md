# ARCHITECTURE_REVIEW.md — review board, 2026-11-06

A review across security, data integrity, concurrency, performance, accessibility, observability, i18n, release and scale. Every "verified" line was checked in the repo today (read, not assumed). Nothing here was run on a real phone.

## Verified solid
| Area | Evidence |
|---|---|
| i18n | No hard-coded UI strings in `src/Angadi.Maui` (searched for `Text/Title/Placeholder = "..."`); strict localizer test in CI |
| Crash safety | No stray `async void` (only lifecycle overrides); handlers wrapped by `SafeUi`; global exception sink |
| Privacy in logs | No phone, PIN or UPI text in log statements; feedback sharing attaches logs only when asked |
| Backup scope | `backup_rules.xml` / `data_extraction_rules.xml` include only the databases and preferences, so `chain.json` (chain code) and `printer.json` stay off Google backup |
| Backup integrity | HMAC over every table that holds rows; new tables only when non-empty so old backups still verify |
| Logging | Serilog daily files, file-count and size limits |
| Concurrency | Single writer + read-only reader (PERF-1); timing tests now margin-safe |
| Scale | `LargeShopTests` (50,000 items, 20,000 customers) |
| Release | Release trims (`PublishTrimmed`), API 28+, coverage gate in CI, 4-job CI on every push |
| Access control | Permission checked in the request pipeline, not only on screens; owner-only actions need the owner's PIN |

## Found and fixed this session
- Flaky timing test failed `main` (PERF-1): margins widened.
- No way to export data: sales / dues / stock CSV, formula-injection-safe, Telugu-correct.
- No staff money: attendance, advances, monthly pay slip, owner-only pay.
- QR photo upload had no preview or remove.

## Open risks (ranked)
1. **Not verified on real hardware:** Bluetooth printer, WhatsApp opening the right chat, Telugu voice in a noisy shop. These can only be proven on a phone.
2. **No server:** a chain owner's view is as fresh as the last file sent; nothing scales past phone-local. Plan: `docs/ENTERPRISE_PLAN.md`, phases 2-5.
3. **Accessibility is thin:** only 5 `SemanticProperties` uses. Text buttons announce themselves, but icon-only or image controls should be labelled. Low priority for the closed test; do before wide release.
4. **No statutory payroll** (PF/ESI/TDS): out of scope without a compliance partner.
5. **Support channel missing:** needs the owner's support WhatsApp number and a hosted privacy policy before Play.
6. **Play rule:** closed test needs 12 testers for 14 days; cannot be shortened.
