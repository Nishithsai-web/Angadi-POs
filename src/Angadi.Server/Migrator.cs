using Npgsql;

namespace Angadi.Server;

/// <summary>
/// Ordered SQL scripts (Sql/NNN_name.sql, embedded), each applied once inside its own transaction and recorded in
/// schema_migrations. A Postgres advisory lock makes two server instances starting together take turns.
/// </summary>
internal static class Migrator
{
    private const long LockId = 72_727_201;
    private const string Prefix = "Angadi.Server.Sql.";

    public static async Task RunAsync(NpgsqlDataSource data, ILogger log, CancellationToken ct)
    {
        await using var conn = await data.OpenConnectionAsync(ct);
        await ExecAsync(conn, null, "SELECT pg_advisory_lock(" + LockId + ")", ct);
        try
        {
            await ExecAsync(conn, null,
                "CREATE TABLE IF NOT EXISTS schema_migrations (version int PRIMARY KEY, name text NOT NULL, applied_at timestamptz NOT NULL DEFAULT now())", ct);
            var applied = new HashSet<int>();
            await using (var read = new NpgsqlCommand("SELECT version FROM schema_migrations", conn))
            await using (var r = await read.ExecuteReaderAsync(ct))
                while (await r.ReadAsync(ct)) applied.Add(r.GetInt32(0));

            var assembly = typeof(Migrator).Assembly;
            foreach (var resource in assembly.GetManifestResourceNames()
                         .Where(n => n.StartsWith(Prefix, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
                         .OrderBy(n => n, StringComparer.Ordinal))
            {
                var name = resource[Prefix.Length..^".sql".Length];
                var version = int.Parse(name.Split('_')[0], System.Globalization.CultureInfo.InvariantCulture);
                if (applied.Contains(version)) continue;
                string sql;
                await using (var stream = assembly.GetManifestResourceStream(resource)!)
                using (var reader = new StreamReader(stream))
                    sql = await reader.ReadToEndAsync(ct);

                await using var tx = await conn.BeginTransactionAsync(ct);
                await ExecAsync(conn, tx, sql, ct);
                await using (var mark = new NpgsqlCommand("INSERT INTO schema_migrations (version, name) VALUES (@v, @n)", conn, tx))
                {
                    mark.Parameters.AddWithValue("v", version);
                    mark.Parameters.AddWithValue("n", name);
                    await mark.ExecuteNonQueryAsync(ct);
                }
                await tx.CommitAsync(ct);
                log.LogInformation("Applied database migration {Name}", name);
            }
        }
        finally
        {
            await ExecAsync(conn, null, "SELECT pg_advisory_unlock(" + LockId + ")", CancellationToken.None);
        }
    }

    private static async Task ExecAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
