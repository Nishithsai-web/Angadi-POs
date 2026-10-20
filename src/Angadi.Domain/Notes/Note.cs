using Angadi.Domain.Common;

namespace Angadi.Domain.Notes;

/// <summary>
/// A shopkeeper's own note: "order 2 bags of rice from Venkat", "Ravi will pay Sunday". Kept on the device,
/// travels in backups. Done notes stay listed (greyed) until deleted, so nothing disappears by accident.
/// </summary>
public sealed class Note : Entity
{
    public const int MaxLength = 1000;

    public string Text { get; set; } = "";
    public bool IsDone { get; set; }
}
