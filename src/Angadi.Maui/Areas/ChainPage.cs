using System.Globalization;
using Angadi.Application.Chain;
using Angadi.Application.Common;
using Angadi.Application.Platform;
using Angadi.Application.Security;
using Angadi.Application.Sync;
using Angadi.Domain.Common;
using Angadi.Maui.Core;

namespace Angadi.Maui.Areas;

/// <summary>
/// CHAIN-1 Settings → Chain &amp; branches. A branch phone sends its signed report file to the head owner; the head
/// owner imports each file and sees every branch read-only (sales, payments, returns, udhaar due, low stock). Nothing
/// leaves a branch except these totals, and only when someone taps Send.
/// </summary>
public sealed class ChainPage : ContentPage
{
    private readonly ISender _sender;
    private readonly IDialogService _dialogs;
    private readonly ILocalizer _text;
    private readonly IReceiptShare _share;
    private readonly IAccessControl _access;
    private readonly IClock _clock;
    private readonly Label _status = Ui.Caption();
    private readonly VerticalStackLayout _branches = new() { Spacing = 8 };
    private readonly VerticalStackLayout _live = new() { Spacing = 8 };   // v2: the live chain over the internet

    public ChainPage(ISender sender, IDialogService dialogs, ILocalizer text, IReceiptShare share, IAccessControl access, IClock clock)
    {
        _sender = sender;
        _dialogs = dialogs;
        _text = text;
        _share = share;
        _access = access;
        _clock = clock;
        Title = text["settings.chain"];

        var setCode = Ui.Secondary(text["chain.code.set"]);
        setCode.Clicked += SafeUi.On(SetCodeAsync);
        var send = Ui.Button(text["chain.send"], big: true);
        send.Clicked += SafeUi.On(SendAsync);
        var import = Ui.Secondary(text["chain.import"]);
        import.Clicked += SafeUi.On(ImportAsync);

        Content = Ui.Scroll(
            Ui.Title(text["chain.title"]), Ui.Caption(text["chain.hint"]), _status, setCode,
            Ui.Title(text["chain.branch.title"]), Ui.Caption(text["chain.branch.hint"]), send,
            Ui.Title(text["chain.head.title"]), Ui.Caption(text["chain.head.hint"]), import, _branches, _live);
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await SafeUi.Run(RefreshAsync);
    }

    private async Task RefreshAsync()
    {
        var status = await _sender.Send(new GetChainStatusQuery());
        _status.Text = status.HasCode ? _text["chain.status.set"] : _text["chain.status.notset"];
        await RefreshLiveAsync();
        _branches.Clear();
        // The branch list is the head owner's view: shown when this person may see it, otherwise one button asks for the PIN.
        if (!await _access.AllowsAsync(Permission.ChainView))
        {
            var show = Ui.Secondary(_text["chain.show"]);
            show.Clicked += SafeUi.On(async () =>
            {
                if (await PinGate.CheckAsync(Permission.ChainView, _dialogs, _text)) await RefreshAsync();
            });
            _branches.Add(show);
            return;
        }
        var r = await _sender.Send(new ListBranchesQuery());
        if (r.IsFailure) { await _dialogs.ToastAsync(_text.Error(r.Error)); return; }
        _branches.Add(Ui.Title(_text["chain.branches"]));
        if (r.Value.Count == 0) _branches.Add(Ui.Caption(_text["chain.none"]));
        foreach (var b in r.Value) _branches.Add(BranchCard(b));
    }

    // ---- v2 live chain (needs cloud sync): create / join, and the head's live dashboard

    private async Task RefreshLiveAsync()
    {
        _live.Clear();
        var sync = await _sender.Send(new GetSyncStatusQuery());
        if (!sync.Available) return;   // this build has no server yet: the file-based chain above is the way
        _live.Add(Ui.Title(_text["chain.live.title"]));
        if (!sync.Linked)
        {
            _live.Add(Ui.Caption(_text["chain.live.needsync"]));
            var open = Ui.Secondary(_text["settings.cloud"]);
            open.Clicked += SafeUi.On(async () => await Shell.Current.GoToAsync(Routes.Cloud));
            _live.Add(open);
            return;
        }
        var membership = await _sender.Send(new GetLiveChainQuery());
        if (membership.IsFailure) { _live.Add(Ui.Caption(_text.Error(membership.Error))); return; }
        if (membership.Value is not { } m)
        {
            _live.Add(Ui.Caption(_text["chain.live.hint"]));
            var create = Ui.Secondary(_text["chain.live.create"]);
            create.Clicked += SafeUi.On(CreateLiveAsync);
            var join = Ui.Secondary(_text["chain.live.join"]);
            join.Clicked += SafeUi.On(JoinLiveAsync);
            _live.Add(create);
            _live.Add(join);
            return;
        }
        _live.Add(Ui.Caption(_text.Format(m.IsHead ? "chain.live.head" : "chain.live.member", m.Name, m.Branches)));
        if (!m.IsHead || !await _access.AllowsAsync(Permission.ChainView)) return;
        var board = await _sender.Send(new GetLiveDashboardQuery(7));
        if (board.IsFailure) { _live.Add(Ui.Caption(_text.Error(board.Error))); return; }
        var today = _clock.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        foreach (var b in board.Value.Branches)
        {
            var card = new VerticalStackLayout { Spacing = 3 };
            card.Add(Ui.Title(b.Name.Length == 0 ? _text["chain.unnamed"] : b.Name));
            var todaySales = b.Days.Where(d => d.Day == today).Sum(d => d.SalesPaise);
            card.Add(Ui.Body(_text.Format("chain.live.today", IndianFormat.Rupees(new Paise(todaySales))), 15));
            card.Add(Ui.Caption(_text.Format("chain.week", IndianFormat.Rupees(new Paise(b.SalesPaise)), b.Bills)));
            card.Add(Ui.Caption(_text.Format("chain.live.money", IndianFormat.Rupees(new Paise(b.DuesCollectedPaise)), IndianFormat.Rupees(new Paise(b.ReturnsPaise)))));
            card.Add(Ui.Caption(b.LastEventAtMs is { } at
                ? _text.Format("chain.updated", DateTimeOffset.FromUnixTimeMilliseconds(at).ToLocalTime().ToString("dd MMM HH:mm", CultureInfo.InvariantCulture))
                : _text["chain.live.nothing"]));
            _live.Add(Ui.Card(card, padding: 10));
        }
    }

    private async Task CreateLiveAsync()
    {
        var name = await _dialogs.PromptAsync(_text["chain.live.create"], _text["chain.live.name"], _text["common.ok"], _text["common.cancel"]);
        if (string.IsNullOrWhiteSpace(name)) return;
        var r = await _sender.Send(new CreateLiveChainCommand(name));
        if (r.IsFailure) { await _dialogs.ToastAsync(_text.Error(r.Error)); return; }
        // Shown once: the server keeps only a hash of it.
        await _dialogs.AlertAsync(_text["chain.live.code.title"], _text.Format("chain.live.code", r.Value.JoinCode), _text["common.ok"]);
        await RefreshAsync();
    }

    private async Task JoinLiveAsync()
    {
        var code = await _dialogs.PromptAsync(_text["chain.live.join"], _text["chain.live.code.prompt"], _text["common.ok"], _text["common.cancel"]);
        if (string.IsNullOrWhiteSpace(code)) return;
        var r = await _sender.Send(new JoinLiveChainCommand(code));
        await _dialogs.ToastAsync(r.IsSuccess ? _text.Format("chain.live.joined", r.Value.Name) : _text.Error(r.Error));
        await RefreshAsync();
    }

    private View BranchCard(BranchPack b)
    {
        var today = _clock.Today;
        var week = b.Days.Where(d => d.Day > today.AddDays(-7) && d.Day <= today).ToList();
        var latest = b.Days.OrderByDescending(d => d.Day).FirstOrDefault();
        var stack = new VerticalStackLayout { Spacing = 3 };
        stack.Add(Ui.Title(b.ShopName.Length == 0 ? _text["chain.unnamed"] : b.ShopName));
        stack.Add(Ui.Caption(_text.Format("chain.updated", b.GeneratedAt.ToLocalTime().ToString("dd MMM HH:mm", CultureInfo.InvariantCulture))));
        stack.Add(Ui.Body(_text.Format("chain.week", IndianFormat.Rupees(new Paise(week.Sum(d => d.Net))), week.Sum(d => d.Bills)), 15));
        if (latest is not null)
            stack.Add(Ui.Caption(_text.Format("chain.latest", latest.Day.ToString("dd MMM", CultureInfo.InvariantCulture), IndianFormat.Rupees(new Paise(latest.Net)),
                IndianFormat.Rupees(new Paise(latest.Cash)), IndianFormat.Rupees(new Paise(latest.Upi)), IndianFormat.Rupees(new Paise(latest.Udhaar)))));
        stack.Add(Ui.Caption(_text.Format("chain.dues", IndianFormat.Rupees(new Paise(b.DuesTotal)))));
        stack.Add(Ui.Caption(_text.Format("chain.stock", b.ItemCount, b.LowCount, b.OutCount)));
        // The last week day by day, so a head owner can spot a slow day without opening anything else.
        foreach (var d in week.OrderByDescending(d => d.Day))
            stack.Add(Ui.Caption($"{d.Day.ToString("dd MMM", CultureInfo.InvariantCulture)} · {d.Bills} · {IndianFormat.Rupees(new Paise(d.Net))}"));
        var remove = Ui.Button(_text["chain.remove"], Theme.Danger);
        var uid = b.ShopUid;
        var name = b.ShopName;
        remove.Clicked += SafeUi.On(async () =>
        {
            if (!await _dialogs.ConfirmAsync(_text["chain.remove"], _text.Format("chain.remove.confirm", name), _text["common.yes"], _text["common.no"])) return;
            var r = await _sender.Send(new RemoveBranchCommand(uid));
            await _dialogs.ToastAsync(r.IsSuccess ? _text["settings.saved"] : _text.Error(r.Error));
            await RefreshAsync();
        });
        stack.Add(remove);
        return Ui.Card(stack, padding: 10);
    }

    private async Task SetCodeAsync()
    {
        var code = await _dialogs.PromptAsync(_text["chain.code.set"], _text["chain.code.prompt"], _text["common.ok"], _text["common.cancel"]);
        if (code is null) return;
        var r = await _sender.Send(new SetChainCodeCommand(code));
        await _dialogs.ToastAsync(r.IsSuccess ? _text["settings.saved"] : _text.Error(r.Error));
        await RefreshAsync();
    }

    private async Task SendAsync()
    {
        var built = await _sender.Send(new BuildChainPackCommand());
        if (built.IsFailure) { await _dialogs.ToastAsync(_text.Error(built.Error)); return; }
        var shared = await _share.ShareFileAsync(built.Value, _text["chain.share.title"]);
        if (shared.IsFailure) await _dialogs.ToastAsync(_text.Error(shared.Error));
    }

    private async Task ImportAsync()
    {
        if (!await PinGate.CheckAsync(Permission.ChainView, _dialogs, _text)) return;
        var picked = await FilePicker.Default.PickAsync();
        if (picked is null) return;
        string text;
        try
        {
            await using var stream = await picked.OpenReadAsync();
            // A branch report is a few KB; anything much bigger is not one, and is never read into memory.
            if (stream.CanSeek && stream.Length > ChainRules.MaxPackBytes) { await _dialogs.ToastAsync(_text.Error(new Error(ErrorCodes.ChainPackUnreadable))); return; }
            using var reader = new StreamReader(stream);
            text = await reader.ReadToEndAsync();
        }
        catch (IOException)
        {
            await _dialogs.ToastAsync(_text.Error(new Error(ErrorCodes.ChainPackUnreadable)));
            return;
        }
        var r = await _sender.Send(new ImportChainPackCommand(text));
        if (r.IsFailure) { await _dialogs.ToastAsync(_text.Error(r.Error)); return; }
        await _dialogs.ToastAsync(_text[r.Value switch
        {
            ImportOutcome.Added => "chain.imported.added",
            ImportOutcome.Updated => "chain.imported.updated",
            _ => "chain.imported.newer",
        }]);
        await RefreshAsync();
    }
}
