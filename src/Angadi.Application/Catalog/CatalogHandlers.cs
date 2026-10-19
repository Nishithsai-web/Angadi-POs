using System.Text.RegularExpressions;
using Angadi.Application.Billing;
using Angadi.Application.Common;
using Angadi.Application.Persistence;
using Angadi.Application.Platform;
using Angadi.Domain.Billing;
using Angadi.Domain.Customers;
using Angadi.Domain.Events;
using Angadi.Domain.Inventory;
using Angadi.Domain.Items;
using Angadi.Domain.Shop;

namespace Angadi.Application.Catalog;

// ---------- Validators ----------

internal sealed class SaveItemValidator : IValidator<SaveItemCommand>
{
    public Error? Validate(SaveItemCommand c)
    {
        var i = c.Item;
        if (string.IsNullOrWhiteSpace(i.TeluguName) && string.IsNullOrWhiteSpace(i.EnglishName))
            return new Error(ErrorCodes.ValidationFailed, "item needs a name");
        if (i.Price.Value < 0) return new Error(ErrorCodes.ValidationFailed, "negative price");
        if (i.LowStockThreshold.Milli < 0) return new Error(ErrorCodes.ValidationFailed, "negative threshold");
        if (c.OpeningStock is { Milli: < 0 }) return new Error(ErrorCodes.ValidationFailed, "negative opening stock");
        if (!Enum.IsDefined(i.GstSlab)) return new Error(ErrorCodes.ValidationFailed, "unknown GST slab");
        return null;
    }
}

internal sealed class SaveCustomerValidator : IValidator<SaveCustomerCommand>
{
    public Error? Validate(SaveCustomerCommand c)
    {
        if (string.IsNullOrWhiteSpace(c.Customer.Name)) return new Error(ErrorCodes.ValidationFailed, "customer needs a name");
        if (!string.IsNullOrWhiteSpace(c.Customer.Phone) && PhoneNumbers.Normalize(c.Customer.Phone) is null)
            return new Error(ErrorCodes.ValidationFailed, "phone must be a 10-digit Indian mobile");
        return null;
    }
}

internal sealed partial class SaveShopProfileValidator : IValidator<SaveShopProfileCommand>
{
    // 2-digit state code, 10-char PAN, entity digit, 'Z', checksum char.
    [GeneratedRegex("^[0-9]{2}[A-Z]{5}[0-9]{4}[A-Z][0-9A-Z]Z[0-9A-Z]$")]
    private static partial Regex Gstin();

    [GeneratedRegex(@"^[A-Za-z0-9.\-_]{2,256}@[A-Za-z]{2,64}$")]
    private static partial Regex Vpa();

    public Error? Validate(SaveShopProfileCommand c)
    {
        var p = c.Profile;
        if (string.IsNullOrWhiteSpace(p.Name)) return new Error(ErrorCodes.ValidationFailed, "shop needs a name");
        if (!Vpa().IsMatch(p.UpiId.Trim())) return new Error(ErrorCodes.ValidationFailed, "UPI ID looks wrong (name@bank)");
        if (!string.IsNullOrWhiteSpace(p.Gstin) && !Gstin().IsMatch(p.Gstin.Trim().ToUpperInvariant()))
            return new Error(ErrorCodes.ValidationFailed, "GSTIN must be 15 characters");
        return null;
    }
}

// ---------- Item handlers ----------

internal sealed class SaveItemHandler(IItemRepository items, IStockLedger ledger, IOutbox outbox, IClock clock)
    : IRequestHandler<SaveItemCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(SaveItemCommand command, CancellationToken ct)
    {
        var item = command.Item;
        item.Aliases = item.Aliases.Select(a => a.Trim()).Where(a => a.Length > 0).Distinct(StringComparer.Ordinal).ToList();

        if (!string.IsNullOrWhiteSpace(item.Barcode))
        {
            var other = await items.GetByBarcodeAsync(item.Barcode, ct);
            if (other is not null && other.Id != item.Id) return Result.Failure<Guid>(ErrorCodes.BarcodeDuplicate);
        }

        var isNew = await items.GetAsync(item.Id, ct) is null;
        await items.SaveAsync(item, ct);

        if (isNew && command.OpeningStock is { Milli: > 0 } opening)
            await ledger.AdjustAsync(item.Id, opening, StockMoveReason.Opening, ct: ct);

        await outbox.AppendAsync("Item", new ItemChanged(item.Id, clock.Now), ct);
        return Result.Success(item.Id);
    }
}

internal sealed class ItemQueries(IItemRepository items, IStockLedger ledger) :
    IRequestHandler<SearchItemsQuery, IReadOnlyList<Item>>,
    IRequestHandler<GetAllItemsQuery, IReadOnlyList<Item>>,
    IRequestHandler<GetQuickSaleItemsQuery, IReadOnlyList<Item>>,
    IRequestHandler<GetLowStockQuery, IReadOnlyList<Item>>,
    IRequestHandler<GetItemQuery, Item?>,
    IRequestHandler<GetItemByBarcodeQuery, Item?>,
    IRequestHandler<GetStockHistoryQuery, IReadOnlyList<StockMove>>
{
    public Task<IReadOnlyList<Item>> Handle(SearchItemsQuery q, CancellationToken ct) => items.SearchAsync(q.Text, q.Limit, ct);

    public async Task<IReadOnlyList<Item>> Handle(GetAllItemsQuery q, CancellationToken ct) =>
        (await items.GetActiveAsync(ct)).OrderBy(i => i.Category, StringComparer.Ordinal).ThenBy(i => i.TeluguName, StringComparer.Ordinal).ToList();

    public Task<IReadOnlyList<Item>> Handle(GetQuickSaleItemsQuery q, CancellationToken ct) => items.GetQuickSaleAsync(ct);
    public Task<IReadOnlyList<Item>> Handle(GetLowStockQuery q, CancellationToken ct) => items.GetLowStockAsync(ct);
    public Task<Item?> Handle(GetItemQuery q, CancellationToken ct) => items.GetAsync(q.ItemId, ct);
    public Task<Item?> Handle(GetItemByBarcodeQuery q, CancellationToken ct) => items.GetByBarcodeAsync(q.Barcode, ct);
    public Task<IReadOnlyList<StockMove>> Handle(GetStockHistoryQuery q, CancellationToken ct) => ledger.GetHistoryAsync(q.ItemId, ct);
}

// ---------- Customer handlers ----------

internal sealed class SaveCustomerHandler(ICustomerRepository customers) : IRequestHandler<SaveCustomerCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(SaveCustomerCommand command, CancellationToken ct)
    {
        var c = command.Customer;
        c.Name = c.Name.Trim();
        c.Phone = PhoneNumbers.Normalize(c.Phone);
        await customers.SaveAsync(c, ct);
        return Result.Success(c.Id);
    }
}

internal sealed class CustomerQueries(ICustomerRepository customers) :
    IRequestHandler<SearchCustomersQuery, IReadOnlyList<Customer>>,
    IRequestHandler<GetCustomerQuery, Customer?>,
    IRequestHandler<GetCustomerLedgerQuery, IReadOnlyList<DuesEntry>>
{
    public Task<IReadOnlyList<Customer>> Handle(SearchCustomersQuery q, CancellationToken ct) => customers.SearchAsync(q.Text, ct);
    public Task<Customer?> Handle(GetCustomerQuery q, CancellationToken ct) => customers.GetAsync(q.CustomerId, ct);
    public Task<IReadOnlyList<DuesEntry>> Handle(GetCustomerLedgerQuery q, CancellationToken ct) => customers.GetLedgerAsync(q.CustomerId, ct);
}

// ---------- Shop, seed, bills, backup ----------

internal sealed class ShopHandlers(IShopProfileRepository shop, ISeedService seed, IBillRepository bills, IBackupService backup, ILocalizer text) :
    IRequestHandler<GetShopProfileQuery, ShopProfile?>,
    IRequestHandler<SaveShopProfileCommand, Result>,
    IRequestHandler<SeedItemsCommand, Result<int>>,
    IRequestHandler<GetBillQuery, Bill?>,
    IRequestHandler<ExportBackupCommand, Result<string>>,
    IRequestHandler<RestoreBackupCommand, Result>
{
    public Task<ShopProfile?> Handle(GetShopProfileQuery q, CancellationToken ct) => shop.GetAsync(ct);

    public async Task<Result> Handle(SaveShopProfileCommand command, CancellationToken ct)
    {
        await shop.SaveAsync(command.Profile, ct);
        text.Language = command.Profile.Language;
        return Result.Success();
    }

    public async Task<Result<int>> Handle(SeedItemsCommand command, CancellationToken ct) =>
        Result.Success(await seed.SeedAsync(command.Kirana, command.Pharmacy, ct));

    public Task<Bill?> Handle(GetBillQuery q, CancellationToken ct) => bills.GetAsync(q.BillId, ct);
    public Task<Result<string>> Handle(ExportBackupCommand command, CancellationToken ct) => backup.ExportAsync(ct);
    public Task<Result> Handle(RestoreBackupCommand command, CancellationToken ct) => backup.RestoreAsync(command.FilePath, ct);
}
