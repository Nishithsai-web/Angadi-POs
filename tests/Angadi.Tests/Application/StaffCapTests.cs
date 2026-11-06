using Angadi.Application.Common;
using Angadi.Application.Platform;
using Angadi.Application.Security;
using Microsoft.Extensions.DependencyInjection;

namespace Angadi.Tests.Application;

/// <summary>STAFF-CAP: the active-staff limit holds when someone is turned back on, not only when added.</summary>
public sealed class StaffCapTests : IDisposable
{
    private readonly AppHost _h = new();
    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task TurningSomeoneBackOn_CannotGoPastTheLimit()
    {
        await _h.SeedShopAsync();
        await _h.Services.GetRequiredService<IPinService>().SetPinAsync("4321");
        var access = _h.Services.GetRequiredService<IAccessControl>();
        Assert.True((await access.SignInAsync("4321")).IsSuccess);

        var first = await _h.Sender.Send(new SaveStaffCommand(null, "Staff 0", Role.Cashier, "7000"));
        Assert.True(first.IsSuccess, first.Error.Code);
        Assert.True((await _h.Sender.Send(new SetStaffActiveCommand(first.Value.Id, false))).IsSuccess);
        for (var i = 1; i <= IAccessControl.MaxStaff; i++)
        {
            var r = await _h.Sender.Send(new SaveStaffCommand(null, "Staff " + i, Role.Cashier, (7000 + i * 37).ToString(System.Globalization.CultureInfo.InvariantCulture)));
            Assert.True(r.IsSuccess, r.Error.Code);
        }

        var back = await _h.Sender.Send(new SetStaffActiveCommand(first.Value.Id, true));
        Assert.Equal(ErrorCodes.StaffLimitReached, back.Error.Code);
    }
}
