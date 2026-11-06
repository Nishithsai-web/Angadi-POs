using Angadi.Application.Common;
using Angadi.Application.Platform;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Angadi.Maui.Core;

/// <summary>
/// In-app guidance: a "?" button on each screen (what it is and what to do) and a short first-time walkthrough that also
/// explains the plan and price. All text is in strings.help.{te,en}.json; the price and free-bill limit come from
/// BillingOptions, never from the text. Everything here is optional: a failure is swallowed so help can never block a sale.
/// </summary>
public static class HelpGuide
{
    private const string HelpId = "angadi.help";
    private const string SeenKey = "angadi.walkthrough.seen";
    private static readonly HashSet<string> Pages = new(HelpPages.Names, StringComparer.Ordinal);

    /// <summary>Adds the "?" button when this screen has help text. Safe to call twice.</summary>
    public static void Attach(Page page, ILocalizer text, IDialogService dialogs)
    {
        var name = page.GetType().Name;
        if (!Pages.Contains(name) || page.ToolbarItems.Any(t => string.Equals(t.ClassId, HelpId, StringComparison.Ordinal))) return;
        var item = new ToolbarItem { Text = text["help.button"], ClassId = HelpId, Order = ToolbarItemOrder.Primary };
        item.Clicked += SafeUi.On(() => dialogs.AlertAsync(text["help." + name + ".title"], text["help." + name + ".body"], text["common.ok"]));
        page.ToolbarItems.Add(item);
    }

    /// <summary>Runs the tour once per phone (after the shop is set up). Skipping also counts as seen.</summary>
    public static async Task RunIfFirstTimeAsync(ILocalizer text, IDialogService dialogs)
    {
        try
        {
            if (Preferences.Default.Get(SeenKey, false)) return;
            await RunAsync(text, dialogs);
        }
        catch (Exception ex)
        {
            GlobalExceptions.Report(ex, "walkthrough");
        }
    }

    /// <summary>The tour, step by step. Settings → How to use Angadi runs it again at any time.</summary>
    public static async Task RunAsync(ILocalizer text, IDialogService dialogs)
    {
        var billing = SafeUi.Services?.GetService<IOptions<BillingOptions>>()?.Value ?? new BillingOptions();
        var total = HelpPages.WalkthroughSteps;
        for (var i = 1; i <= total; i++)
        {
            var title = i + "/" + total + " · " + text["walk." + i + ".title"];
            if (i == total)
            {
                var body = text.Format("walk." + i + ".body", billing.FreeBillsPerMonth, billing.MonthlyPriceRupees, billing.YearlyPriceRupees);
                await dialogs.AlertAsync(title, body, text["walk.done"]);
                break;
            }
            if (!await dialogs.ConfirmAsync(title, text["walk." + i + ".body"], text["walk.next"], text["walk.skip"])) break;
        }
        try { Preferences.Default.Set(SeenKey, true); } catch (Exception ex) { GlobalExceptions.Report(ex, "walkthrough-seen"); }
    }
}
