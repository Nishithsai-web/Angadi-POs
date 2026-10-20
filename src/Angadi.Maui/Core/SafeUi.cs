using Angadi.Application.Common;
using Angadi.Application.Platform;
using Microsoft.Extensions.DependencyInjection;

namespace Angadi.Maui.Core;

/// <summary>
/// Every UI event handler in the app goes through here. An async event handler is async void: an exception in it
/// either crashes the app or (with the global handler) vanishes, and the shopkeeper sees a button that "does nothing".
/// Here it is logged with a correlation id and shown as the friendly UNEXPECTED message, and the screen stays usable.
/// A handler that is still running ignores a second tap (no double bills, no double saves).
/// </summary>
public static class SafeUi
{
    public static IServiceProvider? Services { get; set; }

    public static EventHandler On(Func<Task> work)
    {
        var running = 0;
        return async (_, _) =>
        {
            if (Interlocked.Exchange(ref running, 1) == 1) return;
            try { await Run(work); }
            finally { Interlocked.Exchange(ref running, 0); }
        };
    }

    /// <param name="singleFlight">False for typing events: every keystroke must run (latest text wins).</param>
    public static EventHandler<T> On<T>(Func<T, Task> work, bool singleFlight = true)
    {
        var running = 0;
        return async (_, e) =>
        {
            if (!singleFlight) { await Run(() => work(e)); return; }
            if (Interlocked.Exchange(ref running, 1) == 1) return;
            try { await Run(() => work(e)); }
            finally { Interlocked.Exchange(ref running, 0); }
        };
    }

    /// <summary>For Command(...) and other Action-shaped callbacks.</summary>
    public static Action Fire(Func<Task> work) => () => _ = Run(work);

    public static async Task Run(Func<Task> work)
    {
        try { await work(); }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            GlobalExceptions.Report(ex, "UI");
            await ShowUnexpectedAsync();
        }
    }

    public static async Task ShowUnexpectedAsync()
    {
        try
        {
            if (Services is null) return;
            var text = Services.GetRequiredService<ILocalizer>();
            await Services.GetRequiredService<IDialogService>().ToastAsync(text.Error(new Error(ErrorCodes.Unexpected)));
        }
        catch (Exception) { /* never let the safety net throw */ }
    }
}
