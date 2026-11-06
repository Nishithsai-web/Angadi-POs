using System.Text.Json;
using System.Threading.RateLimiting;
using Angadi.Server;
using Angadi.Sync.Contracts;
using Npgsql;

// Angadi sync server (v2, docs/ENTERPRISE_PLAN.md phase 2). Phones push their outbox events; the server stores each
// event once per shop and serves them back for dashboards. The phone never waits on this: billing stays offline-first.
var builder = WebApplication.CreateBuilder(args);

// Read lazily so test hosts and environment variables can supply it.
builder.Services.AddSingleton(sp => NpgsqlDataSource.Create(
    sp.GetRequiredService<IConfiguration>().GetConnectionString("Angadi")
    ?? throw new InvalidOperationException("ConnectionStrings:Angadi is not set")));
builder.Services.AddSingleton<SyncStore>();
builder.Services.AddSingleton<ChainStore>();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.TypeInfoResolverChain.Insert(0, SyncJsonContext.Default));
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("register", http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = http.RequestServices.GetRequiredService<IConfiguration>().GetValue("RateLimits:RegisterPerHour", 20),
            Window = TimeSpan.FromHours(1),
        }));
    // Join codes are short enough to guess at scale: attempts are limited per address.
    o.AddPolicy("join", http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = http.RequestServices.GetRequiredService<IConfiguration>().GetValue("RateLimits:JoinPerHour", 30),
            Window = TimeSpan.FromHours(1),
        }));
});

var app = builder.Build();
await Migrator.RunAsync(app.Services.GetRequiredService<NpgsqlDataSource>(), app.Logger, CancellationToken.None);
app.UseRateLimiter();

app.MapGet("/health", async (NpgsqlDataSource data, CancellationToken ct) =>
{
    await using var cmd = data.CreateCommand("SELECT 1");
    await cmd.ExecuteScalarAsync(ct);
    return Results.Text("ok");
});

app.MapPost("/v1/shops/register", async (RegisterShopRequest request, SyncStore store, CancellationToken ct) =>
{
    var name = (request.Name ?? "").Trim();
    if (request.ShopId == Guid.Empty || name.Length == 0 || name.Length > SyncLimits.MaxNameLength)
        return Results.BadRequest(new ProblemBody("SHOP_INVALID", "A shop id and a name of up to " + SyncLimits.MaxNameLength + " characters are needed"));
    var key = ShopKeys.New();
    return await store.RegisterAsync(request.ShopId, name, ShopKeys.Hash(key), ct)
        ? Results.Ok(new RegisterShopResponse(request.ShopId, key))
        : Results.Conflict(new ProblemBody("SHOP_EXISTS", "This shop is already registered"));
}).RequireRateLimiting("register");

app.MapPost("/v1/shops/{shopId:guid}/events", async (Guid shopId, PushRequest request, HttpRequest http, SyncStore store, CancellationToken ct) =>
{
    if (!await store.CheckKeyAsync(shopId, http.Headers[SyncLimits.KeyHeader].ToString(), ct)) return Results.Unauthorized();
    var events = request.Events ?? [];
    if (Api.Validate(request.DeviceId, events) is { } problem) return Results.BadRequest(problem);
    var last = await store.PushAsync(shopId, request.DeviceId.Trim(), events, ct);
    return Results.Ok(new PushResponse(events.Select(e => e.Id).Distinct().ToList(), last));
});

app.MapGet("/v1/shops/{shopId:guid}/events", async (Guid shopId, long? after, int? limit, HttpRequest http, SyncStore store, CancellationToken ct) =>
{
    if (!await store.CheckKeyAsync(shopId, http.Headers[SyncLimits.KeyHeader].ToString(), ct)) return Results.Unauthorized();
    var take = Math.Clamp(limit ?? SyncLimits.MaxPage, 1, SyncLimits.MaxPage);
    return Results.Ok(await store.ReadAsync(shopId, Math.Max(0, after ?? 0), take, ct));
});

app.MapGet("/v1/shops/{shopId:guid}/summary", async (Guid shopId, HttpRequest http, SyncStore store, CancellationToken ct) =>
{
    if (!await store.CheckKeyAsync(shopId, http.Headers[SyncLimits.KeyHeader].ToString(), ct)) return Results.Unauthorized();
    return await store.SummaryAsync(shopId, ct) is { } summary ? Results.Ok(summary) : Results.NotFound();
});

// ---- v2 phase 4: chains and the head's live dashboard

app.MapPost("/v1/shops/{shopId:guid}/chain", async (Guid shopId, CreateChainRequest request, HttpRequest http, SyncStore shops, ChainStore chains, CancellationToken ct) =>
{
    if (!await shops.CheckKeyAsync(shopId, http.Headers[SyncLimits.KeyHeader].ToString(), ct)) return Results.Unauthorized();
    var name = (request.Name ?? "").Trim();
    if (name.Length == 0 || name.Length > SyncLimits.MaxNameLength)
        return Results.BadRequest(new ProblemBody("CHAIN_NAME_INVALID", "A chain name of up to " + SyncLimits.MaxNameLength + " characters is needed"));
    var (outcome, created) = await chains.CreateAsync(shopId, name, ct);
    return outcome == ChainOutcome.Ok ? Results.Ok(created) : Results.Conflict(new ProblemBody("ALREADY_IN_CHAIN", "This shop is already in a chain"));
});

app.MapPost("/v1/shops/{shopId:guid}/chain/join", async (Guid shopId, JoinChainRequest request, HttpRequest http, SyncStore shops, ChainStore chains, CancellationToken ct) =>
{
    if (!await shops.CheckKeyAsync(shopId, http.Headers[SyncLimits.KeyHeader].ToString(), ct)) return Results.Unauthorized();
    var code = JoinCodes.Normalize(request.JoinCode);
    if (code.Length != SyncLimits.JoinCodeLength) return Results.NotFound(new ProblemBody("JOIN_CODE_UNKNOWN", "That join code is not right"));
    var (outcome, joined) = await chains.JoinAsync(shopId, code, ct);
    return outcome switch
    {
        ChainOutcome.Ok => Results.Ok(joined),
        ChainOutcome.AlreadyInAChain => Results.Conflict(new ProblemBody("ALREADY_IN_CHAIN", "This shop is already in a chain")),
        _ => Results.NotFound(new ProblemBody("JOIN_CODE_UNKNOWN", "That join code is not right")),
    };
}).RequireRateLimiting("join");

app.MapGet("/v1/shops/{shopId:guid}/chain", async (Guid shopId, HttpRequest http, SyncStore shops, ChainStore chains, CancellationToken ct) =>
{
    if (!await shops.CheckKeyAsync(shopId, http.Headers[SyncLimits.KeyHeader].ToString(), ct)) return Results.Unauthorized();
    return await chains.MembershipAsync(shopId, ct) is { } m ? Results.Ok(m) : Results.NotFound();
});

app.MapGet("/v1/shops/{shopId:guid}/chain/dashboard", async (Guid shopId, int? days, HttpRequest http, SyncStore shops, ChainStore chains, CancellationToken ct) =>
{
    if (!await shops.CheckKeyAsync(shopId, http.Headers[SyncLimits.KeyHeader].ToString(), ct)) return Results.Unauthorized();
    var window = Math.Clamp(days ?? 7, 1, SyncLimits.MaxDashboardDays);
    return await chains.DashboardAsync(shopId, window, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), ct) is { } d
        ? Results.Ok(d)
        : Results.NotFound(new ProblemBody("NOT_CHAIN_HEAD", "Only the head shop of a chain sees the chain dashboard"));
});

await app.RunAsync();

/// <summary>Request checks, kept out of the endpoints so each rule is in one place.</summary>
internal static class Api
{
    public static ProblemBody? Validate(string? deviceId, IReadOnlyList<SyncEvent> events)
    {
        if (string.IsNullOrWhiteSpace(deviceId) || deviceId.Trim().Length > SyncLimits.MaxTextLength)
            return new ProblemBody("DEVICE_INVALID", "A device id of up to " + SyncLimits.MaxTextLength + " characters is needed");
        if (events.Count > SyncLimits.MaxBatch)
            return new ProblemBody("BATCH_TOO_LARGE", "Send at most " + SyncLimits.MaxBatch + " events at a time");
        for (var i = 0; i < events.Count; i++)
        {
            var e = events[i];
            if (e is null || e.Id == Guid.Empty || !Text(e.AggregateType) || !Text(e.EventType)
                || e.PayloadJson is null || e.PayloadJson.Length > SyncLimits.MaxPayloadChars || !IsJson(e.PayloadJson))
                return new ProblemBody("EVENT_INVALID", "Event " + i + " is not valid");
        }
        return null;
    }

    private static bool Text(string? s) => !string.IsNullOrWhiteSpace(s) && s.Length <= SyncLimits.MaxTextLength;

    private static bool IsJson(string s)
    {
        try { using var _ = JsonDocument.Parse(s); return true; }
        catch (JsonException) { return false; }
    }
}

/// <summary>Visible to the test host (WebApplicationFactory).</summary>
public partial class Program;
