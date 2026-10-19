namespace Angadi.Maui.Areas.Items;

/// <summary>
/// Two panes side by side on a landscape tablet, stacked on a portrait phone (NFR-03).
/// Called from a page's OnSizeAllocated; only touches the grid when the mode actually changes.
/// </summary>
internal static class AdaptiveColumns
{
    public const double WideBreakpoint = 720;

    /// <param name="stretchSecondWhenNarrow">True when the second pane is a list that should take the remaining height.</param>
    /// <returns>The mode now applied (true = wide), or <paramref name="current"/> if nothing changed.</returns>
    public static bool? Apply(Grid grid, View first, View second, double width, bool? current, bool stretchSecondWhenNarrow)
    {
        if (width <= 0) return current;
        var wide = width >= WideBreakpoint;
        if (current == wide) return current;

        grid.ColumnDefinitions.Clear();
        grid.RowDefinitions.Clear();
        if (wide)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            grid.RowDefinitions.Add(new RowDefinition(stretchSecondWhenNarrow ? GridLength.Star : GridLength.Auto));
            Grid.SetRow(first, 0); Grid.SetColumn(first, 0);
            Grid.SetRow(second, 0); Grid.SetColumn(second, 1);
        }
        else
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            grid.RowDefinitions.Add(new RowDefinition(stretchSecondWhenNarrow ? GridLength.Star : GridLength.Auto));
            Grid.SetRow(first, 0); Grid.SetColumn(first, 0);
            Grid.SetRow(second, 1); Grid.SetColumn(second, 0);
        }
        return wide;
    }
}
