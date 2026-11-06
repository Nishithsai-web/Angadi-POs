using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Angadi.Application.Chain;
using Angadi.Application.Common;
using Angadi.Application.Persistence;
using Angadi.Application.Platform;
using Angadi.Application.Sync;
using Angadi.Infrastructure.Persistence;
using Angadi.Sync.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Angadi.Infrastructure.Sync;

/// <summary>The HTTP client sync uses (tests swap in a fake handler).</summary>
public sealed record SyncHttp(HttpClient Client);

/// <summary>
/// v2 phase 3 sync client. Reads pending Outbox rows (SyncState 0), sends them in batches, and marks exactly the ids
/// the server acknowledged as sent (1). A batch the server calls invalid is split in half until the one bad event is
/// found; that event is marked rejected (2) and kept on the phone, so one bad row can never block the queue or be lost.
/// Failures back off (2, 4, 8 … 60 minutes) for automatic runs; the "Sync now" button always tries.
/// </summary>
internal sealed class SyncService(
    AngadiDb db, IChainStore chain, IShopProfileRepository shop, IOptions<SyncOptions> options, SyncHttp http, IClock clock,
    ILogger<SyncService> log) : ISyncService
{
    internal const string KeyMeta = "sync.shop_key";
    private const string DeviceMeta = "sync.device_id";
    private const string LastOkMeta = "sync.last_success_ms";
    private const string FailCountMeta = "sync.fail.count";
    private const string FailAtMeta = "sync.fail.last_ms";
    private const int MaxBatchesPerRun = 50;

    private readonly SemaphoreSlim _one = new(1, 1);
    private SyncOptions Options => options.Value;
    private bool Available => Uri.TryCreate(Options.ServerUrl, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.IsLoopback);

    public async Task<SyncStatus> StatusAsync(CancellationToken ct = default)
    {
        var meta = await db.ReadAsync(c => c.Query<MetaRow>("SELECT * FROM Meta WHERE Key LIKE 'sync.%'").ToDictionary(m => m.Key, m => m.Value), ct).ConfigureAwait(false);
        var counts = await db.ReadAsync(c => (
            Pending: c.ExecuteScalar<int>("SELECT COUNT(*) FROM Outbox WHERE SyncState = 0"),
            Rejected: c.ExecuteScalar<int>("SELECT COUNT(*) FROM Outbox WHERE SyncState = 2")), ct).ConfigureAwait(false);
        DateTimeOffset? last = meta.TryGetValue(LastOkMeta, out var ms) && long.TryParse(ms, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? DateTimeOffset.FromUnixTimeMilliseconds(v) : null;
        return new SyncStatus(Available, meta.ContainsKey(KeyMeta), counts.Pending, counts.Rejected, last, Int(meta, FailCountMeta));
    }

    public async Task<Result> LinkAsync(CancellationToken ct = default)
    {
        if (!Available) return Result.Failure(ErrorCodes.SyncNotAvailable);
        if (await GetMetaAsync(KeyMeta, ct).ConfigureAwait(false) is { Length: > 0 }) return Result.Success();
        var shopId = await ShopIdAsync(ct).ConfigureAwait(false);
        var name = (await shop.GetAsync(ct).ConfigureAwait(false))?.Name?.Trim() is { Length: > 0 } n ? n : "Shop";
        if (name.Length > SyncLimits.MaxNameLength) name = name[..SyncLimits.MaxNameLength];
        try
        {
            using var response = await http.Client.PostAsJsonAsync(Url("/v1/shops/register"), new RegisterShopRequest(shopId, name),
                SyncJsonContext.Default.RegisterShopRequest, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Conflict) return Result.Failure(ErrorCodes.SyncAlreadyLinked);
            if (!response.IsSuccessStatusCode) return Result.Failure(ErrorCodes.SyncFailed, ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture));
            var body = await response.Content.ReadFromJsonAsync(SyncJsonContext.Default.RegisterShopResponse, ct).ConfigureAwait(false);
            if (body is null || string.IsNullOrWhiteSpace(body.ShopKey)) return Result.Failure(ErrorCodes.SyncFailed, "no key");
            await SetMetaAsync(KeyMeta, body.ShopKey, ct).ConfigureAwait(false);
            log.LogInformation("Cloud sync linked");
            return Result.Success();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            log.LogWarning(ex, "Cloud sync link failed");
            return Result.Failure(ErrorCodes.SyncOffline, ex.GetType().Name);
        }
    }

    public async Task<Result<int>> PushAsync(bool manual, CancellationToken ct = default)
    {
        if (!Available) return manual ? Result.Failure<int>(ErrorCodes.SyncNotAvailable) : Result.Success(0);
        if (await GetMetaAsync(KeyMeta, ct).ConfigureAwait(false) is not { Length: > 0 } key)
            return manual ? Result.Failure<int>(ErrorCodes.SyncNotLinked) : Result.Success(0);
        if (!manual && await InBackoffAsync(ct).ConfigureAwait(false)) return Result.Success(0);
        if (!await _one.WaitAsync(0, ct).ConfigureAwait(false)) return Result.Success(0);   // a run is already going
        try
        {
            var shopId = await ShopIdAsync(ct).ConfigureAwait(false);
            var device = await DeviceIdAsync(ct).ConfigureAwait(false);
            var size = Math.Clamp(Options.BatchSize, 1, SyncLimits.MaxBatch);
            var sent = 0;
            for (var run = 0; run < MaxBatchesPerRun; run++)
            {
                var take = size;
                var rows = await db.ReadAsync(c => c.Query<OutboxRow>(
                    "SELECT * FROM Outbox WHERE SyncState = 0 ORDER BY CreatedAtMs, Id LIMIT ?", take), ct).ConfigureAwait(false);
                if (rows.Count == 0) break;

                using var request = new HttpRequestMessage(HttpMethod.Post, Url($"/v1/shops/{shopId}/events"))
                {
                    Content = JsonContent.Create(new PushRequest(device, rows.Select(ToWire).ToList()), SyncJsonContext.Default.PushRequest),
                };
                request.Headers.Add(SyncLimits.KeyHeader, key);
                using var response = await http.Client.SendAsync(request, ct).ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    await FailedAsync(ct).ConfigureAwait(false);
                    return Result.Failure<int>(ErrorCodes.SyncKeyRejected);
                }
                if (response.StatusCode == HttpStatusCode.BadRequest)
                {
                    if (rows.Count == 1)
                    {
                        var bad = rows[0].Id;
                        await db.RunAsync(c => c.Execute("UPDATE Outbox SET SyncState = 2 WHERE Id = ?", bad), ct).ConfigureAwait(false);
                        log.LogWarning("Cloud sync: one event was refused as invalid and kept on the phone ({EventType})", rows[0].EventType);
                        size = Math.Clamp(Options.BatchSize, 1, SyncLimits.MaxBatch);
                    }
                    else size = Math.Max(1, rows.Count / 2);   // find the bad one by halving
                    continue;
                }
                if (!response.IsSuccessStatusCode)
                {
                    await FailedAsync(ct).ConfigureAwait(false);
                    return Result.Failure<int>(ErrorCodes.SyncFailed, ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture));
                }

                var ack = await response.Content.ReadFromJsonAsync(SyncJsonContext.Default.PushResponse, ct).ConfigureAwait(false);
                // Only ids that were in THIS batch and that the server confirmed are marked sent.
                var mine = rows.Select(r => r.Id).ToHashSet();
                var acked = (ack?.Acked ?? []).Where(mine.Contains).ToList();
                if (acked.Count > 0)
                    await db.RunAsync(c =>
                    {
                        foreach (var id in acked) c.Execute("UPDATE Outbox SET SyncState = 1 WHERE Id = ? AND SyncState = 0", id);
                        return acked.Count;
                    }, ct).ConfigureAwait(false);
                sent += acked.Count;
                if (acked.Count < rows.Count) break;   // the server kept some back: try again next run, never spin
            }
            await SetMetaAsync(LastOkMeta, clock.Now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
            await SetMetaAsync(FailCountMeta, "0", ct).ConfigureAwait(false);
            return Result.Success(sent);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            await FailedAsync(CancellationToken.None).ConfigureAwait(false);
            log.LogWarning(ex, "Cloud sync could not reach the server");
            return Result.Failure<int>(ErrorCodes.SyncOffline, ex.GetType().Name);
        }
        finally { _one.Release(); }
    }

    private static SyncEvent ToWire(OutboxRow r) => new(r.Id, r.AggregateType, r.AggregateId, r.EventType,
        string.IsNullOrWhiteSpace(r.PayloadJson) ? "{}" : r.PayloadJson, r.CreatedAtMs);

    private Uri Url(string path) => new(new Uri(Options.ServerUrl.TrimEnd('/') + "/"), path.TrimStart('/'));

    private async Task<bool> InBackoffAsync(CancellationToken ct)
    {
        var failures = int.TryParse(await GetMetaAsync(FailCountMeta, ct).ConfigureAwait(false), NumberStyles.Integer, CultureInfo.InvariantCulture, out var f) ? f : 0;
        if (failures <= 0) return false;
        if (!long.TryParse(await GetMetaAsync(FailAtMeta, ct).ConfigureAwait(false), NumberStyles.Integer, CultureInfo.InvariantCulture, out var at)) return false;
        var wait = TimeSpan.FromMinutes(Math.Min(60, Math.Pow(2, Math.Min(failures, 6))));
        return clock.Now < DateTimeOffset.FromUnixTimeMilliseconds(at) + wait;
    }

    private async Task FailedAsync(CancellationToken ct)
    {
        var failures = int.TryParse(await GetMetaAsync(FailCountMeta, ct).ConfigureAwait(false), NumberStyles.Integer, CultureInfo.InvariantCulture, out var f) ? f : 0;
        await SetMetaAsync(FailCountMeta, (failures + 1).ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
        await SetMetaAsync(FailAtMeta, clock.Now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
    }

    private async Task<Guid> ShopIdAsync(CancellationToken ct)
    {
        var uid = await chain.GetShopUidAsync(ct).ConfigureAwait(false);
        return Guid.TryParse(uid, out var g) ? g : throw new InvalidOperationException("shop uid is not a guid");
    }

    private async Task<string> DeviceIdAsync(CancellationToken ct)
    {
        if (await GetMetaAsync(DeviceMeta, ct).ConfigureAwait(false) is { Length: > 0 } id) return id;
        var created = Guid.NewGuid().ToString("N");
        await db.RunAsync(c => c.Execute("INSERT OR IGNORE INTO Meta (Key, Value) VALUES (?, ?)", DeviceMeta, created), ct).ConfigureAwait(false);
        return await GetMetaAsync(DeviceMeta, ct).ConfigureAwait(false) ?? created;
    }

    private Task<string?> GetMetaAsync(string key, CancellationToken ct) => db.ReadAsync(c => c.Find<MetaRow>(key)?.Value, ct);
    private Task SetMetaAsync(string key, string value, CancellationToken ct) => db.RunAsync(c => c.InsertOrReplace(new MetaRow { Key = key, Value = value }), ct);
    private static int Int(IReadOnlyDictionary<string, string> m, string k) => m.TryGetValue(k, out var s) && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
}
