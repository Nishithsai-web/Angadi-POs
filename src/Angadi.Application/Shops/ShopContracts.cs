using Angadi.Application.Common;

namespace Angadi.Application.Shops;

// v2 (v2-print-shops): several shops on one phone. Each shop is its own database file (books, bill numbers, owner
// PIN, unfinished bill, safety copies); the first shop keeps files/angadi.db so existing installs are untouched.
// Rules: the free tier has ONE shop — adding another needs Pro (one subscription covers every shop on the phone);
// the free bill quota counts bills of ALL shops on the phone (it never multiplies per shop); backups and restore act
// on the current shop only.

/// <param name="Id">"main" for the first shop, otherwise 32 hex characters.</param>
/// <param name="Name">The name in the shop list ("" until the shop's setup is saved).</param>
public sealed record ShopEntry(string Id, string Name, bool IsCurrent);

public sealed record ShopList(IReadOnlyList<ShopEntry> Shops)
{
    public ShopEntry Current => Shops.First(s => s.IsCurrent);
    public bool HasSeveral => Shops.Count > 1;
}

/// <summary>The phone's shop registry plus the live database switch (Infrastructure).</summary>
public interface IShopDirectory
{
    IReadOnlyList<ShopEntry> List();
    /// <summary>Creates a new empty shop and switches to it (the shell then shows normal shop setup).</summary>
    Task<Result<ShopEntry>> AddAndSwitchAsync(CancellationToken ct = default);
    /// <summary>Closes the current shop's database cleanly and opens the other one. On failure the current shop stays open.</summary>
    Task<Result> SwitchAsync(string shopId, CancellationToken ct = default);
    Result Rename(string shopId, string name);
}

public sealed record ListShopsQuery : IQuery<ShopList>;
/// <summary>Owner-PIN gated in the UI. Free tier: fails SHOP_NEEDS_PRO when a shop already exists.</summary>
public sealed record AddShopCommand : ICommand<Result<ShopEntry>>, INoTransaction;
public sealed record SwitchShopCommand(string ShopId) : ICommand<Result>, INoTransaction;
public sealed record RenameShopCommand(string ShopId, string Name) : ICommand<Result>, INoTransaction;
