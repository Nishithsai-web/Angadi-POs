using Android.App;
using Android.Content.PM;
using Microsoft.Maui;

namespace Angadi.Maui;

/// <summary>
/// Single activity. SingleTop so a wa.me / share round-trip returns to the same instance, and the
/// ConfigurationChanges list keeps rotation, tablet resize and dark-mode switches from recreating
/// the activity mid-bill.
/// </summary>
[Activity(
    Theme = "@style/Maui.SplashTheme",
    MainLauncher = true,
    LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.ScreenSize
                         | ConfigChanges.Orientation
                         | ConfigChanges.UiMode
                         | ConfigChanges.ScreenLayout
                         | ConfigChanges.SmallestScreenSize
                         | ConfigChanges.Density
                         | ConfigChanges.KeyboardHidden)]
public class MainActivity : MauiAppCompatActivity
{
}
