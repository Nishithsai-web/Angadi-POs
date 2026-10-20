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
        PruneOutbox();
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

    // FIX(M-11, 2026-10-20, FIX-4): app start is when the Outbox retention sweep runs (deletes rows older
    // than 90 days — see PruneOutboxCommand's handler). Fire-and-forget like the safety snapshot: it must
    // never delay or block the counter. Logged in docs/AGENT_FIX_LOG.md.
    private void PruneOutbox() => _ = Task.Run(async () =>
    {
        try { await _services.GetRequiredService<ISender>().Send(new PruneOutboxCommand()); }
        catch (Exception ex) { GlobalExceptions.Report(ex, "outbox-prune"); }
    });
}
