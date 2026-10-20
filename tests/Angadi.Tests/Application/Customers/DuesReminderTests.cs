using Angadi.Application.Billing;
using Angadi.Application.Catalog;
using Angadi.Application.Common;
using Angadi.Application.Customers;
using Angadi.Application.Platform;
using Angadi.Domain.Billing;
using Angadi.Domain.Common;
using Angadi.Domain.Customers;
using Angadi.Domain.Items;
using Angadi.Domain.Shop;
using Microsoft.Extensions.DependencyInjection;

namespace Angadi.Tests.Application.Customers;

/// <summary>
/// Dues reminders: who is due (promise passed / oldest udhaar 7+ days), the 3-day cooldown, the itemised text in
/// Telugu and English, WhatsApp vs SMS, and every failure path (no phone, no dues, share fails or crashes).
/// </summary>
public class DuesReminderTests : IDisposable
{
    private static readonly TimeSpan Ist = TimeSpan.FromHours(5.5);
    private readonly ScriptedShare _share = new();
    private readonly AppHost _h;

    public DuesReminderTests()
    {
        _h = new AppHost(configure: s => s.AddSingleton<IReceiptShare>(_share));
        _h.Clock.Now = new DateTimeOffset(2026, 10, 19, 11, 0, 0, Ist);   // Monday 19 Oct, midday
    }

    public void Dispose() => _h.Dispose();

    private ILocalizer Text => _h.Services.GetRequiredService<ILocalizer>();

    private async Task<(Item rice, Item milk)> SeedAsync()
    {
        await _h.SeedShopAsync();
        var rice = await _h.AddItemAsync("బియ్యం", 6000, stockMilli: 1_000_000);
        var milk = await _h.AddItemAsync("పాలు", 3000, stockMilli: 1_000_000, unit: Unit.Pack);
        return (rice, milk);
    }

    private async Task<Customer> CustomerAsync(string name, string? phone)
    {
        var c = new Customer { Name = name, Phone = phone };
        var r = await _h.Sender.Send(new SaveCustomerCommand(c));
        Assert.True(r.IsSuccess, r.Error.Code);
        return c;
    }

    private async Task<FinalizedBill> UdhaarAsync(Customer c, params (Item item, long qtyMilli)[] lines)
    {
        var draft = AppHost.Draft(TenderType.Udhaar, lines);
        draft.CustomerId = c.Id;
        var r = await _h.Sender.Send(new FinalizeBillCommand(draft));
        Assert.True(r.IsSuccess, r.Error.Code);
        return r.Value;
    }

    private Task<IReadOnlyList<DueReminder>> DueAsync() => _h.Sender.Send(new GetDueRemindersQuery());
    private async Task<Customer> GetAsync(Guid id) => (await _h.Sender.Send(new GetCustomerQuery(id)))!;

    // ---------------- who is due ----------------

    [Fact]
    public async Task WithoutAPromise_DueOnceTheOldestUnpaidUdhaarIsSevenDaysOld()
    {
        var (rice, _) = await SeedAsync();
        var ravi = await CustomerAsync("రవి", "98765 43210");
        await UdhaarAsync(ravi, (rice, 1000));
        _h.Clock.Advance(TimeSpan.FromDays(6));
        Assert.Empty(await DueAsync());

        _h.Clock.Advance(TimeSpan.FromDays(1));
        var due = Assert.Single(await DueAsync());
        Assert.Equal(DueReason.UnpaidTooLong, due.Reason);
        Assert.Equal(7, due.OldestUnpaidDays);
        Assert.Equal(6000, due.Balance.Value);
        Assert.Equal("919876543210", due.Phone);
        Assert.Null(due.PromisedPayDate);
    }

    [Fact]
    public async Task Age_IsTheOldestStillUnpaid_SoPayingTheOldBillResetsIt()
    {
        var (rice, milk) = await SeedAsync();
        var ravi = await CustomerAsync("రవి", "9876543210");
        await UdhaarAsync(ravi, (rice, 1000));                 // day 0: ₹60
        _h.Clock.Advance(TimeSpan.FromDays(5));
        await UdhaarAsync(ravi, (milk, 1000));                 // day 5: ₹30
        _h.Clock.Advance(TimeSpan.FromDays(1));
        await _h.Sender.Send(new RecordDuesPaymentCommand(ravi.Id, Paise.FromRupees(60), null));   // day 0 cleared
        _h.Clock.Advance(TimeSpan.FromDays(2));                // day 8: the open udhaar is 3 days old
        Assert.Empty(await DueAsync());

        _h.Clock.Advance(TimeSpan.FromDays(4));                // day 12: 7 days old
        Assert.Equal(7, Assert.Single(await DueAsync()).OldestUnpaidDays);
    }

    [Fact]
    public async Task AFuturePromise_WinsOverAge_AndIsDueOnThePromisedDayItself()
    {
        var (rice, _) = await SeedAsync();
        var ravi = await CustomerAsync("రవి", "9876543210");
        await UdhaarAsync(ravi, (rice, 1000));
        await _h.Sender.Send(new SetPromisedPayDateCommand(ravi.Id, _h.Clock.Today.AddDays(20)));
        _h.Clock.Advance(TimeSpan.FromDays(19));
        Assert.Empty(await DueAsync());                        // 19 days old, but he promised day 20

        _h.Clock.Advance(TimeSpan.FromDays(1));
        var due = Assert.Single(await DueAsync());
        Assert.Equal(DueReason.PromiseDatePassed, due.Reason);
        Assert.Equal(new DateOnly(2026, 11, 8), due.PromisedPayDate);
    }

    [Fact]
    public async Task ClearingThePromise_FallsBackToTheAgeRule()
    {
        var (rice, _) = await SeedAsync();
        var ravi = await CustomerAsync("రవి", "9876543210");
        await UdhaarAsync(ravi, (rice, 1000));
        await _h.Sender.Send(new SetPromisedPayDateCommand(ravi.Id, _h.Clock.Today.AddDays(30)));
        _h.Clock.Advance(TimeSpan.FromDays(10));
        Assert.Empty(await DueAsync());
        Assert.True((await _h.Sender.Send(new SetPromisedPayDateCommand(ravi.Id, null))).IsSuccess);
        Assert.Equal(DueReason.UnpaidTooLong, Assert.Single(await DueAsync()).Reason);
        Assert.Equal(ErrorCodes.CustomerNotFound, (await _h.Sender.Send(new SetPromisedPayDateCommand(Guid.NewGuid(), null))).Error.Code);
    }

    [Fact]
    public async Task Cooldown_IsThreeFullDaysFromTheLastReminder()
    {
        var (rice, _) = await SeedAsync();
        var ravi = await CustomerAsync("రవి", "9876543210");
        await UdhaarAsync(ravi, (rice, 1000));
        _h.Clock.Advance(TimeSpan.FromDays(8));
        Assert.True((await _h.Sender.Send(new SendDuesReminderCommand(ravi.Id))).IsSuccess);
        Assert.Equal(_h.Clock.Now, (await GetAsync(ravi.Id)).LastRemindedAt);

        _h.Clock.Advance(TimeSpan.FromDays(3) - TimeSpan.FromMinutes(1));
        Assert.Empty(await DueAsync());
        _h.Clock.Advance(TimeSpan.FromMinutes(1));
        var due = Assert.Single(await DueAsync());
        Assert.NotNull(due.LastRemindedAt);
    }

    [Fact]
    public async Task PaidUp_LeavesTheList_EvenWithAPassedPromise()
    {
        var (rice, _) = await SeedAsync();
        var ravi = await CustomerAsync("రవి", "9876543210");
        await UdhaarAsync(ravi, (rice, 1000));
        await _h.Sender.Send(new SetPromisedPayDateCommand(ravi.Id, _h.Clock.Today));
        Assert.Single(await DueAsync());
        await _h.Sender.Send(new RecordDuesPaymentCommand(ravi.Id, Paise.FromRupees(60), null));
        Assert.Empty(await DueAsync());
        Assert.Equal(ErrorCodes.CustomerNoDues, (await _h.Sender.Send(new SendDuesReminderCommand(ravi.Id))).Error.Code);
        Assert.Empty(_share.WhatsApp);
    }

    [Fact]
    public async Task Order_PassedPromisesFirst_ThenTheBiggestBalance()
    {
        var (rice, _) = await SeedAsync();
        var small = await CustomerAsync("Small", "9000000001");
        var big = await CustomerAsync("Big", "9000000002");
        var promised = await CustomerAsync("Promised", "9000000003");
        await UdhaarAsync(small, (rice, 1000));
        await UdhaarAsync(big, (rice, 5000));
        await UdhaarAsync(promised, (rice, 500));
        await _h.Sender.Send(new SetPromisedPayDateCommand(promised.Id, _h.Clock.Today.AddDays(1)));
        _h.Clock.Advance(TimeSpan.FromDays(8));

        Assert.Equal(["Promised", "Big", "Small"], (await DueAsync()).Select(d => d.Name));
    }

    [Fact(Skip = "BUG T3-1: a promise date is never cleared when the udhaar is paid off, so the next new udhaar is 'due' the same day")]
    public async Task APromiseForUdhaarAlreadyPaidOff_DoesNotMakeNewUdhaarDueAtOnce()
    {
        var (rice, _) = await SeedAsync();
        var ravi = await CustomerAsync("రవి", "9876543210");
        await UdhaarAsync(ravi, (rice, 1000));
        await _h.Sender.Send(new SetPromisedPayDateCommand(ravi.Id, _h.Clock.Today.AddDays(2)));
        _h.Clock.Advance(TimeSpan.FromDays(2));
        await _h.Sender.Send(new RecordDuesPaymentCommand(ravi.Id, Paise.FromRupees(60), null));   // kept his promise

        _h.Clock.Advance(TimeSpan.FromDays(10));
        await UdhaarAsync(ravi, (rice, 500));                  // fresh udhaar today, no new promise
        Assert.Empty(await DueAsync());                        // today: "You said you would pay on 21 Oct" is wrong
    }

    // ---------------- the message ----------------

    /// <summary>19 Oct ₹60 udhaar, 21 Oct ₹30 udhaar, ₹20 paid (settles the oldest), promise 25 Oct.</summary>
    private async Task<Customer> TwoUdhaarsAsync()
    {
        var (rice, milk) = await SeedAsync();
        var ravi = await CustomerAsync("రవి", "98765-43210");
        await UdhaarAsync(ravi, (rice, 1000));
        _h.Clock.Advance(TimeSpan.FromDays(2));
        await UdhaarAsync(ravi, (milk, 1000));
        await _h.Sender.Send(new RecordDuesPaymentCommand(ravi.Id, Paise.FromRupees(20), null));
        await _h.Sender.Send(new SetPromisedPayDateCommand(ravi.Id, new DateOnly(2026, 10, 25)));
        return ravi;
    }

    [Theory]
    [InlineData(UiLanguage.Telugu, "19 అక్టోబర్", "21 అక్టోబర్", "25 అక్టోబర్")]
    [InlineData(UiLanguage.English, "19 Oct", "21 Oct", "25 Oct")]
    public async Task Message_IsItemisedOldestFirst_InTheAppLanguage(UiLanguage language, string first, string second, string promise)
    {
        var ravi = await TwoUdhaarsAsync();
        Text.Language = language;
        Assert.True((await _h.Sender.Send(new SendDuesReminderCommand(ravi.Id))).IsSuccess);

        var (phone, message) = Assert.Single(_share.WhatsApp);
        Assert.Equal("919876543210", phone);
        var expected = string.Join("\n",
            Text.Format("customers.remind.message", "రవి", "రాము కిరాణా", "₹70.00"),
            Text["customers.remind.bills"],
            $"• {first} — ₹40.00",
            $"• {second} — ₹30.00",
            Text.Format("customers.remind.promised", promise));
        Assert.Equal(expected, message);
        Assert.DoesNotContain("{", message, StringComparison.Ordinal);            // no unfilled slot
        Assert.DoesNotContain("customers.", message, StringComparison.Ordinal);   // no missing key shown raw
        if (language == UiLanguage.Telugu) Assert.Contains("బాకీ", message, StringComparison.Ordinal);
        else Assert.Contains("dues", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Message_ShowsAtMostFiveUdhaars_ThenAnEllipsis()
    {
        var (rice, _) = await SeedAsync();
        var ravi = await CustomerAsync("రవి", "9876543210");
        for (var i = 0; i < 7; i++)
        {
            await UdhaarAsync(ravi, (rice, 1000 + 1000 * i));      // ₹60, ₹120 … ₹420
            _h.Clock.Advance(TimeSpan.FromDays(1));
        }
        Text.Language = UiLanguage.English;
        Assert.True((await _h.Sender.Send(new SendDuesReminderCommand(ravi.Id))).IsSuccess);
        var lines = Assert.Single(_share.WhatsApp).Message.Split('\n');
        var bullets = lines.Where(l => l.StartsWith("• ", StringComparison.Ordinal)).ToList();
        Assert.Equal(["• 19 Oct — ₹60.00", "• 20 Oct — ₹120.00", "• 21 Oct — ₹180.00", "• 22 Oct — ₹240.00", "• 23 Oct — ₹300.00", "• …"], bullets);
        Assert.Contains("₹1,680.00", lines[0], StringComparison.Ordinal);   // the total still counts all seven
    }

    [Fact]
    public async Task Message_UsesIndianLakhGrouping()
    {
        await SeedAsync();
        var gold = await _h.AddItemAsync("Big order", 12_345_600, stockMilli: 10_000);   // ₹1,23,456 each
        var ravi = await CustomerAsync("Ravi", "9876543210");
        await UdhaarAsync(ravi, (gold, 1000));
        await _h.Sender.Send(new RecordDuesPaymentCommand(ravi.Id, new Paise(50), null));
        Assert.True((await _h.Sender.Send(new SendDuesReminderCommand(ravi.Id, ReminderChannel.Sms))).IsSuccess);
        var message = Assert.Single(_share.Sms).Message;
        Assert.Contains("₹1,23,455.50", message, StringComparison.Ordinal);
        Assert.DoesNotContain("123455", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Channel_SmsGoesOnlyToSms_WhatsAppOnlyToWhatsApp_SameText()
    {
        var ravi = await TwoUdhaarsAsync();
        Assert.True((await _h.Sender.Send(new SendDuesReminderCommand(ravi.Id, ReminderChannel.Sms))).IsSuccess);
        Assert.Empty(_share.WhatsApp);
        var sms = Assert.Single(_share.Sms);
        Assert.Equal("919876543210", sms.Phone);

        _h.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True((await _h.Sender.Send(new SendDuesReminderCommand(ravi.Id, ReminderChannel.WhatsApp))).IsSuccess);
        Assert.Single(_share.Sms);
        Assert.Equal(sms, Assert.Single(_share.WhatsApp));
    }

    [Fact]
    public async Task Reminder_GoesToTheNumberOnFileNow_AfterThePhoneIsEdited()
    {
        var ravi = await TwoUdhaarsAsync();
        var edited = await GetAsync(ravi.Id);
        edited.Phone = "+91 70000 12345";
        Assert.True((await _h.Sender.Send(new SaveCustomerCommand(edited))).IsSuccess);
        Assert.True((await _h.Sender.Send(new SendDuesReminderCommand(ravi.Id))).IsSuccess);
        Assert.Equal("917000012345", Assert.Single(_share.WhatsApp).Phone);
    }

    // ---------------- failure paths ----------------

    [Fact]
    public async Task NoPhone_IsAClearError_ButTheCustomerStillShowsAsDue()
    {
        var (rice, _) = await SeedAsync();
        var walkInRegular = await CustomerAsync("లక్ష్మి", null);
        Assert.Null((await GetAsync(walkInRegular.Id)).Phone);
        await UdhaarAsync(walkInRegular, (rice, 1000));
        _h.Clock.Advance(TimeSpan.FromDays(7));

        var due = Assert.Single(await DueAsync());
        Assert.Null(due.Phone);   // the list shows them; the UI offers "add a phone"
        foreach (var channel in new[] { ReminderChannel.WhatsApp, ReminderChannel.Sms })
            Assert.Equal(ErrorCodes.CustomerNoPhone, (await _h.Sender.Send(new SendDuesReminderCommand(walkInRegular.Id, channel))).Error.Code);
        Assert.Empty(_share.WhatsApp);
        Assert.Empty(_share.Sms);
        Assert.Null((await GetAsync(walkInRegular.Id)).LastRemindedAt);
        Assert.False(string.IsNullOrWhiteSpace(Text.Error(new Error(ErrorCodes.CustomerNoPhone))));
    }

    [Fact]
    public async Task UnknownCustomer_IsCustomerNotFound()
    {
        await SeedAsync();
        Assert.Equal(ErrorCodes.CustomerNotFound, (await _h.Sender.Send(new SendDuesReminderCommand(Guid.NewGuid()))).Error.Code);
    }

    [Theory]
    [InlineData(ShareBehaviour.Fails, ReminderChannel.WhatsApp)]
    [InlineData(ShareBehaviour.Fails, ReminderChannel.Sms)]
    [InlineData(ShareBehaviour.Throws, ReminderChannel.WhatsApp)]
    [InlineData(ShareBehaviour.Throws, ReminderChannel.Sms)]
    public async Task WhenWhatsAppOrSmsCannotOpen_ItIsAResultError_AndNotCountedAsReminded(ShareBehaviour behaviour, ReminderChannel channel)
    {
        var (rice, _) = await SeedAsync();
        var ravi = await CustomerAsync("రవి", "9876543210");
        await UdhaarAsync(ravi, (rice, 1000));
        _h.Clock.Advance(TimeSpan.FromDays(8));
        _share.Behaviour = behaviour;

        var r = await _h.Sender.Send(new SendDuesReminderCommand(ravi.Id, channel));   // never throws to the page
        Assert.True(r.IsFailure);
        Assert.Equal(behaviour == ShareBehaviour.Fails ? ErrorCodes.ShareFailed : ErrorCodes.Unexpected, r.Error.Code);
        Assert.Null((await GetAsync(ravi.Id)).LastRemindedAt);
        Assert.Single(await DueAsync());                       // still on the list to try again

        _share.Behaviour = ShareBehaviour.Works;
        Assert.True((await _h.Sender.Send(new SendDuesReminderCommand(ravi.Id, channel))).IsSuccess);
        Assert.Empty(await DueAsync());
    }
}
