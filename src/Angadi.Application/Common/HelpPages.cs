namespace Angadi.Application.Common;

/// <summary>
/// Screens that have a "?" help button, and the number of first-time walkthrough steps. The help texts live in
/// strings.help.{te,en}.json under "help.&lt;PageName&gt;.title/body" and "walk.N.title/body"; a test checks every name here has both.
/// </summary>
public static class HelpPages
{
    public const int WalkthroughSteps = 8;

    public static readonly IReadOnlyList<string> Names =
    [
        "PosPage", "ItemsPage", "BillsPage", "CustomersPage", "ReportsPage", "SettingsPage",
        "ReceiptPage", "UpiPayPage", "SplitPayPage", "ReturnPage", "PrinterSettingsPage",
        "StaffPage", "ChainPage", "ExportPage", "PayrollPage", "ActivityPage",
        "ItemEditPage", "BillDetailPage", "CustomerDetailPage", "ShopSetupPage", "SubscriptionPage", "NotesPage", "ShopsPage",
        "StockHubPage", "SuppliersPage", "PurchaseEditPage", "PurchasesPage", "PurchaseDetailPage", "OrdersPage", "OrderEditPage",
        "ExpiringPage", "StockReportPage",
    ];
}
