using Angadi.Application.Common;

namespace Angadi.Application.Persistence;

/// <summary>
/// The device has no space left for the database to write. Infrastructure throws this in place of the driver's
/// own exception so the request pipeline can show "phone storage is full" instead of a generic error.
/// </summary>
public sealed class StorageFullException(string message, Exception? inner = null) : IOException(message, inner);

/// <summary>The single database lock was not released within its time limit.</summary>
public sealed class DatabaseBusyException(string message) : TimeoutException(message);

/// <summary>Maps the exceptions storage can throw to the error code the shopkeeper sees.</summary>
public static class StorageFailures
{
    // ENOSPC on Linux/Android; ERROR_DISK_FULL / ERROR_HANDLE_DISK_FULL on Windows.
    private const int Enospc = 28;
    private const int WinDiskFull = unchecked((int)0x80070070);
    private const int WinHandleDiskFull = unchecked((int)0x80070027);

    public static bool IsDiskFull(Exception ex) => ex switch
    {
        StorageFullException => true,
        IOException io => io.HResult is Enospc or WinDiskFull or WinHandleDiskFull,
        _ => false,
    };

    /// <summary>The ErrorCodes value for a storage exception, or null when it is something else.</summary>
    public static string? Classify(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (IsDiskFull(e)) return ErrorCodes.StorageFull;
            if (e is DatabaseBusyException) return ErrorCodes.DatabaseBusy;
        }
        return null;
    }
}
