using System.Net;
using System.Net.Http.Json;
using Angadi.Sync.Contracts;

namespace Angadi.Server.Tests;

/// <summary>v2 phase 4: chains, join codes, isolation, and the head's live dashboard.</summary>
public sealed class ChainServerTests(ServerHost host) : IClassFixture<ServerHost>
{
    private readonly HttpClient _http = host.CreateClient();
    private static readonly SyncJsonContext J = SyncJsonContext.Default;

    private async Task<(Guid Shop, string Key)> ShopAsync(string name)
    {
        var shop = Guid.NewGuid();
        var r = await _http.PostAsJsonAsync("/v1/shops/register", new RegisterShopRequest(shop, name), J.RegisterShopRequest);
        return (shop, (await r.Content.ReadFromJsonAsync(J.RegisterShopResponse))!.ShopKey);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string key, HttpContent? body = null)
    {
        using var req = new HttpRequestMessage(method, url) { Content = body };
        req.Headers.Add(SyncLimits.KeyHeader, key);
        return await _http.SendAsync(req);
    }

    private async Task<CreateChainResponse> CreateChainAsync((Guid Shop, string Key) head, string name = "Ramu stores")
    {
        var r = await SendAsync(HttpMethod.Post, $"/v1/shops/{head.Shop}/chain", head.Key, JsonContent.Create(new CreateChainRequest(name), J.CreateChainRequest));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync(J.CreateChainResponse))!;
    }

    private Task<HttpResponseMessage> JoinAsync((Guid Shop, string Key) branch, string code) =>
        SendAsync(HttpMethod.Post, $"/v1/shops/{branch.Shop}/chain/join", branch.Key, JsonContent.Create(new JoinChainRequest(code), J.JoinChainRequest));

    private static long NowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private Task<HttpResponseMessage> PushAsync((Guid Shop, string Key) shop, params SyncEvent[] events) =>
        SendAsync(HttpMethod.Post, $"/v1/shops/{shop.Shop}/events", shop.Key, JsonContent.Create(new PushRequest("phone", events), J.PushRequest));

    private static SyncEvent Bill(Guid billId, long totalPaise) =>
        new(Guid.NewGuid(), "Bill", billId, "BillFinalized", "{\"aggregateId\":\"" + billId + "\",\"grandTotalPaise\":" + totalPaise + "}", NowMs);

    private static SyncEvent Void(Guid billId) =>
        new(Guid.NewGuid(), "Bill", billId, "BillVoided", "{\"reason\":\"mistake\"}", NowMs);

    [Fact]
    public async Task ABranchJoinsWithTheCode_TypedAnyWay_AndCannotJoinTwice()
    {
        var head = await ShopAsync("Head");
        var branch = await ShopAsync("Branch");
        var chain = await CreateChainAsync(head);
        Assert.Equal(SyncLimits.JoinCodeLength, chain.JoinCode.Length);

        var spaced = chain.JoinCode.ToLowerInvariant()[..5] + " - " + chain.JoinCode.ToLowerInvariant()[5..];
        var joined = await JoinAsync(branch, spaced);
        Assert.Equal(HttpStatusCode.OK, joined.StatusCode);
        var m = await joined.Content.ReadFromJsonAsync(J.ChainMembership);
        Assert.Equal(chain.ChainId, m!.ChainId);
        Assert.False(m.IsHead);
        Assert.Equal(2, m.Branches);

        Assert.Equal(HttpStatusCode.Conflict, (await JoinAsync(branch, chain.JoinCode)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(HttpMethod.Post, $"/v1/shops/{head.Shop}/chain", head.Key,
            JsonContent.Create(new CreateChainRequest("Second"), J.CreateChainRequest))).StatusCode);   // a head can't make a second chain
    }

    [Fact]
    public async Task AWrongCode_OrAnotherShopsKey_GetsNothing()
    {
        var head = await ShopAsync("Head");
        var branch = await ShopAsync("Branch");
        await CreateChainAsync(head);
        Assert.Equal(HttpStatusCode.NotFound, (await JoinAsync(branch, "ABCDEFGHJK")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await JoinAsync(branch, "short")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, $"/v1/shops/{head.Shop}/chain/dashboard", branch.Key)).StatusCode);
    }

    [Fact]
    public async Task OnlyTheHead_SeesTheDashboard()
    {
        var head = await ShopAsync("Head");
        var branch = await ShopAsync("Branch");
        var chain = await CreateChainAsync(head);
        await JoinAsync(branch, chain.JoinCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"/v1/shops/{branch.Shop}/chain/dashboard", branch.Key)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, $"/v1/shops/{head.Shop}/chain/dashboard", head.Key)).StatusCode);
    }

    [Fact]
    public async Task Dashboard_AddsEachBranchsSales_LeavesOutVoidedBills_AndCountsDuesAndReturns()
    {
        var head = await ShopAsync("Head shop");
        var branch = await ShopAsync("Branch 2");
        var chain = await CreateChainAsync(head);
        await JoinAsync(branch, chain.JoinCode);

        var voided = Guid.NewGuid();
        await PushAsync(head, Bill(Guid.NewGuid(), 12_000), Bill(Guid.NewGuid(), 8_000));
        await PushAsync(branch, Bill(Guid.NewGuid(), 50_000), Bill(voided, 99_900), Void(voided),
            new SyncEvent(Guid.NewGuid(), "Customer", Guid.NewGuid(), "DuesPaymentRecorded", "{\"amountPaise\":2500}", NowMs),
            new SyncEvent(Guid.NewGuid(), "Return", Guid.NewGuid(), "ReturnRecorded", "{\"refundPaise\":1500,\"billId\":\"" + Guid.NewGuid() + "\"}", NowMs));

        var r = await SendAsync(HttpMethod.Get, $"/v1/shops/{head.Shop}/chain/dashboard?days=7", head.Key);
        var d = (await r.Content.ReadFromJsonAsync(J.ChainDashboard))!;
        Assert.Equal(2, d.Branches.Count);
        Assert.True(d.Branches[0].IsHead);

        var h = d.Branches.Single(b => b.ShopId == head.Shop);
        Assert.Equal((2, 20_000L), (h.Bills, h.SalesPaise));
        var b2 = d.Branches.Single(b => b.ShopId == branch.Shop);
        Assert.Equal((1, 50_000L), (b2.Bills, b2.SalesPaise));   // the voided bill is not counted
        Assert.Equal(2_500, b2.DuesCollectedPaise);
        Assert.Equal(1_500, b2.ReturnsPaise);
        Assert.NotNull(b2.LastEventAtMs);
        Assert.Equal(b2.Bills, b2.Days.Sum(x => x.Bills));
    }

    [Fact]
    public async Task ABranchWithNothingSentYet_StillAppears_WithZeros()
    {
        var head = await ShopAsync("Head");
        var quiet = await ShopAsync("Quiet branch");
        var chain = await CreateChainAsync(head);
        await JoinAsync(quiet, chain.JoinCode);
        var d = (await (await SendAsync(HttpMethod.Get, $"/v1/shops/{head.Shop}/chain/dashboard", head.Key)).Content.ReadFromJsonAsync(J.ChainDashboard))!;
        var q = d.Branches.Single(b => b.ShopId == quiet.Shop);
        Assert.Equal((0, 0L, (long?)null), (q.Bills, q.SalesPaise, q.LastEventAtMs));
        Assert.Empty(q.Days);
    }
}
