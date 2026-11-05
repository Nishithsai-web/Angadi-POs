using System.Text;
using System.Text.Json;
using Angadi.Application.Chain;
using Angadi.Application.Common;
using Microsoft.Extensions.Logging;

namespace Angadi.Infrastructure.Persistence;

/// <summary>
/// CHAIN-1: files/chain.json holds this phone's chain code and the branch reports it received. It is per PHONE, not
/// per shop file, and not in the database: a shop switch keeps it, and a backup never carries the code or another
/// shop's takings. Missing or damaged → no code and no branches (the owner sets the code and imports again); never
/// throws into the app. The shop's own stable id lives in its database (Meta "chain.uid").
/// </summary>
internal sealed class ChainStore(string path, AngadiDb db, ILogger<ChainStore> log) : IChainStore
{
    private const string UidKey = "chain.uid";
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<string?> GetCodeAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return (await LoadAsync(ct).ConfigureAwait(false)).Code; }
        finally { _gate.Release(); }
    }

    public async Task SetCodeAsync(string code, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var state = await LoadAsync(ct).ConfigureAwait(false);
            await SaveAsync(state with { Code = code }, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<string> GetShopUidAsync(CancellationToken ct = default)
    {
        var existing = await db.ReadAsync(c => c.Find<MetaRow>(UidKey)?.Value, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(existing)) return existing;
        var created = Guid.NewGuid().ToString("N");
        // INSERT OR IGNORE: two callers racing never end up with two ids; both read back the winner.
        await db.RunAsync(c => c.Execute("INSERT OR IGNORE INTO Meta (Key, Value) VALUES (?, ?)", UidKey, created), ct).ConfigureAwait(false);
        return await db.ReadAsync(c => c.Find<MetaRow>(UidKey)?.Value ?? created, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<BranchPack>> ListAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return (await LoadAsync(ct).ConfigureAwait(false)).Branches; }
        finally { _gate.Release(); }
    }

    public async Task<Result<ImportOutcome>> MergeAsync(BranchPack pack, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var state = await LoadAsync(ct).ConfigureAwait(false);
            var branches = state.Branches.ToList();
            var i = branches.FindIndex(b => b.ShopUid == pack.ShopUid);
            ImportOutcome outcome;
            if (i < 0)
            {
                if (branches.Count >= ChainRules.MaxBranches) return Result<ImportOutcome>.Failure(Error.With(ErrorCodes.ChainTooManyBranches, ChainRules.MaxBranches));
                branches.Add(pack with { Days = Trim(pack.Days) });
                outcome = ImportOutcome.Added;
            }
            else
            {
                var old = branches[i];
                // A replayed or older pack must never roll a branch's numbers back.
                if (pack.GeneratedAt <= old.GeneratedAt) return Result<ImportOutcome>.Success(ImportOutcome.AlreadyHaveNewer);
                var merged = old.Days.Where(d => pack.Days.All(n => n.Day != d.Day)).Concat(pack.Days);
                branches[i] = pack with { Days = Trim(merged) };
                outcome = ImportOutcome.Updated;
            }
            await SaveAsync(state with { Branches = branches }, ct).ConfigureAwait(false);
            return Result<ImportOutcome>.Success(outcome);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "Chain file not written");
            return Result<ImportOutcome>.Failure(new Error(StorageFailures.Classify(ex) ?? ErrorCodes.StorageFull, ex.GetType().Name));
        }
        finally { _gate.Release(); }
    }

    public async Task RemoveAsync(string shopUid, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var state = await LoadAsync(ct).ConfigureAwait(false);
            await SaveAsync(state with { Branches = state.Branches.Where(b => b.ShopUid != shopUid).ToList() }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "Chain file not written");
        }
        finally { _gate.Release(); }
    }

    private static List<BranchDay> Trim(IEnumerable<BranchDay> days) =>
        days.OrderByDescending(d => d.Day).Take(ChainRules.KeepDays).ToList();

    private sealed record State(string? Code, IReadOnlyList<BranchPack> Branches);

    private async Task<State> LoadAsync(CancellationToken ct)
    {
        try
        {
            if (!File.Exists(path)) return new State(null, []);
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new State(null, []);
            var code = root.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            var branches = new List<BranchPack>();
            if (root.TryGetProperty("branches", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var item in arr.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { } payload)
                        try { if (ChainPackCodec.ReadPayload(payload) is { } b) branches.Add(b); }
                        catch (Exception ex) when (ex is JsonException or FormatException or OverflowException) { /* one bad entry never hides the rest */ }
            return new State(string.IsNullOrWhiteSpace(code) ? null : code, branches);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "Chain file unreadable; starting empty");
            return new State(null, []);
        }
    }

    private async Task SaveAsync(State state, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var temp = path + ".tmp";
        await using (var file = File.Create(temp))
        await using (var json = new Utf8JsonWriter(file, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            if (state.Code is not null) json.WriteString("code", state.Code);
            json.WriteStartArray("branches");
            foreach (var b in state.Branches) json.WriteStringValue(ChainPackCodec.WritePayload(b));
            json.WriteEndArray();
            json.WriteEndObject();
        }
        File.Move(temp, path, overwrite: true);   // a power cut leaves the old file, never half of a new one
        await Task.CompletedTask;
    }
}
