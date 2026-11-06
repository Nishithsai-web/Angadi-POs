using Angadi.Application.Common;

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
