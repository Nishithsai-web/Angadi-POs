using System.ComponentModel;
using Angadi.Application.Common;
using Angadi.Application.Platform;
using Angadi.Maui.Core;

namespace Angadi.Maui.Areas.Reports;

/// <summary>
/// ViewModelBase plus the bool flags XAML needs for the four states (loading / empty / error / content)
/// without value converters: HasError mirrors ErrorMessage, IsNotBusy mirrors IsBusy.
/// Used by the Reports and Settings areas. Candidate to move into Core (see docs/UI_REPORTS_SETTINGS.md).
/// </summary>
public abstract class StateViewModelBase(ISender sender, ILocalizer text, INavigationService nav, IDialogService dialogs)
    : ViewModelBase(sender, text, nav, dialogs)
{
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public bool IsNotBusy => !IsBusy;

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName == nameof(ErrorMessage)) OnPropertyChanged(nameof(HasError));
        else if (e.PropertyName == nameof(IsBusy)) OnPropertyChanged(nameof(IsNotBusy));
    }
}
