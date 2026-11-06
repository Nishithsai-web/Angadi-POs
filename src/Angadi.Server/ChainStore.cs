using System.Security.Cryptography;
using System.Text;
using Angadi.Sync.Contracts;
using Npgsql;

namespace Angadi.Server;

/// <summary>Join codes: 10 characters from an alphabet without look-alikes (no 0/O, 1/I/L); about 49 bits.</summary>
internal static class JoinCodes
{
    private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    public static string New() => string.Create(SyncLimits.JoinCodeLength, 0, (span, _) =>
    {
        for (var i = 0; i < span.Length; i++) span[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
    });

    /// <summary>Spaces and dashes the owner may type are ignored; case does not matter.</summary>
    public static string Normalize(string? code) =>
        new string((code ?? "").Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    public static byte[] Hash(string code) => SHA256.HashData(Encoding.UTF8.GetBytes("angadi-join:" + Normalize(code)));
}

internal enum ChainOutcome { Ok, AlreadyInAChain, NoSuchCode }

/// <summary>Chains and the head's dashboard. Every query is scoped to the caller's own chain.</summary>
internal sealed class ChainStore(NpgsqlDataSource data)
{
    public async Task<(ChainOutcome Outcome, CreateChainResponse? Created)> CreateAsync(Guid headShopId, string name, CancellationToken ct)
    {
        await using var conn = await data.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        if (await MemberChainAsync(conn, tx, headShopId, ct) is not null) return (ChainOutcome.AlreadyInAChain, null);
        var id = Guid.NewGuid();
        var code = JoinCodes.New();
        await using (var cmd = new NpgsqlCommand(
            "INSERT INTO chains (id, name, head_shop_id, join_hash) VALUES (@id, @name, @head, @hash) ON CONFLICT DO NOTHING", conn, tx))
        {
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.AddWithValue("name", name);
            cmd.Parameters.AddWithValue("head", headShopId);
            cmd.Parameters.AddWithValue("hash", JoinCodes.Hash(code));
            if (await cmd.ExecuteNonQueryAsync(ct) == 0) return (ChainOutcome.AlreadyInAChain, null);
        }
        try
        {
            await AddMemberAsync(conn, tx, headShopId, id, ct);
            await tx.CommitAsync(ct);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return (ChainOutcome.AlreadyInAChain, null);   // two requests raced; the first one won
        }
        return (ChainOutcome.Ok, new CreateChainResponse(id, name, code));
    }

    public async Task<(ChainOutcome Outcome, ChainMembership? Joined)> JoinAsync(Guid shopId, string code, CancellationToken ct)
    {
        await using var conn = await data.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        if (await MemberChainAsync(conn, tx, shopId, ct) is not null) return (ChainOutcome.AlreadyInAChain, null);
        Guid chainId;
        await using (var find = new NpgsqlCommand("SELECT id FROM chains WHERE join_hash = @hash", conn, tx))
        {
            find.Parameters.AddWithValue("hash", JoinCodes.Hash(code));
            if (await find.ExecuteScalarAsync(ct) is not Guid g) return (ChainOutcome.NoSuchCode, null);
            chainId = g;
        }
        try
        {
            await AddMemberAsync(conn, tx, shopId, chainId, ct);
            await tx.CommitAsync(ct);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return (ChainOutcome.AlreadyInAChain, null);
        }
        return (ChainOutcome.Ok, await MembershipAsync(shopId, ct));
    }

    public async Task<ChainMembership?> MembershipAsync(Guid shopId, CancellationToken ct)
    {
        await using var cmd = data.CreateCommand(
            "SELECT c.id, c.name, c.head_shop_id = @shop, (SELECT COUNT(*) FROM chain_members x WHERE x.chain_id = c.id) " +
            "FROM chain_members m JOIN chains c ON c.id = m.chain_id WHERE m.shop_id = @shop");
        cmd.Parameters.AddWithValue("shop", shopId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new ChainMembership(r.GetGuid(0), r.GetString(1), r.GetBoolean(2), (int)r.GetInt64(3)) : null;
    }

    /// <summary>The head's view: every branch's bills, sales (voided bills excluded), returns and dues collected, day by day in India time.</summary>
    public async Task<ChainDashboard?> DashboardAsync(Guid headShopId, int days, long nowMs, CancellationToken ct)
    {
        Guid chainId;
        string chainName;
        await using (var head = data.CreateCommand("SELECT id, name FROM chains WHERE head_shop_id = @shop"))
        {
            head.Parameters.AddWithValue("shop", headShopId);
            await using var r = await head.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;   // not the head of a chain: nothing to show
            (chainId, chainName) = (r.GetGuid(0), r.GetString(1));
        }

        var branches = new List<(Guid Id, string Name, bool IsHead)>();
        await using (var m = data.CreateCommand(
            "SELECT s.id, s.name, s.id = @head FROM chain_members cm JOIN shops s ON s.id = cm.shop_id WHERE cm.chain_id = @chain ORDER BY s.id = @head DESC, s.name"))
        {
            m.Parameters.AddWithValue("chain", chainId);
            m.Parameters.AddWithValue("head", headShopId);
            await using var r = await m.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) branches.Add((r.GetGuid(0), r.GetString(1), r.GetBoolean(2)));
        }
        var ids = branches.Select(b => b.Id).ToArray();
        var since = nowMs - (long)days * 24 * 60 * 60 * 1000;

        var perDay = new Dictionary<Guid, List<DayTotal>>();
        await using (var sales = data.CreateCommand("""
            SELECT f.shop_id,
                   to_char(to_timestamp(f.created_at_ms / 1000.0) AT TIME ZONE 'Asia/Kolkata', 'YYYY-MM-DD') AS day,
                   COUNT(*),
                   COALESCE(SUM(CASE WHEN jsonb_typeof(f.payload->'grandTotalPaise') = 'number' THEN (f.payload->>'grandTotalPaise')::bigint ELSE 0 END), 0)::bigint
            FROM events f
            WHERE f.shop_id = ANY(@shops) AND f.event_type = 'BillFinalized' AND f.created_at_ms >= @since
              AND NOT EXISTS (SELECT 1 FROM events v WHERE v.shop_id = f.shop_id AND v.event_type = 'BillVoided' AND v.aggregate_id = f.aggregate_id)
            GROUP BY 1, 2 ORDER BY 1, 2
            """))
        {
            sales.Parameters.AddWithValue("shops", ids);
            sales.Parameters.AddWithValue("since", since);
            await using var r = await sales.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var shop = r.GetGuid(0);
                if (!perDay.TryGetValue(shop, out var list)) perDay[shop] = list = [];
                list.Add(new DayTotal(r.GetString(1), (int)r.GetInt64(2), r.GetInt64(3)));
            }
        }

        var money = new Dictionary<Guid, (long Returns, long Dues, long? Last)>();
        await using (var other = data.CreateCommand("""
            SELECT shop_id,
                   COALESCE(SUM(CASE WHEN event_type = 'ReturnRecorded' AND created_at_ms >= @since AND jsonb_typeof(payload->'refundPaise') = 'number'
                                     THEN (payload->>'refundPaise')::bigint ELSE 0 END), 0)::bigint,
                   COALESCE(SUM(CASE WHEN event_type = 'DuesPaymentRecorded' AND created_at_ms >= @since AND jsonb_typeof(payload->'amountPaise') = 'number'
                                     THEN (payload->>'amountPaise')::bigint ELSE 0 END), 0)::bigint,
                   MAX(created_at_ms)
            FROM events WHERE shop_id = ANY(@shops) GROUP BY shop_id
            """))
        {
            other.Parameters.AddWithValue("shops", ids);
            other.Parameters.AddWithValue("since", since);
            await using var r = await other.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) money[r.GetGuid(0)] = (r.GetInt64(1), r.GetInt64(2), r.IsDBNull(3) ? null : r.GetInt64(3));
        }

        var live = branches.Select(b =>
        {
            var d = perDay.TryGetValue(b.Id, out var l) ? l : [];
            var mo = money.TryGetValue(b.Id, out var x) ? x : (0L, 0L, (long?)null);
            return new BranchLive(b.Id, b.Name, b.IsHead, mo.Item3, d.Sum(t => t.Bills), d.Sum(t => t.SalesPaise), mo.Item1, mo.Item2, d);
        }).ToList();
        return new ChainDashboard(chainId, chainName, days, live);
    }

    private static async Task<Guid?> MemberChainAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid shopId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT chain_id FROM chain_members WHERE shop_id = @shop FOR UPDATE", conn, tx);
        cmd.Parameters.AddWithValue("shop", shopId);
        return await cmd.ExecuteScalarAsync(ct) is Guid g ? g : null;
    }

    private static async Task AddMemberAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid shopId, Guid chainId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("INSERT INTO chain_members (shop_id, chain_id) VALUES (@shop, @chain)", conn, tx);
        cmd.Parameters.AddWithValue("shop", shopId);
        cmd.Parameters.AddWithValue("chain", chainId);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
