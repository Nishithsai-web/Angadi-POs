# deployment-and-rollback.md — Release process for Angadi POS v1

## Release track
Google Play **closed test track** first (Play policy: new accounts need a 14-day closed test with 12+ testers before production). Then production rollout.

## Pre-release checklist
- [ ] `dotnet build` green, all tests green, coverage gates pass (CI observed, not assumed)
- [ ] `docs/CHANGELOG.md` updated with the release's changes and whys
- [ ] Version bumped (`Major.Minor.Patch+build`); version shown in Settings → About
- [ ] Signed Android APK/AAB built from the release branch (Release config)
- [ ] Fresh-install test on a 7" and a 10" tablet (Telugu locale, offline)
- [ ] Upgrade test: install over the previous build — bills, stock, dues, and settings survive
- [ ] Backup/restore round-trip verified on-device (H3 invariants)
- [ ] Free-tier quota behaves across the upgrade (bill count preserved)
- [ ] Telugu voice battery re-run on-device (≥29/30 auto-add)

## Rollout
1. Upload AAB to the closed-test track; add the 12+ tester emails.
2. Testers run the UAT script (billing, voice, udhaar, reports, backup) for 14 days.
3. Fix, re-release to the track, repeat until UAT passes.
4. Promote to production.

## Rollback plan
- **App rollback:** every release keeps the previous AAB. If a release corrupts data or crashes on launch, halt the rollout in Play Console and re-publish the previous AAB. Play handles downgrade delivery.
- **Data rollback:** the app auto-backs-up the SQLite DB to `<db>.pre-update` before applying any schema migration. If a migration fails, the app restores the pre-update copy and reports `DB_MIGRATION_FAILED` instead of opening on a half-migrated database.
- **Never:** push a release that changes the bill/stock schema without the pre-update backup; delete the previous AAB until the new release has 7 clean days.

## Why this exists
A POS holds a shopkeeper's money records. A bad release that loses bills or bricks the app destroys trust permanently — the rollback path must exist *before* the first release, not after the first disaster.
