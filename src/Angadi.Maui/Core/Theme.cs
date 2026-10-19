using Microsoft.Maui.Controls.Shapes;

namespace Angadi.Maui.Core;

/// <summary>
/// The whole visual system in one place (docs/UI_SPEC.md): light, high-contrast for sunlit counters,
/// Telugu-first type with +20% line height, ≥48 dp touch targets, ≥64 dp on billing actions.
/// Built in C# (no XAML) so every value is compile-checked.
/// </summary>
public static class Theme
{
    public static readonly Color Primary = Color.FromArgb("#0F5132");
    public static readonly Color OnPrimary = Colors.White;
    public static readonly Color PrimarySoft = Color.FromArgb("#DDEFE4");
    public static readonly Color Accent = Color.FromArgb("#F4A300");
    public static readonly Color Surface = Colors.White;
    public static readonly Color Background = Color.FromArgb("#F4F3EE");
    public static readonly Color TextPrimary = Color.FromArgb("#101814");
    public static readonly Color TextSecondary = Color.FromArgb("#44504A");
    public static readonly Color Border = Color.FromArgb("#C9CFC9");
    public static readonly Color Danger = Color.FromArgb("#B3261E");
    public static readonly Color Success = Color.FromArgb("#146C2E");
    public static readonly Color Warning = Color.FromArgb("#B45309");
    public static readonly Color CashColor = Color.FromArgb("#146C2E");
    public static readonly Color UpiColor = Color.FromArgb("#3B2A8C");
    public static readonly Color UdhaarColor = Color.FromArgb("#9A4A00");

    public const string Font = "Telugu";
    public const string FontBold = "TeluguBold";
    public const double LineHeight = 1.2;
    public const double Touch = 48;
    public const double BigTouch = 64;

    /// <summary>Implicit styles so plain controls already look right.</summary>
    public static ResourceDictionary Build()
    {
        var r = new ResourceDictionary();
        r.Add(new Style(typeof(Label))
        {
            Setters =
            {
                new Setter { Property = Label.FontFamilyProperty, Value = Font },
                new Setter { Property = Label.TextColorProperty, Value = TextPrimary },
                new Setter { Property = Label.FontSizeProperty, Value = 16d },
                new Setter { Property = Label.LineHeightProperty, Value = LineHeight },
            },
        });
        r.Add(new Style(typeof(Button))
        {
            Setters =
            {
                new Setter { Property = Button.FontFamilyProperty, Value = FontBold },
                new Setter { Property = Button.FontSizeProperty, Value = 17d },
                new Setter { Property = Button.BackgroundColorProperty, Value = Primary },
                new Setter { Property = Button.TextColorProperty, Value = OnPrimary },
                new Setter { Property = Button.CornerRadiusProperty, Value = 12 },
                new Setter { Property = VisualElement.MinimumHeightRequestProperty, Value = Touch },
                new Setter { Property = Button.PaddingProperty, Value = new Thickness(16, 8) },
            },
        });
        r.Add(new Style(typeof(Entry))
        {
            Setters =
            {
                new Setter { Property = Entry.FontFamilyProperty, Value = Font },
                new Setter { Property = Entry.FontSizeProperty, Value = 17d },
                new Setter { Property = Entry.TextColorProperty, Value = TextPrimary },
                new Setter { Property = Entry.PlaceholderColorProperty, Value = TextSecondary },
                new Setter { Property = VisualElement.MinimumHeightRequestProperty, Value = Touch },
            },
        });
        r.Add(new Style(typeof(ContentPage))
        {
            Setters = { new Setter { Property = VisualElement.BackgroundColorProperty, Value = Background } },
        });
        return r;
    }
}

/// <summary>Small factory for the recurring controls, so pages stay short and consistent.</summary>
public static class Ui
{
    public static Label Title(string text) => new()
    {
        Text = text, FontFamily = Theme.FontBold, FontSize = 22, Margin = new Thickness(0, 4, 0, 8),
    };

    public static Label Body(string text = "", double size = 16) => new() { Text = text, FontSize = size };

    public static Label Caption(string text = "") => new() { Text = text, FontSize = 13, TextColor = Theme.TextSecondary };

    public static Label Amount(string text = "") => new()
    {
        Text = text, FontFamily = Theme.FontBold, FontSize = 30, TextColor = Theme.Primary, HorizontalTextAlignment = TextAlignment.End,
    };

    public static Button Button(string text, Color? color = null, bool big = false)
    {
        var b = new Button { Text = text };
        if (color is not null) b.BackgroundColor = color;
        if (big) { b.MinimumHeightRequest = Theme.BigTouch; b.FontSize = 19; }
        return b;
    }

    public static Button Secondary(string text) => new()
    {
        Text = text, BackgroundColor = Theme.PrimarySoft, TextColor = Theme.Primary,
    };

    public static Entry Entry(string placeholder, Keyboard? keyboard = null) => new()
    {
        Placeholder = placeholder, Keyboard = keyboard ?? Keyboard.Default, BackgroundColor = Theme.Surface,
    };

    public static Border Card(View content, double padding = 12) => new()
    {
        Content = content,
        Padding = new Thickness(padding),
        BackgroundColor = Theme.Surface,
        Stroke = Theme.Border,
        StrokeThickness = 1,
        StrokeShape = new RoundRectangle { CornerRadius = 12 },
    };

    /// <summary>Label + value row, value right-aligned (totals, report lines).</summary>
    public static Grid Row(string label, string bindingPath, bool strong = false)
    {
        var g = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) } };
        var l = Body(label);
        var v = new Label { HorizontalTextAlignment = TextAlignment.End, FontSize = strong ? 20 : 16, FontFamily = strong ? Theme.FontBold : Theme.Font };
        v.SetBinding(Label.TextProperty, bindingPath);
        g.Add(l, 0, 0);
        g.Add(v, 1, 0);
        return g;
    }

    /// <summary>Empty / loading / error line bound to a VM string; hidden when the string is empty.</summary>
    public static Label StateLine(string bindingPath)
    {
        var l = Caption();
        l.HorizontalTextAlignment = TextAlignment.Center;
        l.Margin = new Thickness(0, 16);
        l.SetBinding(Label.TextProperty, bindingPath);
        l.SetBinding(VisualElement.IsVisibleProperty, bindingPath, converter: new NotEmptyConverter());
        return l;
    }

    public static ScrollView Scroll(params View[] children)
    {
        var stack = new VerticalStackLayout { Spacing = 12, Padding = new Thickness(16) };
        foreach (var c in children) stack.Add(c);
        return new ScrollView { Content = stack };
    }
}

public sealed class NotEmptyConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => value is string s ? s.Length > 0 : value is not null;

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}
