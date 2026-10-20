using Microsoft.Extensions.Logging;

namespace Angadi.Maui.Core;

/// <summary>Shell routes. Tabs are registered in AppShell.xaml; detail pages by each area's Add*Area().</summary>
public static class Routes
{
    // Tabs (AppShell)
    public const string Pos = "pos";
    public const string Items = "items";
    public const string Bills = "bills";
    public const string Customers = "customers";
    public const string Reports = "reports";
    public const string Settings = "settings";

    // Detail pages
    public const string Receipt = "receipt";             // ?billId=
    public const string Scanner = "scanner";             // returns barcode via INavigationService result
    public const string ItemEdit = "item-edit";          // ?itemId= (absent = new) &barcode=
    public const string StockAdjust = "stock-adjust";    // ?itemId=
    public const string LowStock = "low-stock";
    public const string CustomerDetail = "customer";     // ?customerId=
    public const string Notes = "notes";                 // notes + feedback
    public const string CustomerEdit = "customer-edit";  // ?customerId= (absent = new)
    public const string BillDetail = "bill";             // ?billId=
    public const string Insights = "insights";
    public const string ShopSetup = "shop-setup";        // first run + settings edit
    public const string Subscription = "subscription";
    public const string UpiPay = "upi-pay";              // ?payload=&amount= → returns "paid"
}

/// <summary>Testable navigation over Shell (implemented in Core/Services.cs by the shell agent).</summary>
public interface INavigationService
{
    Task GoToAsync(string route, IDictionary<string, object>? parameters = null);
    Task GoBackAsync();
    /// <summary>Opens a page and awaits a value it returns via <see cref="ReturnAsync{T}"/> (e.g. the scanner's barcode). Null if cancelled.</summary>
    Task<T?> GoForResultAsync<T>(string route, IDictionary<string, object>? parameters = null) where T : class;
    Task ReturnAsync<T>(T? result) where T : class;
}

/// <summary>Toasts, confirms and prompts. Text passed in is already localised.</summary>
public interface IDialogService
{
    Task ToastAsync(string message);
    Task<bool> ConfirmAsync(string title, string message, string accept, string cancel);
    Task<string?> PromptAsync(string title, string message, string accept, string cancel, string? initial = null, Keyboard? keyboard = null);
    /// <summary>Action sheet; returns the chosen option or null.</summary>
    Task<string?> ChooseAsync(string title, string cancel, params string[] options);
}

/// <summary>
/// The one place unexpected exceptions go (NFR-07): logged with a correlation id, never a hard crash
/// at the counter. Wired to AppDomain/TaskScheduler/Android handlers in MauiProgram by the shell agent.
/// </summary>
public static class GlobalExceptions
{
    public static ILogger? Logger { get; set; }

    public static string Report(Exception ex, string source)
    {
        var correlationId = Guid.NewGuid().ToString("N")[..8];
        Logger?.LogError(ex, "Unhandled in {Source} [{CorrelationId}]", source, correlationId);
        return correlationId;
    }
}
