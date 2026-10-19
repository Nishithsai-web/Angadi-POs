namespace Angadi.Maui.Areas.Reports;

/// <summary>
/// Lays a Grid's children out in two columns on a tablet (width ≥ 800) and one column on a phone.
/// Called from a page's SizeChanged. Child order in XAML is the reading order.
/// </summary>
internal static class AdaptiveColumns
{
    public const double TwoColumnMinWidth = 800;

    public static void Apply(Grid grid, double width)
    {
        if (width <= 0) return;

        var columns = width >= TwoColumnMinWidth ? 2 : 1;
        var count = grid.Children.Count;
        var rows = (count + columns - 1) / columns;
        if (grid.ColumnDefinitions.Count == columns && grid.RowDefinitions.Count == rows) return;

        grid.ColumnDefinitions.Clear();
        for (var c = 0; c < columns; c++) grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));

        grid.RowDefinitions.Clear();
        for (var r = 0; r < rows; r++) grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        for (var i = 0; i < count; i++)
        {
            if (grid.Children[i] is not BindableObject child) continue;
            Grid.SetColumn(child, i % columns);
            Grid.SetRow(child, i / columns);
        }
    }
}
