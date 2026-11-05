using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Angadi.Application.Catalog;
using Angadi.Application.Common;
using Angadi.Application.Inventory;
using Angadi.Application.Platform;
using Angadi.Application.Reports;

namespace Angadi.Application.Chain;

// CHAIN-1 (2026-11-05): a head owner sees every branch's numbers, with no server. Each branch phone makes a signed
// "report pack" (the last 14 days of day-end totals plus today's dues and stock counts) and sends the file to the
// head owner (WhatsApp share sheet). The head owner's phone checks the signature with the shared CHAIN CODE and keeps
// the numbers read-only. A branch's books, items and customers never leave the branch phone: a pack holds totals only.
// Why not a cloud sync: no server, no cost, no privacy-policy change; the price is "as fresh as the last pack".

/// <summary>One day's totals for one branch (all money in paise).</summary>
public sealed record BranchDay(DateOnly Day, int Bills, int Voids, long Sales, long Cash, long Upi, long Udhaar, long Returns, long DuesCollected)
{
    public long Net => Sales - Returns;
}

/// <param name="ShopUid">Stable per shop, survives backup/restore; never reused across shops.</param>
public sealed record BranchPack(string ShopUid, string ShopName, DateTimeOffset GeneratedAt, long DuesTotal, int ItemCount, int LowCount, int OutCount, IReadOnlyList<BranchDay> Days);

public enum ImportOutcome { Added = 0, Updated = 1, AlreadyHaveNewer = 2 }

public sealed record ChainStatus(bool HasCode);

public static class ChainRules
{
    public const int MinCodeLength = 6;
    public const int MaxCodeLength = 40;
    public const int PackDays = 14;
    public const int KeepDays = 60;
    public const int MaxBranches = 50;
    public const int MaxPackBytes = 256 * 1024;
    public const string PackExtension = ".angadi";
    public const string Marker = "angadi-chain-pack";

    public static string Normalize(string? code) => (code ?? "").Trim();
    public static bool IsValidCode(string? code) => Normalize(code).Length is >= MinCodeLength and <= MaxCodeLength;
}

/// <summary>
/// The signed file. Layout: {"t":"angadi-chain-pack","v":1,"p":"<payload JSON as a string>","s":"<HMAC-SHA256 hex>"}.
/// The signature covers the exact payload string, so no re-serialisation can change what was signed. Hand-written
/// JSON (no reflection): the trimmed Release build keeps working.
/// </summary>
public static class ChainPackCodec
{
    private static byte[] Key(string code) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(ChainRules.Normalize(code)), Encoding.UTF8.GetBytes("angadi-chain-v1"), 100_000, HashAlgorithmName.SHA256, 32);

    public static string Encode(BranchPack pack, string code)
    {
        var payload = WritePayload(pack);
        var sig = Convert.ToHexString(HMACSHA256.HashData(Key(code), Encoding.UTF8.GetBytes(payload)));
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("t", ChainRules.Marker);
            w.WriteNumber("v", 1);
            w.WriteString("p", payload);
            w.WriteString("s", sig);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>CHAIN_PACK_UNREADABLE (not one of our files) or CHAIN_PACK_REJECTED (wrong chain code, or the file was changed).</summary>
    public static Result<BranchPack> Decode(string text, string code)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > ChainRules.MaxPackBytes) return Result<BranchPack>.Failure(new Error(ErrorCodes.ChainPackUnreadable));
        string payload, signature;
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Str(root, "t") != ChainRules.Marker || Str(root, "p") is not { } p || Str(root, "s") is not { } s)
                return Result<BranchPack>.Failure(new Error(ErrorCodes.ChainPackUnreadable));
            (payload, signature) = (p, s);
        }
        catch (JsonException)
        {
            return Result<BranchPack>.Failure(new Error(ErrorCodes.ChainPackUnreadable));
        }

        byte[] given;
        try { given = Convert.FromHexString(signature); }
        catch (FormatException) { return Result<BranchPack>.Failure(new Error(ErrorCodes.ChainPackUnreadable)); }
        var expected = HMACSHA256.HashData(Key(code), Encoding.UTF8.GetBytes(payload));
        if (!CryptographicOperations.FixedTimeEquals(expected, given)) return Result<BranchPack>.Failure(new Error(ErrorCodes.ChainPackRejected));

        try { return ReadPayload(payload) is { } pack ? Result<BranchPack>.Success(pack) : Result<BranchPack>.Failure(new Error(ErrorCodes.ChainPackUnreadable)); }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or OverflowException or KeyNotFoundException)
        {
            return Result<BranchPack>.Failure(new Error(ErrorCodes.ChainPackUnreadable));
        }
    }

    public static string WritePayload(BranchPack pack)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("uid", pack.ShopUid);
            w.WriteString("name", pack.ShopName);
            w.WriteString("at", pack.GeneratedAt.ToString("o", CultureInfo.InvariantCulture));
            w.WriteNumber("dues", pack.DuesTotal);
            w.WriteNumber("items", pack.ItemCount);
            w.WriteNumber("low", pack.LowCount);
            w.WriteNumber("out", pack.OutCount);
            w.WriteStartArray("days");
            foreach (var d in pack.Days)
            {
                w.WriteStartObject();
                w.WriteString("d", d.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                w.WriteNumber("bills", d.Bills);
                w.WriteNumber("voids", d.Voids);
                w.WriteNumber("sales", d.Sales);
                w.WriteNumber("cash", d.Cash);
                w.WriteNumber("upi", d.Upi);
                w.WriteNumber("udhaar", d.Udhaar);
                w.WriteNumber("returns", d.Returns);
                w.WriteNumber("collected", d.DuesCollected);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>Also used by the phone's chain file (same shape, no signature).</summary>
    public static BranchPack? ReadPayload(string payload)
    {
        using var doc = JsonDocument.Parse(payload);
        var r = doc.RootElement;
        if (r.ValueKind != JsonValueKind.Object) return null;
        var uid = Str(r, "uid");
        if (string.IsNullOrWhiteSpace(uid) || uid.Length > 64) return null;
        var days = new List<BranchDay>();
        if (r.TryGetProperty("days", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var d in arr.EnumerateArray().Take(ChainRules.KeepDays))
            {
                if (!DateOnly.TryParseExact(Str(d, "d"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)) continue;
                days.Add(new BranchDay(day, (int)Num(d, "bills"), (int)Num(d, "voids"), Num(d, "sales"), Num(d, "cash"), Num(d, "upi"), Num(d, "udhaar"), Num(d, "returns"), Num(d, "collected")));
            }
        if (!DateTimeOffset.TryParse(Str(r, "at"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)) return null;
        var name = (Str(r, "name") ?? "").Trim();
        return new BranchPack(uid, name.Length > 60 ? name[..60] : name, at, Num(r, "dues"), (int)Num(r, "items"), (int)Num(r, "low"), (int)Num(r, "out"), days);
    }

    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static long Num(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;
}

/// <summary>This phone's chain code (never in the database or a backup) plus the branch reports it received.</summary>
public interface IChainStore
{
    Task<string?> GetCodeAsync(CancellationToken ct = default);
    Task SetCodeAsync(string code, CancellationToken ct = default);
    /// <summary>The current shop's stable id (made on first use, kept in the shop's own database).</summary>
    Task<string> GetShopUidAsync(CancellationToken ct = default);
    Task<IReadOnlyList<BranchPack>> ListAsync(CancellationToken ct = default);
    /// <summary>Keeps only a newer pack per branch; merges days (newest wins), keeps the last 60 days.</summary>
    Task<Result<ImportOutcome>> MergeAsync(BranchPack pack, CancellationToken ct = default);
    Task RemoveAsync(string shopUid, CancellationToken ct = default);
}

/// <summary>Owner only (Permission.ChainSetup). The code must be the same on every branch phone and the head owner's phone.</summary>
public sealed record SetChainCodeCommand(string Code) : ICommand<Result>, INoTransaction
{
    public override string ToString() => nameof(SetChainCodeCommand);   // never print the code
}

/// <summary>Manager or owner at a branch: writes the signed report pack file and returns its path (the screen shares it).</summary>
public sealed record BuildChainPackCommand : ICommand<Result<string>>, INoTransaction;

/// <summary>Head owner: checks a received pack and keeps it. Owner only (Permission.ChainView).</summary>
public sealed record ImportChainPackCommand(string Text) : ICommand<Result<ImportOutcome>>, INoTransaction
{
    public override string ToString() => nameof(ImportChainPackCommand);   // a pack is the branch's takings: not for logs
}

public sealed record ListBranchesQuery : IQuery<Result<IReadOnlyList<BranchPack>>>;
public sealed record RemoveBranchCommand(string ShopUid) : ICommand<Result>, INoTransaction;
public sealed record GetChainStatusQuery : IQuery<ChainStatus>;

internal sealed class ChainHandlers(IChainStore store, ISender sender, IClock clock, ChainPackFolder folder) :
    IRequestHandler<SetChainCodeCommand, Result>,
    IRequestHandler<BuildChainPackCommand, Result<string>>,
    IRequestHandler<ImportChainPackCommand, Result<ImportOutcome>>,
    IRequestHandler<ListBranchesQuery, Result<IReadOnlyList<BranchPack>>>,
    IRequestHandler<RemoveBranchCommand, Result>,
    IRequestHandler<GetChainStatusQuery, ChainStatus>
{
    public async Task<Result> Handle(SetChainCodeCommand r, CancellationToken ct)
    {
        if (!ChainRules.IsValidCode(r.Code)) return Result.Failure(Error.With(ErrorCodes.ChainCodeInvalid, ChainRules.MinCodeLength));
        await store.SetCodeAsync(ChainRules.Normalize(r.Code), ct);
        return Result.Success();
    }

    public async Task<ChainStatus> Handle(GetChainStatusQuery r, CancellationToken ct) => new(!string.IsNullOrEmpty(await store.GetCodeAsync(ct)));

    public async Task<Result<string>> Handle(BuildChainPackCommand r, CancellationToken ct)
    {
        if (await store.GetCodeAsync(ct) is not { Length: > 0 } code) return Result<string>.Failure(new Error(ErrorCodes.ChainCodeMissing));
        var profile = await sender.Send(new GetShopProfileQuery(), ct);
        var days = new List<BranchDay>();
        for (var i = 0; i < ChainRules.PackDays; i++)
        {
            var d = await sender.Send(new GetDayEndReportQuery(clock.Today.AddDays(-i)), ct);
            // Empty days are left out: a quiet day is the same as no row, and the pack stays small.
            if (d.BillCount == 0 && d.VoidCount == 0 && d.ReturnCount == 0 && d.DuesCollected.Value == 0) continue;
            days.Add(new BranchDay(d.Day, d.BillCount, d.VoidCount, d.SalesTotal.Value, d.Cash.Value, d.Upi.Value, d.Udhaar.Value, d.Returns.Value, d.DuesCollected.Value));
        }
        var dues = await sender.Send(new GetDuesOutstandingQuery(), ct);
        var stock = await sender.Send(new GetInventoryReportQuery(), ct);
        var pack = new BranchPack(await store.GetShopUidAsync(ct), profile?.Name ?? "", clock.Now, dues.Total.Value, stock.ItemCount, stock.LowCount, stock.OutCount, days);
        try
        {
            var path = Path.Combine(folder.Path, "angadi-" + Slug(profile?.Name) + "-" + clock.Today.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ChainRules.PackExtension);
            Directory.CreateDirectory(folder.Path);
            await File.WriteAllTextAsync(path, ChainPackCodec.Encode(pack, code), ct);
            return Result<string>.Success(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result<string>.Failure(new Error(StorageFailures.Classify(ex) ?? ErrorCodes.ShareFailed, ex.GetType().Name));
        }
    }

    public async Task<Result<ImportOutcome>> Handle(ImportChainPackCommand r, CancellationToken ct)
    {
        if (await store.GetCodeAsync(ct) is not { Length: > 0 } code) return Result<ImportOutcome>.Failure(new Error(ErrorCodes.ChainCodeMissing));
        var decoded = ChainPackCodec.Decode(r.Text, code);
        return decoded.IsFailure ? Result<ImportOutcome>.Failure(decoded.Error) : await store.MergeAsync(decoded.Value, ct);
    }

    public async Task<Result<IReadOnlyList<BranchPack>>> Handle(ListBranchesQuery r, CancellationToken ct) =>
        Result<IReadOnlyList<BranchPack>>.Success((await store.ListAsync(ct)).OrderBy(b => b.ShopName, StringComparer.CurrentCultureIgnoreCase).ToList());

    public async Task<Result> Handle(RemoveBranchCommand r, CancellationToken ct)
    {
        await store.RemoveAsync(r.ShopUid, ct);
        return Result.Success();
    }

    private static string Slug(string? name)
    {
        var s = new string((name ?? "").Where(char.IsAsciiLetterOrDigit).Take(20).ToArray());
        return s.Length == 0 ? "shop" : s;
    }
}

/// <summary>Where pack files are written (the shareable cache folder; Infrastructure sets it).</summary>
public sealed record ChainPackFolder(string Path);
