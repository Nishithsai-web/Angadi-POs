using System.Text.Json.Serialization;

namespace Angadi.Sync.Contracts;

// v2 sync wire format, shared by the phone and the server. Additive changes only: a field is never renamed or removed,
// so an old app keeps working against a newer server (docs/ENTERPRISE_PLAN.md, phase 2).

/// <summary>One outbox event from a phone. <see cref="Id"/> is the phone's outbox row id: the server stores each id once per shop.</summary>
public sealed record SyncEvent(Guid Id, string AggregateType, Guid AggregateId, string EventType, string PayloadJson, long CreatedAtMs);

public sealed record PushRequest(string DeviceId, IReadOnlyList<SyncEvent> Events);

/// <summary>Every event id the server now holds from this batch (new or already there). <see cref="LastSeq"/> is the shop's newest sequence.</summary>
public sealed record PushResponse(IReadOnlyList<Guid> Acked, long LastSeq);

public sealed record RegisterShopRequest(Guid ShopId, string Name);

/// <summary>The shop's secret key, returned exactly once at registration. The server keeps only its hash.</summary>
public sealed record RegisterShopResponse(Guid ShopId, string ShopKey);

public sealed record StoredEvent(long Seq, Guid Id, string AggregateType, Guid AggregateId, string EventType, string PayloadJson, long CreatedAtMs, string DeviceId);

public sealed record EventsPage(IReadOnlyList<StoredEvent> Events, long LastSeq, bool More);

public sealed record ShopSummary(Guid ShopId, string Name, long EventCount, long LastSeq, long? LastEventAtMs, IReadOnlyDictionary<string, long> ByType);

public sealed record ProblemBody(string Code, string Message);

public static class SyncLimits
{
    public const int MaxBatch = 500;
    public const int MaxPayloadChars = 64 * 1024;
    public const int MaxNameLength = 80;
    public const int MaxTextLength = 100;
    public const int MaxPage = 1000;
    public const string KeyHeader = "X-Shop-Key";
}

/// <summary>Source-generated JSON (the phone's trimmed Release build cannot use reflection).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PushRequest))]
[JsonSerializable(typeof(PushResponse))]
[JsonSerializable(typeof(RegisterShopRequest))]
[JsonSerializable(typeof(RegisterShopResponse))]
[JsonSerializable(typeof(EventsPage))]
[JsonSerializable(typeof(ShopSummary))]
[JsonSerializable(typeof(ProblemBody))]
public sealed partial class SyncJsonContext : JsonSerializerContext;
