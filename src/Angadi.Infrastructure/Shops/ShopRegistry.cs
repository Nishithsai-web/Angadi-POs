using System.Globalization;
using System.Text.Json;
using Angadi.Application.Common;
using Angadi.Application.Persistence;
using Angadi.Application.Shops;
using Angadi.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;

namespace Angadi.Infrastructure.Shops;

/// <summary>
/// v2 several shops: the phone's list of shops, in files/shops.json (device-level, outside every shop database).
/// File layout — paths are DERIVED from the id, never read from the JSON:
///   first shop  ("main"): files/angadi.db, safety copies in files/snapshots          (existing installs untouched)
///   other shops (32 hex): files/shops/&lt;id&gt;.db, safety copies in files/shop-snapshots/&lt;id&gt;
/// Safety copies stay outside files/shops/ so Android Auto Backup (which includes files/shops/) does not carry them.
/// No registry file = one shop ("main"). A shop database found in files/shops/ but missing from the registry (lost or
/// damaged shops.json, an Auto Backup restore) is listed again with an empty name, so no shop's books are ever
/// orphaned. An unknown current id falls back to "main".
/// </summary>
public sealed class ShopRegistry
{
    public const string MainId = "main";
    private const string FileName = "shops.json";
    private readonly string _dataDirectory;
    private readonly Lock _lock = new();

    public ShopRegistry(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _dataDirectory = dataDirectory;
    }

    public string RegistryPath => Path.Combine(_dataDirectory, FileName);

    public string DatabasePath(string id) => string.Equals(id, MainId, StringComparison.Ordinal)
        ? Path.Combine(_dataDirectory, "angadi.db")
        : Path.Combine(_dataDirectory, "shops", Checked(id) + ".db");

    public string SnapshotDirectory(string id) => string.Equals(id, MainId, StringComparison.Ordinal)
        ? Path.Combine(_dataDirectory, "snapshots")
        : Path.Combine(_dataDirectory, "shop-snapshots", Checked(id));

    public string CurrentId
    {
        get { lock (_lock) return Load().Current; }
    }

    public IReadOnlyList<ShopEntry> List()
    {
        lock (_lock)
        {
            var state = Load();
            return state.Shops.Select(s => new ShopEntry(s.Id, s.Name, string.Equals(s.Id, state.Current, StringComparison.Ordinal))).ToList();
        }
    }

    /// <summary>Database files of every shop except the open one (the free quota counts bills of all shops).</summary>
    public IReadOnlyList<string> OtherDatabasePaths(string openDatabasePath)
    {
        var open = Path.GetFullPath(openDatabasePath);
        return List().Select(s => DatabasePath(s.Id))
            .Where(p => !string.Equals(Path.GetFullPath(p), open, StringComparison.Ordinal))
            .ToList();
    }

    public static string NewId() => Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

    /// <summary>Adds (when <paramref name="add"/>) and makes <paramref name="id"/> current, in one atomic write.</summary>
    public void SetCurrent(string id, bool add = false)
    {
        lock (_lock)
        {
            var state = Load();
            if (!state.Shops.Exists(s => string.Equals(s.Id, id, StringComparison.Ordinal)))
            {
                if (!add) throw new InvalidOperationException("unknown shop");
                state.Shops.Add(new ShopRecord { Id = Checked(id), Name = "" });
            }
            state.Current = id;
            Save(state);
        }
    }

    public bool Rename(string id, string name)
    {
        lock (_lock)
        {
            var state = Load();
            var shop = state.Shops.Find(s => string.Equals(s.Id, id, StringComparison.Ordinal));
            if (shop is null) return false;
            shop.Name = name;
            Save(state);
            return true;
        }
    }

    // ───────────────────────────── file ─────────────────────────────

    private State Load()
    {
        State state;
        try
        {
            state = File.Exists(RegistryPath)
                ? JsonSerializer.Deserialize<State>(File.ReadAllText(RegistryPath), Json) ?? new State()
                : new State();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            state = new State();   // rebuilt below from the files on disk
        }

        // Keep only well-formed ids, once each; the first shop is always there and always first.
        var shops = (state.Shops ?? []).Where(s => s is not null && IsValidId(s.Id))
            .GroupBy(s => s.Id, StringComparer.Ordinal).Select(g => g.First()).ToList();
        foreach (var s in shops) s.Name = (s.Name ?? "").Trim();
        var main = shops.Find(s => string.Equals(s.Id, MainId, StringComparison.Ordinal)) ?? new ShopRecord { Id = MainId, Name = "" };
        shops = [main, .. shops.Where(s => !string.Equals(s.Id, MainId, StringComparison.Ordinal))];

        // Shop files the registry does not know about (see class summary).
        var folder = Path.Combine(_dataDirectory, "shops");
        if (Directory.Exists(folder))
            foreach (var file in Directory.GetFiles(folder, "*.db").Order(StringComparer.Ordinal))
            {
                var id = Path.GetFileNameWithoutExtension(file);
                if (file.EndsWith(".db", StringComparison.Ordinal) && IsValidId(id) && !string.Equals(id, MainId, StringComparison.Ordinal)
                    && !shops.Exists(s => string.Equals(s.Id, id, StringComparison.Ordinal)))
                    shops.Add(new ShopRecord { Id = id, Name = "" });
            }

        state.Shops = shops;
        if (state.Current is null || !shops.Exists(s => string.Equals(s.Id, state.Current, StringComparison.Ordinal))) state.Current = MainId;
        return state;
    }

    private void Save(State state)
    {
        Directory.CreateDirectory(_dataDirectory);
        var temp = RegistryPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state, Json));
        File.Move(temp, RegistryPath, overwrite: true);
    }

    private static bool IsValidId(string? id) =>
        id is not null && (string.Equals(id, MainId, StringComparison.Ordinal)
                           || (id.Length == 32 && id.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'))));

    private static string Checked(string id) =>
        IsValidId(id) ? id : throw new ArgumentException("invalid shop id", nameof(id));

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    internal sealed class State
    {
        public string Current { get; set; } = MainId;
        public List<ShopRecord> Shops { get; set; } = [];
    }

    internal sealed class ShopRecord
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
    }
}

/// <summary>
/// <see cref="IShopDirectory"/>: the registry plus the live database. Order for a switch: open the other file first
/// (AngadiDb.SwitchToAsync, which stays on the current shop if that fails), then record it as current; if recording
/// fails the database is switched back, so the registry and the open file never disagree.
/// </summary>
internal sealed class ShopDirectory(ShopRegistry registry, AngadiDb db, ILogger<ShopDirectory> log) : IShopDirectory
{
    public IReadOnlyList<ShopEntry> List() => registry.List();

    public async Task<Result<ShopEntry>> AddAndSwitchAsync(CancellationToken ct = default)
    {
        var id = ShopRegistry.NewId();
        var switched = await SwitchCoreAsync(id, add: true, ct).ConfigureAwait(false);
        return switched.IsSuccess
            ? Result.Success(registry.List().First(s => string.Equals(s.Id, id, StringComparison.Ordinal)))
            : Result.Failure<ShopEntry>(switched.Error);
    }

    public Task<Result> SwitchAsync(string shopId, CancellationToken ct = default)
    {
        if (!registry.List().Any(s => string.Equals(s.Id, shopId, StringComparison.Ordinal)))
            return Task.FromResult(Result.Failure(ErrorCodes.ShopNotFound));
        return SwitchCoreAsync(shopId, add: false, ct);
    }

    public Result Rename(string shopId, string name)
    {
        try
        {
            return registry.Rename(shopId, name) ? Result.Success() : Result.Failure(ErrorCodes.ShopNotFound);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "Shop rename not saved");
            return Result.Failure(StorageFailures.Classify(ex) ?? ErrorCodes.Unexpected, ex.GetType().Name);
        }
    }

    private async Task<Result> SwitchCoreAsync(string id, bool add, CancellationToken ct)
    {
        var previous = registry.CurrentId;
        try
        {
            await db.SwitchToAsync(registry.DatabasePath(id), registry.SnapshotDirectory(id), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Shop switch failed; still on the previous shop");
            return Result.Failure(StorageFailures.Classify(ex) ?? ErrorCodes.ShopSwitchFailed, ex.GetType().Name);
        }

        try
        {
            registry.SetCurrent(id, add);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogError(ex, "Shop list not saved; switching back");
            await db.SwitchToAsync(registry.DatabasePath(previous), registry.SnapshotDirectory(previous), CancellationToken.None).ConfigureAwait(false);
            return Result.Failure(StorageFailures.Classify(ex) ?? ErrorCodes.ShopSwitchFailed, ex.GetType().Name);
        }
        log.LogInformation("Switched shop (generation {Generation})", db.Generation.ToString(CultureInfo.InvariantCulture));
        return Result.Success();
    }
}
