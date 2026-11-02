using Angadi.Application.Billing;
using Angadi.Application.Catalog;
using Angadi.Application.Common;
using Angadi.Application.Printing;
using Angadi.Domain.Billing;
using Angadi.Domain.Common;
using Angadi.Domain.Customers;
using Angadi.Domain.Items;
using Angadi.Domain.Shop;
using Microsoft.Extensions.DependencyInjection;

namespace Angadi.Tests.Application;

/// <summary>
/// v2 printing through the real handler graph: settings, the "no printer" answer, render → send with the chosen
/// address and paper, failures passed through as Results, and the bounded timeout. The Bluetooth link itself is
/// Android hardware (not testable here); a fake transport stands in for it.
/// </summary>
public sealed class PrintingTests : IDisposable
{
    private readonly FakeThermal _thermal = new();
    private readonly FakeTransport _transport = new();
    private readonly AppHost _h;

    public PrintingTests()
    {
        _h = new AppHost(configure: s =>
        {
            s.AddSingleton<IThermalReceiptRenderer>(_thermal);
            s.AddSingleton<IBluetoothPrinterTransport>(_transport);
            s.Configure<PrintingOptions>(o => o.Timeout = TimeSpan.FromMilliseconds(500));
        });
        _h.Subscription.IsPro = true;
    }

    public void Dispose() => _h.Dispose();

    private async Task<Bill> SellAsync(TenderType tender = TenderType.Cash)
    {
        await _h.SeedShopAsync();
        var rice = await _h.AddItemAsync("బియ్యం", 6_000, GstSlab.Five, 100_000);
        var r = await _h.Sender.Send(new FinalizeBillCommand(AppHost.Draft(tender, (rice, 2_000))));
        Assert.True(r.IsSuccess, r.IsFailure ? r.Error.Code : "");
        return (await _h.Sender.Send(new GetBillQuery(r.Value.BillId)))!;
    }

    private async Task ChoosePrinterAsync(PaperWidth paper = PaperWidth.Mm58, bool auto = false)
    {
        var saved = await _h.Sender.Send(new SavePrinterSettingsCommand(new PrinterSettings("00:11:22:33:44:55", "BT-58", paper, auto)));
        Assert.True(saved.IsSuccess);
    }

    [Fact]
    public async Task NoPrinterChosen_PrintSaysSo_AndNothingIsRenderedOrSent()
    {
        var bill = await SellAsync();
        Assert.False((await _h.Sender.Send(new GetPrinterSettingsQuery())).IsSet);

        var r = await _h.Sender.Send(new PrintReceiptCommand(bill.Id));
        var t = await _h.Sender.Send(new PrintTestPageCommand());
        var n = await _h.Sender.Send(new PrintReturnNoteCommand(Guid.NewGuid()));

        Assert.All(new[] { r, t, n }, x => Assert.Equal(ErrorCodes.PrinterNotSet, x.Error.Code));
        Assert.Empty(_thermal.Calls);
        Assert.Empty(_transport.Jobs);
    }

    [Fact]
    public async Task PrintReceipt_RendersForTheChosenPaper_AndSendsToTheChosenPrinter()
    {
        var bill = await SellAsync();
        await ChoosePrinterAsync(PaperWidth.Mm80);

        var r = await _h.Sender.Send(new PrintReceiptCommand(bill.Id));

        Assert.True(r.IsSuccess, r.IsFailure ? r.Error.Code : "");
        Assert.Equal(("receipt:" + bill.Number, PaperWidth.Mm80), _thermal.Calls.Single());
        var job = Assert.Single(_transport.Jobs);
        Assert.Equal("00:11:22:33:44:55", job.Address);
        Assert.Equal(FakeThermal.Job, job.Bytes);
    }

    [Fact]
    public async Task PrintReturnNote_RendersTheReturnAgainstItsBill()
    {
        var bill = await SellAsync();
        await ChoosePrinterAsync();
        var ret = await _h.Sender.Send(new RecordReturnCommand(Guid.NewGuid(), bill.Id,
            [new ReturnRequestLine(bill.Lines[0].Id, new Qty(1_000))], RefundMethod.Cash));
        Assert.True(ret.IsSuccess, ret.IsFailure ? ret.Error.Code : "");

        var r = await _h.Sender.Send(new PrintReturnNoteCommand(ret.Value.ReturnId));

        Assert.True(r.IsSuccess, r.IsFailure ? r.Error.Code : "");
        Assert.Equal(("return:" + ret.Value.Number + "/" + bill.Number, PaperWidth.Mm58), _thermal.Calls.Single());
        Assert.Single(_transport.Jobs);
    }

    [Fact]
    public async Task UnknownBillOrReturn_IsRefused_WithoutTouchingThePrinter()
    {
        await _h.SeedShopAsync();
        await ChoosePrinterAsync();
        Assert.Equal(ErrorCodes.BillNotFound, (await _h.Sender.Send(new PrintReceiptCommand(Guid.NewGuid()))).Error.Code);
        Assert.Equal(ErrorCodes.ValidationFailed, (await _h.Sender.Send(new PrintReturnNoteCommand(Guid.NewGuid()))).Error.Code);
        Assert.Empty(_transport.Jobs);
    }

    [Theory]
    [InlineData(ErrorCodes.PrinterNoPaper)]
    [InlineData(ErrorCodes.PrinterConnectFailed)]
    [InlineData(ErrorCodes.PrinterDisconnected)]
    [InlineData(ErrorCodes.PrinterBluetoothOff)]
    public async Task PrinterProblems_ComeBackAsTheirOwnResult(string code)
    {
        var bill = await SellAsync();
        await ChoosePrinterAsync();
        _transport.Answer = Result.Failure(code);

        var r = await _h.Sender.Send(new PrintReceiptCommand(bill.Id));

        Assert.Equal(code, r.Error.Code);
        // The bill is untouched by a failed print.
        Assert.Equal(BillStatus.Finalized, (await _h.Sender.Send(new GetBillQuery(bill.Id)))!.Status);
    }

    [Fact]
    public async Task RenderFailure_IsReturned_AndNothingIsSent()
    {
        var bill = await SellAsync();
        await ChoosePrinterAsync();
        _thermal.Fail = true;

        var r = await _h.Sender.Send(new PrintReceiptCommand(bill.Id));

        Assert.Equal(ErrorCodes.ReceiptRenderFailed, r.Error.Code);
        Assert.Empty(_transport.Jobs);
    }

    [Fact]
    public async Task APrinterThatNeverAnswers_TimesOut_InsteadOfHangingTheScreen()
    {
        var bill = await SellAsync();
        await ChoosePrinterAsync();
        _transport.HangIgnoringCancellation = true;   // worst case: the transport ignores the token

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var r = await _h.Sender.Send(new PrintReceiptCommand(bill.Id));

        Assert.Equal(ErrorCodes.PrinterTimeout, r.Error.Code);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), clock.Elapsed.ToString());
        _transport.Release();
    }

    [Fact]
    public async Task TestPage_UsesTheShopName_AndThePaper()
    {
        await _h.SeedShopAsync();
        await ChoosePrinterAsync(PaperWidth.Mm80);

        var r = await _h.Sender.Send(new PrintTestPageCommand());

        Assert.True(r.IsSuccess);
        Assert.Equal(("test:రాము కిరాణా", PaperWidth.Mm80), _thermal.Calls.Single());
    }

    [Fact]
    public async Task Settings_RoundTrip_AndRemovingThePrinterTurnsAutoPrintOff()
    {
        await ChoosePrinterAsync(PaperWidth.Mm80, auto: true);
        var s = await _h.Sender.Send(new GetPrinterSettingsQuery());
        Assert.Equal(new PrinterSettings("00:11:22:33:44:55", "BT-58", PaperWidth.Mm80, true), s);

        await _h.Sender.Send(new SavePrinterSettingsCommand(s with { Address = null }));
        var cleared = await _h.Sender.Send(new GetPrinterSettingsQuery());
        Assert.False(cleared.IsSet);
        Assert.False(cleared.AutoPrint);
        Assert.Null(cleared.Name);
        Assert.Equal(PaperWidth.Mm80, cleared.Paper);

        var bad = await _h.Sender.Send(new SavePrinterSettingsCommand(s with { Paper = (PaperWidth)99 }));
        Assert.Equal(ErrorCodes.ValidationFailed, bad.Error.Code);
    }

    [Fact]
    public async Task WithoutBluetooth_ListingPrintersSaysSo()
    {
        using var plain = new AppHost();   // Infrastructure's own no-Bluetooth transport (non-Android)
        var r = await plain.Sender.Send(new ListPairedPrintersQuery());
        Assert.Equal(ErrorCodes.PrinterNoBluetooth, r.Error.Code);

        _transport.Paired = [new PairedPrinter("BT-58", "00:11:22:33:44:55")];
        var listed = await _h.Sender.Send(new ListPairedPrintersQuery());
        Assert.Equal("BT-58", Assert.Single(listed.Value).Name);
    }

    private sealed class FakeThermal : IThermalReceiptRenderer
    {
        public static readonly byte[] Job = [0x1B, 0x40, 0x1B, 0x64, 0x04];
        public List<(string What, PaperWidth Paper)> Calls { get; } = [];
        public bool Fail { get; set; }

        private Task<Result<byte[]>> Done(string what, PaperWidth paper)
        {
            Calls.Add((what, paper));
            return Task.FromResult(Fail ? Result.Failure<byte[]>(ErrorCodes.ReceiptRenderFailed) : Result.Success(Job));
        }

        public Task<Result<byte[]>> RenderReceiptAsync(Bill bill, ShopProfile shop, Customer? customer, PaperWidth paper, CancellationToken ct = default) =>
            Done("receipt:" + bill.Number, paper);

        public Task<Result<byte[]>> RenderReturnAsync(SalesReturn ret, Bill bill, ShopProfile shop, Customer? customer, PaperWidth paper, CancellationToken ct = default) =>
            Done("return:" + ret.Number + "/" + bill.Number, paper);

        public Task<Result<byte[]>> RenderTestPageAsync(string shopName, PaperWidth paper, CancellationToken ct = default) =>
            Done("test:" + shopName, paper);
    }

    private sealed class FakeTransport : IBluetoothPrinterTransport
    {
        private readonly TaskCompletionSource _hang = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<(string Address, byte[] Bytes)> Jobs { get; } = [];
        public Result Answer { get; set; } = Result.Success();
        public bool HangIgnoringCancellation { get; set; }
        public IReadOnlyList<PairedPrinter> Paired { get; set; } = [];

        public void Release() => _hang.TrySetResult();

        public Task<Result<IReadOnlyList<PairedPrinter>>> ListPairedAsync(CancellationToken ct = default) =>
            Task.FromResult(Paired.Count == 0 ? Result.Failure<IReadOnlyList<PairedPrinter>>(ErrorCodes.PrinterNonePaired) : Result.Success(Paired));

        public async Task<Result> SendAsync(string address, byte[] job, CancellationToken ct = default)
        {
            if (HangIgnoringCancellation) await _hang.Task;
            Jobs.Add((address, job));
            return Answer;
        }
    }
}
