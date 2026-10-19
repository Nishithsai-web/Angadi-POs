using Android.App;
using Android.Runtime;
using Microsoft.Maui;
using Microsoft.Maui.Hosting;

namespace Angadi.Maui;

[Application]
public class MainApplication : MauiApplication
{
    public MainApplication(IntPtr handle, JniHandleOwnership ownership)
        : base(handle, ownership)
    {
    }

    /// <summary>MauiProgram is owned by the shell agent (namespace Angadi.Maui).</summary>
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
