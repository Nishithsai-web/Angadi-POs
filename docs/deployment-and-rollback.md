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
<!-- FIX(RELEASE-2, 2026-10-20, FIX-5): Flaw: this section used to say "re-publish the previous AAB. Play
     handles downgrade delivery." That is impossible — Play never delivers a lower versionCode to a device
     already on a higher one, so following the old text during an incident would have done nothing.
     Fix: the forward-fix procedure below. Why: a rollback plan that cannot execute is worse than none.
     Logged in docs/AGENT_FIX_LOG.md. -->
- **App rollback (there is no downgrade — roll FORWARD):** Google Play never sends an older version to a phone that already has a newer one, and `release.yml` makes version codes strictly increase, so the previous AAB can never be re-delivered to affected devices. If a release corrupts data or crashes on launch:
  1. **Halt the rollout** in Play Console immediately (Release → Halt rollout). Devices that have not updated yet stay on the good version.
  2. **Hotfix on top of the bad version:** fix the defect in a new commit on `main` (revert the bad change if that is the fastest safe fix). Do not check out the old release — the fix ships as a new version.
  3. **Publish a NEW AAB with a HIGHER versionCode** than the bad release, containing the fixed code, through the normal `release.yml` flow (bump `version.json` first).
  4. **Test the upgrade FROM the bad version explicitly** before rollout: install the bad build, create bills/stock/dues, then upgrade to the hotfix build and verify the data survives and the defect is gone. Upgrading from the last *good* version is not the same test — a bad migration may only bite devices that already ran the bad build.
  5. Resume the staged rollout (20% → 50% → 100%) watching Android vitals.
  Keep every release's AAB artifact anyway: it is the record of exactly what shipped, and the hotfix's test baseline.
- **Data rollback:** the app auto-backs-up the SQLite DB to `<db>.pre-update` before applying any schema migration. If a migration fails, the app restores the pre-update copy and reports `DB_MIGRATION_FAILED` instead of opening on a half-migrated database.
- **Never:** push a release that changes the bill/stock schema without the pre-update backup; delete the previous AAB until the new release has 7 clean days.

## Why this exists
A POS holds a shopkeeper's money records. A bad release that loses bills or bricks the app destroys trust permanently — the rollback path must exist *before* the first release, not after the first disaster.
