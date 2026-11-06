using SQLite;

namespace Angadi.Infrastructure.Persistence;

/// <summary>One upgrade step: brings a database from <c>Version - 1</c> to <paramref name="Version"/>. Runs inside the upgrade transaction.</summary>
internal sealed record MigrationStep(int Version, string Name, Action<SQLiteConnection> Up);

/// <summary>
/// DATA-1: the ordered upgrade history. Rules for a new schema version:
/// 1. Bump <see cref="AngadiDb.SchemaVersion"/>.
/// 2. Add the new tables/columns to the CreateTable list (sqlite-net adds missing ones).
/// 3. Append ONE step here with that version. Put anything CreateTable cannot do in it: copying data into a new table,
///    back-filling a column, re-typing or splitting (create new, copy, drop old). Never edit a released step.
/// Steps run in order, once, all-or-nothing; a test checks the list is complete and ascending.
/// </summary>
internal static class Migrations
{
    private static void Additive(SQLiteConnection _) { }   // the tables/columns themselves come from CreateTable

    public static readonly IReadOnlyList<MigrationStep> Steps =
    [
        new(2, "customer account columns + Note", Additive),
        new(3, "shop's own UPI QR", Additive),
        new(4, "split tender (BillPayment) + returns", Additive),
        new(5, "batches, suppliers, purchases, orders", Additive),
        new(6, "staff roles + activity log", Additive),
        new(7, "staff money: Worker, Attendance, PayEntry", Additive),
    ];
}
