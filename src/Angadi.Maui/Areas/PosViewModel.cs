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
    private BillDraft _draft;
    private bool _draftChecked;
    private readonly SemaphoreSlim _draftSave = new(1, 1);
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
        UseDraft(new BillDraft(shopHasGstin: false));
    }

    /// <summary>Every draft refreshes the screen and is saved after each change, so closing the app never loses it.</summary>
    [System.Diagnostics.CodeAnalysis.MemberNotNull(nameof(_draft))]
    private void UseDraft(BillDraft draft)
    {
        _draft = draft;
        draft.Changed += (_, _) =>
        {
            if (!ReferenceEquals(draft, _draft)) return;   // an old draft's late event
            Refresh();
            _ = SaveDraftAsync();
        };
    }

    private async Task SaveDraftAsync()
    {
        try
        {
            await _draftSave.WaitAsync();
            try { await Sender.Send(new SaveBillDraftCommand(_draft.ToSnapshot())); }   // the newest state at write time
            finally { _draftSave.Release(); }
        }
        catch (Exception ex)
        {
            GlobalExceptions.Report(ex, "draft-save");   // never breaks billing; the bill on screen is unaffected
        }
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
    /// <summary>"3 customers are due a dues reminder — tap" (empty = hidden).</summary>
    [ObservableProperty] private string _dueBanner = "";
    /// <summary>The customer whose account this bill goes to (any tender); "+ Customer" when none.</summary>
    [ObservableProperty] private string _customerLabel = "";
    private Customer? _customer;
    private static readonly TimeSpan FinalizeTimeout = TimeSpan.FromSeconds(45);

    public override async Task OnAppearingAsync()
    {
        _shop = await Sender.Send(new GetShopProfileQuery());
        var gst = !string.IsNullOrWhiteSpace(_shop?.Gstin);
        var restored = false;
        Angadi.Application.Platform.DataRecoveryReport? recovery = null;
        if (!_draftChecked)
        {
            // Startup found the data file damaged and repaired it: shown here, on the first screen that is fully up
            // (an alert raised from the shell while it is still being attached is dropped on Android).
            recovery = await Sender.Send(new GetDataRecoveryQuery());
            // The bill that was on screen when the app was closed (phone call, Android memory, crash) comes back.
            _draftChecked = true;
            if (_draft.IsEmpty && await Sender.Send(new GetBillDraftQuery()) is { } saved)
            {
                UseDraft(BillDraft.Restore(saved, gst));
                _customer = saved.CustomerId is { } cid ? await Sender.Send(new GetCustomerQuery(cid)) : null;
                restored = !_draft.IsEmpty;
            }
        }
        if (_draft.IsEmpty) UseDraft(new BillDraft(gst));
        SetCustomer(_customer);   // label text, and keeps an attached customer on a fresh draft
        QuickSale.Clear();
        foreach (var i in await Sender.Send(new GetQuickSaleItemsQuery())) QuickSale.Add(Tile(i));
        var quota = await Sender.Send(new GetBillQuotaQuery());
        QuotaText = quota.IsPro ? "" : Text.Format("sub.limit.left", Math.Max(0, quota.FreeLimit - quota.UsedThisMonth));
        var due = await Sender.Send(new Angadi.Application.Customers.GetDueRemindersQuery());
        DueBanner = due.Count > 0 ? Text.Format("pos.due.banner", due.Count) : "";
        Refresh();
        if (recovery is not null) await ShowRecoveryNoticeAsync(recovery);
        if (restored) await Dialogs.ToastAsync(Text["pos.draft.restored"]);
    }

    private Task ShowRecoveryNoticeAsync(Angadi.Application.Platform.DataRecoveryReport report)
    {
        var message = report.RestoredFrom is { } at
            ? Text.Format("data.recovered", Text.Short(at), at.ToLocalTime().ToString("h:mm tt", System.Globalization.CultureInfo.InvariantCulture))
            : Text["data.recovered.none"];
        return Dialogs.AlertAsync(Text["data.recovered.title"], message, Text["common.ok"]);
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
        catch (Exception ex)
        {
            // FIX(UI-3, 2026-10-20, FIX-3): Flaw: SearchAsync is fire-and-forget from OnSearchTextChanged and
            // caught only cancellation — a sender failure faulted an unobserved task and search silently
            // returned nothing. Fix: report it (tagged "search") so support logs show it; no toast, because
            // typing must never nag the shopkeeper. Why: silent-but-logged beats invisible. Logged in
            // docs/AGENT_FIX_LOG.md.
            GlobalExceptions.Report(ex, "search");
        }
    }

    public void Add(ItemTile tile, LineSource source = LineSource.Manual)
    {
        _draft.Add(tile.Item, source: source);
        SearchText = "";
    }

    [RelayCommand]
    private Task Mic() => SafeAsync(MicCore);

    private async Task MicCore()
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
    private Task Scan() => SafeAsync(ScanCore);

    private async Task ScanCore()
    {
        var code = await _scanner.ScanAsync();
        if (code.IsFailure) return;   // cancelled
        if (Barcodes.Normalize(code.Value) is not { } barcode || !Barcodes.IsValid(barcode))
        {
            await Dialogs.ToastAsync(Text.Error(new Error(ErrorCodes.BarcodeInvalid)));
            return;
        }
        var item = await Sender.Send(new GetItemByBarcodeQuery(barcode));
        if (item is not null) { _draft.Add(item, source: LineSource.Barcode); return; }

        // Unknown packet: make it a new item, or put the code on an item that already exists (then bill it).
        var asNew = Text["pos.scan.new"];
        var attach = Text["pos.scan.attach"];
        var choice = await Dialogs.ChooseAsync(Text.Format("pos.scan.unknown", barcode), Text["common.cancel"], attach, asNew);
        if (choice == asNew)
        {
            await Nav.GoToAsync(Routes.ItemEdit, new Dictionary<string, object> { ["barcode"] = barcode });
            return;
        }
        if (choice != attach) return;

        var search = await Dialogs.PromptAsync(Text["pos.scan.attach"], Text["pos.search"], Text["common.ok"], Text["common.cancel"]);
        if (string.IsNullOrWhiteSpace(search)) return;
        var found = await Sender.Send(new SearchItemsQuery(search, 15));
        if (found.Count == 0) { await Dialogs.ToastAsync(Text.Error(new Error(ErrorCodes.VoiceNoMatch))); return; }
        var labels = found.Select(i => $"{Text.Name(i)} · {IndianFormat.Rupees(i.Price)}").ToList();
        var pick = await Dialogs.ChooseAsync(Text["pos.scan.attach"], Text["common.cancel"], [.. labels]);
        var index = pick is null ? -1 : labels.IndexOf(pick);
        if (index < 0) return;
        var target = found[index];
        var set = await Sender.Send(new SetItemBarcodeCommand(target.Id, barcode));
        if (await ShowIfFailedAsync(set)) return;
        await Dialogs.ToastAsync(Text.Format("pos.scan.attached", Text.Name(target)));
        _draft.Add(target, source: LineSource.Barcode);
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

    // FIX(UI-2, 2026-10-20, FIX-3): Flaw: EditLine was the only POS command not routed through SafeAsync —
    // a prompt/parse exception faulted the command task instead of showing the friendly toast. Fix: route
    // through SafeAsync like its 9 siblings. Why: one unwrapped command is all it takes to reintroduce the
    // "tap does nothing" failure at the counter. Logged in docs/AGENT_FIX_LOG.md.
    [RelayCommand]
    private Task EditLine(LineRow row) => SafeAsync(() => EditLineAsync(row.Id));

    private async Task EditLineAsync(Guid lineId)
    {
        var line = _draft.Lines.FirstOrDefault(l => l.Id == lineId);
        if (line is null) return;
        var value = await Dialogs.PromptAsync(Text.Name(line), Text["bill.qty"] + " (" + Text.UnitLabel(line.Unit) + ")",
            Text["common.ok"], Text["common.cancel"], IndianFormat.Quantity(line.Qty), Keyboard.Numeric);
        if (value is not null && PosInput.TryParseQty(value, out var qty)) _draft.SetQty(lineId, qty);
    }

    [RelayCommand]
    private Task BillDiscount() => SafeAsync(BillDiscountCore);

    private async Task BillDiscountCore()
    {
        if (_draft.IsEmpty) { await Dialogs.ToastAsync(Text.Error(new Error(ErrorCodes.BillEmpty))); return; }
        var value = await Dialogs.PromptAsync(Text["pos.discount.bill"], "%", Text["common.ok"], Text["common.cancel"], "0", Keyboard.Numeric);
        if (value is null || !PosInput.TryParseDecimal(value, out var pct)) return;
        if (pct > 100) { await Dialogs.ToastAsync(Text.Error(new Error(ErrorCodes.BillDiscountTooLarge))); return; }
        _draft.SetBillDiscount(pct == 0 ? Angadi.Domain.Common.Discount.None : Angadi.Domain.Common.Discount.Percent((int)Math.Round(pct * 100)));
    }

    [RelayCommand]
    private Task NewBill() => SafeAsync(NewBillCore);

    private async Task NewBillCore()
    {
        if (!_draft.IsEmpty && !await Dialogs.ConfirmAsync(Text["pos.newbill"], Text["pos.newbill.confirm"], Text["common.yes"], Text["common.no"])) return;
        _draft.Clear();
        SetCustomer(null);
    }

    [RelayCommand]
    private Task PayCash() => SafeAsync(PayCashCore);

    private async Task PayCashCore()
    {
        if (_draft.IsEmpty) { await Dialogs.ToastAsync(Text.Error(new Error(ErrorCodes.BillEmpty))); return; }
        var value = await Dialogs.PromptAsync(Text["tender.cash"], Text["tender.received"] + " (" + Text["pos.optional"] + ")",
            Text["tender.pay"], Text["common.cancel"], "", Keyboard.Numeric);
        if (value is null) return;
        Paise? received = PosInput.TryParseRupees(value, out var p) && p.Value > 0 ? p : null;
        _draft.SetTender(TenderType.Cash, cashReceived: received);
        await FinalizeAsync();
    }

    [RelayCommand]
    private Task PayUpi() => SafeAsync(PayUpiCore);

    private async Task PayUpiCore()
    {
        if (_draft.IsEmpty) { await Dialogs.ToastAsync(Text.Error(new Error(ErrorCodes.BillEmpty))); return; }
        _shop ??= await Sender.Send(new GetShopProfileQuery());
        if (_shop is null) { await Nav.GoToAsync(Routes.ShopSetup); return; }
        var paid = await Nav.GoForResultAsync<string>(Routes.UpiPay, new Dictionary<string, object>
        {
            ["payload"] = UpiLink.ForShop(_shop, _draft.Bill.GrandTotal),
            ["amount"] = IndianFormat.Rupees(_draft.Bill.GrandTotal),
        });
        if (paid != "paid") return;
        var upiRef = await Dialogs.PromptAsync(Text["tender.upi"], Text["pos.upi.ref"], Text["common.ok"], Text["common.cancel"], keyboard: Keyboard.Numeric);
        _draft.SetTender(TenderType.Upi, upiRef: upiRef);
        await FinalizeAsync();
    }

    [RelayCommand]
    private Task PayUdhaar() => SafeAsync(PayUdhaarCore);

    private async Task PayUdhaarCore()
    {
        if (_draft.IsEmpty) { await Dialogs.ToastAsync(Text.Error(new Error(ErrorCodes.BillEmpty))); return; }
        var customer = _customer ?? await PickCustomerAsync();
        if (customer is null) return;
        SetCustomer(customer);
        _draft.SetTender(TenderType.Udhaar, customer.Id);
        await FinalizeAsync();
    }

    [RelayCommand]
    private Task OpenDue() => SafeAsync(() => Nav.GoToAsync("//" + Routes.Customers));

    /// <summary>"+ Customer" on the bill: attach a customer so the bill shows in their account; tap again to remove.</summary>
    [RelayCommand]
    private Task AttachCustomer() => SafeAsync(AttachCustomerCore);

    private async Task AttachCustomerCore()
    {
        if (_customer is not null)
        {
            if (await Dialogs.ConfirmAsync(_customer.Name, Text["pos.customer.remove"], Text["common.yes"], Text["common.no"])) SetCustomer(null);
            return;
        }
        SetCustomer(await PickCustomerAsync());
    }

    private void SetCustomer(Customer? customer)
    {
        _customer = customer;
        _draft.SetCustomer(customer?.Id);
        CustomerLabel = customer is null ? Text["pos.customer.add"]
            : customer.DuesBalance.Value > 0 ? $"{customer.Name} · {IndianFormat.Rupees(customer.DuesBalance)}"
            : customer.Name;
    }

    private async Task<Customer?> PickCustomerAsync()
    {
        var search = await Dialogs.PromptAsync(Text["nav.customers"], Text["customers.search"], Text["common.ok"], Text["common.cancel"]);
        if (search is null) return null;
        var found = await Sender.Send(new SearchCustomersQuery(search));
        var newOne = Text["customers.add"];
        var labels = found.Select(c => $"{c.Name} · {IndianFormat.Rupees(c.DuesBalance)}").ToList();
        var pick = await Dialogs.ChooseAsync(Text["nav.customers"], Text["common.cancel"], [.. labels, newOne]);
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
            // Watchdog: if anything ever stalls, the counter gets a message instead of a dead screen.
            Result<FinalizedBill> result;
            try { result = await Sender.Send(new FinalizeBillCommand(_draft.Bill)).WaitAsync(FinalizeTimeout); }
            catch (TimeoutException)
            {
                // Still saving in the background. Paying again is safe: finalize returns the same bill if it saved.
                await Dialogs.ToastAsync(Text.Error(new Error(ErrorCodes.BillSaveSlow)));
                return;
            }
            if (result.IsFailure)
            {
                await ShowIfFailedAsync(result);
                if (result.Error.Code == ErrorCodes.FreeLimitReached) await Nav.GoToAsync(Routes.Subscription);
                return;
            }
            var done = result.Value;
            UseDraft(new BillDraft(shopHasGstin: !string.IsNullOrWhiteSpace(_shop?.Gstin)));
            SetCustomer(null);
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
