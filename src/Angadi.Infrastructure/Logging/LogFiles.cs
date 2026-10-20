using System.IO.Compression;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

namespace Angadi.Infrastructure.Logging;

/// <summary>
/// The whole v1 crash-reporting system (blueprint §6): rolling daily log files, 5 MB per file.
/// FIX(LOG-3i, 2026-10-20, FIX-3): the retention comment said "7 days" — Serilog's
/// retainedFileCountLimit counts FILES, not days: at most <see cref="RetainedDays"/> files are kept
/// (≈35 MB total), and a busy day that rolls several files shortens the real history below 7 days.
/// The comment now says what the code does. Logged in docs/AGENT_FIX_LOG.md.
/// Settings → Export logs zips them for WhatsApp (FR-36).
/// </summary>
public sealed class LogFiles(string directory)
{
    public const long MaxFileBytes = 5 * 1024 * 1024;
    public const int RetainedDays = 7;

    public string Directory { get; } = directory;

    /// <summary>Call once in MauiProgram before building the app.</summary>
    public ILoggerProvider CreateProvider(bool debug)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var logger = new LoggerConfiguration()
            .MinimumLevel.Is(debug ? LogEventLevel.Debug : LogEventLevel.Information)
            .Enrich.FromLogContext()
            .WriteTo.File(
                Path.Combine(Directory, "angadi-.log"),
                formatProvider: System.Globalization.CultureInfo.InvariantCulture,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: RetainedDays,
                fileSizeLimitBytes: MaxFileBytes,
                rollOnFileSizeLimit: true,
                shared: false,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext} {CorrelationId} {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
        return new Serilog.Extensions.Logging.SerilogLoggerProvider(logger, dispose: true);
    }

    /// <summary>FR-36: one zip of every log file, written to <paramref name="outputDirectory"/>.</summary>
    public string ExportZip(string outputDirectory, DateTimeOffset now)
    {
        System.IO.Directory.CreateDirectory(outputDirectory);
        var zipPath = Path.Combine(outputDirectory, $"angadi-logs-{now:yyyyMMdd-HHmm}.zip");
        if (File.Exists(zipPath)) File.Delete(zipPath);

        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        if (System.IO.Directory.Exists(Directory))
        {
            foreach (var file in System.IO.Directory.GetFiles(Directory, "*.log"))
            {
                // The live file is open for writing; copy via a shared read.
                using var src = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var entry = zip.CreateEntry(Path.GetFileName(file), CompressionLevel.Optimal);
                using var dst = entry.Open();
                src.CopyTo(dst);
            }
        }
        return zipPath;
    }
}

/// <summary>H11 adapter: LogFiles behind the Application port, IO failures turned into a Result.</summary>
public sealed class LogExporter(LogFiles files, string outputDirectory, Angadi.Application.Platform.IClock clock, ILogger<LogExporter> log)
    : Angadi.Application.Platform.ILogExporter
{
    public Task<Angadi.Application.Common.Result<string>> ExportAsync(CancellationToken ct = default)
    {
        try
        {
            return Task.FromResult(Angadi.Application.Common.Result.Success(files.ExportZip(outputDirectory, clock.Now)));
        }
        // FIX(LOG-3c, 2026-10-20, FIX-3): Flaw: the catch covered only IO/Unauthorized, so a corrupt zip
        // (InvalidData) or an archive-state error (InvalidOperation) escaped as UNEXPECTED instead of the
        // designed LOG_EXPORT_FAILED message. Fix: broaden the filter to the failure shapes a zip export
        // can actually produce. Why: the shopkeeper should hear "logs could not be exported", not a generic
        // crash message, when support asks for logs. Logged in docs/AGENT_FIX_LOG.md.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            log.LogError(ex, "Log export failed");
            return Task.FromResult(Angadi.Application.Common.Result.Failure<string>(
                Angadi.Application.Common.ErrorCodes.LogExportFailed));
        }
    }
}
