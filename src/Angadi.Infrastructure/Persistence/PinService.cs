using System.Globalization;
using System.Security.Cryptography;
using Angadi.Application.Platform;

namespace Angadi.Infrastructure.Persistence;

/// <summary>
/// FIX(SEC-4, 2026-10-20, FIX-3): Flaw: anyone holding the shop tablet could void a bill, fake a dues
/// payment, export the whole database or restore an old one — no access control existed at all.
/// Fix (owner-approved scope): an OPTIONAL owner PIN gates only those sensitive actions at the UI layer
/// (see PinGate in the MAUI head); everyday billing stays friction-free. The PIN is stored ONLY as a
/// salted PBKDF2-HMAC-SHA256 hash (100k iterations, per-PIN random salt) in the DB Meta table — never
/// plaintext, never in Preferences. Five wrong attempts persist a 60-second lockout timestamp in Meta,
/// so force-closing the app does not reset it. Why this shape: the threat is staff/bystanders at the
/// counter, not remote attackers; a hash in the same DB the actions protect is proportionate for v1 and
/// keeps the PIN travelling with backups. Logged in docs/AGENT_FIX_LOG.md.
/// </summary>
internal sealed class PinService(AngadiDb db, IClock clock) : IPinService
{
    private const string HashKey = "pin.hash";
    private const string FailsKey = "pin.fails";
    private const string LockKey = "pin.lock_until_ms";
    private const int MaxFails = 5;
    private const int LockSeconds = 60;
    private const int Iterations = 100_000;

    public Task<bool> HasPinAsync(CancellationToken ct = default) =>
        db.RunAsync(c => c.Find<MetaRow>(HashKey) is not null, ct);

    public Task SetPinAsync(string pin, CancellationToken ct = default) =>
        db.RunAsync(c =>
        {
            var salt = RandomNumberGenerator.GetBytes(16);
            var hash = Hash(pin, salt);
            Put(c, HashKey, Convert.ToBase64String(salt) + ":" + Convert.ToBase64String(hash));
            Put(c, FailsKey, "0");
            Put(c, LockKey, "0");
        }, ct);

    public Task<PinVerification> VerifyAsync(string pin, CancellationToken ct = default) =>
        db.RunAsync(c =>
        {
            var stored = c.Find<MetaRow>(HashKey)?.Value;
            if (stored is null) return new PinVerification(PinCheck.NoPin);

            var nowMs = clock.Now.ToUnixTimeMilliseconds();
            var lockUntil = GetLong(c, LockKey);
            if (lockUntil > nowMs)
                return new PinVerification(PinCheck.Locked, (int)((lockUntil - nowMs + 999) / 1000));

            var parts = stored.Split(':', 2);
            if (parts.Length != 2) return new PinVerification(PinCheck.Wrong);   // corrupt record: fail closed
            var salt = Convert.FromBase64String(parts[0]);
            var expected = Convert.FromBase64String(parts[1]);
            if (CryptographicOperations.FixedTimeEquals(Hash(pin, salt), expected))
            {
                Put(c, FailsKey, "0");
                return new PinVerification(PinCheck.Ok);
            }

            var fails = (int)GetLong(c, FailsKey) + 1;
            if (fails >= MaxFails)
            {
                Put(c, LockKey, (nowMs + LockSeconds * 1000L).ToString(CultureInfo.InvariantCulture));
                Put(c, FailsKey, "0");
                return new PinVerification(PinCheck.Locked, LockSeconds);
            }
            Put(c, FailsKey, fails.ToString(CultureInfo.InvariantCulture));
            return new PinVerification(PinCheck.Wrong);
        }, ct);

    private static byte[] Hash(string pin, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(pin, salt, Iterations, HashAlgorithmName.SHA256, 32);

    private static void Put(SQLite.SQLiteConnection c, string key, string value) =>
        c.InsertOrReplace(new MetaRow { Key = key, Value = value });

    private static long GetLong(SQLite.SQLiteConnection c, string key) =>
        c.Find<MetaRow>(key) is { } row && long.TryParse(row.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
}
