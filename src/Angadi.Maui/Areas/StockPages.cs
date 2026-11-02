using System.Globalization;
using Angadi.Application.Catalog;
using Angadi.Application.Common;
using Angadi.Application.Platform;
using Angadi.Application.Stock;
using Angadi.Domain.Common;
using Angadi.Domain.Inventory;
using Angadi.Domain.Items;
using Angadi.Domain.Purchasing;
using Angadi.Maui.Areas.Pos;
using Angadi.Maui.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Angadi.Maui.Areas;

// v2-stock: suppliers, purchase entry, purchase orders, expiry list and the purchases & stock report.
// Code-built pages like the rest; every handler goes through SafeUi; every text comes from strings.inventory2.*.json.

public static class StockRoutes
{
    public const string Hub = "stock-hub";
    public const string Suppliers = "suppliers";
    public const string PurchaseNew = "purchase-new";      // ?orderId= (Receive pre-fills from the order)
    public const string Purchases = "purchases";
    public const string Purchase = "purchase";             // ?purchaseId=
    public const string Orders = "orders";
    public const string OrderEdit = "order-edit";          // ?orderId= (absent = new)
    public const string Expiring = "expiring";
    public const string Report = "stock-report";

    public static void Register(IServiceCollection s)
    {
        PagesRegistration.Route<StockHubPage>(s, Hub);
        PagesRegistration.Route<SuppliersPage>(s, Suppliers);
        PagesRegistration.Route<PurchaseEditPage>(s, PurchaseNew);
        PagesRegistration.Route<PurchasesPage>(s, Purchases);
        PagesRegistration.Route<PurchaseDetailPage>(s, Purchase);
        PagesRegistration.Route<OrdersPage>(s, Orders);
        PagesRegistration.Route<OrderEditPage>(s, OrderEdit);
        PagesRegistration.Route<ExpiringPage>(s, Expiring);
        PagesRegistration.Route<StockReportPage>(s, Report);
    }

    internal static Dictionary<string, object> Arg(string key, Guid id) => new() { [key] = id.ToString("D") };
}

/// <summary>Shared shape: a scrolling body rebuilt on every appearance, loaded through SafeUi.</summary>
public abstract class StockPageBase : ContentPage
{
    protected ISender Sender { get; }
    protected ILocalizer Text { get; }
    protected IDialogService Dialogs { get; }
    protected VerticalStackLayout Body { get; } = new() { Spacing = 10, Padding = new Thickness(16) };

    protected StockPageBase(ISender sender, ILocalizer text, IDialogService dialogs, string titleKey)
    {
        Sender = sender;
        Text = text;
        Dialogs = dialogs;
        Title = text[titleKey];
        Content = new ScrollView { Content = Body };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await SafeUi.Run(LoadAsync);
    }

    protected abstract Task LoadAsync();

    protected static string Dmy(DateOnly d) => d.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
    protected static string Exp(DateOnly? d) => d?.ToString("MM/yy", CultureInfo.InvariantCulture) ?? "";
    protected static string Count(int n) => n.ToString(CultureInfo.InvariantCulture);
    protected string QtyText(Qty q, Unit unit) => $"{IndianFormat.Quantity(q)} {Text.UnitLabel(unit)}";

    protected Button Btn(string key, Func<Task> work, bool big = false, bool primary = true)
    {
        var b = primary ? Ui.Button(Text[key], big: big) : Ui.Secondary(Text[key]);
        b.MinimumHeightRequest = Math.Max(b.MinimumHeightRequest, 48);
        b.Clicked += SafeUi.On(work);
        return b;
    }

    /// <summary>A label/value row, tappable when <paramref name="tap"/> is given (≥ 48 dp).</summary>
    protected static View Row(string label, string value, Func<Task>? tap = null, Color? color = null)
    {
        var g = new Grid { MinimumHeightRequest = 48, ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto) }, ColumnSpacing = 8 };
        var l = Ui.Body(label);
        l.VerticalTextAlignment = TextAlignment.Center;
        if (color is not null) l.TextColor = color;
        g.Add(l, 0, 0);
        g.Add(new Label { Text = value, FontFamily = Theme.FontBold, HorizontalTextAlignment = TextAlignment.End, VerticalTextAlignment = TextAlignment.Center }, 1, 0);
        if (tap is not null)
        {
            var t = new TapGestureRecognizer();
            t.Tapped += SafeUi.On<TappedEventArgs>(_ => tap());
            g.GestureRecognizers.Add(t);
        }
        return g;
    }

    protected View Card(string title, IEnumerable<View> rows)
    {
        var stack = new VerticalStackLayout { Spacing = 2, Children = { new Label { Text = title, FontFamily = Theme.FontBold, FontSize = 18 } } };
        var any = false;
        foreach (var r in rows) { stack.Add(r); any = true; }
        if (!any) stack.Add(Ui.Caption(Text["common.empty"]));
        return Ui.Card(stack);
    }

    protected async Task<bool> FailedAsync(Result r)
    {
        if (r.IsSuccess) return false;
        await Dialogs.ToastAsync(Text.Error(r.Error));
        return true;
    }

    protected Task<string?> AskAsync(string titleKey, string messageKey, string? initial = null, Keyboard? keyboard = null) =>
        Dialogs.PromptAsync(Text[titleKey], Text[messageKey], Text["common.ok"], Text["common.cancel"], initial, keyboard);

    /// <summary>Search → pick one active item (action sheet), or null.</summary>
    protected async Task<Item?> PickItemAsync()
    {
        var q = await AskAsync("inv2.item.pick", "inv2.item.search");
        if (string.IsNullOrWhiteSpace(q)) return null;
        var found = await Sender.Send(new SearchItemsQuery(q.Trim(), 12));
        if (found.Count == 0) { await Dialogs.ToastAsync(Text["inv2.item.none"]); return null; }
        var labels = ChoiceLabels.Numbered(found.Select(i => $"{Text.Name(i)} · {Text.UnitLabel(i.Unit)}"));
        var pick = await Dialogs.ChooseAsync(Text["inv2.item.pick"], Text["common.cancel"], [.. labels]);
        return pick is not null && labels.IndexOf(pick) is var at and >= 0 ? found[at] : null;
    }

    /// <summary>Choose a supplier, or add a new one on the spot.</summary>
    protected async Task<Supplier?> PickSupplierAsync()
    {
        var all = await Sender.Send(new ListSuppliersQuery());
        var labels = ChoiceLabels.Numbered(all.Select(s => s.Name));
        var add = "+ " + Text["inv2.supplier.add"];
        var pick = await Dialogs.ChooseAsync(Text["inv2.supplier.pick"], Text["common.cancel"], [.. labels, add]);
        if (pick is null) return null;
        if (pick != add) return labels.IndexOf(pick) is var at and >= 0 ? all[at] : null;
        return await SupplierPrompts.EditAsync(this, new Supplier());
    }

    internal static class SupplierPrompts
    {
        /// <summary>Name, phone, GSTIN, notes as four short prompts; cancelling the name cancels the whole edit.</summary>
        public static async Task<Supplier?> EditAsync(StockPageBase page, Supplier s)
        {
            var name = await page.AskAsync("inv2.supplier.title", "inv2.supplier.name", s.Name);
            if (string.IsNullOrWhiteSpace(name)) return null;
            s.Name = name;
            s.Phone = await page.AskAsync("inv2.supplier.title", "inv2.supplier.phone", s.Phone, Keyboard.Telephone);
            s.Gstin = await page.AskAsync("inv2.supplier.title", "inv2.supplier.gstin", s.Gstin);
            s.Notes = await page.AskAsync("inv2.supplier.title", "inv2.supplier.notes", s.Notes);
            var r = await page.Sender.Send(new SaveSupplierCommand(s));
            return await page.FailedAsync(r) ? null : s;
        }
    }
}

/// <summary>Entry point from the Items tab: everything about buying and expiry in one place.</summary>
public sealed class StockHubPage(ISender sender, ILocalizer text, IDialogService dialogs) : StockPageBase(sender, text, dialogs, "inv2.hub")
{
    protected override async Task LoadAsync()
    {
        var expiring = await Sender.Send(new GetExpiringQuery());
        Body.Children.Clear();
        if (expiring.HasAlerts)
            Body.Add(new Label { Text = Text.Format("inv2.expiry.banner", expiring.Expired.Count, expiring.Within30.Count), TextColor = Theme.Danger, FontFamily = Theme.FontBold });
        Body.Add(Btn("inv2.purchase.new", () => Shell.Current.GoToAsync(StockRoutes.PurchaseNew), big: true));
        Body.Add(Btn("inv2.purchases", () => Shell.Current.GoToAsync(StockRoutes.Purchases), primary: false));
        Body.Add(Btn("inv2.orders", () => Shell.Current.GoToAsync(StockRoutes.Orders), primary: false));
        Body.Add(Btn("inv2.suppliers", () => Shell.Current.GoToAsync(StockRoutes.Suppliers), primary: false));
        Body.Add(Btn("inv2.expiring", () => Shell.Current.GoToAsync(StockRoutes.Expiring), primary: false));
        // RBAC-1: cost, stock value and margin are for a manager or the owner, not the counter.
        Body.Add(Btn("inv2.report", async () => { if (await PinGate.CheckAsync(Angadi.Application.Security.Permission.ViewCosts, Dialogs, Text)) await Shell.Current.GoToAsync(StockRoutes.Report); }, primary: false));
    }
}

public sealed class SuppliersPage(ISender sender, ILocalizer text, IDialogService dialogs) : StockPageBase(sender, text, dialogs, "inv2.suppliers")
{
    protected override async Task LoadAsync()
    {
        var all = await Sender.Send(new ListSuppliersQuery());
        Body.Children.Clear();
        Body.Add(Btn("inv2.supplier.add", async () => { if (await SupplierPrompts.EditAsync(this, new Supplier()) is not null) await LoadAsync(); }));
        Body.Add(Card(Text["inv2.suppliers"], all.Select(s => Row(s.Name, ChoiceLabels.PhoneTail(s.Phone) ?? "", async () =>
        {
            if (await SupplierPrompts.EditAsync(this, s) is not null) await LoadAsync();
        }))));
    }
}

/// <summary>A purchase line being typed in (before it is saved).</summary>
internal sealed class DraftLine(Item item, Qty qty, Paise cost, GstSlab slab, string? batchNo, DateOnly? expiry)
{
    public Item Item { get; } = item;
    public Qty Qty { get; set; } = qty;
    public Paise Cost { get; set; } = cost;
    public GstSlab Slab { get; set; } = slab;
    public string? BatchNo { get; set; } = batchNo;
    public DateOnly? Expiry { get; set; } = expiry;
}

/// <summary>Enter a supplier's bill: lines into stock (batches for tracked items) in one save. Saved purchases are final.</summary>
public sealed class PurchaseEditPage : StockPageBase, IQueryAttributable
{
    private readonly Entry _billNo;
    private readonly DatePicker _date = new() { Format = "dd/MM/yyyy" };
    private readonly List<DraftLine> _lines = [];
    private Supplier? _supplier;
    private Guid? _orderId;
    private bool _loaded;

    public PurchaseEditPage(ISender sender, ILocalizer text, IDialogService dialogs, IClock clock) : base(sender, text, dialogs, "inv2.purchase.new")
    {
        _billNo = Ui.Entry(text["inv2.purchase.billno"]);
        _date.Date = clock.Today.ToDateTime(TimeOnly.MinValue);
    }

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("orderId", out var id) && Guid.TryParse(id?.ToString(), out var orderId)) _orderId = orderId;
    }

    protected override async Task LoadAsync()
    {
        if (!_loaded && _orderId is { } orderId)
        {
            _loaded = true;
            var prefill = await Sender.Send(new PrefillPurchaseFromOrderQuery(orderId));
            if (await FailedAsync(prefill)) { _orderId = null; }
            else
            {
                _supplier = (await Sender.Send(new ListSuppliersQuery())).FirstOrDefault(s => s.Id == prefill.Value.SupplierId);
                foreach (var l in prefill.Value.Lines)
                    if (await Sender.Send(new GetItemQuery(l.ItemId)) is { } item)
                        _lines.Add(new DraftLine(item, l.Qty, l.CostPrice, l.GstSlab, null, null));
            }
        }
        _loaded = true;
        Render();
    }

    private void Render()
    {
        Body.Children.Clear();
        Body.Add(Row(Text["inv2.supplier"], _supplier?.Name ?? Text["inv2.supplier.choose"], async () =>
        {
            if (await PickSupplierAsync() is { } s) { _supplier = s; Render(); }
        }));
        Body.Add(Ui.Caption(Text["inv2.purchase.billno"]));
        Body.Add(_billNo);
        Body.Add(Ui.Caption(Text["inv2.purchase.date"]));
        Body.Add(_date);
        long total = 0;
        var rows = new List<View>();
        foreach (var l in _lines)
        {
            var lineTotal = PurchaseLine.Total(l.Qty, l.Cost, l.Slab);
            total += lineTotal.Value;
            var detail = $"{Text.Name(l.Item)} · {QtyText(l.Qty, l.Item.Unit)} × {IndianFormat.Rupees(l.Cost)} · GST {(int)l.Slab / 100}%";
            if (l.Item.TracksBatches)
                detail += l.BatchNo is null || l.Expiry is null ? " · " + Text["inv2.batch.missing"] : $" · {l.BatchNo} · {Text["inv2.batch.exp"]} {Exp(l.Expiry)}";
            var line = l;
            rows.Add(Row(detail, IndianFormat.Rupees(lineTotal), () => EditLineAsync(line), l.Item.TracksBatches && l.BatchNo is null ? Theme.Danger : null));
        }
        Body.Add(Card(Text["inv2.purchase.lines"], rows));
        Body.Add(Row(Text["inv2.purchase.total"], IndianFormat.Rupees(new Paise(total))));
        Body.Add(Btn("inv2.purchase.additem", AddLineAsync, primary: false));
        Body.Add(Btn("inv2.purchase.save", SaveAsync, big: true));
        Body.Add(Ui.Caption(Text["inv2.purchase.final"]));
    }

    private async Task AddLineAsync()
    {
        if (await PickItemAsync() is not { } item) return;
        var line = new DraftLine(item, Qty.Zero, item.Cost, item.GstSlab, null, null);
        if (await AskLineAsync(line)) { _lines.Add(line); Render(); }
    }

    private async Task EditLineAsync(DraftLine line)
    {
        var edit = Text["inv2.line.edit"];
        var remove = Text["inv2.line.remove"];
        var pick = await Dialogs.ChooseAsync(Text.Name(line.Item), Text["common.cancel"], edit, remove);
        if (pick == remove) _lines.Remove(line);
        else if (pick == edit) await AskLineAsync(line);
        Render();
    }

    /// <summary>Quantity, cost before GST, GST slab, and batch + expiry for tracked items. False when cancelled.</summary>
    private async Task<bool> AskLineAsync(DraftLine line)
    {
        var qtyText = await AskAsync("inv2.purchase.additem", "inv2.line.qty", line.Qty.Milli > 0 ? IndianFormat.Quantity(line.Qty) : null, Keyboard.Numeric);
        if (qtyText is null) return false;
        if (!PosInput.TryParseQty(qtyText, out var qty) || qty.Milli <= 0) { await Dialogs.ToastAsync(Text.Error(new Error(ErrorCodes.PurchaseLineInvalid))); return false; }
        var costText = await AskAsync("inv2.purchase.additem", "inv2.line.cost",
            line.Cost.Value > 0 ? (line.Cost.Value / 100m).ToString("0.##", CultureInfo.InvariantCulture) : null, Keyboard.Numeric);
        if (costText is null) return false;
        if (!PosInput.TryParseRupees(costText, out var cost)) { await Dialogs.ToastAsync(Text.Error(new Error(ErrorCodes.PurchaseLineInvalid))); return false; }
        var slabs = GstSlabs.Current.OrderBy(s => s == line.Slab ? 0 : 1).ToList();
        var slabLabels = slabs.Select(s => $"GST {(int)s / 100}%").ToArray();
        var slabPick = await Dialogs.ChooseAsync(Text["inv2.line.gst"], Text["common.cancel"], slabLabels);
        if (slabPick is null) return false;
        line.Slab = slabs[Array.IndexOf(slabLabels, slabPick)];
        line.Qty = qty;
        line.Cost = cost;
        if (line.Item.TracksBatches)
        {
            var batch = await AskAsync("inv2.batch.title", "inv2.batch.no", line.BatchNo);
            if (string.IsNullOrWhiteSpace(batch)) return false;
            var expiryText = await AskAsync("inv2.batch.title", "inv2.batch.expiry", line.Expiry?.ToString("MM/yy", CultureInfo.InvariantCulture), Keyboard.Numeric);
            if (BatchAllocator.ParseExpiry(expiryText) is not { } expiry)
            {
                await Dialogs.ToastAsync(Text.Error(Error.With(ErrorCodes.BatchDetailsRequired, Text.Name(line.Item))));
                return false;
            }
            line.BatchNo = batch.Trim().ToUpperInvariant();
            line.Expiry = expiry;
        }
        return true;
    }

    private async Task SaveAsync()
    {
        if (_supplier is null) { await Dialogs.ToastAsync(Text["inv2.supplier.choose"]); return; }
        var entry = new PurchaseEntry(_supplier.Id, _billNo.Text ?? "", DateOnly.FromDateTime((DateTime?)_date.Date ?? DateTime.Today),
            _lines.Select(l => new PurchaseEntryLine(l.Item.Id, l.Qty, l.Cost, l.Slab, l.BatchNo, l.Expiry)).ToList(), _orderId);
        var r = await Sender.Send(new RecordPurchaseCommand(entry));
        if (await FailedAsync(r)) return;
        await Dialogs.ToastAsync(Text["inv2.purchase.saved"]);
        await Shell.Current.GoToAsync("..");
    }
}

/// <summary>This month's purchases: totals by supplier, then each supplier bill (tap for its lines).</summary>
public sealed class PurchasesPage(ISender sender, ILocalizer text, IDialogService dialogs, IClock clock) : StockPageBase(sender, text, dialogs, "inv2.purchases")
{
    protected override async Task LoadAsync()
    {
        var today = clock.Today;
        var from = new DateOnly(today.Year, today.Month, 1);
        var bySupplier = await Sender.Send(new GetPurchasesBySupplierQuery(from, today));
        var list = await Sender.Send(new ListPurchasesQuery(from, today));
        Body.Children.Clear();
        Body.Add(Card(Text["inv2.report.bysupplier"], bySupplier.Select(s => Row($"{s.SupplierName} · {Text.Format("inv2.bills", s.Bills)}", IndianFormat.Rupees(s.Total)))));
        Body.Add(Card(Text["inv2.purchases.month"], list.Select(p => Row($"{Dmy(p.BillDate)} · {p.SupplierName} #{p.SupplierBillNo}", IndianFormat.Rupees(p.Total),
            () => Shell.Current.GoToAsync(StockRoutes.Purchase, StockRoutes.Arg("purchaseId", p.Id))))));
    }
}

/// <summary>One saved purchase. It cannot be edited; the owner corrects a wrong quantity with a linked stock correction.</summary>
public sealed class PurchaseDetailPage(ISender sender, ILocalizer text, IDialogService dialogs)
    : StockPageBase(sender, text, dialogs, "inv2.purchase"), IQueryAttributable
{
    private Guid _id;

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("purchaseId", out var id) && Guid.TryParse(id?.ToString(), out var pid)) _id = pid;
    }

    protected override async Task LoadAsync()
    {
        Body.Children.Clear();
        if (await Sender.Send(new GetPurchaseQuery(_id)) is not { } p) return;
        Body.Add(Ui.Title($"{p.SupplierName} #{p.SupplierBillNo}"));
        Body.Add(Ui.Caption(Dmy(p.BillDate)));
        Body.Add(Card(Text["inv2.purchase.lines"], p.Lines.Select(l => Row(LineText(l), IndianFormat.Rupees(l.LineTotal)))));
        Body.Add(Row(Text["inv2.purchase.total"], IndianFormat.Rupees(p.Total)));
        Body.Add(Ui.Caption(Text["inv2.purchase.final"]));
        Body.Add(Btn("inv2.purchase.correct", () => CorrectAsync(p), primary: false));
    }

    private string LineText(PurchaseLine l)
    {
        var name = ItemNames.Pick(Text.Language, l.TeluguName, l.EnglishName);
        var s = $"{name} · {QtyText(l.Qty, l.Unit)} × {IndianFormat.Rupees(l.CostPrice)} · GST {(int)l.GstSlab / 100}%";
        return l.BatchNo is null ? s : $"{s} · {l.BatchNo} · {Text["inv2.batch.exp"]} {Exp(l.Expiry)}";
    }

    private async Task CorrectAsync(Purchase p)
    {
        if (!await PinGate.CheckAsync(Angadi.Application.Security.Permission.Purchases, Dialogs, Text)) return;
        var labels = ChoiceLabels.Numbered(p.Lines.Select(LineText));
        var pick = await Dialogs.ChooseAsync(Text["inv2.purchase.correct"], Text["common.cancel"], [.. labels]);
        var at = pick is null ? -1 : labels.IndexOf(pick);
        if (at < 0) return;
        var qtyText = await AskAsync("inv2.purchase.correct", "inv2.purchase.correct.qty", keyboard: Keyboard.Numeric);
        if (qtyText is null || !PosInput.TryParseQty(qtyText, out var qty) || qty.Milli <= 0) return;
        var r = await Sender.Send(new CorrectPurchaseLineCommand(p.Id, p.Lines[at].Id, qty));
        if (await FailedAsync(r)) return;
        await Dialogs.ToastAsync(Text["inv2.purchase.corrected"]);
    }
}

public sealed class OrdersPage(ISender sender, ILocalizer text, IDialogService dialogs) : StockPageBase(sender, text, dialogs, "inv2.orders")
{
    protected override async Task LoadAsync()
    {
        var orders = await Sender.Send(new ListPurchaseOrdersQuery());
        var names = (await Sender.Send(new ListSuppliersQuery())).ToDictionary(s => s.Id, s => s.Name);
        Body.Children.Clear();
        Body.Add(Btn("inv2.order.new", () => Shell.Current.GoToAsync(StockRoutes.OrderEdit)));
        Body.Add(Card(Text["inv2.orders"], orders.Select(o => Row(
            $"{(names.TryGetValue(o.SupplierId, out var n) ? n : "?")} · {Text.Format("inv2.order.items", o.Lines.Count)}",
            Text["inv2.order.status." + o.Status],
            () => Shell.Current.GoToAsync(StockRoutes.OrderEdit, StockRoutes.Arg("orderId", o.Id))))));
    }
}

/// <summary>Draft an order (or suggest it from low stock), share it as text (→ Sent), and Receive it into a purchase.</summary>
public sealed class OrderEditPage(ISender sender, ILocalizer text, IDialogService dialogs)
    : StockPageBase(sender, text, dialogs, "inv2.order"), IQueryAttributable
{
    private PurchaseOrder _order = new();
    private Guid? _loadId;
    private Supplier? _supplier;
    private readonly Dictionary<Guid, Item> _items = [];

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("orderId", out var id) && Guid.TryParse(id?.ToString(), out var oid)) _loadId = oid;
    }

    protected override async Task LoadAsync()
    {
        if (_loadId is { } id)
        {
            _loadId = null;
            if (await Sender.Send(new GetPurchaseOrderQuery(id)) is { } o)
            {
                _order = o;
                _supplier = (await Sender.Send(new ListSuppliersQuery())).FirstOrDefault(s => s.Id == o.SupplierId);
            }
        }
        else if (_order.Status != PurchaseOrderStatus.Draft && await Sender.Send(new GetPurchaseOrderQuery(_order.Id)) is { } fresh)
            _order = fresh;   // back from Receive: show it as received
        foreach (var l in _order.Lines.Where(l => !_items.ContainsKey(l.ItemId)))
            if (await Sender.Send(new GetItemQuery(l.ItemId)) is { } item) _items[item.Id] = item;
        Render();
    }

    private bool Editable => _order.Status == PurchaseOrderStatus.Draft;

    private void Render()
    {
        Body.Children.Clear();
        Body.Add(Row(Text["inv2.supplier"], _supplier?.Name ?? Text["inv2.supplier.choose"], Editable ? async () =>
        {
            if (await PickSupplierAsync() is { } s) { _supplier = s; Render(); }
        } : null));
        Body.Add(Row(Text["inv2.order.status"], Text["inv2.order.status." + _order.Status]));
        Body.Add(Card(Text["inv2.purchase.lines"], _order.Lines.Select(l =>
        {
            var name = _items.TryGetValue(l.ItemId, out var i) ? Text.Name(i) : "?";
            var unit = i?.Unit ?? Unit.Piece;
            var line = l;
            return Row(name, QtyText(l.Qty, unit), Editable ? () => EditLineAsync(line) : null);
        })));
        if (Editable)
        {
            Body.Add(Btn("inv2.order.suggest", SuggestAsync, primary: false));
            Body.Add(Btn("inv2.purchase.additem", AddLineAsync, primary: false));
            Body.Add(Btn("inv2.order.save", async () => { if (await SaveAsync()) await Dialogs.ToastAsync(Text["inv2.order.saved"]); }));
        }
        if (_order.Status != PurchaseOrderStatus.Received)
        {
            Body.Add(Btn("inv2.order.share", ShareAsync, primary: false));
            Body.Add(Btn("inv2.order.receive", ReceiveAsync, big: true));
        }
    }

    private async Task SuggestAsync()
    {
        var suggested = await Sender.Send(new SuggestOrderFromLowStockQuery());
        if (suggested.Count == 0) { await Dialogs.ToastAsync(Text["inv2.order.suggest.none"]); return; }
        foreach (var s in suggested.Where(s => _order.Lines.All(l => l.ItemId != s.ItemId)))
        {
            _order.Lines.Add(s);
            if (await Sender.Send(new GetItemQuery(s.ItemId)) is { } item) _items[item.Id] = item;
        }
        Render();
    }

    private async Task AddLineAsync()
    {
        if (await PickItemAsync() is not { } item) return;
        var qtyText = await AskAsync("inv2.purchase.additem", "inv2.line.qty", keyboard: Keyboard.Numeric);
        if (qtyText is null || !PosInput.TryParseQty(qtyText, out var qty) || qty.Milli <= 0) return;
        _items[item.Id] = item;
        _order.Lines.Add(new PurchaseOrderLine(item.Id, qty));
        Render();
    }

    private async Task EditLineAsync(PurchaseOrderLine line)
    {
        var edit = Text["inv2.line.edit"];
        var remove = Text["inv2.line.remove"];
        var pick = await Dialogs.ChooseAsync(_items.TryGetValue(line.ItemId, out var i) ? Text.Name(i) : "?", Text["common.cancel"], edit, remove);
        var at = _order.Lines.IndexOf(line);
        if (at < 0) return;
        if (pick == remove) _order.Lines.RemoveAt(at);
        else if (pick == edit)
        {
            var qtyText = await AskAsync("inv2.line.edit", "inv2.line.qty", IndianFormat.Quantity(line.Qty), Keyboard.Numeric);
            if (qtyText is not null && PosInput.TryParseQty(qtyText, out var qty) && qty.Milli > 0) _order.Lines[at] = line with { Qty = qty };
        }
        Render();
    }

    private async Task<bool> SaveAsync()
    {
        if (!Editable) return true;
        if (_supplier is null) { await Dialogs.ToastAsync(Text["inv2.supplier.choose"]); return false; }
        _order.SupplierId = _supplier.Id;
        var r = await Sender.Send(new SavePurchaseOrderCommand(_order));
        return !await FailedAsync(r);
    }

    private async Task ShareAsync()
    {
        if (!await SaveAsync()) return;
        var r = await Sender.Send(new SharePurchaseOrderCommand(_order.Id));
        if (await FailedAsync(r)) return;
        if (await Sender.Send(new GetPurchaseOrderQuery(_order.Id)) is { } fresh) _order = fresh;
        Render();
    }

    private async Task ReceiveAsync()
    {
        if (!await SaveAsync()) return;
        await Shell.Current.GoToAsync(StockRoutes.PurchaseNew, StockRoutes.Arg("orderId", _order.Id));
    }
}

/// <summary>Batches that are expired or expire within 90 days, with their value at cost.</summary>
public sealed class ExpiringPage(ISender sender, ILocalizer text, IDialogService dialogs) : StockPageBase(sender, text, dialogs, "inv2.expiring")
{
    protected override async Task LoadAsync()
    {
        var r = await Sender.Send(new GetExpiringQuery());
        Body.Children.Clear();
        Body.Add(Card(Text["inv2.expiring"],
        [
            Row(Text.Format("inv2.expiry.expired", r.Expired.Count), IndianFormat.Rupees(r.Expired.Value), color: r.Expired.Count > 0 ? Theme.Danger : null),
            Row(Text.Format("inv2.expiry.within", 30, r.Within30.Count), IndianFormat.Rupees(r.Within30.Value)),
            Row(Text.Format("inv2.expiry.within", 60, r.Within60.Count), IndianFormat.Rupees(r.Within60.Value)),
            Row(Text.Format("inv2.expiry.within", 90, r.Within90.Count), IndianFormat.Rupees(r.Within90.Value)),
        ]));
        Body.Add(Card(Text["inv2.expiry.batches"], r.Lines.Select(l => Row(
            $"{l.Name} · {l.BatchNo} · {Text["inv2.batch.exp"]} {Dmy(l.Expiry)} · {QtyText(l.Qty, l.Unit)}"
                + (l.DaysLeft < 0 ? " · " + Text["inv2.expiry.expired.tag"] : " · " + Text.Format("inv2.expiry.days", l.DaysLeft)),
            IndianFormat.Rupees(l.Value), color: l.DaysLeft < 0 ? Theme.Danger : l.DaysLeft <= 30 ? Theme.Warning : null))));
    }
}

/// <summary>Purchases this month by supplier, stock at cost, and an estimated gross margin (clearly labelled).</summary>
public sealed class StockReportPage(ISender sender, ILocalizer text, IDialogService dialogs, IClock clock) : StockPageBase(sender, text, dialogs, "inv2.report")
{
    protected override async Task LoadAsync()
    {
        var today = clock.Today;
        var from = new DateOnly(today.Year, today.Month, 1);
        var bySupplier = await Sender.Send(new GetPurchasesBySupplierQuery(from, today));
        var valuation = await Sender.Send(new GetStockValuationQuery());
        var margin = await Sender.Send(new GetMarginEstimateQuery(from, today));
        var expiring = await Sender.Send(new GetExpiringQuery());
        Body.Children.Clear();
        Body.Add(Card(Text["inv2.report.bysupplier"], bySupplier.Select(s => Row($"{s.SupplierName} · {Text.Format("inv2.bills", s.Bills)}", IndianFormat.Rupees(s.Total)))
            .Append(Row(Text["inv2.purchase.total"], IndianFormat.Rupees(new Paise(bySupplier.Sum(s => s.Total.Value)))))));
        var valueRows = new List<View>
        {
            Row(Text["inv2.report.atcost"], IndianFormat.Rupees(valuation.AtCost)),
            Row(Text.Format("inv2.report.valued", valuation.ItemsValued), ""),
        };
        if (valuation.ItemsWithoutCost > 0) valueRows.Add(Row(Text.Format("inv2.report.nocost", valuation.ItemsWithoutCost), "", color: Theme.Warning));
        valueRows.Add(valuation.BatchMismatches == 0
            ? Row(Text["inv2.report.batches.ok"], "")
            : Row(Text.Format("inv2.report.batches.bad", valuation.BatchMismatches), "", color: Theme.Danger));
        Body.Add(Card(Text["inv2.report.value"], valueRows));
        Body.Add(Card(Text["inv2.report.margin"],
        [
            Row(Text["inv2.report.margin.sales"], IndianFormat.Rupees(margin.SalesBeforeGst)),
            Row(Text["inv2.report.margin.cost"], IndianFormat.Rupees(margin.Cost)),
            Row(Text["inv2.report.margin.est"], $"{IndianFormat.Rupees(margin.Margin)} ({(margin.MarginBasisPoints / 100m).ToString("0.#", CultureInfo.InvariantCulture)}%)"),
            Row(Text.Format("inv2.report.margin.nocost", margin.LinesWithoutCost), ""),
        ]));
        Body.Add(Ui.Caption(Text["inv2.report.margin.note"]));
        Body.Add(Card(Text["inv2.expiring"],
        [
            Row(Text.Format("inv2.expiry.expired", expiring.Expired.Count), IndianFormat.Rupees(expiring.Expired.Value)),
            Row(Text.Format("inv2.expiry.within", 90, expiring.Within90.Count), IndianFormat.Rupees(expiring.Within90.Value)),
        ]));
    }
}
