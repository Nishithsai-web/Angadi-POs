using System.Net;
using System.Net.Http.Json;
using Angadi.Application.Billing;
using Angadi.Application.Catalog;
using Angadi.Application.Common;
using Angadi.Application.Sync;
using Angadi.Domain.Billing;
using Angadi.Infrastructure.Sync;
using Angadi.Sync.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Angadi.Tests.Application;

/// <summary>v2 phase 3: the phone's sync client against a fake server: exactly-once marking, bisecting bad batches, back-off, prune safety.</summary>
public sealed class SyncClientTests : IDisposable
{
    private readonly FakeServer _server = new();
    private readonly AppHost _h;

    public SyncClientTests() => _h = new AppHost(configure: s =>
    {
        s.Configure<SyncOptions>(o => { o.ServerUrl = "https://sync.angadi.test"; o.BatchSize = 4; });
        s.AddSingleton(new SyncHttp(new HttpClient(_server)));
    });

    public void Dispose() => _h.Dispose();

    private async Task SellAsync(int bills)
    {
        await _h.SeedShopAsync();
        var rice = await _h.AddItemAsync("బియ్యం", 6000, stockMilli: 1_000_000);
        for (var i = 0; i < bills; i++)
            Assert.True((await _h.Sender.Send(new FinalizeBillCommand(AppHost.Draft(TenderType.Cash, (rice, 1000))))).IsSuccess);
    }

    private Task<SyncStatus> Status() => _h.Sender.Send(new GetSyncStatusQuery());

    [Fact]
    public async Task WithoutAServerAddress_SyncIsOff_AndAutomaticRunsDoNothing()
    {
        using var off = new AppHost();
        Assert.False((await off.Sender.Send(new GetSyncStatusQuery())).Available);
        Assert.Equal(ErrorCodes.SyncNotAvailable, (await off.Sender.Send(new SyncNowCommand(true))).Error.Code);
        Assert.True((await off.Sender.Send(new SyncNowCommand(false))).IsSuccess);
    }

    [Fact]
    public async Task NothingIsSent_UntilTheOwnerTurnsItOn()
    {
        await SellAsync(2);
        Assert.True((await _h.Sender.Send(new SyncNowCommand(false))).IsSuccess);
        Assert.Equal(ErrorCodes.SyncNotLinked, (await _h.Sender.Send(new SyncNowCommand(true))).Error.Code);
        Assert.Empty(_server.Pushed);
    }

    [Fact]
    public async Task LinkingOnce_ThenEveryPendingEventIsSent_InBatches_AndMarkedOnce()
    {
        await SellAsync(3);
        Assert.True((await _h.Sender.Send(new LinkCloudSyncCommand())).IsSuccess);
        Assert.True((await _h.Sender.Send(new LinkCloudSyncCommand())).IsSuccess);   // already linked: no second registration
        Assert.Equal(1, _server.Registrations);

        var before = (await Status()).Pending;
        Assert.True(before > 4);   // several batches of 4
        var sent = await _h.Sender.Send(new SyncNowCommand(true));
        Assert.True(sent.IsSuccess, sent.Error.Code);
        Assert.Equal(before, sent.Value);
        Assert.Equal(0, (await Status()).Pending);
        Assert.Equal(before, _server.Pushed.Distinct().Count());
        Assert.All(_server.Batches, b => Assert.True(b <= 4));

        Assert.Equal(0, (await _h.Sender.Send(new SyncNowCommand(true))).Value);   // nothing left: nothing re-sent
    }

    [Fact]
    public async Task OnlyAcknowledgedEvents_AreMarkedSent()
    {
        await SellAsync(1);
        await _h.Sender.Send(new LinkCloudSyncCommand());
        _server.AckOnlyFirst = true;
        var pending = (await Status()).Pending;
        var r = await _h.Sender.Send(new SyncNowCommand(true));
        Assert.Equal(1, r.Value);
        Assert.Equal(pending - 1, (await Status()).Pending);
    }

    [Fact]
    public async Task OneBadEvent_IsFoundByHalving_KeptOnThePhone_AndNeverBlocksTheRest()
    {
        await SellAsync(3);
        await _h.Sender.Send(new LinkCloudSyncCommand());
        var pending = (await Status()).Pending;
        _server.RejectNth = 2;   // the third event the server ever sees is "invalid"
        var r = await _h.Sender.Send(new SyncNowCommand(true));
        Assert.True(r.IsSuccess, r.Error.Code);
        var status = await Status();
        Assert.Equal(0, status.Pending);
        Assert.Equal(1, status.Rejected);
        Assert.Equal(pending - 1, r.Value);
    }

    [Fact]
    public async Task Offline_KeepsEverything_AndAutomaticRunsBackOff_ButSyncNowStillTries()
    {
        await SellAsync(1);
        await _h.Sender.Send(new LinkCloudSyncCommand());
        var pending = (await Status()).Pending;
        _server.Offline = true;
        Assert.Equal(ErrorCodes.SyncOffline, (await _h.Sender.Send(new SyncNowCommand(true))).Error.Code);
        Assert.Equal(pending, (await Status()).Pending);
        Assert.Equal(1, (await Status()).Failures);

        _server.Offline = false;   // the network is back, but an automatic run inside the back-off must not even try
        var calls = _server.PushCalls;
        Assert.True((await _h.Sender.Send(new SyncNowCommand(false))).IsSuccess);
        Assert.Equal(calls, _server.PushCalls);

        Assert.Equal(pending, (await _h.Sender.Send(new SyncNowCommand(true))).Value);   // the button ignores the back-off
        Assert.Equal(0, (await Status()).Failures);
    }

    [Fact]
    public async Task ARejectedKey_IsReported()
    {
        await SellAsync(1);
        await _h.Sender.Send(new LinkCloudSyncCommand());
        _server.Unauthorized = true;
        Assert.Equal(ErrorCodes.SyncKeyRejected, (await _h.Sender.Send(new SyncNowCommand(true))).Error.Code);
    }

    [Fact]
    public async Task OnceLinked_UnsentEventsSurviveTheOutboxPrune()
    {
        await SellAsync(1);
        await _h.Sender.Send(new LinkCloudSyncCommand());
        var pending = (await Status()).Pending;
        _h.Clock.Advance(TimeSpan.FromDays(400));
        await _h.Sender.Send(new PruneOutboxCommand());
        Assert.Equal(pending, (await Status()).Pending);
    }

    [Fact]
    public async Task AnAlreadyRegisteredShop_IsExplained()
    {
        await SellAsync(1);
        _server.RegisterConflict = true;
        Assert.Equal(ErrorCodes.SyncAlreadyLinked, (await _h.Sender.Send(new LinkCloudSyncCommand())).Error.Code);
    }

    private sealed class FakeServer : HttpMessageHandler
    {
        public bool Offline { get; set; }
        public bool Unauthorized { get; set; }
        public bool AckOnlyFirst { get; set; }
        public bool RegisterConflict { get; set; }
        public int RejectNth { get; set; } = -1;
        public int Registrations { get; private set; }
        public int PushCalls { get; private set; }
        public List<Guid> Pushed { get; } = [];
        public List<int> Batches { get; } = [];
        private readonly List<Guid> _seen = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Offline) throw new HttpRequestException("no network");
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/register", StringComparison.Ordinal))
            {
                if (RegisterConflict) return new HttpResponseMessage(HttpStatusCode.Conflict);
                Registrations++;
                var reg = await request.Content!.ReadFromJsonAsync(SyncJsonContext.Default.RegisterShopRequest, ct);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new RegisterShopResponse(reg!.ShopId, new string('k', 64)), SyncJsonContext.Default.RegisterShopResponse),
                };
            }
            PushCalls++;
            if (Unauthorized || !request.Headers.Contains(SyncLimits.KeyHeader)) return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            var push = await request.Content!.ReadFromJsonAsync(SyncJsonContext.Default.PushRequest, ct);
            var ids = push!.Events.Select(e => e.Id).ToList();
            foreach (var id in ids) if (!_seen.Contains(id)) _seen.Add(id);
            if (RejectNth >= 0 && RejectNth < _seen.Count && ids.Contains(_seen[RejectNth])) return new HttpResponseMessage(HttpStatusCode.BadRequest);
            Batches.Add(ids.Count);
            var acked = AckOnlyFirst ? ids.Take(1).ToList() : ids;
            Pushed.AddRange(acked);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new PushResponse(acked, Pushed.Count), SyncJsonContext.Default.PushResponse),
            };
        }
    }
}
