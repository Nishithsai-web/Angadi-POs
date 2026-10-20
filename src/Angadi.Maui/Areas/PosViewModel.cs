using System.Collections.ObjectModel;
using Angadi.Application.Billing;
using Angadi.Application.Catalog;
using Angadi.Application.Common;
using Angadi.Application.Platform;
using Angadi.Application.Voice;
using Angadi.Domain.Billing;
using Angadi.Domain.Common;
using Angadi.Domain.Customers;
using Angadi.Domain.Items;
using Angadi.Maui.Areas.Pos;
using Angadi.Maui.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Options;

namespace Angadi.Maui.Areas;

public sealed record ItemTile(Guid Id, string Name, string Price, Item Item);
public sealed record LineRow(Guid Id, string Name, string QtyText, string Amount);

/// <summary>The counter (FR-06–15, FR-34). All bill editing goes through BillDraft; all data through ISender.</summary>
public sealed partial class PosViewModel : ViewModelBase
{
    private readonly ISpeechToText _speech;
    private readonly IVoiceBillingPipeline _voice;
    private readonly IAliasLearner _aliases;
    private readonly IBarcodeScanner _scanner;
    private readonly VoiceOptions _voiceOptions;
    private BillDraft _draft = new(shopHasGstin: false);
    private Angadi.Domain.Shop.ShopProfile? _shop;
    private CancellationTokenSource? _searchCts;

    public PosViewModel(ISender sender, ILocalizer text, INavigationService nav, IDialogService dialogs,
        ISpeechToText speech, IVoiceBillingPipeline voice, IAliasLearner aliases, IBarcodeScanner scanner, IOptions<VoiceOptions> voiceOptions)
        : base(sender, text, nav, dialogs)
    {
        _speech = speech;
        _voice = voice;
        _aliases = aliases;
        _scanner = scanner;
        _voiceOptions = voiceOptions.Value;
        _draft.Changed += (_, _) => Refresh();
    }

    public ObservableCollection<ItemTile> QuickSale { get; } = [];
    public ObservableCollection<ItemTile> SearchResults { get; } = [];
    public ObservableCollection<LineRow> Lines { get; } = [];

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _voiceText = "";
    [ObservableProperty] private bool _isListening;
    [ObservableProperty] private string _subTotal = "";
    [ObservableProperty] private string _discount = "";
    [ObservableProperty] private string _tax = "";
    [ObservableProperty] private string _rounding = "";
    [ObservableProperty] private string _total = IndianFormat.Rupees(Paise.Zero);
    [ObservableProperty] private string _emptyText = "";
    [ObservableProperty] private string _quotaText = "";

    public override async Task OnAppearingAsync()
    {
        _shop = await Sender.Send(new GetShopProfileQuery());
        if (_draft.IsEmpty)
        {
            _draft = new BillDraft(shopHasGstin: !string.IsNullOrWhiteSpace(_shop?.Gstin));
            _draft.Changed += (_, _) => Refresh();
        }
        QuickSale.Clear();
        foreach (var i in await Sender.Send(new GetQuickSaleItemsQuery())) QuickSale.Add(Tile(i));
        var quota = await Sender.Send(new GetBillQuotaQuery());
        QuotaText = quota.IsPro ? "" : Text.Format("sub.limit.left", Math.Max(0, quota.FreeLimit - quota.UsedThisMonth));
        Refresh();
    }

    private ItemTile Tile(Item i) => new(i.Id, Text.Name(i), IndianFormat.Rupees(i.Price) + " / " + Text.UnitLabel(i.Unit), i);

    partial void OnSearchTextChanged(string value) => _ = SearchAsync(value);

    private async Task SearchAsync(string value)
    {
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        try
        {
            await Task.Delay(250, cts.Token);   // debounce typing
            IReadOnlyList<Item> results = string.IsNullOrWhiteSpace(value) ? Array.Empty<Item>() : await Sender.Send(new SearchItemsQuery(value), cts.Token);
            if (cts.IsCancellationRequested) return;
            SearchResults.Clear();
            foreach (var i in results) SearchResults.Add(Tile(i));
        }
        catch (OperationCanceledException) { }
    }

    public void Add(ItemTile tile, LineSource source = LineSource.Manual)
    {
        _draft.Add(tile.Item, source: source);
        SearchText = "";
    }

    [RelayCommand]
    private async Task Mic()
    {
        if (IsListening) return;
        IsListening = true;
        VoiceText = Text["pos.listening"];
        try
        {
            var heard = await _speech.ListenAsync(_voiceOptions.Culture, new Progress<string>(p => VoiceText = p));
            if (heard.IsFailure)
            {
                if (heard.Error.Detail == "manual") { await Dialogs.ToastAsync(Text["pos.voice.offline"]); return; }
                if (heard.Error.Detail != "cancelled") await ShowIfFailedAsync(heard);
                return;
            }
            VoiceText = heard.Value;
            var matches = await _voice.ProcessUtteranceAsync(heard.Value);
            if (await ShowIfFailedAsync(matches)) return;
            foreach (var m in matches.Value) await ApplyMatchAsync(m);
        }
        finally { IsListening = false; }
    }

    /// <summary>FR-07: auto-add, ask, or hand the phrase to search.</summary>
    private async Task ApplyMatchAsync(MatchResult m)
    {
        Guid? chosen = null;
        if (m.Kind == MatchKind.AutoAdd && m.Best is { } best) chosen = best.ItemId;
        else if (m.Kind == MatchKind.Confirm && m.Candidates.Count > 0)
        {
            var none = Text["pos.confirm.none"];
            var pick = await Dialogs.ChooseAsync($"{Text["pos.confirm.title"]} ({m.Line.Raw})", Text["common.cancel"],
                [.. m.Candidates.Select(c => c.DisplayName), none]);
            var candidate = m.Candidates.FirstOrDefault(c => c.DisplayName == pick);
            if (candidate is not null)
            {
                chosen = candidate.ItemId;
                await _aliases.LearnAsync(candidate.ItemId, m.Line.Name);
            }
        }

        if (chosen is null)
        {
            await Dialogs.ToastAsync(Text.Error(new Error(ErrorCodes.VoiceNoMatch)));
            SearchText = m.Line.Name;
            return;
        }

        var item = await Sender.Send(new GetItemQuery(chosen.Value));
        if (item is null) return;
        var line = _draft.AddFromVoice(item, m.Line, out var needsConfirm);
        if (needsConfirm) await EditLineAsync(line.Id);
    }

    [RelayCommand]
    private async Task Scan()
    {
        var code = await _scanner.ScanAsync();
        if (code.IsFailure) return;   // cancelled
        var item = await Sender.Send(new GetItemByBarcodeQuery(code.Value));
        if (item is not null) { _draft.Add(item, source: LineSource.Barcode); return; }

        var create = await Dialogs.ConfirmAsync(Text["pos.scan"], Text.Error(new Error(ErrorCodes.BarcodeUnknown)), Text["common.yes"], Text["common.no"]);
        if (create) await Nav.GoToAsync(Routes.ItemEdit, new Dictionary<string, object> { ["barcode"] = code.Value });
    }

    [RelayCommand]
    private void IncLine(LineRow row)
    {
        var line = _draft.Lines.FirstOrDefault(l => l.Id == row.Id);
        if (line is not null) _draft.SetQty(row.Id, line.Qty + Qty.Whole(1));
    }

    [RelayCommand]
    private void DecLine(LineRow row)
    {
        var line = _draft.Lines.FirstOrDefault(l => l.Id == row.Id);
        if (line is not null) _draft.SetQty(row.Id, line.Qty - Qty.Whole(1));
    }

    [RelayCommand]
    private Task EditLine(LineRow row) => EditLineAsync(row.Id);

    private async Task EditLineAsync(Guid lineId)
    {
        var line = _draft.Lines.FirstOrDefault(l => l.Id == lineId);
        if (line is null) return;
        var value = await Dialogs.PromptAsync(Text.Name(line), Text["bill.qty"] + " (" + Text.UnitLabel(line.Unit) + ")",
            Text["common.ok"], Text["common.cancel"], IndianFormat.Quantity(line.Qty), Keyboard.Numeric);
        if (value is not null && PosInput.TryParseQty(value, out var qty)) _draft.SetQty(lineId, qty);
    }

    [RelayCommand]
    private async Task BillDiscount()
    {
        if (_draft.IsEmpty) return;
        var value = await Dialogs.PromptAsync(Text["pos.discount.bill"], "%", Text["common.ok"], Text["common.cancel"], "0", Keyboard.Numeric);
        if (value is null || !PosInput.TryParseDecimal(value, out var pct)) return;
        if (pct > 100) { await Dialogs.ToastAsync(Text.Error(new Error(ErrorCodes.BillDiscountTooLarge))); return; }
        _draft.SetBillDiscount(pct == 0 ? Angadi.Domain.Common.Discount.None : Angadi.Domain.Common.Discount.Percent((int)Math.Round(pct * 100)));
    }

    [RelayCommand]
    private async Task NewBill()
    {
        if (!_draft.IsEmpty && !await Dialogs.ConfirmAsync(Text["pos.newbill"], Text["pos.newbill.confirm"], Text["common.yes"], Text["common.no"])) return;
        _draft.Clear();
    }

    [RelayCommand]
    private async Task PayCash()
    {
        if (_draft.IsEmpty) return;
        var value = await Dialogs.PromptAsync(Text["tender.cash"], Text["tender.received"] + " (" + Text["pos.optional"] + ")",
            Text["tender.pay"], Text["common.cancel"], "", Keyboard.Numeric);
        if (value is null) return;
        Paise? received = PosInput.TryParseRupees(value, out var p) && p.Value > 0 ? p : null;
        _draft.SetTender(TenderType.Cash, cashReceived: received);
        await FinalizeAsync();
    }

    [RelayCommand]
    private async Task PayUpi()
    {
        if (_draft.IsEmpty || _shop is null) return;
        var paid = await Nav.GoForResultAsync<string>(Routes.UpiPay, new Dictionary<string, object>
        {
            ["payload"] = UpiLink.Build(_shop.UpiId, _shop.Name, _draft.Bill.GrandTotal),
            ["amount"] = IndianFormat.Rupees(_draft.Bill.GrandTotal),
        });
        if (paid != "paid") return;
        var upiRef = await Dialogs.PromptAsync(Text["tender.upi"], Text["pos.upi.ref"], Text["common.ok"], Text["common.cancel"], keyboard: Keyboard.Numeric);
        _draft.SetTender(TenderType.Upi, upiRef: upiRef);
        await FinalizeAsync();
    }

    [RelayCommand]
    private async Task PayUdhaar()
    {
        if (_draft.IsEmpty) return;
        var customer = await PickCustomerAsync();
        if (customer is null) return;
        _draft.SetTender(TenderType.Udhaar, customer.Id);
        await FinalizeAsync();
    }

    private async Task<Customer?> PickCustomerAsync()
    {
        var search = await Dialogs.PromptAsync(Text["tender.udhaar"], Text["customers.name"], Text["common.ok"], Text["common.cancel"]);
        if (search is null) return null;
        var found = await Sender.Send(new SearchCustomersQuery(search));
        var newOne = Text["customers.add"];
        var labels = found.Select(c => $"{c.Name} · {IndianFormat.Rupees(c.DuesBalance)}").ToList();
        var pick = await Dialogs.ChooseAsync(Text["tender.udhaar"], Text["common.cancel"], [.. labels, newOne]);
        if (pick is null) return null;
        if (pick != newOne) return found[labels.IndexOf(pick)];

        var phone = await Dialogs.PromptAsync(newOne, Text["customers.phone"], Text["common.ok"], Text["common.cancel"], keyboard: Keyboard.Telephone);
        var customer = new Customer { Name = string.IsNullOrWhiteSpace(search) ? newOne : search.Trim(), Phone = phone };
        var saved = await Sender.Send(new SaveCustomerCommand(customer));
        return await ShowIfFailedAsync(saved) ? null : customer;
    }

    private async Task FinalizeAsync()
    {
        await RunBusyAsync(async () =>
        {
            var result = await Sender.Send(new FinalizeBillCommand(_draft.Bill));
            if (result.IsFailure)
            {
                await ShowIfFailedAsync(result);
                if (result.Error.Code == ErrorCodes.FreeLimitReached) await Nav.GoToAsync(Routes.Subscription);
                return;
            }
            var done = result.Value;
            _draft = new BillDraft(shopHasGstin: !string.IsNullOrWhiteSpace(_shop?.Gstin));
            _draft.Changed += (_, _) => Refresh();
            Refresh();
            await Nav.GoToAsync(Routes.Receipt, new Dictionary<string, object>
            {
                ["billId"] = done.BillId.ToString("D"),
                ["change"] = IndianFormat.Rupees(done.ChangeDue),
            });
        });
    }

    private void Refresh()
    {
        Lines.Clear();
        foreach (var l in _draft.Lines)
            Lines.Add(new LineRow(l.Id, Text.Name(l),
                IndianFormat.Quantity(l.Qty) + " " + Text.UnitLabel(l.Unit) + " × " + IndianFormat.Rupees(l.UnitPrice),
                IndianFormat.Rupees(l.LineTotal)));
        var b = _draft.Bill;
        SubTotal = IndianFormat.Rupees(b.SubTotal);
        Discount = IndianFormat.Rupees(BillCalculator.TotalDiscount(b));
        Tax = IndianFormat.Rupees(b.TaxTotal);
        Rounding = IndianFormat.Rupees(b.RoundingAdjustment);
        Total = IndianFormat.Rupees(b.GrandTotal);
        EmptyText = _draft.IsEmpty ? Text["pos.empty"] : "";
    }
}
