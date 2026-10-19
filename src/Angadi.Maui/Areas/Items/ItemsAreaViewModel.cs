using System.ComponentModel;
using Angadi.Application.Common;
using Angadi.Application.Platform;
using Angadi.Maui.Core;

namespace Angadi.Maui.Areas.Items;

/// <summary>Shared bits for the Items area ViewModels: a bindable HasError and query-parameter parsing.</summary>
public abstract partial class ItemsAreaViewModel(ISender sender, ILocalizer text, INavigationService nav, IDialogService dialogs)
    : ViewModelBase(sender, text, nav, dialogs)
{
    /// <summary>Error state for the page (ErrorMessage is set by RunBusyAsync).</summary>
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName == nameof(ErrorMessage)) OnPropertyChanged(nameof(HasError));
    }

    /// <summary>Accepts a Guid object or its string form (Shell URIs deliver strings).</summary>
    protected static Guid? ReadGuid(IDictionary<string, object> query, string key)
    {
        if (!query.TryGetValue(key, out var value) || value is null) return null;
        if (value is Guid guid) return guid;
        if (value is string s && Guid.TryParse(s, out var parsed)) return parsed;
        return null;
    }

    protected static string? ReadString(IDictionary<string, object> query, string key)
    {
        if (!query.TryGetValue(key, out var value) || value is null) return null;
        var s = value.ToString();
        return string.IsNullOrWhiteSpace(s) ? null : Uri.UnescapeDataString(s.Trim());
    }

    /// <summary>Navigation from a selection handler: never let an exception escape to the UI thread.</summary>
    protected async Task GoSafeAsync(string route, IDictionary<string, object>? parameters = null)
    {
        try { await Nav.GoToAsync(route, parameters); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            GlobalExceptions.Report(ex, GetType().Name);
        }
    }

    protected Task ShowErrorCodeAsync(string code) => ShowIfFailedAsync(Result.Failure(code));
}
