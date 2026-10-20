# Build and run Angadi on your Windows PC

The same steps CI runs. Takes about 20 minutes the first time, mostly installs.

## 1. Install the right Visual Studio (this is the cause of most errors)

Angadi targets **.NET 10**. Microsoft only supports targeting .NET 10 in **Visual Studio 2026** (version 18) or later. **Visual Studio 2022 cannot build it.** VS 2022 falls back to .NET 9, which produces errors like `obj\Debug\net9.0\...` and "metadata file could not be found".

1. Download **Visual Studio 2026 Community** (free): https://visualstudio.microsoft.com/downloads/
2. In the installer, tick the **.NET Multi-platform App UI development** workload. That brings the .NET 10 SDK, the Android SDK and an Android emulator.
3. Open a terminal and check:
   ```
   dotnet --version        → 10.0.x
   dotnet workload list    → maui-android listed
   ```
   If `maui-android` is missing: `dotnet workload install maui-android`

`global.json` pins SDK 10.0.x. With an older SDK, `dotnet` now says so plainly instead of failing with confusing compile errors.

## 2. Get a clean copy of the code

`main` always holds the latest green build. In the repo folder, in PowerShell:

```powershell
git fetch origin
git checkout main
git reset --hard origin/main      # discards any local edits (e.g. a net9.0 retarget); copy them elsewhere first if you need them
Get-ChildItem -Recurse -Directory -Include bin,obj | Remove-Item -Recurse -Force   # old net9.0 build output
```

The fonts (Noto Sans Telugu, Noto Sans; OFL licence) are committed in the repo, so there is nothing to download.

## 3. Run the tests (2 minutes, no emulator needed)

```powershell
dotnet test tests/Angadi.Tests/Angadi.Tests.csproj
```

All tests should pass. This proves billing, GST, voice parsing, the database, quotas and backups on your machine.

## 4. Run the app

1. Open `Angadi.sln` in Visual Studio 2026.
2. Right-click **Angadi.Maui** → *Set as Startup Project*.
3. In the run dropdown choose **Android Emulators** → *Pixel 7 – API 35* (Tools → Android → Android Device Manager creates one if the list is empty), or a real phone with USB debugging on.
4. Press **F5**. First launch asks **భాష / Language**, then the shop setup.

Command-line alternative:
```powershell
dotnet build src/Angadi.Maui/Angadi.Maui.csproj -f net10.0-android -t:Run
```

## 5. The errors you saw, and what each one means

| Message | Cause | Fix |
|---|---|---|
| `obj\Debug\net9.0\...Angadi.Infrastructure.dll could not be found` | Built with VS 2022 / .NET 9 | Steps 1 and 2 |
| `namespace 'Receipts' does not exist in 'Angadi.Infrastructure'` | Follows from the line above: Infrastructure didn't compile, so nothing referencing it can | Same |
| `CA1305 long.Parse`, `CA1307 string.Replace`, `Parameter 'customers' is unread`, `splash.svg does not exist` | Old code from before these were fixed (an old `main`) | Step 2: `main` now has the fixes |
| `Detected package version outside of dependency constraint: Xamarin.AndroidX.Lifecycle...` (NU1608) | A warning from Android support libraries pulled in by MAUI; the newer version is compatible | Nothing to do; it is not an error |

Still stuck? Copy the **first** error from the Error List (later errors usually follow from it) and send it over.

## Where things are

| Folder | What it is |
|---|---|
| `src/Angadi.Domain` | Money, GST, bills, stock: pure C#, no I/O |
| `src/Angadi.Application` | Use cases (billing, voice, reports, notes), Telugu and English text in `Resources/*.json` |
| `src/Angadi.Infrastructure` | SQLite, receipts (PDF), backups, logs |
| `src/Angadi.Maui` | The Android app: screens, mic, camera, Play Billing |
| `tests/Angadi.Tests` | All tests |
