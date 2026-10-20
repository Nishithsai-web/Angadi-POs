namespace Angadi.Tests.Architecture;

/// <summary>
/// UI safety-net rules, checked against the Maui sources (the test project does not reference the MAUI head,
/// so these are source-level guards — the same grep-level check the UI-1 fix prescribes).
/// FIX(UI-1, 2026-10-20, FIX-3): Flaw: 8 secondary pages awaited sender calls in an unwrapped
/// `async void OnAppearing`; a failure left a half-loaded page with no toast. Fix: every page load now runs
/// through SafeUi.Run, and this test fails if a new page ever bypasses the net. Logged in docs/AGENT_FIX_LOG.md.
/// </summary>
public class UiSafetyTests
{
    internal static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "Angadi.Maui"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Repo root (src/Angadi.Maui) not found above " + AppContext.BaseDirectory);
    }

    internal static string MauiFile(string relative) => File.ReadAllText(Path.Combine(RepoRoot(), "src", "Angadi.Maui", relative));

    [Fact]
    public void EveryOnAppearing_IsBehindTheSafetyNet()
    {
        var maui = Path.Combine(RepoRoot(), "src", "Angadi.Maui");
        var offenders = new List<string>();
        foreach (var file in Directory.GetFiles(maui, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) continue;
            var text = File.ReadAllText(file);
            if (!text.Contains("async void OnAppearing", StringComparison.Ordinal)) continue;
            var name = Path.GetFileName(file);
            if (name is "PageBase.cs") continue;   // the wrapped base itself: catches + reports every VM load
            if (text.Contains("SafeUi.Run(", StringComparison.Ordinal)) continue;
            // AppShell guards its whole body in try/catch + GlobalExceptions.Report (+ toast since LOG-3h).
            if (name is "AppShell.cs" && text.Contains("GlobalExceptions.Report(", StringComparison.Ordinal)) continue;
            offenders.Add(Path.GetRelativePath(maui, file));
        }
        Assert.True(offenders.Count == 0, "OnAppearing outside the SafeUi safety net: " + string.Join(", ", offenders));
    }

    /// <summary>
    /// LOG-1: the 45 s finalize watchdog must leave evidence. The VM cannot be instantiated in this test
    /// project (no MAUI head), so the wiring is asserted at source level here and the watchdog's claim —
    /// a timed-out finalize still completes exactly once in the background — is asserted behaviourally
    /// in LoggingTests.LOG1_WatchdogTimeout_TheSaveStillCompletesExactlyOnce.
    /// </summary>
    [Fact]
    public void FinalizeWatchdog_LeavesLogEvidence()
    {
        var vm = MauiFile(Path.Combine("Areas", "PosViewModel.cs"));
        Assert.Contains("ILogger<PosViewModel>", vm, StringComparison.Ordinal);
        Assert.Contains("\"finalize-watchdog\"", vm, StringComparison.Ordinal);
        Assert.Contains("LogError", vm, StringComparison.Ordinal);
        Assert.Contains("LogInformation", vm, StringComparison.Ordinal);   // late completion is logged too
    }
}
