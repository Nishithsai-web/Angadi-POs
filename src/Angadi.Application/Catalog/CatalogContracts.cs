using Angadi.Application.Common;
using Angadi.Domain.Billing;
using Angadi.Domain.Common;
using Angadi.Domain.Customers;
using Angadi.Domain.Inventory;
using Angadi.Domain.Items;
using Angadi.Domain.Shop;

namespace Angadi.Application.Catalog;

// Items (FR-03/05/08/10/25/26)
public sealed record SearchItemsQuery(string Text, int Limit = 30) : IQuery<IReadOnlyList<Item>>;
public sealed record GetAllItemsQuery : IQuery<IReadOnlyList<Item>>;
public sealed record GetQuickSaleItemsQuery : IQuery<IReadOnlyList<Item>>;
public sealed record GetLowStockQuery : IQuery<IReadOnlyList<Item>>;
public sealed record GetItemQuery(Guid ItemId) : IQuery<Item?>;
public sealed record GetItemByBarcodeQuery(string Barcode) : IQuery<Item?>;
public sealed record GetStockHistoryQuery(Guid ItemId) : IQuery<IReadOnlyList<StockMove>>;

/// <summary>FR-03. New items may carry an opening stock, written to the ledger.</summary>
public sealed record SaveItemCommand(Item Item, Qty? OpeningStock = null) : ICommand<Result<Guid>>;

// Customers (FR-21/22)
public sealed record SearchCustomersQuery(string Text) : IQuery<IReadOnlyList<Customer>>;
public sealed record GetCustomerQuery(Guid CustomerId) : IQuery<Customer?>;
public sealed record GetCustomerLedgerQuery(Guid CustomerId) : IQuery<IReadOnlyList<DuesEntry>>;
public sealed record SaveCustomerCommand(Customer Customer) : ICommand<Result<Guid>>;

// Shop (FR-01)
public sealed record GetShopProfileQuery : IQuery<ShopProfile?>;
public sealed record SaveShopProfileCommand(ShopProfile Profile) : ICommand<Result>;
public sealed record SeedItemsCommand(bool Kirana, bool Pharmacy) : ICommand<Result<int>>;

// Bills (FR-20)
public sealed record GetBillQuery(Guid BillId) : IQuery<Bill?>;

// Data safety (FR-36/37). Not transactional: restore swaps the database file.
public sealed record ExportBackupCommand : ICommand<Result<string>>, INoTransaction;
public sealed record RestoreBackupCommand(string FilePath) : ICommand<Result>, INoTransaction;
