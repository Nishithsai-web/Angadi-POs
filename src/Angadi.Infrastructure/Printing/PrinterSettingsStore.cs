using System.Text.Json;
using Angadi.Application.Common;
using Angadi.Application.Printing;

namespace Angadi.Infrastructure.Printing;

/// <summary>
/// files/printer.json — the phone's printer. It is hardware, not books: not in the database, so a restored backup or
/// a new phone never points at a printer that is not there. Missing or unreadable → no printer (the owner picks it
/// again); never throws into billing.
/// </summary>
internal sealed class PrinterSettingsStore(string path) : IPrinterSettingsStore
{
    // Hand-written JSON (JsonDocument / Utf8JsonWriter): no reflection, so the trimmed Release build keeps working.
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<PrinterSettings> LoadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!File.Exists(path)) return PrinterSettings.None;
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return PrinterSettings.None;
            var rawAddress = Text(root, "address");
            var address = string.IsNullOrWhiteSpace(rawAddress) ? null : rawAddress.Trim();
            var paperValue = root.TryGetProperty("paper", out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var n) ? n : (int)PaperWidth.Mm58;
            var paper = Enum.IsDefined((PaperWidth)paperValue) ? (PaperWidth)paperValue : PaperWidth.Mm58;
            var auto = root.TryGetProperty("autoPrint", out var a) && a.ValueKind == JsonValueKind.True;
            return new PrinterSettings(address, address is null ? null : Text(root, "name"), paper, address is not null && auto);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return PrinterSettings.None;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(PrinterSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var temp = path + ".tmp";
            await using (var file = File.Create(temp))
            await using (var json = new Utf8JsonWriter(file, new JsonWriterOptions { Indented = true }))
            {
                json.WriteStartObject();
                json.WriteString("address", settings.Address);
                json.WriteString("name", settings.Name);
                json.WriteNumber("paper", (int)settings.Paper);
                json.WriteBoolean("autoPrint", settings.AutoPrint);
                json.WriteEndObject();
                await json.FlushAsync(ct).ConfigureAwait(false);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>Registered by Infrastructure; the Android head replaces it with the real Bluetooth SPP transport.</summary>
internal sealed class NoBluetoothPrinterTransport : IBluetoothPrinterTransport
{
    public Task<Result<IReadOnlyList<PairedPrinter>>> ListPairedAsync(CancellationToken ct = default) =>
        Task.FromResult(Result.Failure<IReadOnlyList<PairedPrinter>>(ErrorCodes.PrinterNoBluetooth));

    public Task<Result> SendAsync(string address, byte[] job, CancellationToken ct = default) =>
        Task.FromResult(Result.Failure(ErrorCodes.PrinterNoBluetooth));
}
