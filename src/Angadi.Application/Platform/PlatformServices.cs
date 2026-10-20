using Angadi.Application.Common;
using Angadi.Domain.Billing;
using Angadi.Domain.Shop;

namespace Angadi.Application.Platform;

/// <summary>Android SpeechRecognizer te-IN (Agent B). Needs internet (NFR-01).</summary>
public interface ISpeechToText
{
    /// <summary>
    /// Listens once until silence. Partial transcripts stream to <paramref name="partials"/>.
    /// Failures: VOICE_NO_INTERNET, VOICE_PERMISSION_DENIED, VOICE_NOTHING_HEARD.
    /// </summary>
    Task<Result<string>> ListenAsync(string culture, IProgress<string>? partials, CancellationToken ct = default);
}

/// <summary>Camera via BarcodeScanning.Native.Maui (Agent C). Cancel → failure with no code shown.</summary>
public interface IBarcodeScanner
{
    Task<Result<string>> ScanAsync(CancellationToken ct = default);
}

/// <summary>Renders the receipt PDF (Agent D). Telugu must shape correctly (FR-17).</summary>
public interface IReceiptRenderer
{
    /// <summary>Returns the path of a PDF in app cache.</summary>
    Task<Result<string>> RenderAsync(Bill bill, ShopProfile shop, CancellationToken ct = default);
}

/// <summary>OS share sheet + wa.me links. Never the paid WhatsApp Business API (ADR-4).</summary>
public interface IReceiptShare
{
    Task<Result> ShareFileAsync(string filePath, string title, CancellationToken ct = default);
    /// <summary>Opens wa.me/&lt;phone&gt;?text=… (FR-19 receipt note, FR-23 dues reminder).</summary>
    Task<Result> OpenWhatsAppAsync(string phoneE164, string message, CancellationToken ct = default);
    /// <summary>Opens the SMS app with the message filled in (customers without WhatsApp). The owner taps Send.</summary>
    Task<Result> OpenSmsAsync(string phoneE164, string message, CancellationToken ct = default);
    /// <summary>OS share sheet with plain text (feedback when no support number is configured).</summary>
    Task<Result> ShareTextAsync(string text, string title, CancellationToken ct = default);
}

/// <summary>Google Play Billing (Agent D). FR-34/35.</summary>
public interface ISubscriptionService
{
    /// <summary>
    /// CONTRACT: answers from local state immediately — never waits on the network or Play. Billing calls it inside
    /// the bill's database transaction; a slow answer there froze the whole app (emulator without Play services).
    /// Implementations refresh in the background.
    /// </summary>
    Task<SubscriptionStatus> GetStatusAsync(CancellationToken ct = default);
    Task<Result<SubscriptionStatus>> PurchaseAsync(SubscriptionPlan plan, CancellationToken ct = default);
    Task<Result<SubscriptionStatus>> RestoreAsync(CancellationToken ct = default);
}

public enum SubscriptionPlan { Monthly = 0, Yearly = 1 }
/// <param name="NeedsReconfirm">A2-11: still Pro (a paying shop is never blocked at the counter for being offline),
/// but the last confirmation with Play is older than the offline grace — the counter asks to connect.</param>
public sealed record SubscriptionStatus(bool IsPro, SubscriptionPlan? Plan, DateTimeOffset? RenewsOn, bool NeedsReconfirm = false);

/// <summary>FR-37. Copies the SQLite file out/in; restore validates schema version first.</summary>
public interface IBackupService
{
    Task<Result<string>> ExportAsync(CancellationToken ct = default);   // returns file path to share
    Task<Result> RestoreAsync(string filePath, CancellationToken ct = default);
}

/// <summary>
/// Automatic data safety. Rolling validated copies of the database are kept on the phone; if the live file is ever
/// found damaged at startup it is moved aside and the newest good copy is put back automatically.
/// </summary>
public interface IDataSafety
{
    /// <summary>Saves a validated copy if anything changed since the last one. Cheap when nothing changed.</summary>
    Task<Result> SnapshotAsync(CancellationToken ct = default);
    /// <summary>What startup recovery did, returned once (null when the data opened normally or it was already shown).</summary>
    DataRecoveryReport? TakeRecoveryReport();
}

/// <param name="RestoredFrom">When the copy that was put back was saved; null when no good copy existed.</param>
/// <param name="Reason">Why the live file was rejected (for the log, not shown to the shopkeeper).</param>
public sealed record DataRecoveryReport(DateTimeOffset At, DateTimeOffset? RestoredFrom, string Reason);

/// <summary>H11: zips the rolling log files into the shareable cache folder; returns the zip path.</summary>
public interface ILogExporter
{
    Task<Result<string>> ExportAsync(CancellationToken ct = default);
}

/// <summary>
/// M4: the v2 sync seam. v1 registers nothing, so no code path can pretend to sync. A v2 implementation drains
/// unsent Outbox rows (in order, by PayloadVersion) and returns how many it delivered.
/// </summary>
public interface ISyncService
{
    Task<Result<int>> DrainAsync(CancellationToken ct = default);
}

/// <summary>Looks up Telugu/English strings from Resources/strings.*.json (FR-02, NFR-04).</summary>
public interface ILocalizer
{
    UiLanguage Language { get; set; }
    string this[string key] { get; }
    string Error(Error error);
    string Format(string key, params object[] args);
    string UnitLabel(Angadi.Domain.Items.Unit unit);
}

/// <summary>Testable time. Bills, reports and insights never call DateTime.Now directly.</summary>
public interface IClock
{
    DateTimeOffset Now { get; }
    DateOnly Today { get; }
}
