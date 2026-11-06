using Angadi.Application.Common;
using Angadi.Application.Platform;
using Angadi.Application.Reports;
using Angadi.Maui.Core;

namespace Angadi.Maui.Areas;

/// <summary>Settings → Export to Excel: sales, udhaar dues and stock as a CSV file shared through the phone's share sheet.</summary>
public sealed class ExportPage : ContentPage
{
    private readonly ISender _sender;
    private readonly IDialogService _dialogs;
    private readonly ILocalizer _text;
    private readonly IReceiptShare _share;
    private readonly IClock _clock;

    public ExportPage(ISender sender, IDialogService dialogs, ILocalizer text, IReceiptShare share, IClock clock)
    {
        _sender = sender;
        _dialogs = dialogs;
        _text = text;
        _share = share;
        _clock = clock;
        Title = text["settings.export"];
        var thisMonth = Ui.Button(text["export.sales.month"], big: true);
        thisMonth.Clicked += SafeUi.On(() => ExportAsync(CsvKind.Sales, MonthStart(0), _clock.Today));
        var lastMonth = Ui.Secondary(text["export.sales.last"]);
        lastMonth.Clicked += SafeUi.On(() => ExportAsync(CsvKind.Sales, MonthStart(-1), MonthStart(0).AddDays(-1)));
        var dues = Ui.Secondary(text["export.dues"]);
        dues.Clicked += SafeUi.On(() => ExportAsync(CsvKind.Dues, _clock.Today, _clock.Today));
        var stock = Ui.Secondary(text["export.stock"]);
        stock.Clicked += SafeUi.On(() => ExportAsync(CsvKind.Stock, _clock.Today, _clock.Today));
        Content = Ui.Scroll(Ui.Title(text["export.title"]), Ui.Caption(text["export.hint"]), thisMonth, lastMonth, dues, stock);
    }

    private DateOnly MonthStart(int monthsBack)
    {
        var d = _clock.Today.AddMonths(monthsBack);
        return new DateOnly(d.Year, d.Month, 1);
    }

    private async Task ExportAsync(CsvKind kind, DateOnly from, DateOnly to)
    {
        var made = await _sender.Send(new ExportCsvCommand(kind, from, to));
        if (made.IsFailure) { await _dialogs.ToastAsync(_text.Error(made.Error)); return; }
        var shared = await _share.ShareFileAsync(made.Value, _text["export.share.title"]);
        if (shared.IsFailure) await _dialogs.ToastAsync(_text.Error(shared.Error));
    }
}
