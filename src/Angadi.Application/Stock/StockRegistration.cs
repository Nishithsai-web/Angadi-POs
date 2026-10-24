using Angadi.Application.Common;
using Angadi.Domain.Inventory;
using Angadi.Domain.Purchasing;
using Microsoft.Extensions.DependencyInjection;

namespace Angadi.Application.Stock;

/// <summary>v2-stock registrations, explicit like the rest (trim-safe, no assembly scanning — ADR-2).</summary>
internal static class StockRegistration
{
    public static void AddStock(IServiceCollection services)
    {
        services.AddSingleton<IValidator<SaveSupplierCommand>, SaveSupplierValidator>();
        services.AddSingleton<IValidator<RecordPurchaseCommand>, RecordPurchaseValidator>();
        services.AddSingleton<IValidator<SavePurchaseOrderCommand>, SavePurchaseOrderValidator>();
        services.AddSingleton<IValidator<AdjustBatchStockCommand>, AdjustBatchStockValidator>();
        services.AddSingleton<IValidator<StockInBatchCommand>, StockInBatchValidator>();

        services.AddTransient<ExpiredSaleChecker>();

        services.AddTransient<SupplierHandlers>();
        services.AddTransient<IRequestHandler<SaveSupplierCommand, Result<Guid>>>(sp => sp.GetRequiredService<SupplierHandlers>());
        services.AddTransient<IRequestHandler<ListSuppliersQuery, IReadOnlyList<Supplier>>>(sp => sp.GetRequiredService<SupplierHandlers>());

        services.AddTransient<IRequestHandler<RecordPurchaseCommand, Result<Guid>>, RecordPurchaseHandler>();
        services.AddTransient<PurchaseQueries>();
        services.AddTransient<IRequestHandler<ListPurchasesQuery, IReadOnlyList<Purchase>>>(sp => sp.GetRequiredService<PurchaseQueries>());
        services.AddTransient<IRequestHandler<GetPurchaseQuery, Purchase?>>(sp => sp.GetRequiredService<PurchaseQueries>());
        services.AddTransient<IRequestHandler<GetPurchasesBySupplierQuery, IReadOnlyList<SupplierPurchaseTotal>>>(sp => sp.GetRequiredService<PurchaseQueries>());

        services.AddTransient<PurchaseOrderHandlers>();
        services.AddTransient<IRequestHandler<SavePurchaseOrderCommand, Result<Guid>>>(sp => sp.GetRequiredService<PurchaseOrderHandlers>());
        services.AddTransient<IRequestHandler<ListPurchaseOrdersQuery, IReadOnlyList<PurchaseOrder>>>(sp => sp.GetRequiredService<PurchaseOrderHandlers>());
        services.AddTransient<IRequestHandler<GetPurchaseOrderQuery, PurchaseOrder?>>(sp => sp.GetRequiredService<PurchaseOrderHandlers>());
        services.AddTransient<IRequestHandler<SuggestOrderFromLowStockQuery, IReadOnlyList<PurchaseOrderLine>>>(sp => sp.GetRequiredService<PurchaseOrderHandlers>());
        services.AddTransient<IRequestHandler<MarkPurchaseOrderSentCommand, Result>>(sp => sp.GetRequiredService<PurchaseOrderHandlers>());
        services.AddTransient<IRequestHandler<PrefillPurchaseFromOrderQuery, Result<PurchaseEntry>>>(sp => sp.GetRequiredService<PurchaseOrderHandlers>());
        services.AddTransient<IRequestHandler<SharePurchaseOrderCommand, Result>, SharePurchaseOrderHandler>();

        services.AddTransient<BatchHandlers>();
        services.AddTransient<IRequestHandler<GetItemBatchesQuery, IReadOnlyList<Batch>>>(sp => sp.GetRequiredService<BatchHandlers>());
        services.AddTransient<IRequestHandler<AdjustBatchStockCommand, Result>>(sp => sp.GetRequiredService<BatchHandlers>());
        services.AddTransient<IRequestHandler<StockInBatchCommand, Result>>(sp => sp.GetRequiredService<BatchHandlers>());
        services.AddTransient<IRequestHandler<GetExpiringQuery, ExpiringReport>>(sp => sp.GetRequiredService<BatchHandlers>());
        services.AddTransient<IRequestHandler<CheckExpiredSaleQuery, IReadOnlyList<ExpiredSaleLine>>>(sp => sp.GetRequiredService<BatchHandlers>());

        services.AddTransient<IRequestHandler<GetMarginEstimateQuery, MarginEstimate>, MarginHandler>();
    }
}
