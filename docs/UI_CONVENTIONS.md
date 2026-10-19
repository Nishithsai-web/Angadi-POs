# UI conventions (Angadi.Maui) — every UI agent follows these

## Folder ownership
| Folder | Owner |
|---|---|
| `Core/` (ViewModelBase, PageBase, Contracts, Translate) | integrator. Read-only for agents. Ask for changes in your report. |
| `Core/Services.cs`, `App.xaml(.cs)`, `AppShell.xaml(.cs)`, `MauiProgram.cs`, `Resources/Styles/*`, `Resources/AppIcon`, `Resources/Splash`, `appsettings.json` | shell agent |
| `Platform/`, `Platforms/Android/` | platform agent |
| `Areas/Pos/` | POS agent |
| `Areas/Items/` | items agent |
| `Areas/Customers/` (customers, udhaar, bill history, bill detail) | customers agent |
| `Areas/Reports/` (reports, insights) and `Areas/Settings/` (settings, shop setup, subscription) | reports/settings agent |

## Each area exposes one DI hook
`Areas/<Area>/<Area>Registration.cs`:
```csharp
public static class PosRegistration
{
    public static IServiceCollection AddPosArea(this IServiceCollection s)
    {
        s.AddTransient<PosViewModel>(); s.AddTransient<PosPage>();
        Routing.RegisterRoute(Routes.Receipt, typeof(ReceiptPage));
        s.AddTransient<ReceiptViewModel>(); s.AddTransient<ReceiptPage>();
        return s;
    }
}
```
Hook names, all called from MauiProgram:
- `AddPosArea`
- `AddItemsArea`
- `AddCustomersArea`
- `AddReportsArea`
- `AddSettingsArea`
- `AddAndroidPlatform`

Tab pages are resolved from DI by AppShell. The tab page classes must be:
- `Areas.Pos.PosPage`
- `Areas.Items.ItemsPage`
- `Areas.Customers.BillsPage`
- `Areas.Customers.CustomersPage`
- `Areas.Reports.ReportsPage`
- `Areas.Settings.SettingsPage`

## Pages and ViewModels
- **Pages:** XAML + tiny code-behind. Root element is `core:PageBase`, `x:DataType` is set on the page **and on every DataTemplate** (compiled bindings), and the constructor is `public XPage(XViewModel vm) : base(vm) { InitializeComponent(); }`.
- **ViewModels:** inherit `ViewModelBase`, with constructor `(ISender, ILocalizer, INavigationService, IDialogService, …your services)`.
  - Use `[ObservableProperty] private T _name;` field form and `[RelayCommand]` (CommunityToolkit.Mvvm 8.4).
  - Load data in `OnAppearingAsync()`, never in the constructor.
- **Data access:** only `Sender.Send(new SomeQuery(...))` and `Sender.Send(new SomeCommand(...))`. Never repositories. Contracts are in `Angadi.Application.{Billing,Catalog,Reports,Insights,Voice}`.
- **Errors:** a failed `Result` goes through `await ShowIfFailedAsync(result)`. Wrap long work in `RunBusyAsync(...)`.
- **Query parameters:** implement `IQueryAttributable` on the ViewModel (`ApplyQueryAttributes`).
- **Results from a page** (e.g. the scanner): `await Nav.GoForResultAsync<string>(Routes.Scanner)`. The scanner page calls `Nav.ReturnAsync(code)`.

## Text
- **XAML:** `{core:Translate some.key}`. **C#:** `Text["some.key"]`, `Text.Format("key", args)`, `Text.Error(error)`, `Text.UnitLabel(unit)`.
- **No user-facing literal strings in XAML or C#.** Numbers and ₹ go through `IndianFormat.Rupees(paise)` and `IndianFormat.Quantity(qty)`.
- **New keys** go in YOUR area file only: `src/Angadi.Application/Resources/strings.<area>.te.json` and `strings.<area>.en.json`.
  - Use the same keys in both files, prefixed with your area (`pos.`, `items.`, `customers.`, `bills.`, `reports.`, `insights.`, `settings.`, `setup.`, `sub.`, `platform.`).
  - Both files are flat JSON objects. A key defined twice anywhere throws at startup (and in a test), so check `strings.te.json` before adding.
- Telugu is primary: show the Telugu label large and English small under it only where the spec says "English secondary".

## Theme (Resources/Styles, by the shell agent) — use these keys only, never literal colours or fonts
- **Colours** (`StaticResource`):
  - `Primary`, `OnPrimary`, `PrimarySoft`, `Surface`, `Background`, `TextPrimary`, `TextSecondary`, `Border`
  - Status: `Danger`, `Success`, `Warning`
  - Tenders: `CashColor`, `UpiColor`, `UdhaarColor`
- **Styles** (`Style="{StaticResource X}"`):
  - Labels: `TitleLabel`, `SubtitleLabel`, `BodyLabel`, `CaptionLabel`, `AmountLabel` (big ₹ total), `SecondaryEnglishLabel` (small English under Telugu)
  - Buttons: `PrimaryButton`, `SecondaryButton`, `DangerButton`, `TenderButton`, `MicButton` (the giant round mic), `IconButton`
  - Containers and inputs: `Card` (Border), `FormEntry`, `FormPicker`
- **Fonts:** `Telugu` (NotoSansTelugu-Regular) and `TeluguBold`. The default font is already Telugu.
- **Touch targets:** ≥ 48 dp, billing actions ≥ 64 dp (NFR-09). Telugu line height is +15% (set in the styles).
- **Layout:** tablet-first landscape, but every page must work on a 6" phone in portrait. Use `OnIdiom` or a `Grid` that collapses (the POS uses an AdaptiveTrigger-like width check in code-behind `SizeChanged`).

## States
Every list page handles four states: loading (`IsBusy`), empty (`common.empty` or an area-specific message), error (`ErrorMessage`), and content. Never a dead screen.
