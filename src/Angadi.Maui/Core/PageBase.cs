namespace Angadi.Maui.Core;

/// <summary>
/// Base for every page. XAML root: &lt;core:PageBase ... x:Class="..." x:DataType="vm:YourViewModel"&gt;.
/// Code-behind: public YourPage(YourViewModel vm) : base(vm) { InitializeComponent(); }
/// </summary>
public class PageBase : ContentPage
{
    private readonly ViewModelBase _viewModel;

    // Parameterless ctor required by the XAML compiler for derived pages' designer; never used at runtime.
    public PageBase() => _viewModel = null!;

    protected PageBase(ViewModelBase viewModel)
    {
        _viewModel = viewModel;
        BindingContext = viewModel;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (_viewModel is null) return;
        _ = SafeUi.Run(_viewModel.OnAppearingAsync);   // A1-04: a failed load is logged AND toasted, never silent
    }
}
