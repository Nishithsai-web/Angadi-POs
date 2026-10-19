using Angadi.Application.Platform;
using Microsoft.Maui.Controls.Xaml;

namespace Angadi.Maui.Core;

/// <summary>
/// XAML: xmlns:core="clr-namespace:Angadi.Maui.Core" then Text="{core:Translate pos.mic}".
/// Every user-facing string comes from the JSON resources (NFR-04). Language switches take effect
/// when a page is next created (pages are transient).
/// </summary>
[ContentProperty(nameof(Key))]
[AcceptEmptyServiceProvider]
public sealed class TranslateExtension : IMarkupExtension<string>
{
    public string Key { get; set; } = "";

    public string ProvideValue(IServiceProvider serviceProvider) => Localizer?[Key] ?? Key;

    object IMarkupExtension.ProvideValue(IServiceProvider serviceProvider) => ProvideValue(serviceProvider);

    /// <summary>Set once in MauiProgram after the container is built.</summary>
    public static ILocalizer? Localizer { get; set; }
}
