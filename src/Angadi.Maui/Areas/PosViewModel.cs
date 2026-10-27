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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Angadi.Maui.Areas;

public sealed record ItemTile(Guid Id, string Name, string Price, Item Item);
public sealed record LineRow(Guid Id, string Name, string QtyText, string Amount);

/// <summary>The counter (FR-06–15, FR-34). All bill editing goes through BillDraft; all data through ISender.</summary>
public sealed partial class PosViewModel : ViewModelBase
{
    private readonly ISpeechToText _speech;
    private readonly IVoiceBillingPipeline _voice;
    private readonly IBarcodeScanner _scanner;
    private readonly VoiceOptions _voiceOptions;
    private readonly ILogger<PosViewModel> _log;
    private BillDraft _draft;
    private bool _draftChecked;
    private readonly SemaphoreSlim _draftSave = new(1, 1);
    private Angadi.Domain.Shop.ShopProfile? _shop;
    private CancellationTokenSource? _searchCts;

    public PosViewModel(ISender sender, ILocalizer text, INavigationService nav, IDialogService dialogs,
        ISpeechToText speech, IVoiceBillingPipeline voice, IBarcodeScanner scanner, IOptions<VoiceOptions> voiceOptions,
        ILogger<PosViewModel> log)
        : base(sender, text, nav, dialogs)
    {
        _speech = speech;
        _voice = voice;
        _scanner = scanner;
        _voiceOptions = voiceOptions.Value;
        _log = log;
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
    /// <summary>A2-03: "no backup for 7 days" (empty = hidden). Tap → Settings; ✕ hides it until tomorrow.</summary>
    [ObservableProperty] private string _backupBanner = "";
    /// <summary>The customer whose account this bill goes to (any tender); "+ Customer" when none.</summary>
    [ObservableProperty] private string _customerLabel = "";
    private Customer? _customer;
    private static readonly TimeSpan FinalizeTimeout = TimeSpan.FromSeconds(45);

    public override async Task OnAppearingAsync()
    {
        // FIX(A1-04, 2026-10-21, S2): Flaw: the one-time recovery report was taken first but shown last — any later
        // failing query lost it for good, and _draftChecked was set before the draft restore ran, so a failed restore
        // was never retried. Fix: show the report the moment it is taken; mark the draft checked only after the
        // restore succeeded. Why: "your data was repaired" and the bill on screen must never silently vanish.
        // Logged in docs/AGENT_FIX_LOG.md.
        // Startup found the data file damaged and repaired it: shown here, on the first screen that is fully up
        // (an alert raised from the shell while it is still being attached is dropped on Android).
        if (await Sender.Send(new GetDataRecoveryQuery()) is { } recovery) await ShowRecoveryNoticeAsync(recovery);

        _shop = await Sender.Send(new GetShopProfileQuery());
        var gst = _shop?.ChargesGst ?? false;
        var restored = false;
        if (!_draftChecked)
        {
            // The bill that was on screen when the app was closed (phone call, Android memory, crash) comes back.
            if (_draft.IsEmpty && await Sender.Send(new GetBillDraftQuery()) is { } saved)
            {
                UseDraft(BillDraft.Restore(saved, gst));
                _customer = saved.CustomerId is { } cid ? await Sender.Send(new GetCustomerQuery(cid)) : null;
                restored = !_draft.IsEmpty;
            }
            _draftChecked = true;
        }
        if (_draft.IsEmpty) UseDraft(new BillDraft(gst));
        SetCustomer(_customer);   // label text, and keeps an attached customer on a fresh draft
        QuickSale.Clear();
        foreach (var i in await Sender.Send(new GetQuickSaleItemsQuery())) QuickSale.Add(Tile(i));
        var quota = await Sender.Send(new GetBillQuotaQuery());
        // A2-11: a Pro shop offline for over 7 days keeps billing; it is only asked to connect.
        QuotaText = !quota.IsPro ? Text.Format("sub.limit.left", Math.Max(0, quota.FreeLimit - quota.UsedThisMonth))
            : quota.NeedsReconfirm ? Text["sub.reconfirm"] : "";
        var due = await Sender.Send(new Angadi.Application.Customers.GetDueRemindersQuery());
        DueBanner = due.Count > 0 ? Text.Format("pos.due.banner", due.Count) : "";
        var backup = await Sender.Send(new GetBackupStatusQuery());
        BackupBanner = !backup.ShowReminder ? "" : Text[backup.IsLarge ? "backup.reminder.large" : "backup.reminder"];
        Refresh();
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

    /// <summary>
    /// Raised when voice falls back to typed search (the "manual" failure): the page focuses the
    /// search box (VOICE-9, FIX-1 2026-10-20).
    /// </summary>
    public event EventHandler? SearchFocusRequested;

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
                // FIX(VOICE-9, 2026-10-20, FIX-1): Flaw: the "manual" fallback only toasted — the
                // shopkeeper was told to type, but the search box was never focused, despite the
                // fallback chain's contract promising typed search opens. Fix: raise
                // SearchFocusRequested; PosPage focuses the search entry.
                // Why: a fallback that needs a hunt for the box is a dead mic at a busy counter.
                // Logged in docs/AGENT_FIX_LOG.md.
                if (heard.Error.Detail == "manual") { await Dialogs.ToastAsync(Text["pos.voice.offline"]); SearchFocusRequested?.Invoke(this, EventArgs.Empty); return; }
                if (heard.Error.Detail == Angadi.Maui.Platform.AndroidSpeechToText.PermissionDeniedPermanently)
                {
                    // A2-18: Android won't ask again; Settings is the only way back — one tap there.
                    if (await Dialogs.ConfirmAsync(Text["perm.title"], Text["perm.mic.off"], Text["perm.open.settings"], Text["common.cancel"]))
                        AppInfo.Current.ShowSettingsUI();
                    return;
                }
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
            // A1-06: two items can share a display name; pick by position, never by label text.
            var labels = ChoiceLabels.Numbered(m.Candidates.Select(c => c.DisplayName));
            var pick = await Dialogs.ChooseAsync($"{Text["pos.confirm.title"]} ({m.Line.Raw})", Text["common.cancel"],
                [.. labels, none]);
            var at = pick is null ? -1 : labels.IndexOf(pick);
            var candidate = at >= 0 ? m.Candidates[at] : null;
            if (candidate is not null)
            {
                chosen = candidate.ItemId;
                // FIX(A1-11, 2026-10-21, S2): Flaw: the view model called IAliasLearner directly — no correlation id,
                // no logging, and the multi-item alias rewrite ran outside a transaction. Fix: LearnAliasCommand through
                // the pipeline. Why: a half-written alias table mis-hears the next bill. Logged in docs/AGENT_FIX_LOG.md.
                await Sender.Send(new LearnAliasCommand(candidate.ItemId, m.Line.Name));
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
        var labels = ChoiceLabels.Numbered(found.Select(i => $"{Text.Name(i)} · {IndianFormat.Rupees(i.Price)}/{Text.UnitLabel(i.Unit)}"));   // A1-06
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

    // FIX(A2-03, 2026-10-21, S2): Flaw: nothing ever told the owner that no manual backup existed — Android's Auto
    // Backup silently stops past 25 MB. Fix: a counter banner after 7 days without one (tap → Settings, ✕ = hide for
    // today), status kept in Meta. Why: a lost phone must not mean lost udhaar books. Logged in docs/AGENT_FIX_LOG.md.
    [RelayCommand]
    private Task BackupNow() => SafeAsync(() => Nav.GoToAsync("//" + Routes.Settings));

    [RelayCommand]
    private Task HideBackupBanner() => SafeAsync(async () =>
    {
        if (!await ShowIfFailedAsync(await Sender.Send(new DismissBackupReminderCommand()))) BackupBanner = "";
    });

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
        if (value is null) return;
        // A1-08: a quantity past what one line may hold says so, instead of "must be more than zero".
        if (PosInput.TryParseQty(value, out var qty) ? !_draft.SetQty(lineId, qty) : PosInput.IsQtyTooLarge(value))
            await Dialogs.ToastAsync(Text.Error(new Error(ErrorCodes.BillQtyTooLarge)));
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

    /// <summary>
    /// v2 split tender: one bill paid in two parts — cash + UPI, cash + udhaar, or UPI + udhaar. The shopkeeper types
    /// the first part on the big number pad; the rest goes to the second tender. Cash may take "received" for change,
    /// UPI shows the QR for its part only, udhaar needs the customer. Finalize checks the parts add up exactly.
    /// </summary>
    [RelayCommand]
    private Task PaySplit() => SafeAsync(PaySplitCore);

    private async Task PaySplitCore()
    {
        if (_draft.IsEmpty) { await Dialogs.ToastAsync(Text.Error(new Error(ErrorCodes.BillEmpty))); return; }
        var total = _draft.Bill.GrandTotal;
        string cashUpi = Text["split.cash_upi"], cashUdhaar = Text["split.cash_udhaar"], upiUdhaar = Text["split.upi_udhaar"];
        var pick = await Dialogs.ChooseAsync(Text.Format("split.title", IndianFormat.Rupees(total)), Text["common.cancel"], cashUpi, cashUdhaar, upiUdhaar);
        if (pick is null || (pick != cashUpi && pick != cashUdhaar && pick != upiUdhaar)) return;
        var (first, second) = pick == cashUpi ? (TenderType.Cash, TenderType.Upi)
            : pick == cashUdhaar ? (TenderType.Cash, TenderType.Udhaar)
            : (TenderType.Upi, TenderType.Udhaar);

        var typed = await Dialogs.PromptAsync(TenderName(first),
            Text.Format("split.amount", TenderName(first), IndianFormat.Rupees(total), TenderName(second)),
            Text["common.ok"], Text["common.cancel"], "", Keyboard.Numeric);
        if (typed is null) return;
        if (!PosInput.TryParseRupees(typed, out var firstAmount) || firstAmount.Value <= 0 || firstAmount >= total)
        {
            await Dialogs.ToastAsync(Text.Error(new Error(ErrorCodes.BillSplitInvalid)));
            return;
        }
        var parts = new List<BillPayment>
        {
            new() { Tender = first, Amount = firstAmount },
            new() { Tender = second, Amount = total - firstAmount },
        };

        if (second == TenderType.Udhaar)
        {
            var customer = _customer ?? await PickCustomerAsync();
            if (customer is null) return;
            SetCustomer(customer);
        }
        if (parts.FirstOrDefault(p => p.Tender == TenderType.Cash) is { } cash)
        {
            var received = await Dialogs.PromptAsync(TenderName(TenderType.Cash) + " " + IndianFormat.Rupees(cash.Amount),
                Text["tender.received"] + " (" + Text["pos.optional"] + ")", Text["tender.pay"], Text["common.cancel"], "", Keyboard.Numeric);
            if (received is null) return;
            if (PosInput.TryParseRupees(received, out var r) && r.Value > 0) cash.CashReceived = r;
        }
        if (parts.FirstOrDefault(p => p.Tender == TenderType.Upi) is { } upi)
        {
            _shop ??= await Sender.Send(new GetShopProfileQuery());
            if (_shop is null) { await Nav.GoToAsync(Routes.ShopSetup); return; }
            var paid = await Nav.GoForResultAsync<string>(Routes.UpiPay, new Dictionary<string, object>
            {
                ["payload"] = UpiLink.ForShop(_shop, upi.Amount),
                ["amount"] = IndianFormat.Rupees(upi.Amount),
            });
            if (paid != "paid") return;
            upi.UpiRef = await Dialogs.PromptAsync(Text["tender.upi"], Text["pos.upi.ref"], Text["common.ok"], Text["common.cancel"], keyboard: Keyboard.Numeric);
        }
        _draft.SetSplit(parts, _customer?.Id);
        await FinalizeAsync();
    }

    private string TenderName(TenderType tender) => tender switch
    {
        TenderType.Cash => Text["tender.cash"],
        TenderType.Upi => Text["tender.upi"],
        _ => Text["tender.udhaar"],
    };

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
        // A1-06: two "Ramesh · ₹0.00" must never collapse into the first one (udhaar to the wrong person):
        // number every row and show the phone's last 4 digits.
        var labels = ChoiceLabels.Numbered(found.Select(c => $"{c.Name} · {IndianFormat.Rupees(c.DuesBalance)}"
            + (ChoiceLabels.PhoneTail(c.Phone) is { Length: > 0 } tail ? " · " + tail : "")));
        var pick = await Dialogs.ChooseAsync(Text["nav.customers"], Text["common.cancel"], [.. labels, newOne]);
        if (pick is null) return null;
        if (pick != newOne) return labels.IndexOf(pick) is var at and >= 0 ? found[at] : null;

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
            var bill = _draft.Bill;
            // FIX(L-7, 2026-10-20, FIX-3): Flaw: a bill whose lines total ₹0 (100% discount, or a free item)
            // finalized silently — burning a bill number and a quota slot and decrementing stock with no
            // warning. Fix: a ₹0 bill WITH lines needs an explicit "give it free?" confirmation in the app
            // language; cancelling returns before anything is sent, so there is no quota/stock effect. The
            // answer rides on the command's FreeBillConfirmed flag (FIX-2's validator enforces the same rule
            // on the handler side). Why: a free bill is legitimate (a goodwill packet) but must be a decision,
            // never an accident. Logged in docs/AGENT_FIX_LOG.md.
            var freeConfirmed = false;
            if (bill.Lines.Count > 0 && bill.GrandTotal.Value == 0)
            {
                if (!await Dialogs.ConfirmAsync(Text["pos.freebill.title"], Text["pos.freebill.confirm"], Text["common.yes"], Text["common.no"])) return;
                freeConfirmed = true;
            }
            var send = Sender.Send(new FinalizeBillCommand(bill) { FreeBillConfirmed = freeConfirmed });
            Result<FinalizedBill> result;
            try { result = await send.WaitAsync(FinalizeTimeout); }
            catch (TimeoutException)
            {
                // FIX(LOG-1, 2026-10-20, FIX-3): Flaw: the watchdog showed the BILL_SAVE_SLOW toast but logged
                // NOTHING — the VM had no ILogger and the pipeline logs only on completion, so a true hang (the
                // counter-freeze scenario this watchdog exists for) left zero evidence in the exported logs.
                // Fix: report + log Error with the bill id and correlation id, and watch the still-running save:
                // its late completion is logged at Information, a late fault is reported too. Why: a hang that
                // leaves no trace can never be diagnosed from a shopkeeper's log export. Logged in
                // docs/AGENT_FIX_LOG.md.
                var correlation = GlobalExceptions.Report(
                    new TimeoutException($"Finalize exceeded {FinalizeTimeout.TotalSeconds:0} s"), "finalize-watchdog");
                _log.LogError("Finalize watchdog fired for bill {BillId} (correlation {CorrelationId}); the save continues in the background",
                    bill.Id, correlation);
                _ = send.ContinueWith(t =>
                {
                    if (t.IsCompletedSuccessfully)
                        _log.LogInformation("Finalize for bill {BillId} completed after the watchdog fired (correlation {CorrelationId})", bill.Id, correlation);
                    else if (t.IsFaulted)
                        GlobalExceptions.Report(t.Exception!.GetBaseException(), "finalize-late");
                }, TaskScheduler.Default);
                // Still saving in the background. Paying again is safe: finalize returns the same bill if it saved.
                await Dialogs.ToastAsync(Text.Error(new Error(ErrorCodes.BillSaveSlow)));
                return;
            }
            if (result.IsFailure)
            {
                await ShowIfFailedAsync(result);
                if (result.Error.Code == ErrorCodes.FreeLimitReached) await Nav.GoToAsync(Routes.Subscription);
                if (result.Error.Code == ErrorCodes.BillChangedAfterSave)
                {
                    // T1-01: the old bill id is spent (saved at its old total). A fresh, empty bill id for the added items.
                    UseDraft(new BillDraft(shopHasGstin: _shop?.ChargesGst ?? false));
                    SetCustomer(null);
                    Refresh();
                }
                return;
            }
            var done = result.Value;
            UseDraft(new BillDraft(shopHasGstin: _shop?.ChargesGst ?? false));
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
