using Angadi.Application.Common;
using Angadi.Application.Platform;
using Angadi.Application.Printing;
using Angadi.Maui.Core;

namespace Angadi.Maui.Areas;

/// <summary>
/// v2 printing: Settings → Receipt printer. Pick a printer already paired in Android's Bluetooth settings (the
/// Android 12+ "Nearby devices" permission is asked here and only here), choose 58/80 mm, print a test page, and turn
/// on printing after every bill. Opened behind the owner PIN (SettingsPage), like every other owner setting.
/// </summary>
public sealed class PrinterSettingsPage : ContentPage
{
    private readonly ISender _sender;
    private readonly IDialogService _dialogs;
    private readonly ILocalizer _text;
    private readonly Label _status = Ui.Body("", 18);
    private readonly Button _paper58;
    private readonly Button _paper80;
    private readonly Button _auto;
    private readonly Button _forget;
    private PrinterSettings _settings = PrinterSettings.None;

    public PrinterSettingsPage(ISender sender, IDialogService dialogs, ILocalizer text)
    {
        _sender = sender;
        _dialogs = dialogs;
        _text = text;
        Title = text["printer.title"];

        var choose = Ui.Button(text["printer.choose"], Theme.Primary, big: true);
        choose.Clicked += SafeUi.On(ChooseAsync);
        _paper58 = Toggle(() => SaveAsync(_settings with { Paper = PaperWidth.Mm58 }));
        _paper80 = Toggle(() => SaveAsync(_settings with { Paper = PaperWidth.Mm80 }));
        _auto = Toggle(() => SaveAsync(_settings with { AutoPrint = !_settings.AutoPrint }));
        var test = Ui.Button(text["printer.test"], Theme.Success, big: true);
        test.Clicked += SafeUi.On(TestAsync);
        _forget = Ui.Button(text["printer.forget"], Theme.Danger);
        _forget.Clicked += SafeUi.On(() => SaveAsync(PrinterSettings.None with { Paper = _settings.Paper }));

        Content = Ui.Scroll(
            Ui.Title(text["printer.title"]),
            _status,
            choose,
            Ui.Caption(text["printer.paper"]),
            _paper58,
            _paper80,
            _auto,
            test,
            _forget,
            Ui.Caption(text["printer.help"]));
        Show();
    }

    private static Button Toggle(Func<Task> action)
    {
        var b = Ui.Secondary("");
        b.MinimumHeightRequest = 48;
        b.Clicked += SafeUi.On(action);
        return b;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await SafeUi.Run(async () =>
        {
            _settings = await _sender.Send(new GetPrinterSettingsQuery());
            Show();
        });
    }

    private void Show()
    {
        _status.Text = _settings.IsSet ? _text.Format("printer.current", _settings.Name ?? _settings.Address ?? "") : _text["printer.none"];
        _paper58.Text = Mark(_settings.Paper == PaperWidth.Mm58) + _text["printer.paper.58"];
        _paper80.Text = Mark(_settings.Paper == PaperWidth.Mm80) + _text["printer.paper.80"];
        _auto.Text = Mark(_settings.AutoPrint) + _text["printer.auto"];
        _auto.IsEnabled = _settings.IsSet;
        _forget.IsVisible = _settings.IsSet;
    }

    private static string Mark(bool on) => on ? "☑  " : "☐  ";

    private async Task ChooseAsync()
    {
        var paired = await _sender.Send(new ListPairedPrintersQuery());
        if (paired.IsFailure) { await ShowErrorAsync(paired.Error); return; }
        var printers = paired.Value;
        var labels = printers.Select(p => p.Name + "  ·  " + p.Address).ToArray();
        var choice = await _dialogs.ChooseAsync(_text["printer.choose"], _text["common.cancel"], labels);
        var index = choice is null ? -1 : Array.IndexOf(labels, choice);
        if (index < 0) return;
        await SaveAsync(_settings with { Address = printers[index].Address, Name = printers[index].Name });
    }

    private async Task SaveAsync(PrinterSettings settings)
    {
        var r = await _sender.Send(new SavePrinterSettingsCommand(settings));
        if (r.IsFailure) { await _dialogs.ToastAsync(_text.Error(r.Error)); return; }
        _settings = await _sender.Send(new GetPrinterSettingsQuery());
        Show();
        await _dialogs.ToastAsync(_text["printer.saved"]);
    }

    private async Task TestAsync()
    {
        var r = await _sender.Send(new PrintTestPageCommand());
        if (r.IsFailure) await ShowErrorAsync(r.Error);
        else await _dialogs.ToastAsync(_text["printer.sent"]);
    }

    private Task ShowErrorAsync(Error error) => _dialogs.AlertAsync(_text["printer.error.title"], _text.Error(error), _text["common.ok"]);
}

/// <summary>
/// The Print buttons on the receipt and return-note screens. The bill is already saved: a print problem is told in a
/// dialog when the shopkeeper tapped Print, and in a toast after an automatic print, never blocking the counter.
/// </summary>
internal static class PrintActions
{
    public static async Task PrintAsync(ISender sender, IDialogService dialogs, ILocalizer text, ICommand<Result> command, bool quiet = false)
    {
        var r = await sender.Send(command);
        if (r.IsSuccess) { await dialogs.ToastAsync(text["printer.sent"]); return; }
        if (quiet) await dialogs.ToastAsync(text.Error(r.Error));
        else await dialogs.AlertAsync(text["printer.error.title"], text.Error(r.Error), text["common.ok"]);
    }

    /// <summary>After a bill: prints in the background when "print after every bill" is on. Never awaited by the counter.</summary>
    public static async Task AutoPrintAsync(ISender sender, IDialogService dialogs, ILocalizer text, Guid billId)
    {
        var settings = await sender.Send(new GetPrinterSettingsQuery());
        if (!settings.IsSet || !settings.AutoPrint) return;
        await PrintAsync(sender, dialogs, text, new PrintReceiptCommand(billId), quiet: true);
    }
}
