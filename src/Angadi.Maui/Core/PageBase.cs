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

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_viewModel is null) return;
        try { await _viewModel.OnAppearingAsync(); }
        catch (Exception ex) { GlobalExceptions.Report(ex, GetType().Name); }
    }
}
