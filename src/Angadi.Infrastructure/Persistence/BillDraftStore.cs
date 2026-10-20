using Angadi.Application.Persistence;

namespace Angadi.Infrastructure.Persistence;

/// <summary>
/// The counter's unfinished bill, one Meta row ("pos.draft"). In the database rather than app preferences so it
/// shares the bill's transaction: finalize clears it in the same commit that saves the bill.
/// </summary>
internal sealed class BillDraftStore(AngadiDb db) : IBillDraftStore
{
    internal const string Key = "pos.draft";

    public Task<string?> LoadAsync(CancellationToken ct = default)
        => db.RunAsync(c => c.Find<MetaRow>(Key)?.Value, ct);

    public Task SaveAsync(string json, CancellationToken ct = default)
        => db.RunAsync(c => c.InsertOrReplace(new MetaRow { Key = Key, Value = json }), ct);

    public Task ClearAsync(CancellationToken ct = default)
        => db.RunAsync(c => c.Execute("DELETE FROM Meta WHERE Key = ?", Key), ct);

    public Task<bool> BillExistsAsync(Guid billId, CancellationToken ct = default)
        => db.RunAsync(c => c.Find<BillRow>(billId) is not null, ct);
}
