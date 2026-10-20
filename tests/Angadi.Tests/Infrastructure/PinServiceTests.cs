using Angadi.Application.Platform;
using Angadi.Infrastructure.Persistence;
using Angadi.Tests.Application;
using Microsoft.Extensions.DependencyInjection;

namespace Angadi.Tests.Infrastructure;

/// <summary>
/// SEC-4: the owner PIN is a salted hash, never the PIN; verification locks out after 5 wrong tries
/// for 60 s (persisted — app restarts don't reset it), timed here with the fake clock.
/// </summary>
public class PinServiceTests
{
    [Fact]
    public async Task NoPin_VerifyReportsNoPin_AndGateStaysOpen()
    {
        using var h = new AppHost();
        var pin = h.Services.GetRequiredService<IPinService>();

        Assert.False(await pin.HasPinAsync());
        Assert.Equal(PinCheck.NoPin, (await pin.VerifyAsync("2468")).Result);
    }

    [Fact]
    public async Task SetPin_StoresOnlyASaltedHash_NeverThePin()
    {
        using var h = new AppHost();
        var pin = h.Services.GetRequiredService<IPinService>();
        await pin.SetPinAsync("2468");
        Assert.True(await pin.HasPinAsync());

        var db = h.Services.GetRequiredService<AngadiDb>();
        var stored = await db.RunAsync(c => c.ExecuteScalar<string>("SELECT Value FROM Meta WHERE Key = 'pin.hash'"));
        Assert.NotNull(stored);
        Assert.NotEqual("2468", stored);
        Assert.DoesNotContain("2468", stored, StringComparison.Ordinal);
        Assert.Contains(":", stored, StringComparison.Ordinal);   // salt:hash

        // Same PIN, new salt → a different stored value (salting actually happens).
        await pin.SetPinAsync("2468");
        var again = await db.RunAsync(c => c.ExecuteScalar<string>("SELECT Value FROM Meta WHERE Key = 'pin.hash'"));
        Assert.NotEqual(stored, again);
    }

    [Fact]
    public async Task Verify_CorrectPin_Ok_WrongPin_Wrong()
    {
        using var h = new AppHost();
        var pin = h.Services.GetRequiredService<IPinService>();
        await pin.SetPinAsync("1357");

        Assert.Equal(PinCheck.Ok, (await pin.VerifyAsync("1357")).Result);
        Assert.Equal(PinCheck.Wrong, (await pin.VerifyAsync("9999")).Result);
        Assert.Equal(PinCheck.Ok, (await pin.VerifyAsync("1357")).Result);   // a wrong try doesn't poison the right one
    }

    [Fact]
    public async Task Verify_FiveWrongTries_LockForSixtySeconds_UnderFakeClock()
    {
        using var h = new AppHost();
        var pin = h.Services.GetRequiredService<IPinService>();
        await pin.SetPinAsync("1357");

        for (var i = 0; i < 4; i++)
            Assert.Equal(PinCheck.Wrong, (await pin.VerifyAsync("0000")).Result);
        var fifth = await pin.VerifyAsync("0000");
        Assert.Equal(PinCheck.Locked, fifth.Result);
        Assert.InRange(fifth.LockedSecondsRemaining, 55, 60);

        // Even the CORRECT pin is refused during the lockout.
        Assert.Equal(PinCheck.Locked, (await pin.VerifyAsync("1357")).Result);

        h.Clock.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(PinCheck.Ok, (await pin.VerifyAsync("1357")).Result);
    }
}
