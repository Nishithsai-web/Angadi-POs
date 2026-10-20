using Angadi.Application.Catalog;
using Angadi.Application.Common;
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

    // Safety copies: when the app opens, and every time it goes to the background (screen off, phone call, home
    // button) — the moments just before Android may close it. Skipped cheaply when nothing changed.
    protected override void OnStart()
    {
        base.OnStart();
        TakeSafetySnapshot();
    }

    protected override void OnSleep()
    {
        base.OnSleep();
        TakeSafetySnapshot();
    }

    private void TakeSafetySnapshot() => _ = Task.Run(async () =>
    {
        try { await _services.GetRequiredService<ISender>().Send(new TakeSafetySnapshotCommand()); }
        catch (Exception ex) { GlobalExceptions.Report(ex, "safety-snapshot"); }
    });
}
