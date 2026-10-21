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

    /// <summary>
    /// LOG-2: a malformed appsettings.json must degrade to defaults, never crash startup. MauiProgram is
    /// MAUI-head code (not loadable in this test project), so the guard is asserted at source level:
    /// the config load's catch filter must include FormatException and the fallback must be surfaced
    /// as a startup warning once logging exists.
    /// </summary>
    [Fact]
    public void MalformedAppSettings_DegradesToDefaults_NeverCrashesStartup()
    {
        var program = MauiFile("MauiProgram.cs");
        Assert.Contains("IOException or FormatException", program, StringComparison.Ordinal);
        Assert.Contains("_configProblem", program, StringComparison.Ordinal);
        Assert.Contains("LogWarning", program, StringComparison.Ordinal);
    }

    /// <summary>
    /// L-7 (UI half): a ₹0 bill with lines must be an explicit decision — PosViewModel asks in the app
    /// language and passes the answer on the finalize command's FreeBillConfirmed flag (the validator
    /// half is FIX-2's). Source-level for the same reason as the watchdog test: no MAUI head here.
    /// </summary>
    [Fact]
    public void FreeBill_RequiresAnExplicitConfirmation()
    {
        var vm = MauiFile(Path.Combine("Areas", "PosViewModel.cs"));
        Assert.Contains("FreeBillConfirmed", vm, StringComparison.Ordinal);
        Assert.Contains("pos.freebill.confirm", vm, StringComparison.Ordinal);
        Assert.Contains("GrandTotal.Value == 0", vm, StringComparison.Ordinal);
    }

    /// <summary>
    /// SEC-4: every sensitive action must pass the PIN gate (the service half — hash, lockout — is
    /// behaviourally tested in PinServiceTests; the gate is MAUI code, so its wiring is asserted here):
    /// void (LedgerPages), dues payment (CustomerPages), backup export + restore + UPI/GSTIN edit
    /// (SetupAndSettingsPages), and the gate itself must let actions proceed when no PIN is set.
    /// </summary>
    [Fact]
    public void PinGate_CoversEverySensitiveAction()
    {
        var gate = MauiFile(Path.Combine("Core", "PinGate.cs"));
        Assert.Contains("HasPinAsync", gate, StringComparison.Ordinal);   // no PIN set → proceed

        var ledger = MauiFile(Path.Combine("Areas", "LedgerPages.cs"));
        Assert.Contains("PinGate.CheckAsync", ledger, StringComparison.Ordinal);   // void bill

        var customers = MauiFile(Path.Combine("Areas", "CustomerPages.cs"));
        Assert.Contains("PinGate.CheckAsync", customers, StringComparison.Ordinal);   // record dues payment

        var settings = MauiFile(Path.Combine("Areas", "SetupAndSettingsPages.cs"));
        Assert.True(settings.Split("PinGate.CheckAsync").Length - 1 >= 3,
            "backup export, restore and UPI/GSTIN edits must all be PIN-gated");
        Assert.Contains("PinGate.SetPinAsync", settings, StringComparison.Ordinal);   // set-PIN entry + pharmacy prompt
        Assert.Contains("settings.pin.nudge", settings, StringComparison.Ordinal);   // persistent no-PIN nudge
    }

    /// <summary>
    /// SEC-5 (UI half): the write-off action exists on the customer account, is PIN-gated, sends the
    /// dedicated command (never a payment), and the ledger renders WriteOff entries distinctly.
    /// </summary>
    [Fact]
    public void DuesWriteOff_IsGatedDistinctAndNeverAPayment()
    {
        var customers = MauiFile(Path.Combine("Areas", "CustomerPages.cs"));
        Assert.Contains("RecordDuesWriteOffCommand", customers, StringComparison.Ordinal);
        Assert.Contains("DuesEntryKind.WriteOff", customers, StringComparison.Ordinal);   // distinct ledger label
        Assert.Contains("DuesWrittenOffSoFar", customers, StringComparison.Ordinal);     // distinct report line
        // The write-off path passes the gate before it prompts (gate appears in PayAsync AND WriteOffAsync).
        Assert.True(customers.Split("PinGate.CheckAsync").Length - 1 >= 2,
            "dues payment and dues write-off must both be PIN-gated");
    }
}
