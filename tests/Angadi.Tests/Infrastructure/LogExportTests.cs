using Angadi.Application.Common;
using Angadi.Infrastructure.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Angadi.Tests.Infrastructure;

/// <summary>
/// LOG-3c: the log exporter's failure path. Only the success path was tested before; a failure must
/// surface as a LOG_EXPORT_FAILED Result (the designed, translated message), never as an exception
/// that the UI turns into a generic UNEXPECTED.
/// </summary>
public class LogExportTests
{
    [Fact]
    public async Task Export_Success_WritesAZipAndReturnsItsPath()
    {
        var root = Path.Combine(Path.GetTempPath(), "angadi-logexport", Guid.NewGuid().ToString("N"));
        try
        {
            var files = new LogFiles(Path.Combine(root, "logs"));
            Directory.CreateDirectory(files.Directory);
            await File.WriteAllTextAsync(Path.Combine(files.Directory, "angadi-20261020.log"), "line");
            var exporter = new LogExporter(files, Path.Combine(root, "out"), new FakeClock(), NullLogger<LogExporter>.Instance);

            var r = await exporter.ExportAsync();

            Assert.True(r.IsSuccess, r.Error.Code);
            Assert.True(File.Exists(r.Value));
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task Export_WhenTheOutputFolderCannotBeCreated_ReturnsLogExportFailed()
    {
        var root = Path.Combine(Path.GetTempPath(), "angadi-logexport", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var blocker = Path.Combine(root, "blocker");
        await File.WriteAllTextAsync(blocker, "a file, not a folder");
        try
        {
            var files = new LogFiles(Path.Combine(root, "logs"));
            // The output path's parent is a FILE: creating the folder must fail inside the exporter.
            var exporter = new LogExporter(files, Path.Combine(blocker, "out"), new FakeClock(), NullLogger<LogExporter>.Instance);

            var r = await exporter.ExportAsync();

            Assert.True(r.IsFailure);
            Assert.Equal(ErrorCodes.LogExportFailed, r.Error.Code);
        }
        finally { TryDelete(root); }
    }

    private static void TryDelete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
