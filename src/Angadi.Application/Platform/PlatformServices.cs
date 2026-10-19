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
}

/// <summary>Google Play Billing (Agent D). FR-34/35.</summary>
public interface ISubscriptionService
{
    Task<SubscriptionStatus> GetStatusAsync(CancellationToken ct = default);
    Task<Result<SubscriptionStatus>> PurchaseAsync(SubscriptionPlan plan, CancellationToken ct = default);
    Task<Result<SubscriptionStatus>> RestoreAsync(CancellationToken ct = default);
}

public enum SubscriptionPlan { Monthly = 0, Yearly = 1 }
public sealed record SubscriptionStatus(bool IsPro, SubscriptionPlan? Plan, DateTimeOffset? RenewsOn);

/// <summary>FR-37. Copies the SQLite file out/in; restore validates schema version first.</summary>
public interface IBackupService
{
    Task<Result<string>> ExportAsync(CancellationToken ct = default);   // returns file path to share
    Task<Result> RestoreAsync(string filePath, CancellationToken ct = default);
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
