using Angadi.Maui.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Angadi.Maui;

public sealed class App : Microsoft.Maui.Controls.Application
{
    private readonly IServiceProvider _services;

    public App(IServiceProvider services)
    {
        _services = services;
        Resources = Theme.Build();
        UserAppTheme = AppTheme.Light;   // high contrast for sunlit counters
    }

    protected override Window CreateWindow(IActivationState? activationState) =>
        new(_services.GetRequiredService<AppShell>());
}
