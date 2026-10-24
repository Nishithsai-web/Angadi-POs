using Angadi.Application.Common;
using Angadi.Application.Platform;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Angadi.Maui.Core;

/// <summary>
/// Base for every ViewModel. ViewModels talk only to ISender (commands/queries), ILocalizer (text),
/// INavigationService and IDialogService. No hand-written INotifyPropertyChanged: use
/// [ObservableProperty] and [RelayCommand] (CommunityToolkit.Mvvm source generators).
/// </summary>
public abstract partial class ViewModelBase(ISender sender, ILocalizer text, INavigationService nav, IDialogService dialogs) : ObservableObject
{
    protected ISender Sender { get; } = sender;
    public ILocalizer Text { get; } = text;
    protected INavigationService Nav { get; } = nav;
    protected IDialogService Dialogs { get; } = dialogs;

    /// <summary>Loading / empty / error states (blueprint §5: never a dead screen).</summary>
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _errorMessage;

    /// <summary>Called by the page's OnAppearing. Load data here, not in the constructor.</summary>
    public virtual Task OnAppearingAsync() => Task.CompletedTask;

    /// <summary>Shows a failed Result as a Telugu/English toast from errors.json. Returns true if it failed.</summary>
    protected async Task<bool> ShowIfFailedAsync(Result result)
    {
        if (result.IsSuccess) return false;
        await Dialogs.ToastAsync(Text.Error(result.Error));
        return true;
    }

    /// <summary>
    /// Every button goes through this: an exception never escapes into an async-void command (which crashes or,
    /// worse, silently does nothing). It is logged with its source and shown as the friendly UNEXPECTED message.
    /// </summary>
    protected async Task SafeAsync(Func<Task> work, [System.Runtime.CompilerServices.CallerMemberName] string source = "")
    {
        try { await work(); }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            GlobalExceptions.Report(ex, GetType().Name + "." + source);
            await Dialogs.ToastAsync(Text.Error(new Error(ErrorCodes.Unexpected)));
        }
    }

    /// <summary>Runs work with IsBusy set; an unexpected exception shows the friendly UNEXPECTED message.</summary>
    protected async Task RunBusyAsync(Func<Task> work)
    {
        if (IsBusy) return;
        IsBusy = true;
        ErrorMessage = null;
        try { await work(); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ErrorMessage = Text.Error(new Error(ErrorCodes.Unexpected));
            GlobalExceptions.Report(ex, GetType().Name);
            await Dialogs.ToastAsync(ErrorMessage);   // A1-07: nothing binds ErrorMessage — say it, like SafeAsync
        }
        finally { IsBusy = false; }
    }
}
