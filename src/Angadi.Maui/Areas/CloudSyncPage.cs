using System.Globalization;
using Angadi.Application.Common;
using Angadi.Application.Platform;
using Angadi.Application.Sync;
using Angadi.Maui.Core;

namespace Angadi.Maui.Areas;

/// <summary>
/// Settings → Cloud sync: turn on copying this shop's records to the Angadi server (owner only), see what is waiting
/// and send now. Billing never depends on it. Hidden behind "not available" until the build has a server address.
/// </summary>
public sealed class CloudSyncPage : ContentPage
{
    private readonly ISender _sender;
    private readonly IDialogService _dialogs;
    private readonly ILocalizer _text;
    private readonly Label _status = Ui.Body("", 16);
    private readonly Label _detail = Ui.Caption();
    private readonly Button _link;
    private readonly Button _now;

    public CloudSyncPage(ISender sender, IDialogService dialogs, ILocalizer text)
    {
        _sender = sender;
        _dialogs = dialogs;
        _text = text;
        Title = text["settings.cloud"];
        _link = Ui.Button(text["cloud.link"], big: true);
        _link.Clicked += SafeUi.On(LinkAsync);
        _now = Ui.Secondary(text["cloud.now"]);
        _now.Clicked += SafeUi.On(NowAsync);
        Content = Ui.Scroll(Ui.Title(text["cloud.title"]), Ui.Caption(text["cloud.hint"]), _status, _detail, _link, _now);
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await SafeUi.Run(RefreshAsync);
    }

    private async Task RefreshAsync()
    {
        var s = await _sender.Send(new GetSyncStatusQuery());
        _link.IsVisible = s.Available && !s.Linked;
        _now.IsVisible = s.Available && s.Linked;
        _status.Text = !s.Available ? _text["cloud.unavailable"] : s.Linked ? _text["cloud.on"] : _text["cloud.off"];
        _detail.Text = !s.Linked ? "" : _text.Format("cloud.detail", s.Pending,
            s.LastSuccess is { } at ? at.ToLocalTime().ToString("dd MMM HH:mm", CultureInfo.InvariantCulture) : _text["cloud.never"])
            + (s.Rejected > 0 ? "\n" + _text.Format("cloud.rejected", s.Rejected) : "");
    }

    private async Task LinkAsync()
    {
        if (!await _dialogs.ConfirmAsync(_text["cloud.link"], _text["cloud.link.confirm"], _text["common.yes"], _text["common.no"])) return;
        var r = await _sender.Send(new LinkCloudSyncCommand());
        if (r.IsFailure) { await _dialogs.ToastAsync(_text.Error(r.Error)); return; }
        await NowAsync();
    }

    private async Task NowAsync()
    {
        var r = await _sender.Send(new SyncNowCommand(true));
        await _dialogs.ToastAsync(r.IsSuccess ? _text.Format("cloud.sent", r.Value) : _text.Error(r.Error));
        await RefreshAsync();
    }
}
