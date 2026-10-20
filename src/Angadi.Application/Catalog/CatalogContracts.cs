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
/// <summary>Attach a scanned barcode to an existing item (unknown packet at the counter).</summary>
public sealed record SetItemBarcodeCommand(Guid ItemId, string Barcode) : ICommand<Result>;
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

/// <summary>H11: Settings → Export logs goes through the dispatcher like every other action, so it is logged and testable.</summary>
/// <summary>Background safety copy (app start, app going to the background). Never waits on the user.</summary>
public sealed record TakeSafetySnapshotCommand : ICommand<Result>, INoTransaction;
/// <summary>The startup recovery report, once; null when there is nothing to tell the shopkeeper.</summary>
public sealed record GetDataRecoveryQuery : IQuery<Angadi.Application.Platform.DataRecoveryReport?>;
public sealed record ExportLogsCommand : ICommand<Result<string>>, INoTransaction;

/// <summary>H8: active items still on a pre-GST-2.0 slab (12/28%). They cannot be billed until re-slabbed.</summary>
public sealed record GetLegacySlabItemsQuery : IQuery<IReadOnlyList<Item>>;
