using System.Security.Cryptography;
using System.Text;
using Angadi.Sync.Contracts;
using Npgsql;
using NpgsqlTypes;

namespace Angadi.Server;

/// <summary>Shop keys: 256 random bits, given to the phone once; only the SHA-256 is stored.</summary>
internal static class ShopKeys
{
    public static string New() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
    public static byte[] Hash(string key) => SHA256.HashData(Encoding.UTF8.GetBytes(key));
}

/// <summary>All Postgres access. Every statement is filtered by shop id; nothing reads across shops.</summary>
internal sealed class SyncStore(NpgsqlDataSource data)
{
    private static readonly byte[] NoShop = SHA256.HashData("no-such-shop"u8);

    public async Task<bool> RegisterAsync(Guid shopId, string name, byte[] keyHash, CancellationToken ct)
    {
        await using var cmd = data.CreateCommand("INSERT INTO shops (id, name, key_hash) VALUES (@id, @name, @hash) ON CONFLICT (id) DO NOTHING");
        cmd.Parameters.AddWithValue("id", shopId);
        cmd.Parameters.AddWithValue("name", name);
        cmd.Parameters.AddWithValue("hash", keyHash);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    /// <summary>Constant-time key check; an unknown shop costs the same as a wrong key, so ids cannot be probed.</summary>
    public async Task<bool> CheckKeyAsync(Guid shopId, string? key, CancellationToken ct)
    {
        await using var cmd = data.CreateCommand("SELECT key_hash FROM shops WHERE id = @id");
        cmd.Parameters.AddWithValue("id", shopId);
        var stored = await cmd.ExecuteScalarAsync(ct) as byte[];
        var given = ShopKeys.Hash(key ?? "");
        var match = CryptographicOperations.FixedTimeEquals(stored ?? NoShop, given);
        return stored is not null && !string.IsNullOrEmpty(key) && match;
    }

    /// <summary>Stores each event once (shop + event id); replays and overlapping batches are harmless. One transaction.</summary>
    public async Task<long> PushAsync(Guid shopId, string deviceId, IReadOnlyList<SyncEvent> events, CancellationToken ct)
    {
        await using var conn = await data.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        foreach (var e in events)
        {
            await using var cmd = new NpgsqlCommand(
                "INSERT INTO events (shop_id, event_id, aggregate_type, aggregate_id, event_type, payload, created_at_ms, device_id) " +
                "VALUES (@shop, @id, @atype, @aid, @etype, @payload, @at, @device) ON CONFLICT (shop_id, event_id) DO NOTHING", conn, tx);
            cmd.Parameters.AddWithValue("shop", shopId);
            cmd.Parameters.AddWithValue("id", e.Id);
            cmd.Parameters.AddWithValue("atype", e.AggregateType);
            cmd.Parameters.AddWithValue("aid", e.AggregateId);
            cmd.Parameters.AddWithValue("etype", e.EventType);
            cmd.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, e.PayloadJson);
            cmd.Parameters.AddWithValue("at", e.CreatedAtMs);
            cmd.Parameters.AddWithValue("device", deviceId);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        long last;
        await using (var seq = new NpgsqlCommand("SELECT COALESCE(MAX(seq), 0) FROM events WHERE shop_id = @shop", conn, tx))
        {
            seq.Parameters.AddWithValue("shop", shopId);
            last = Convert.ToInt64(await seq.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
        }
        await tx.CommitAsync(ct);
        return last;
    }

    public async Task<EventsPage> ReadAsync(Guid shopId, long after, int limit, CancellationToken ct)
    {
        await using var cmd = data.CreateCommand(
            "SELECT seq, event_id, aggregate_type, aggregate_id, event_type, payload::text, created_at_ms, device_id " +
            "FROM events WHERE shop_id = @shop AND seq > @after ORDER BY seq LIMIT @take");
        cmd.Parameters.AddWithValue("shop", shopId);
        cmd.Parameters.AddWithValue("after", after);
        cmd.Parameters.AddWithValue("take", limit + 1);
        var list = new List<StoredEvent>();
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                list.Add(new StoredEvent(r.GetInt64(0), r.GetGuid(1), r.GetString(2), r.GetGuid(3), r.GetString(4), r.GetString(5), r.GetInt64(6), r.GetString(7)));
        var more = list.Count > limit;
        if (more) list.RemoveAt(list.Count - 1);
        return new EventsPage(list, list.Count > 0 ? list[^1].Seq : after, more);
    }

    public async Task<ShopSummary?> SummaryAsync(Guid shopId, CancellationToken ct)
    {
        string? name;
        await using (var shop = data.CreateCommand("SELECT name FROM shops WHERE id = @id"))
        {
            shop.Parameters.AddWithValue("id", shopId);
            name = await shop.ExecuteScalarAsync(ct) as string;
        }
        if (name is null) return null;
        var byType = new Dictionary<string, long>(StringComparer.Ordinal);
        long count = 0, lastSeq = 0;
        long? lastAt = null;
        await using var cmd = data.CreateCommand(
            "SELECT event_type, COUNT(*), MAX(seq), MAX(created_at_ms) FROM events WHERE shop_id = @id GROUP BY event_type");
        cmd.Parameters.AddWithValue("id", shopId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var n = r.GetInt64(1);
            byType[r.GetString(0)] = n;
            count += n;
            lastSeq = Math.Max(lastSeq, r.GetInt64(2));
            lastAt = Math.Max(lastAt ?? long.MinValue, r.GetInt64(3));
        }
        return new ShopSummary(shopId, name, count, lastSeq, lastAt, byType);
    }
}
