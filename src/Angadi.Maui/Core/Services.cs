using Microsoft.Extensions.DependencyInjection;

namespace Angadi.Maui.Core;

/// <summary>Rebuilds the shell (after a language change or a restore) so every page re-reads its text and data.</summary>
public interface IAppRestart
{
    void Restart();
}

public sealed class ShellNavigationService : INavigationService
{
    private readonly Stack<TaskCompletionSource<object?>> _pending = new();

    public Task GoToAsync(string route, IDictionary<string, object>? parameters = null) =>
        MainThread.InvokeOnMainThreadAsync(() => parameters is null
            ? Shell.Current.GoToAsync(route)
            : Shell.Current.GoToAsync(route, parameters));

    public Task GoBackAsync() => MainThread.InvokeOnMainThreadAsync(() => Shell.Current.GoToAsync(".."));

    public async Task<T?> GoForResultAsync<T>(string route, IDictionary<string, object>? parameters = null) where T : class
    {
        var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending.Push(tcs);
        try { await GoToAsync(route, parameters); }
        catch
        {
            // Navigation failed: don't leave a result nobody will ever complete (it would hang the caller forever
            // and hand the next page's result to the wrong caller).
            if (_pending.Count > 0 && ReferenceEquals(_pending.Peek(), tcs)) _pending.Pop();
            throw;
        }
        return await tcs.Task as T;
    }

    public async Task ReturnAsync<T>(T? result) where T : class
    {
        if (_pending.Count > 0) _pending.Pop().TrySetResult(result);
        await GoBackAsync();
    }

    /// <summary>Called when a result page disappears without returning (back gesture) — completes with null.</summary>
    public void CancelPending()
    {
        if (_pending.Count > 0) _pending.Pop().TrySetResult(null);
    }
}

internal sealed class DialogService : IDialogService
{
    private static Page? Top => Shell.Current?.CurrentPage ?? Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault()?.Page;

    public Task ToastAsync(string message) => MainThread.InvokeOnMainThreadAsync(() =>
    {
#if ANDROID
        Android.Widget.Toast.MakeText(Microsoft.Maui.ApplicationModel.Platform.AppContext, message, Android.Widget.ToastLength.Long)?.Show();
#endif
    });

    public Task<bool> ConfirmAsync(string title, string message, string accept, string cancel) =>
        MainThread.InvokeOnMainThreadAsync(() => Top?.DisplayAlert(title, message, accept, cancel) ?? Task.FromResult(false));

    public Task AlertAsync(string title, string message, string ok) =>
        MainThread.InvokeOnMainThreadAsync(() => Top?.DisplayAlert(title, message, ok) ?? Task.CompletedTask);

    public Task<string?> PromptAsync(string title, string message, string accept, string cancel, string? initial = null, Keyboard? keyboard = null) =>
        MainThread.InvokeOnMainThreadAsync(async () =>
        {
            if (Top is not { } page) return null;
            string? value = await page.DisplayPromptAsync(title, message, accept, cancel, initialValue: initial ?? "", keyboard: keyboard ?? Keyboard.Default);
            return value;
        });

    public Task<string?> ChooseAsync(string title, string cancel, params string[] options) =>
        MainThread.InvokeOnMainThreadAsync(async () =>
        {
            if (Top is not { } page) return null;
            var choice = await page.DisplayActionSheet(title, cancel, null, options);
            return choice == cancel ? null : choice;
        });
}

internal sealed class AppRestart(IServiceProvider services) : IAppRestart
{
    public void Restart() => MainThread.BeginInvokeOnMainThread(() =>
    {
        var app = Microsoft.Maui.Controls.Application.Current;
        Theme.UseLatin = services.GetRequiredService<Angadi.Application.Platform.ILocalizer>().Language == Angadi.Domain.Shop.UiLanguage.English;
        if (app is not null) app.Resources = Theme.Build();   // implicit styles pick up the language's typeface
        var window = app?.Windows.FirstOrDefault();
        if (window is not null) window.Page = services.GetRequiredService<AppShell>();
    });
}

public static class ShellServicesRegistration
{
    public static IServiceCollection AddShellServices(this IServiceCollection s)
    {
        s.AddSingleton<ShellNavigationService>();
        s.AddSingleton<INavigationService>(sp => sp.GetRequiredService<ShellNavigationService>());
        s.AddSingleton<IDialogService, DialogService>();
        s.AddSingleton<IAppRestart, AppRestart>();
        s.AddTransient<AppShell>();
        return s;
    }
}
