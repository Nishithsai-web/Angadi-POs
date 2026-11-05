using Angadi.Application.Billing;
using Angadi.Application.Chain;
using Angadi.Application.Common;
using Angadi.Application.Security;
using Angadi.Domain.Billing;
using Angadi.Domain.Shop;
using Angadi.Application.Platform;
using Microsoft.Extensions.DependencyInjection;

namespace Angadi.Tests.Application;

/// <summary>CHAIN-1: signed branch report packs (build, import, tamper, replay) and who may use them.</summary>
public sealed class ChainTests : IDisposable
{
    private const string Code = "ramu-chain-2026";
    private readonly AppHost _branch = new();
    private readonly AppHost _head = new();

    public void Dispose() { _branch.Dispose(); _head.Dispose(); }

    private static BranchPack Sample(DateTimeOffset at) => new("uid-1", "రాము కిరాణా", at, 12_500, 40, 3, 1,
        [new BranchDay(new DateOnly(2026, 11, 1), 12, 1, 100_000, 60_000, 30_000, 10_000, 5_000, 2_000)]);

    [Fact]
    public void Codec_RoundTrips_AndKeepsTeluguNamesAndNumbers()
    {
        var pack = Sample(new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero));
        var back = ChainPackCodec.Decode(ChainPackCodec.Encode(pack, Code), Code);
        Assert.True(back.IsSuccess, back.Error.Code);
        Assert.Equal(pack.ShopName, back.Value.ShopName);
        Assert.Equal(pack.DuesTotal, back.Value.DuesTotal);
        Assert.Equal(95_000, back.Value.Days[0].Net);
    }

    [Fact]
    public void WrongCode_IsRejected_NotTreatedAsGarbage()
    {
        var text = ChainPackCodec.Encode(Sample(DateTimeOffset.UtcNow), Code);
        Assert.Equal(ErrorCodes.ChainPackRejected, ChainPackCodec.Decode(text, "another-code-1").Error.Code);
    }

    [Fact]
    public void TamperedPack_IsRejected()
    {
        var text = ChainPackCodec.Encode(Sample(DateTimeOffset.UtcNow), Code);
        var tampered = text.Replace("12500", "99999");
        Assert.NotEqual(text, tampered);
        Assert.True(ChainPackCodec.Decode(tampered, Code).IsFailure);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"t\":\"angadi-chain-pack\",\"p\":\"x\",\"s\":\"zz\"}")]
    public void Garbage_IsUnreadable(string text) =>
        Assert.Equal(ErrorCodes.ChainPackUnreadable, ChainPackCodec.Decode(text, Code).Error.Code);

    [Theory]
    [InlineData("abc")]
    [InlineData("      ")]
    public async Task ShortCode_IsRefused(string code)
    {
        var r = await _branch.Sender.Send(new SetChainCodeCommand(code));
        Assert.Equal(ErrorCodes.ChainCodeInvalid, r.Error.Code);
        Assert.False((await _branch.Sender.Send(new GetChainStatusQuery())).HasCode);
    }

    [Fact]
    public async Task WithoutACode_NothingCanBeSentOrImported()
    {
        Assert.Equal(ErrorCodes.ChainCodeMissing, (await _branch.Sender.Send(new BuildChainPackCommand())).Error.Code);
        Assert.Equal(ErrorCodes.ChainCodeMissing, (await _branch.Sender.Send(new ImportChainPackCommand("x"))).Error.Code);
    }

    [Fact]
    public async Task BranchBuilds_HeadImports_AndSeesSalesDuesAndStock()
    {
        await _branch.SeedShopAsync();
        var rice = await _branch.AddItemAsync("బియ్యం", 6000);
        Assert.True((await _branch.Sender.Send(new FinalizeBillCommand(AppHost.Draft(TenderType.Cash, (rice, 2000))))).IsSuccess);
        await _branch.Sender.Send(new SetChainCodeCommand(Code));
        await _head.Sender.Send(new SetChainCodeCommand(Code));

        var built = await _branch.Sender.Send(new BuildChainPackCommand());
        Assert.True(built.IsSuccess, built.Error.Code);
        Assert.EndsWith(ChainRules.PackExtension, built.Value);

        var text = await File.ReadAllTextAsync(built.Value);
        Assert.Equal(ImportOutcome.Added, (await _head.Sender.Send(new ImportChainPackCommand(text))).Value);

        var branches = (await _head.Sender.Send(new ListBranchesQuery())).Value;
        var b = Assert.Single(branches);
        Assert.Equal("రాము కిరాణా", b.ShopName);
        Assert.Equal(1, b.ItemCount);
        Assert.Equal(12_000, Assert.Single(b.Days).Sales);   // 2 kg × ₹60, in paise
    }

    [Fact]
    public async Task ReplayedOrOlderPack_NeverRollsBackNewerNumbers_AndANewerOneUpdates()
    {
        await _head.Sender.Send(new SetChainCodeCommand(Code));
        var t = new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);
        Task<Result<ImportOutcome>> Import(DateTimeOffset at) => _head.Sender.Send(new ImportChainPackCommand(ChainPackCodec.Encode(Sample(at), Code)));

        Assert.Equal(ImportOutcome.Added, (await Import(t)).Value);
        Assert.Equal(ImportOutcome.AlreadyHaveNewer, (await Import(t)).Value);
        Assert.Equal(ImportOutcome.AlreadyHaveNewer, (await Import(t.AddHours(-5))).Value);
        Assert.Equal(ImportOutcome.Updated, (await Import(t.AddDays(1))).Value);
        Assert.Single((await _head.Sender.Send(new ListBranchesQuery())).Value);
    }

    [Fact]
    public async Task RemovedBranch_Disappears()
    {
        await _head.Sender.Send(new SetChainCodeCommand(Code));
        await _head.Sender.Send(new ImportChainPackCommand(ChainPackCodec.Encode(Sample(DateTimeOffset.UtcNow), Code)));
        Assert.True((await _head.Sender.Send(new RemoveBranchCommand("uid-1"))).IsSuccess);
        Assert.Empty((await _head.Sender.Send(new ListBranchesQuery())).Value);
    }

    [Fact]
    public async Task Chain_IsOwnerOnlyToSetUpAndView_ButAManagerMayStillSendTheBranchReport()
    {
        await _branch.SeedShopAsync();
        await _branch.Services.GetRequiredService<IPinService>().SetPinAsync("4321");
        var access = _branch.Services.GetRequiredService<IAccessControl>();
        await access.SignInAsync("4321");
        await _branch.Sender.Send(new SaveStaffCommand(null, "Lakshmi", Role.Manager, "1111"));
        access.SignOut();
        await access.SignInAsync("1111");

        Assert.False(await access.AllowsAsync(Permission.ChainSetup));
        Assert.False(await access.AllowsAsync(Permission.ChainView));
        Assert.True(await access.AllowsAsync(Permission.SendChainReport));
    }
}
