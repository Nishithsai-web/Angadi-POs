using Angadi.Application.Billing;
using Angadi.Application.Catalog;
using Angadi.Application.Common;
using Angadi.Application.Customers;
using Angadi.Application.Insights;
using Angadi.Application.Inventory;
using Angadi.Application.Notes;
using Angadi.Application.Platform;
using Angadi.Application.Printing;
using Angadi.Application.Reports;
using Angadi.Application.Voice;
using Angadi.Domain.Billing;
using Angadi.Domain.Customers;
using Angadi.Domain.Inventory;
using Angadi.Domain.Items;
using Angadi.Domain.Notes;
using Angadi.Domain.Shop;
using Microsoft.Extensions.DependencyInjection;

namespace Angadi.Application;

public static class DependencyInjection
{
    /// <summary>Registers every handler explicitly (trim-safe, no assembly scanning — ADR-2).</summary>
    public static IServiceCollection AddAngadiApplication(this IServiceCollection services)
    {
        services.AddOptions<VoiceOptions>();
        services.AddOptions<BillingOptions>();
        services.AddOptions<InsightsOptions>();

        services.AddSingleton<ISender, Sender>();
        services.AddSingleton<JsonLocalizer>();
        services.AddSingleton<ILocalizer>(sp => sp.GetRequiredService<JsonLocalizer>());

        // Voice (FR-06/07)
        services.AddSingleton(VoiceRules.Default);
        services.AddSingleton<ITeluguParser, TeluguParser>();
        services.AddSingleton<IItemMatcher, ItemMatcher>();
        services.AddSingleton<IAliasLearner, AliasLearner>();
        services.AddSingleton<IVoiceBillingPipeline, VoiceBillingPipeline>();
        services.AddTransient<IRequestHandler<LearnAliasCommand, Result>, LearnAliasHandler>();   // A1-11

        services.AddSingleton<IInsightsService, InsightsService>();

        // Validators
        services.AddSingleton<IValidator<FinalizeBillCommand>, FinalizeBillValidator>();
        services.AddSingleton<IValidator<VoidBillCommand>, VoidBillValidator>();
        services.AddSingleton<IValidator<RecordDuesPaymentCommand>, RecordDuesPaymentValidator>();
        services.AddSingleton<IValidator<AdjustStockCommand>, AdjustStockValidator>();
        services.AddSingleton<IValidator<RecordReturnCommand>, RecordReturnValidator>();   // v2 returns
        services.AddSingleton<IValidator<SaveItemCommand>, SaveItemValidator>();
        services.AddSingleton<IValidator<SaveCustomerCommand>, SaveCustomerValidator>();
        services.AddSingleton<IValidator<SaveShopProfileCommand>, SaveShopProfileValidator>();

        // Billing
        // v2-stock: FinalizeBillHandler is wrapped by the expired-batch guard (Stock/StockHandlers.cs); the handler itself is unchanged.
        services.AddTransient<FinalizeBillHandler>();
        services.AddTransient<IRequestHandler<FinalizeBillCommand, Result<FinalizedBill>>, Angadi.Application.Stock.ExpiryGuardedFinalizeHandler>();
        services.AddTransient<IRequestHandler<VoidBillCommand, Result>, VoidBillHandler>();
        services.AddTransient<IRequestHandler<ShareReceiptCommand, Result>, ShareReceiptHandler>();
        services.AddTransient<IRequestHandler<SendBillToCustomerCommand, Result>, SendBillToCustomerHandler>();
        services.AddTransient<IRequestHandler<RecordDuesPaymentCommand, Result>, RecordDuesPaymentHandler>();
        services.AddTransient<IRequestHandler<SendDuesReminderCommand, Result>, SendDuesReminderHandler>();
        services.AddTransient<IRequestHandler<AdjustStockCommand, Result>, AdjustStockHandler>();
        services.AddTransient<IRequestHandler<GetBillQuotaQuery, BillQuota>, GetBillQuotaHandler>();
        // v2 returns / partial refunds (split tender lives inside FinalizeBill)
        services.AddTransient<IRequestHandler<RecordReturnCommand, Result<RecordedReturn>>, RecordReturnHandler>();
        services.AddTransient<IRequestHandler<GetBillReturnsQuery, BillReturns?>, GetBillReturnsHandler>();
        services.AddTransient<IRequestHandler<GetCustomerReturnsQuery, IReadOnlyList<Angadi.Domain.Billing.SalesReturn>>, GetCustomerReturnsHandler>();
        services.AddTransient<IRequestHandler<ShareReturnNoteCommand, Result>, ShareReturnNoteHandler>();
        // v2 printing (Bluetooth thermal receipts)
        services.AddOptions<PrintingOptions>();
        services.AddTransient<PrinterSettingsHandlers>();
        services.AddTransient<IRequestHandler<GetPrinterSettingsQuery, PrinterSettings>>(sp => sp.GetRequiredService<PrinterSettingsHandlers>());
        services.AddTransient<IRequestHandler<SavePrinterSettingsCommand, Result>>(sp => sp.GetRequiredService<PrinterSettingsHandlers>());
        services.AddTransient<IRequestHandler<ListPairedPrintersQuery, Result<IReadOnlyList<PairedPrinter>>>>(sp => sp.GetRequiredService<PrinterSettingsHandlers>());
        services.AddTransient<PrintHandlers>();
        services.AddTransient<IRequestHandler<PrintReceiptCommand, Result>>(sp => sp.GetRequiredService<PrintHandlers>());
        services.AddTransient<IRequestHandler<PrintReturnNoteCommand, Result>>(sp => sp.GetRequiredService<PrintHandlers>());
        services.AddTransient<IRequestHandler<PrintTestPageCommand, Result>>(sp => sp.GetRequiredService<PrintHandlers>());
        services.AddTransient<BillDraftHandlers>();
        services.AddTransient<IRequestHandler<SaveBillDraftCommand, Result>>(sp => sp.GetRequiredService<BillDraftHandlers>());
        services.AddTransient<IRequestHandler<GetBillDraftQuery, BillDraftSnapshot?>>(sp => sp.GetRequiredService<BillDraftHandlers>());

        // Catalog
        services.AddTransient<IRequestHandler<SaveItemCommand, Result<Guid>>, SaveItemHandler>();
        services.AddTransient<IRequestHandler<SaveCustomerCommand, Result<Guid>>, SaveCustomerHandler>();
        services.AddTransient<IRequestHandler<RemoveCustomerCommand, Result>, RemoveCustomerHandler>();   // CUST-1
        services.AddTransient<ItemQueries>();
        services.AddTransient<IRequestHandler<SearchItemsQuery, IReadOnlyList<Item>>>(sp => sp.GetRequiredService<ItemQueries>());
        services.AddTransient<IRequestHandler<GetAllItemsQuery, IReadOnlyList<Item>>>(sp => sp.GetRequiredService<ItemQueries>());
        services.AddTransient<IRequestHandler<GetQuickSaleItemsQuery, IReadOnlyList<Item>>>(sp => sp.GetRequiredService<ItemQueries>());
        services.AddTransient<IRequestHandler<GetLowStockQuery, IReadOnlyList<Item>>>(sp => sp.GetRequiredService<ItemQueries>());
        services.AddTransient<IRequestHandler<GetItemQuery, Item?>>(sp => sp.GetRequiredService<ItemQueries>());
        services.AddTransient<IRequestHandler<GetItemByBarcodeQuery, Item?>>(sp => sp.GetRequiredService<ItemQueries>());
        services.AddTransient<IRequestHandler<GetStockHistoryQuery, IReadOnlyList<StockMove>>>(sp => sp.GetRequiredService<ItemQueries>());
        services.AddTransient<IRequestHandler<GetLegacySlabItemsQuery, IReadOnlyList<Item>>>(sp => sp.GetRequiredService<ItemQueries>());
        services.AddTransient<CustomerQueries>();
        services.AddTransient<IRequestHandler<SearchCustomersQuery, IReadOnlyList<Customer>>>(sp => sp.GetRequiredService<CustomerQueries>());
        services.AddTransient<IRequestHandler<GetCustomerQuery, Customer?>>(sp => sp.GetRequiredService<CustomerQueries>());
        services.AddTransient<IRequestHandler<GetCustomerLedgerQuery, IReadOnlyList<DuesEntry>>>(sp => sp.GetRequiredService<CustomerQueries>());
        services.AddTransient<ShopHandlers>();
        services.AddTransient<IRequestHandler<GetShopProfileQuery, ShopProfile?>>(sp => sp.GetRequiredService<ShopHandlers>());
        services.AddTransient<IRequestHandler<SaveShopProfileCommand, Result>>(sp => sp.GetRequiredService<ShopHandlers>());
        services.AddTransient<IRequestHandler<SeedItemsCommand, Result<int>>>(sp => sp.GetRequiredService<ShopHandlers>());
        services.AddTransient<IRequestHandler<GetBillQuery, Bill?>>(sp => sp.GetRequiredService<ShopHandlers>());
        // A2-03: export records the manual-backup time; the counter and Settings read it back.
        services.AddTransient<BackupHandlers>();
        services.AddTransient<IRequestHandler<ExportBackupCommand, Result<string>>>(sp => sp.GetRequiredService<BackupHandlers>());
        services.AddTransient<IRequestHandler<GetBackupStatusQuery, BackupStatus>>(sp => sp.GetRequiredService<BackupHandlers>());
        services.AddTransient<IRequestHandler<DismissBackupReminderCommand, Result>>(sp => sp.GetRequiredService<BackupHandlers>());
        services.AddTransient<IRequestHandler<RestoreBackupCommand, Result>>(sp => sp.GetRequiredService<ShopHandlers>());
        services.AddTransient<IRequestHandler<ExportLogsCommand, Result<string>>, ExportLogsHandler>();
        services.AddTransient<DataSafetyHandlers>();
        services.AddTransient<IRequestHandler<TakeSafetySnapshotCommand, Result>>(sp => sp.GetRequiredService<DataSafetyHandlers>());
        services.AddTransient<IRequestHandler<GetDataRecoveryQuery, DataRecoveryReport?>>(sp => sp.GetRequiredService<DataSafetyHandlers>());
        services.AddTransient<IRequestHandler<PruneOutboxCommand, Result>>(sp => sp.GetRequiredService<DataSafetyHandlers>());   // M-11 (FIX-4)
        services.AddTransient<IRequestHandler<SetItemBarcodeCommand, Result>, SetItemBarcodeHandler>();

        // Notes & feedback
        services.AddSingleton<IValidator<SaveNoteCommand>, SaveNoteValidator>();
        services.AddSingleton<IValidator<SendFeedbackCommand>, SendFeedbackValidator>();
        services.AddTransient<NotesHandlers>();
        services.AddTransient<IRequestHandler<SaveNoteCommand, Result<Guid>>>(sp => sp.GetRequiredService<NotesHandlers>());
        services.AddTransient<IRequestHandler<SetNoteDoneCommand, Result>>(sp => sp.GetRequiredService<NotesHandlers>());
        services.AddTransient<IRequestHandler<DeleteNoteCommand, Result>>(sp => sp.GetRequiredService<NotesHandlers>());
        services.AddTransient<IRequestHandler<ListNotesQuery, IReadOnlyList<Note>>>(sp => sp.GetRequiredService<NotesHandlers>());
        services.AddTransient<IRequestHandler<SendFeedbackCommand, Result>, SendFeedbackHandler>();

        // Customer accounts
        services.AddTransient<CustomerAccountHandlers>();
        services.AddTransient<IRequestHandler<GetCustomerBillsQuery, IReadOnlyList<BillSummary>>>(sp => sp.GetRequiredService<CustomerAccountHandlers>());
        services.AddTransient<IRequestHandler<GetCustomerReportQuery, CustomerReport?>>(sp => sp.GetRequiredService<CustomerAccountHandlers>());
        services.AddTransient<IRequestHandler<SetPromisedPayDateCommand, Result>>(sp => sp.GetRequiredService<CustomerAccountHandlers>());
        services.AddTransient<IRequestHandler<GetDueRemindersQuery, IReadOnlyList<DueReminder>>>(sp => sp.GetRequiredService<CustomerAccountHandlers>());
        services.AddSingleton<IValidator<RecordDuesWriteOffCommand>, RecordDuesWriteOffValidator>();   // SEC-5 (FIX-4)
        services.AddTransient<IRequestHandler<RecordDuesWriteOffCommand, Result>, RecordDuesWriteOffHandler>();   // SEC-5 (FIX-4)

        // Inventory
        services.AddTransient<InventoryHandlers>();
        services.AddTransient<IRequestHandler<GetInventoryReportQuery, InventoryReport>>(sp => sp.GetRequiredService<InventoryHandlers>());
        services.AddTransient<IRequestHandler<GetStockMovesQuery, IReadOnlyList<StockMoveLine>>>(sp => sp.GetRequiredService<InventoryHandlers>());

        // Reports
        services.AddTransient<ReportHandlers>();
        services.AddTransient<IRequestHandler<GetDayEndReportQuery, DayEndReport>>(sp => sp.GetRequiredService<ReportHandlers>());
        services.AddTransient<IRequestHandler<GetGstSummaryQuery, IReadOnlyList<TaxSplit>>>(sp => sp.GetRequiredService<ReportHandlers>());
        services.AddTransient<IRequestHandler<GetTopItemsQuery, IReadOnlyList<TopItem>>>(sp => sp.GetRequiredService<ReportHandlers>());
        services.AddTransient<IRequestHandler<GetDuesOutstandingQuery, DuesOutstanding>>(sp => sp.GetRequiredService<ReportHandlers>());
        services.AddTransient<IRequestHandler<ListBillsQuery, IReadOnlyList<BillSummary>>>(sp => sp.GetRequiredService<ReportHandlers>());

        // v2-stock: suppliers, purchases, purchase orders, batch & expiry
        Angadi.Application.Stock.StockRegistration.AddStock(services);
        Angadi.Application.Payroll.PayrollRegistration.AddPayroll(services);   // staff money

        // v2 several shops on one phone (Settings → Shops)
        Angadi.Application.Shops.ShopsRegistration.AddShops(services);

        // CHAIN-1 branch report packs (IChainStore and ChainPackFolder come from Infrastructure).
        services.AddTransient<Chain.ChainHandlers>();
        services.AddTransient<IRequestHandler<Chain.SetChainCodeCommand, Result>>(sp => sp.GetRequiredService<Chain.ChainHandlers>());
        services.AddTransient<IRequestHandler<Chain.BuildChainPackCommand, Result<string>>>(sp => sp.GetRequiredService<Chain.ChainHandlers>());
        services.AddTransient<IRequestHandler<Chain.ImportChainPackCommand, Result<Chain.ImportOutcome>>>(sp => sp.GetRequiredService<Chain.ChainHandlers>());
        services.AddTransient<IRequestHandler<Chain.ListBranchesQuery, Result<IReadOnlyList<Chain.BranchPack>>>>(sp => sp.GetRequiredService<Chain.ChainHandlers>());
        services.AddTransient<IRequestHandler<Chain.RemoveBranchCommand, Result>>(sp => sp.GetRequiredService<Chain.ChainHandlers>());
        services.AddTransient<IRequestHandler<Chain.GetChainStatusQuery, Chain.ChainStatus>>(sp => sp.GetRequiredService<Chain.ChainHandlers>());

        // v2 cloud sync (ISyncService comes from Infrastructure).
        services.AddTransient<Sync.SyncHandlers>();
        services.AddTransient<IRequestHandler<Sync.GetSyncStatusQuery, Sync.SyncStatus>>(sp => sp.GetRequiredService<Sync.SyncHandlers>());
        services.AddTransient<IRequestHandler<Sync.LinkCloudSyncCommand, Result>>(sp => sp.GetRequiredService<Sync.SyncHandlers>());
        services.AddTransient<IRequestHandler<Sync.SyncNowCommand, Result<int>>>(sp => sp.GetRequiredService<Sync.SyncHandlers>());

        // Spreadsheet export (sales, dues, stock); ExportFolder comes from Infrastructure.
        services.AddTransient<IRequestHandler<Reports.ExportCsvCommand, Result<string>>, Reports.ExportCsvHandler>();

        // RBAC-1 roles, staff and activity log (IAccessControl itself is registered by Infrastructure).
        services.AddTransient<Security.AccessHandlers>();
        services.AddTransient<IRequestHandler<Security.GetCurrentUserQuery, Security.AccessUser>>(sp => sp.GetRequiredService<Security.AccessHandlers>());
        services.AddTransient<IRequestHandler<Security.ListStaffQuery, IReadOnlyList<Security.StaffMember>>>(sp => sp.GetRequiredService<Security.AccessHandlers>());
        services.AddTransient<IRequestHandler<Security.GetCashierGrantsQuery, IReadOnlySet<Security.Permission>>>(sp => sp.GetRequiredService<Security.AccessHandlers>());
        services.AddTransient<IRequestHandler<Security.SaveStaffCommand, Result<Security.StaffMember>>>(sp => sp.GetRequiredService<Security.AccessHandlers>());
        services.AddTransient<IRequestHandler<Security.SetStaffActiveCommand, Result>>(sp => sp.GetRequiredService<Security.AccessHandlers>());
        services.AddTransient<IRequestHandler<Security.SaveCashierGrantsCommand, Result>>(sp => sp.GetRequiredService<Security.AccessHandlers>());
        services.AddTransient<IRequestHandler<Security.ListActivityQuery, Result<IReadOnlyList<Security.ActivityEntry>>>>(sp => sp.GetRequiredService<Security.AccessHandlers>());
        services.AddTransient<IRequestHandler<Security.SetShopLanguageCommand, Result>>(sp => sp.GetRequiredService<Security.AccessHandlers>());
        return services;
    }
}
