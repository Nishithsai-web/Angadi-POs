using Angadi.Application.Billing;
using Angadi.Application.Catalog;
using Angadi.Application.Common;
using Angadi.Application.Customers;
using Angadi.Application.Insights;
using Angadi.Application.Inventory;
using Angadi.Application.Notes;
using Angadi.Application.Platform;
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

        services.AddSingleton<IInsightsService, InsightsService>();

        // Validators
        services.AddSingleton<IValidator<FinalizeBillCommand>, FinalizeBillValidator>();
        services.AddSingleton<IValidator<VoidBillCommand>, VoidBillValidator>();
        services.AddSingleton<IValidator<RecordDuesPaymentCommand>, RecordDuesPaymentValidator>();
        services.AddSingleton<IValidator<AdjustStockCommand>, AdjustStockValidator>();
        services.AddSingleton<IValidator<SaveItemCommand>, SaveItemValidator>();
        services.AddSingleton<IValidator<SaveCustomerCommand>, SaveCustomerValidator>();
        services.AddSingleton<IValidator<SaveShopProfileCommand>, SaveShopProfileValidator>();

        // Billing
        services.AddTransient<IRequestHandler<FinalizeBillCommand, Result<FinalizedBill>>, FinalizeBillHandler>();
        services.AddTransient<IRequestHandler<VoidBillCommand, Result>, VoidBillHandler>();
        services.AddTransient<IRequestHandler<ShareReceiptCommand, Result>, ShareReceiptHandler>();
        services.AddTransient<IRequestHandler<SendBillToCustomerCommand, Result>, SendBillToCustomerHandler>();
        services.AddTransient<IRequestHandler<RecordDuesPaymentCommand, Result>, RecordDuesPaymentHandler>();
        services.AddTransient<IRequestHandler<SendDuesReminderCommand, Result>, SendDuesReminderHandler>();
        services.AddTransient<IRequestHandler<AdjustStockCommand, Result>, AdjustStockHandler>();
        services.AddTransient<IRequestHandler<GetBillQuotaQuery, BillQuota>, GetBillQuotaHandler>();

        // Catalog
        services.AddTransient<IRequestHandler<SaveItemCommand, Result<Guid>>, SaveItemHandler>();
        services.AddTransient<IRequestHandler<SaveCustomerCommand, Result<Guid>>, SaveCustomerHandler>();
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
        services.AddTransient<IRequestHandler<ExportBackupCommand, Result<string>>>(sp => sp.GetRequiredService<ShopHandlers>());
        services.AddTransient<IRequestHandler<RestoreBackupCommand, Result>>(sp => sp.GetRequiredService<ShopHandlers>());
        services.AddTransient<IRequestHandler<ExportLogsCommand, Result<string>>, ExportLogsHandler>();
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

        return services;
    }
}
