# Releasing Angadi to Google Play

The step-by-step for turning a green `main` into an app in Play. One-time setup first, then the same three steps every release. Rollback rules live in `deployment-and-rollback.md`.

## A. One-time setup (owner, about 1 hour)

### 1. Create the upload key (keep it forever)
On your PC, in PowerShell (Java is installed with Visual Studio's Android tools):

```powershell
keytool -genkeypair -v -keystore angadi-upload.keystore -alias angadi -keyalg RSA -keysize 2048 -validity 10000
```

Choose a strong password and answer the name questions. **Back up `angadi-upload.keystore` and the password in two safe places.** Never commit the file to the repo. With Play App Signing (step 3), Google holds the real app-signing key. If this upload key is lost, Google can reset it, but that takes days.

### 2. Give GitHub the key (encrypted secrets)
```powershell
[Convert]::ToBase64String([IO.File]::ReadAllBytes("angadi-upload.keystore")) | Set-Clipboard
```
GitHub → the repo → **Settings → Secrets and variables → Actions → New repository secret**, four times:

| Secret | Value |
|---|---|
| `ANDROID_KEYSTORE_BASE64` | paste (the clipboard from above) |
| `ANDROID_KEYSTORE_PASSWORD` | the keystore password |
| `ANDROID_KEY_ALIAS` | `angadi` |
| `ANDROID_KEY_PASSWORD` | the key password (same as the keystore's if you pressed Enter) |

### 3. Play Console (one-time, US$25 developer fee: owner's decision)
1. Create the app: name **Angadi**, default language Telugu or English, App, Free (subscriptions inside).
2. **App integrity → Play App Signing:** accept (Google manages the signing key).
3. **Policy → App content:**
   - **Privacy policy:** the public URL of `docs/PRIVACY_POLICY.md` (fill in its two `[…]` fields first).
   - **Data safety:** no data collected or shared by Angadi itself; app data is stored only on the device. Third-party SDK: the barcode scanner's **Google ML Kit** sends Google diagnostics/usage data — declare exactly the categories listed on https://developers.google.com/ml-kit/android-data-disclosure (device or other IDs; app info and performance). Voice input may be processed by Google's speech service (Android). Angadi's own logs leave the phone only when the user taps Export logs.
   - **Target API:** the app targets API 36 (Android 16), which Play requires for new apps and updates from 31 Aug 2026. CI's *Google Play policy gate* fails any build below that, any debuggable release, and any library-added sensitive permission (advertising ID, SMS, location, all-packages).
   - **Android backup:** only the database and settings go to the owner's own Google account (Android Auto Backup); this is not "collection" by Angadi.
   - **Ads:** no ads. **Target audience:** 18+ (business users). **Content rating:** fill in the questionnaire (utility app).
4. **Monetize → Subscriptions:** create `angadi_pro_monthly` (₹200 / month) and `angadi_pro_yearly` (₹1,999 / year). The IDs must match exactly.
5. **Testing → Closed testing:** create a track and add at least 12 testers' emails. A new personal developer account needs 14 days of closed testing before production.

### 4. Optional: automatic upload to internal testing
Play Console → **Setup → API access** → create a service account with "Release to testing tracks". Download its JSON key and save it as the repository secret `PLAY_SERVICE_ACCOUNT_JSON`. The release workflow then uploads each bundle to the **internal** track by itself. It never touches production.

## B. Every release (10 minutes + Play review)

1. **Green main.** CI must be green on `main`: tests, the Android build and the emulator end-to-end test.
2. **Tag it.** In the repo:
   ```powershell
   git checkout main; git pull
   git tag v1.0.0; git push origin v1.0.0
   ```
   GitHub → **Actions → release** runs the tests again, builds the signed `.aab`, and attaches it to the run as `angadi-release-1.0.0`. Without the secrets it stops at once and names what's missing.
3. **Upload.** Play Console → the testing track → **Create release** → upload the `.aab` (or it is already there, if step A4 is set up) → release notes from `CHANGELOG.md` → **Start rollout**.
4. **Promote.** After the closed test passes (and 14 days for a new account): **Promote release → Production**, staged rollout 20% → 50% → 100% while watching **Android vitals** and the **pre-launch report**. Production needs the owner's explicit go-ahead.

Version numbers: the tag sets what users see (`1.0.0`). The build number Play needs is `100 + the workflow run number`, so it always goes up.

## C. Before the very first production release
- [ ] `PRIVACY_POLICY.md` fields filled in and hosted at a public URL
- [ ] Support WhatsApp number in `appsettings.json` → `Support:WhatsAppNumber`
- [ ] Subscriptions created in Play with the exact IDs above
- [ ] Closed test: billing, voice, scanning, udhaar, reports and backup/restore on at least one 7" and one 10" tablet
- [ ] Upgrade test: install the closed-test build over an older one. Bills, stock and dues must survive (the app keeps `angadi.db.pre-update` before migrating)
- [ ] Owner's explicit OK for production
