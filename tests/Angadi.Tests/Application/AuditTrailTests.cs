using System.Text.Json;
using Angadi.Application.Catalog;
using Angadi.Domain.Common;
using Angadi.Domain.Shop;
using Angadi.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Angadi.Tests.Application;

/// <summary>
/// SEC-3: the frauds that actually happen in shops — a swapped UPI ID and quiet price edits — must leave an
/// old→new record. The record lives in the Outbox payload, so it is inside every backup the shopkeeper keeps.
/// </summary>
public class AuditTrailTests : IDisposable
{
    private readonly AppHost _h = new();
    public void Dispose() => _h.Dispose();

    private async Task<JsonElement> LatestPayloadAsync(string eventType)
    {
        var db = _h.Services.GetRequiredService<AngadiDb>();
        var json = await db.RunAsync(c => c.ExecuteScalar<string>(
            "SELECT PayloadJson FROM Outbox WHERE EventType = ? ORDER BY CreatedAtMs DESC, rowid DESC LIMIT 1", eventType));
        Assert.False(string.IsNullOrEmpty(json), $"no {eventType} row in the Outbox");
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    [Fact]
    public async Task PriceEdit_LeavesOldAndNewPrice_InTheOutbox()
    {
        await _h.SeedShopAsync();
        var rice = await _h.AddItemAsync("బియ్యం", 6000);              // ₹60 at create

        rice.Price = new Paise(6500);                                 // the edit a price-shrinkage fraud makes
        Assert.True((await _h.Sender.Send(new SaveItemCommand(rice))).IsSuccess);

        var payload = await LatestPayloadAsync("ItemChanged");
        Assert.Equal(6000, payload.GetProperty("oldPricePaise").GetInt64());
        Assert.Equal(6500, payload.GetProperty("newPricePaise").GetInt64());
        Assert.Equal(rice.Id, payload.GetProperty("aggregateId").GetGuid());
    }

    [Fact]
    public async Task NewItem_RecordsItsCreatePrice_WithNoOldPrice()
    {
        await _h.SeedShopAsync();
        var rice = await _h.AddItemAsync("బియ్యం", 6000);

        var payload = await LatestPayloadAsync("ItemChanged");
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("oldPricePaise").ValueKind);
        Assert.Equal(6000, payload.GetProperty("newPricePaise").GetInt64());
        Assert.Equal(rice.Id, payload.GetProperty("aggregateId").GetGuid());
    }

    [Fact]
    public async Task ShopProfileSave_LeavesOldAndNewUpiIdAndGstin_InTheOutbox()
    {
        await _h.SeedShopAsync();                                     // UPI ramu@okaxis, no GSTIN

        var changed = new ShopProfile { Name = "రాము కిరాణా", UpiId = "ravi@okhdfc", Gstin = "37ABCDE1234F1Z5" };
        Assert.True((await _h.Sender.Send(new SaveShopProfileCommand(changed))).IsSuccess);

        var payload = await LatestPayloadAsync("ShopProfileChanged");
        Assert.Equal("ramu@okaxis", payload.GetProperty("oldUpiId").GetString());
        Assert.Equal("ravi@okhdfc", payload.GetProperty("newUpiId").GetString());
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("oldGstin").ValueKind);
        Assert.Equal("37ABCDE1234F1Z5", payload.GetProperty("newGstin").GetString());
    }

    [Fact]
    public async Task FirstProfileSave_RecordsTheNewUpiId_WithNoOldValue()
    {
        await _h.SeedShopAsync();

        var payload = await LatestPayloadAsync("ShopProfileChanged");
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("oldUpiId").ValueKind);
        Assert.Equal("ramu@okaxis", payload.GetProperty("newUpiId").GetString());
    }
}
