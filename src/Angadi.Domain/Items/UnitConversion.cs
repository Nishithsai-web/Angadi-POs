using Angadi.Domain.Common;

namespace Angadi.Domain.Items;

/// <summary>Maps a spoken quantity onto the item's own unit (FR-06).</summary>
public static class UnitConversion
{
    /// <summary>
    /// "200 గ్రాములు" of an item sold per kg → 0.2 kg. Kg↔g and L↔ml convert exactly.
    /// <paramref name="exact"/> is false when the families differ (e.g. "2 kg" of an item sold per piece):
    /// the number is kept and the UI should ask the shopkeeper to confirm.
    /// </summary>
    public static Qty ToItemUnit(Qty spoken, Unit? spokenUnit, Unit itemUnit, out bool exact)
    {
        exact = true;
        if (spokenUnit is null || spokenUnit == itemUnit) return spoken;

        var from = spokenUnit.Value;
        if (Family(from) != Family(itemUnit) || Family(from) == 0)
        {
            exact = false;
            return spoken;
        }

        var factor = Scale(from) / (double)Scale(itemUnit);   // e.g. g→kg = 1/1000
        return new Qty((long)Math.Round(spoken.Milli * factor, MidpointRounding.AwayFromZero));
    }

    /// <summary>"పది రూపాయల ఉల్లిపాయలు": quantity = amount ÷ unit price, in milli-units, half up.</summary>
    public static Qty FromAmount(Paise amount, Paise unitPrice)
    {
        if (unitPrice.Value <= 0 || amount.Value <= 0) return Qty.Whole(1);
        return new Qty((2 * amount.Value * 1000 + unitPrice.Value) / (2 * unitPrice.Value));
    }

    private static int Family(Unit u) => u switch
    {
        Unit.Kilogram or Unit.Gram => 1,
        Unit.Litre or Unit.Millilitre => 2,
        _ => 0,
    };

    private static long Scale(Unit u) => u switch
    {
        Unit.Kilogram or Unit.Litre => 1000,
        _ => 1,
    };
}
