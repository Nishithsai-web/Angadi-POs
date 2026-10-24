using System.Text.Json;
using Angadi.Application.Common;
using Angadi.Application.Printing;

namespace Angadi.Infrastructure.Printing;

/// <summary>
/// files/printer.json — the phone's printer, shared by every shop on the phone (it is hardware, not books).
/// Missing or unreadable → no printer (the shopkeeper picks it again); never throws into billing.
/// </summary>
internal sealed class PrinterSettingsStore(string path) : IPrinterSettingsStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<PrinterSettings> LoadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!File.Exists(path)) return PrinterSettings.None;
            var dto = JsonSerializer.Deserialize<Dto>(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false), Json);
            if (dto is null) return PrinterSettings.None;
            var paper = Enum.IsDefined((PaperWidth)dto.Paper) ? (PaperWidth)dto.Paper : PaperWidth.Mm58;
            return new PrinterSettings(string.IsNullOrWhiteSpace(dto.Address) ? null : dto.Address, dto.Name, paper, dto.AutoPrint);
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
            var dto = new Dto { Address = settings.Address, Name = settings.Name, Paper = (int)settings.Paper, AutoPrint = settings.AutoPrint };
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(dto, Json), ct).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed class Dto
    {
        public string? Address { get; set; }
        public string? Name { get; set; }
        public int Paper { get; set; } = (int)PaperWidth.Mm58;
        public bool AutoPrint { get; set; }
    }
}

/// <summary>Registered by Infrastructure; the Android head replaces it with the real SPP transport.</summary>
internal sealed class NoBluetoothPrinterTransport : IBluetoothPrinterTransport
{
    public Task<Result<IReadOnlyList<PairedPrinter>>> ListPairedAsync(CancellationToken ct = default) =>
        Task.FromResult(Result.Failure<IReadOnlyList<PairedPrinter>>(ErrorCodes.PrinterNoBluetooth));

    public Task<Result> SendAsync(string address, byte[] data, CancellationToken ct = default) =>
        Task.FromResult(Result.Failure(ErrorCodes.PrinterNoBluetooth));
}
