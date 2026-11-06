using Angadi.Application.Common;
using Angadi.Sync.Contracts;

namespace Angadi.Application.Sync;

/// <summary>Bound from appsettings.json "Sync". An empty ServerUrl turns cloud sync off entirely (the default until hosting is approved).</summary>
public sealed class SyncOptions
{
    public string ServerUrl { get; set; } = "";
    public int BatchSize { get; set; } = 200;
}

/// <param name="Available">A server address is configured in this build.</param>
/// <param name="Linked">This shop has its key, so it is sending.</param>
/// <param name="Rejected">Events the server refused as invalid; kept on the phone, never sent again.</param>
public sealed record SyncStatus(bool Available, bool Linked, int Pending, int Rejected, DateTimeOffset? LastSuccess, int Failures);

/// <summary>v2 phase 3: sends this shop's outbox to the sync server. Never on the billing path.</summary>
public interface ISyncService
{
    Task<SyncStatus> StatusAsync(CancellationToken ct = default);

    /// <summary>Registers the shop with the server once and keeps its key.</summary>
    Task<Result> LinkAsync(CancellationToken ct = default);

    /// <summary>Sends pending events in batches; returns how many the server took. Automatic runs back off after failures.</summary>
    Task<Result<int>> PushAsync(bool manual, CancellationToken ct = default);
}

/// <summary>v2 phase 4: the live chain on the server (needs cloud sync turned on).</summary>
public interface ICloudChain
{
    Task<Result<CreateChainResponse>> CreateAsync(string name, CancellationToken ct = default);
    Task<Result<ChainMembership>> JoinAsync(string joinCode, CancellationToken ct = default);
    /// <summary>Success with null when this shop is not in a live chain.</summary>
    Task<Result<ChainMembership?>> MembershipAsync(CancellationToken ct = default);
    Task<Result<ChainDashboard>> DashboardAsync(int days, CancellationToken ct = default);
}

public sealed record CreateLiveChainCommand(string Name) : ICommand<Result<CreateChainResponse>>, INoTransaction;
public sealed record JoinLiveChainCommand(string JoinCode) : ICommand<Result<ChainMembership>>, INoTransaction
{
    public override string ToString() => nameof(JoinLiveChainCommand);   // never print the code
}
public sealed record GetLiveChainQuery : IQuery<Result<ChainMembership?>>;
public sealed record GetLiveDashboardQuery(int Days = 7) : IQuery<Result<ChainDashboard>>;

internal sealed class CloudChainHandlers(ICloudChain chain) :
    IRequestHandler<CreateLiveChainCommand, Result<CreateChainResponse>>,
    IRequestHandler<JoinLiveChainCommand, Result<ChainMembership>>,
    IRequestHandler<GetLiveChainQuery, Result<ChainMembership?>>,
    IRequestHandler<GetLiveDashboardQuery, Result<ChainDashboard>>
{
    public Task<Result<CreateChainResponse>> Handle(CreateLiveChainCommand r, CancellationToken ct) => chain.CreateAsync(r.Name, ct);
    public Task<Result<ChainMembership>> Handle(JoinLiveChainCommand r, CancellationToken ct) => chain.JoinAsync(r.JoinCode, ct);
    public Task<Result<ChainMembership?>> Handle(GetLiveChainQuery r, CancellationToken ct) => chain.MembershipAsync(ct);
    public Task<Result<ChainDashboard>> Handle(GetLiveDashboardQuery r, CancellationToken ct) => chain.DashboardAsync(r.Days, ct);
}

public sealed record GetSyncStatusQuery : IQuery<SyncStatus>;

/// <summary>Owner only: from now on this shop's records are copied to the Angadi server.</summary>
public sealed record LinkCloudSyncCommand : ICommand<Result>, INoTransaction;

/// <summary>Manual = the "Sync now" button (ignores the back-off); automatic = app start / going to the background.</summary>
public sealed record SyncNowCommand(bool Manual) : ICommand<Result<int>>, INoTransaction;

internal sealed class SyncHandlers(ISyncService sync) :
    IRequestHandler<GetSyncStatusQuery, SyncStatus>,
    IRequestHandler<LinkCloudSyncCommand, Result>,
    IRequestHandler<SyncNowCommand, Result<int>>
{
    public Task<SyncStatus> Handle(GetSyncStatusQuery r, CancellationToken ct) => sync.StatusAsync(ct);
    public Task<Result> Handle(LinkCloudSyncCommand r, CancellationToken ct) => sync.LinkAsync(ct);
    public Task<Result<int>> Handle(SyncNowCommand r, CancellationToken ct) => sync.PushAsync(r.Manual, ct);
}
