using System.Net;
using System.Net.Http.Json;
using Angadi.Sync.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Angadi.Server.Tests;

/// <summary>Runs the real server against a real Postgres (CI service container; set ANGADI_TEST_PG locally).</summary>
public sealed class ServerHost : WebApplicationFactory<Program>
{
    public static string ConnectionString =>
        Environment.GetEnvironmentVariable("ANGADI_TEST_PG") ?? "Host=localhost;Port=5432;Username=postgres;Password=angadi;Database=angadi_test";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Angadi", ConnectionString);
        builder.UseSetting("RateLimits:RegisterPerHour", "100000");
    }
}

public sealed class SyncServerTests(ServerHost host) : IClassFixture<ServerHost>
{
    private readonly HttpClient _http = host.CreateClient();
    private static readonly SyncJsonContext J = SyncJsonContext.Default;

    private async Task<(Guid Shop, string Key)> RegisterAsync(string name = "రాము కిరాణా")
    {
        var shop = Guid.NewGuid();
        var r = await _http.PostAsJsonAsync("/v1/shops/register", new RegisterShopRequest(shop, name), J.RegisterShopRequest);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = await r.Content.ReadFromJsonAsync(J.RegisterShopResponse);
        Assert.NotNull(body);
        Assert.Equal(64, body.ShopKey.Length);
        return (shop, body.ShopKey);
    }

    private static SyncEvent Event(Guid? id = null, string payload = "{\"totalPaise\":12000}") =>
        new(id ?? Guid.NewGuid(), "Bill", Guid.NewGuid(), "BillFinalized", payload, 1_762_400_000_000);

    private async Task<HttpResponseMessage> PushAsync(Guid shop, string key, params SyncEvent[] events)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/v1/shops/{shop}/events")
        {
            Content = JsonContent.Create(new PushRequest("phone-1", events), J.PushRequest),
        };
        req.Headers.Add(SyncLimits.KeyHeader, key);
        return await _http.SendAsync(req);
    }

    private async Task<EventsPage> ReadAsync(Guid shop, string key, long after = 0, int? limit = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"/v1/shops/{shop}/events?after={after}" + (limit is { } l ? "&limit=" + l : ""));
        req.Headers.Add(SyncLimits.KeyHeader, key);
        var r = await _http.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync(J.EventsPage))!;
    }

    [Fact]
    public async Task Health_IsOk() => Assert.Equal("ok", await _http.GetStringAsync("/health"));

    [Fact]
    public async Task AShopRegistersOnce_AndItsKeyIsNeverIssuedAgain()
    {
        var (shop, _) = await RegisterAsync();
        var again = await _http.PostAsJsonAsync("/v1/shops/register", new RegisterShopRequest(shop, "x"), J.RegisterShopRequest);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Register_NeedsAName(string name)
    {
        var r = await _http.PostAsJsonAsync("/v1/shops/register", new RegisterShopRequest(Guid.NewGuid(), name), J.RegisterShopRequest);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task AReplayedBatch_IsAcknowledged_ButStoredOnce()
    {
        var (shop, key) = await RegisterAsync();
        var batch = new[] { Event(), Event(), Event() };
        Assert.Equal(HttpStatusCode.OK, (await PushAsync(shop, key, batch)).StatusCode);
        var again = await PushAsync(shop, key, batch);   // the phone never got the first answer and sends again
        var ack = await again.Content.ReadFromJsonAsync(J.PushResponse);
        Assert.Equal(batch.Select(e => e.Id).OrderBy(x => x), ack!.Acked.OrderBy(x => x));
        Assert.Equal(3, (await ReadAsync(shop, key)).Events.Count);
    }

    [Fact]
    public async Task OverlappingBatches_KeepEveryEventOnce_InArrivalOrder()
    {
        var (shop, key) = await RegisterAsync();
        var a = Event(); var b = Event(); var c = Event();
        await PushAsync(shop, key, a, b);
        await PushAsync(shop, key, b, c);
        var page = await ReadAsync(shop, key);
        Assert.Equal(new[] { a.Id, b.Id, c.Id }, page.Events.Select(e => e.Id).ToArray());
        Assert.True(page.Events.Zip(page.Events.Skip(1)).All(p => p.First.Seq < p.Second.Seq));
    }

    [Fact]
    public async Task Paging_ReturnsEverythingExactlyOnce()
    {
        var (shop, key) = await RegisterAsync();
        var sent = Enumerable.Range(0, 7).Select(_ => Event()).ToArray();
        await PushAsync(shop, key, sent);
        var seen = new List<Guid>();
        long after = 0;
        while (true)
        {
            var page = await ReadAsync(shop, key, after, limit: 3);
            seen.AddRange(page.Events.Select(e => e.Id));
            after = page.LastSeq;
            if (!page.More) break;
        }
        Assert.Equal(sent.Select(e => e.Id), seen);
    }

    [Fact]
    public async Task WrongKeys_AndOtherShopsKeys_AreRefused()
    {
        var (shopA, keyA) = await RegisterAsync();
        var (_, keyB) = await RegisterAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await PushAsync(shopA, "nope", Event())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PushAsync(shopA, keyB, Event())).StatusCode);   // B cannot write into A
        Assert.Equal(HttpStatusCode.Unauthorized, (await PushAsync(Guid.NewGuid(), keyA, Event())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PushAsync(shopA, "", Event())).StatusCode);

        using var read = new HttpRequestMessage(HttpMethod.Get, $"/v1/shops/{shopA}/events");
        read.Headers.Add(SyncLimits.KeyHeader, keyB);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.SendAsync(read)).StatusCode);   // nor read A
    }

    [Fact]
    public async Task BadBatches_AreRefusedWhole_AndNothingIsStored()
    {
        var (shop, key) = await RegisterAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await PushAsync(shop, key, Event(), Event(payload: "{not json"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PushAsync(shop, key, Event(Guid.Empty))).StatusCode);
        var tooMany = Enumerable.Range(0, SyncLimits.MaxBatch + 1).Select(_ => Event()).ToArray();
        Assert.Equal(HttpStatusCode.BadRequest, (await PushAsync(shop, key, tooMany)).StatusCode);
        Assert.Empty((await ReadAsync(shop, key)).Events);
    }

    [Fact]
    public async Task Summary_CountsEventsByType()
    {
        var (shop, key) = await RegisterAsync("Branch 2");
        await PushAsync(shop, key, Event(), Event());
        using var req = new HttpRequestMessage(HttpMethod.Get, $"/v1/shops/{shop}/summary");
        req.Headers.Add(SyncLimits.KeyHeader, key);
        var summary = await (await _http.SendAsync(req)).Content.ReadFromJsonAsync(J.ShopSummary);
        Assert.Equal("Branch 2", summary!.Name);
        Assert.Equal(2, summary.EventCount);
        Assert.Equal(2, summary.ByType["BillFinalized"]);
    }
}
