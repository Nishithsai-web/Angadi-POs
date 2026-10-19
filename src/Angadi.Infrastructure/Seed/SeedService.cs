using Angadi.Application.Persistence;
using Angadi.Domain.Inventory;
using Microsoft.Extensions.Logging;

namespace Angadi.Infrastructure.Seed;

/// <summary>FR-04. Idempotent: does nothing once any item exists. Opening stock goes through the ledger.</summary>
internal sealed class SeedService(IUnitOfWork uow, IItemRepository items, IStockLedger ledger, ILogger<SeedService> log) : ISeedService
{
    public async Task<int> SeedAsync(bool kirana, bool pharmacy, CancellationToken ct = default)
    {
        if (!kirana && !pharmacy) return 0;

        return await uow.ExecuteAsync(async () =>
        {
            if (await items.AnyAsync(ct)) return 0;

            var rows = (kirana ? SeedCatalog.Kirana() : []).Concat(pharmacy ? SeedCatalog.Pharmacy() : []).ToList();
            foreach (var seed in rows)
            {
                await items.SaveAsync(seed.Item, ct);
                if (seed.OpeningStock.Milli > 0)
                    await ledger.AdjustAsync(seed.Item.Id, seed.OpeningStock, StockMoveReason.Opening, note: "seed", ct: ct);
            }
            log.LogInformation("Seeded {Count} items (kirana={Kirana}, pharmacy={Pharmacy})", rows.Count, kirana, pharmacy);
            return rows.Count;
        }, _ => true, ct);
    }
}
