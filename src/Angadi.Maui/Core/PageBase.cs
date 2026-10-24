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
        // FIX(A1-04, 2026-10-21, S2): Flaw: a failed view-model load was only logged — the page sat half-empty with
        // no message. Fix: the same SafeUi.Run wrapping UI-1 gave every ContentPage (logged AND toasted).
        // Why: one never-fail mechanism for every page. Logged in docs/AGENT_FIX_LOG.md.
        await SafeUi.Run(_viewModel.OnAppearingAsync);
    }
}
