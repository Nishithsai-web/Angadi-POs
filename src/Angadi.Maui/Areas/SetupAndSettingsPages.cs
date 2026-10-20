using Angadi.Application.Billing;
using Angadi.Application.Catalog;
using Angadi.Application.Common;
using Angadi.Application.Platform;
using Angadi.Domain.Shop;
using Angadi.Maui.Core;
using Microsoft.Extensions.Options;

namespace Angadi.Maui.Areas;

/// <summary>FR-01/FR-04: first run and later edits of the shop profile; optional sample items.</summary>
public sealed class ShopSetupPage : ContentPage
{
    private readonly ISender _sender;
    private readonly IDialogService _dialogs;
    private readonly ILocalizer _text;
    private readonly IAppRestart _restart;
    private readonly Entry _name, _address, _phone, _upi, _gstin, _prefix;
    private readonly Picker _language = new();
    private readonly CheckBox _kirana = new() { IsChecked = true, Color = Theme.Primary };
    private readonly CheckBox _pharmacy = new() { Color = Theme.Primary };
    private readonly View _seedBox;
    private readonly Label _qrStatus = Ui.Caption();
    private ShopProfile? _existing;
    private string? _qrPayload;
    private byte[]? _qrImage;

    public ShopSetupPage(ISender sender, IDialogService dialogs, ILocalizer text, IAppRestart restart, IBarcodeScanner scanner)
    {
        _sender = sender;
        _dialogs = dialogs;
        _text = text;
        _restart = restart;
        Title = text["settings.shop"];
        _name = Ui.Entry(text["settings.shop.name"]);
        _address = Ui.Entry(text["settings.shop.address"]);
        _phone = Ui.Entry(text["settings.shop.phone"], Keyboard.Telephone);
        _upi = Ui.Entry(text["settings.shop.upi"], Keyboard.Email);
        _gstin = Ui.Entry(text["settings.shop.gstin"]);
        _prefix = Ui.Entry("A");
        _language.Items.Add(text["lang.telugu"]);
        _language.Items.Add(text["lang.english"]);
        _language.SelectedIndex = text.Language == UiLanguage.English ? 1 : 0;   // follows the first-run choice

        _seedBox = new VerticalStackLayout
        {
            Spacing = 4,
            Children =
            {
                Ui.Body(text["setup.seed"]),
                Labeled(_kirana, text["setup.seed.kirana"]),
                Labeled(_pharmacy, text["setup.seed.pharmacy"]),
            },
        };
        // The shop's own UPI QR: scan it (reads the UPI ID and merchant details) or upload a photo of it.
        var scanQr = Ui.Secondary(text["setup.upi.scan"]);
        scanQr.Clicked += SafeUi.On(async () =>
        {
            var r = await scanner.ScanAsync();
            if (r.IsFailure) return;
            if (UpiQr.Parse(r.Value) is not { } qr) { await _dialogs.ToastAsync(_text.Error(new Error(ErrorCodes.UpiQrInvalid))); return; }
            _qrPayload = r.Value.Trim();
            _upi.Text = qr.PayeeAddress;
            _qrStatus.Text = _text.Format("setup.upi.found", qr.PayeeAddress);
        });
        var uploadQr = Ui.Secondary(text["setup.upi.upload"]);
        uploadQr.Clicked += SafeUi.On(UploadQrAsync);
        var qrRow = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Star) }, ColumnSpacing = 8 };
        qrRow.Add(scanQr, 0, 0);
        qrRow.Add(uploadQr, 1, 0);

        var save = Ui.Button(text["setup.start"], big: true);
        save.Clicked += SafeUi.On(async () => await SaveAsync());

        Content = Ui.Scroll(
            Ui.Title(text["setup.welcome"]),
            Ui.Caption(text["settings.shop.name"]), _name,
            Ui.Caption(text["settings.shop.address"]), _address,
            Ui.Caption(text["settings.shop.phone"]), _phone,
            Ui.Caption(text["settings.shop.upi"]), _upi,
            Ui.Caption(text["setup.upi.help"]), qrRow, _qrStatus,
            Ui.Caption(text["settings.shop.gstin"]), _gstin,
            Ui.Caption(text["setup.gstin.note"]),
            Ui.Caption(text["setup.prefix"]), _prefix,
            Ui.Caption(text["settings.language"]), _language,
            _seedBox,
            save);
    }

    private static View Labeled(CheckBox box, string label) => new HorizontalStackLayout { Spacing = 8, Children = { box, new Label { Text = label, VerticalTextAlignment = TextAlignment.Center } } };

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        // FIX(UI-1, 2026-10-20, FIX-3): Flaw: unwrapped async void OnAppearing on a ContentPage — a failed load
        // escaped SafeUi (no toast, half-loaded page). Fix: run the load through SafeUi.Run like the other 7
        // secondary pages. Why: "never-fail UI" must hold on every page. Logged in docs/AGENT_FIX_LOG.md.
        await SafeUi.Run(async () =>
        {
            _existing = await _sender.Send(new GetShopProfileQuery());
            _seedBox.IsVisible = _existing is null;
            if (_existing is null) return;
            _name.Text = _existing.Name;
            _address.Text = _existing.Address;
            _phone.Text = _existing.Phone;
            _upi.Text = _existing.UpiId;
            _gstin.Text = _existing.Gstin;
            _prefix.Text = _existing.BillPrefix;
            _language.SelectedIndex = _existing.Language == UiLanguage.English ? 1 : 0;
            _qrPayload = _existing.UpiQrPayload;
            _qrImage = _existing.UpiQrImage;
            if (UpiQr.Parse(_qrPayload) is { } qr) _qrStatus.Text = _text.Format("setup.upi.found", qr.PayeeAddress);
            else if (_qrImage is not null) _qrStatus.Text = _text["setup.upi.photo.saved"];
        });
    }

    private async Task UploadQrAsync()
    {
        var photo = await MediaPicker.Default.PickPhotoAsync();
        if (photo is null) return;
        await using var stream = await photo.OpenReadAsync();
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy);
        if (copy.Length > UpiQr.MaxImageBytes) { await _dialogs.ToastAsync(_text.Error(new Error(ErrorCodes.UpiQrImageTooLarge))); return; }
        _qrImage = copy.ToArray();
        _qrStatus.Text = _text["setup.upi.photo.saved"];
    }

    /// <summary>No profile yet → the shop can't bill; back is blocked on first run.</summary>
    protected override bool OnBackButtonPressed() => _existing is null || base.OnBackButtonPressed();

    private async Task SaveAsync()
    {
        var firstRun = _existing is null;
        var profile = _existing ?? new ShopProfile();
        var oldLanguage = profile.Language;
        profile.Name = _name.Text ?? "";
        profile.Address = _address.Text ?? "";
        profile.Phone = _phone.Text ?? "";
        profile.UpiId = (_upi.Text ?? "").Trim();
        profile.Gstin = string.IsNullOrWhiteSpace(_gstin.Text) ? null : _gstin.Text.Trim().ToUpperInvariant();
        profile.BillPrefix = string.IsNullOrWhiteSpace(_prefix.Text) ? "A" : _prefix.Text.Trim();
        profile.Language = _language.SelectedIndex == 1 ? UiLanguage.English : UiLanguage.Telugu;
        // A scanned QR only counts while the UPI ID still matches it (the owner may have typed a different one).
        profile.UpiQrPayload = UpiQr.Parse(_qrPayload) is { } own && string.Equals(own.PayeeAddress, profile.UpiId, StringComparison.OrdinalIgnoreCase) ? _qrPayload : null;
        profile.UpiQrImage = _qrImage;

        var saved = await _sender.Send(new SaveShopProfileCommand(profile));
        if (saved.IsFailure) { await _dialogs.ToastAsync(_text.Error(saved.Error) + (saved.Error.Detail is { } d ? " (" + d + ")" : "")); return; }

        if (firstRun && (_kirana.IsChecked || _pharmacy.IsChecked))
            await _sender.Send(new SeedItemsCommand(_kirana.IsChecked, _pharmacy.IsChecked));

        _existing = profile;
        if (firstRun || oldLanguage != profile.Language) _restart.Restart();
        else await Shell.Current.GoToAsync("..");
    }
}

/// <summary>FR-34/35: quota meter and plans. The real Play purchase lands with the billing adapter (v1.1).</summary>
public sealed class SubscriptionPage : ContentPage
{
    private readonly ISender _sender;
    private readonly ISubscriptionService _subscription;
    private readonly IDialogService _dialogs;
    private readonly ILocalizer _text;
    private readonly Label _status = Ui.Body();

    public SubscriptionPage(ISender sender, ISubscriptionService subscription, IDialogService dialogs, ILocalizer text, IOptions<BillingOptions> billing)
    {
        _sender = sender;
        _subscription = subscription;
        _dialogs = dialogs;
        _text = text;
        Title = text["settings.subscription"];
        var o = billing.Value;
        var monthly = Ui.Button(text.Format("sub.monthly.price", IndianFormat.Group(o.MonthlyPriceRupees)), big: true);
        monthly.Clicked += SafeUi.On(async () => await BuyAsync(SubscriptionPlan.Monthly));
        var yearly = Ui.Button(text.Format("sub.yearly.price", IndianFormat.Group(o.YearlyPriceRupees)), Theme.Accent, big: true);
        yearly.TextColor = Theme.TextPrimary;
        yearly.Clicked += SafeUi.On(async () => await BuyAsync(SubscriptionPlan.Yearly));
        var restore = Ui.Secondary(text["sub.restore"]);
        restore.Clicked += SafeUi.On(async () => await ShowAsync(await _subscription.RestoreAsync()));
        Content = Ui.Scroll(Ui.Title(text["settings.subscription"]), _status, Ui.Caption(text["sub.included"]), monthly, yearly, restore);
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        // FIX(UI-1, 2026-10-20, FIX-3): Flaw: unwrapped async void OnAppearing on a ContentPage — a failed load
        // escaped SafeUi (no toast, half-loaded page). Fix: run the load through SafeUi.Run like the other 7
        // secondary pages. Why: "never-fail UI" must hold on every page. Logged in docs/AGENT_FIX_LOG.md.
        await SafeUi.Run(async () =>
        {
            var q = await _sender.Send(new GetBillQuotaQuery());
            _status.Text = q.IsPro ? _text["sub.pro"] : _text.Format("sub.free", q.UsedThisMonth, q.FreeLimit);
        });
    }

    private async Task BuyAsync(SubscriptionPlan plan) => await ShowAsync(await _subscription.PurchaseAsync(plan));

    private async Task ShowAsync(Result<SubscriptionStatus> r)
    {
        if (r.IsFailure) { await _dialogs.ToastAsync(_text.Error(r.Error)); return; }
        OnAppearing();
    }
}

/// <summary>Settings tab: shop, language, subscription, backup/restore (FR-37), export logs (FR-36).</summary>
public sealed class SettingsPage : ContentPage
{
    private readonly ISender _sender;
    private readonly IReceiptShare _share;
    private readonly IDialogService _dialogs;
    private readonly ILocalizer _text;
    private readonly IAppRestart _restart;

    public SettingsPage(ISender sender, IReceiptShare share, IDialogService dialogs, ILocalizer text, IAppRestart restart)
    {
        _sender = sender;
        _share = share;
        _dialogs = dialogs;
        _text = text;
        _restart = restart;
        Title = text["nav.settings"];

        Button Item(string key, Func<Task> action, bool danger = false)
        {
            var b = danger ? Ui.Button(text[key], Theme.Danger) : Ui.Secondary(text[key]);
            b.Clicked += SafeUi.On(async () => await action());
            return b;
        }

        Content = Ui.Scroll(
            Ui.Title(text["nav.settings"]),
            Item("settings.shop", () => Shell.Current.GoToAsync(Routes.ShopSetup)),
            Item("settings.subscription", () => Shell.Current.GoToAsync(Routes.Subscription)),
            Item("settings.notes", () => Shell.Current.GoToAsync(Routes.Notes)),
            Item("settings.backup", BackupAsync),
            Item("settings.restore", RestoreAsync, danger: true),
            Item("settings.logs", ExportLogsAsync),
            Ui.Caption(text["settings.backup.tip"]),
            Ui.Caption("v" + AppInfo.Current.VersionString));
    }

    private async Task BackupAsync()
    {
        var r = await _sender.Send(new ExportBackupCommand());
        if (r.IsFailure) { await _dialogs.ToastAsync(_text.Error(r.Error)); return; }
        await _share.ShareFileAsync(r.Value, _text["settings.backup"]);
    }

    private async Task RestoreAsync()
    {
        var picked = await FilePicker.Default.PickAsync();
        if (picked is null) return;
        if (!await _dialogs.ConfirmAsync(_text["settings.restore"], _text["settings.restore.warn"], _text["common.yes"], _text["common.no"])) return;

        // H3: stage the picked (content-URI) file locally first, then restore from the staged copy.
        var staged = Path.Combine(FileSystem.CacheDirectory, "restore-staged.db");
        await using (var source = await picked.OpenReadAsync())
        await using (var target = File.Create(staged))
            await source.CopyToAsync(target);

        var r = await _sender.Send(new RestoreBackupCommand(staged));
        if (r.IsFailure) { await _dialogs.ToastAsync(_text.Error(r.Error)); return; }
        _restart.Restart();
    }

    private async Task ExportLogsAsync()
    {
        // H11: through the dispatcher — logged, Result-shaped, and a full disk shows a Telugu message instead of crashing.
        var zip = await _sender.Send(new ExportLogsCommand());
        if (zip.IsFailure) { await _dialogs.ToastAsync(_text.Error(zip.Error)); return; }
        // FIX(LOG-3d, 2026-10-20, FIX-3): Flaw: the share Result was discarded — when the share itself failed,
        // the shopkeeper got no feedback and believed the logs had been sent. Fix: a failed share shows its
        // error toast. Why: "send us your logs" is the support path; a silent failure there strands the shop.
        // Logged in docs/AGENT_FIX_LOG.md.
        var shared = await _share.ShareFileAsync(zip.Value, _text["settings.logs"]);
        if (shared.IsFailure) await _dialogs.ToastAsync(_text.Error(shared.Error));
    }
}
