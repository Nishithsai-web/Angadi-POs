using Angadi.Application;
using Angadi.Application.Catalog;
using Angadi.Application.Common;
using Angadi.Application.Platform;
using Angadi.Infrastructure;
using Angadi.Infrastructure.Logging;
using Angadi.Infrastructure.Receipts;
using Angadi.Maui.Areas;
using Angadi.Maui.Core;
using Angadi.Maui.Platform;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Angadi.Maui;

/// <summary>
/// P1: the app's entry point. Composition root for Application + Infrastructure + platform adapters + pages.
/// </summary>
public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("NotoSansTelugu-Regular.ttf", Theme.Font);
                fonts.AddFont("NotoSansTelugu-Bold.ttf", Theme.FontBold);
                fonts.AddFont("NotoSans-Regular.ttf", "Latin");
                fonts.AddFont("NotoSans-Bold.ttf", "LatinBold");
            });

        // H7: options come from Resources/Raw/appsettings.json, not compile-time constants.
        var config = LoadConfiguration();
        builder.Configuration.AddConfiguration(config);
        builder.Services.Configure<VoiceOptions>(o => config.GetSection("Voice").Bind(o));
        builder.Services.Configure<BillingOptions>(o => config.GetSection("Billing").Bind(o));
        builder.Services.Configure<InsightsOptions>(o => config.GetSection("Insights").Bind(o));

        // NFR-07/FR-36: rolling file logs on the device, exported from Settings.
        var logs = new LogFiles(Path.Combine(FileSystem.AppDataDirectory, "logs"));
#if DEBUG
        builder.Logging.AddProvider(logs.CreateProvider(debug: true));
#else
        builder.Logging.AddProvider(logs.CreateProvider(debug: false));
#endif

        builder.Services.AddAngadiApplication();
        builder.Services.AddAngadiInfrastructure(new InfrastructureOptions
        {
            DataDirectory = FileSystem.AppDataDirectory,
            CacheDirectory = FileSystem.CacheDirectory,
            ReceiptFonts = new ReceiptFonts(
                () => OpenAsset("fonts/NotoSansTelugu-Regular.ttf"),
                () => OpenAsset("fonts/NotoSansTelugu-Bold.ttf"),
                () => OpenAsset("fonts/NotoSans-Regular.ttf"),
                () => OpenAsset("fonts/NotoSans-Bold.ttf")),
        });
        builder.Services.AddAndroidPlatform();
        builder.Services.AddShellServices();
        builder.Services.AddAppPages();

        var app = builder.Build();

        GlobalExceptions.Logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Angadi.Unhandled");
        TranslateExtension.Localizer = app.Services.GetRequiredService<ILocalizer>();
        HookUnhandledExceptions();
        ApplySavedLanguage(app.Services);
        return app;
    }

    private static IConfiguration LoadConfiguration()
    {
        var builder = new ConfigurationBuilder();
        try
        {
            using var stream = FileSystem.OpenAppPackageFileAsync("appsettings.json").GetAwaiter().GetResult();
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            copy.Position = 0;
            builder.AddJsonStream(new MemoryStream(copy.ToArray()));
        }
        catch (Exception ex) when (ex is FileNotFoundException or IOException)
        {
            // Missing settings must never stop the app: the Options defaults match the SRS.
        }
        return builder.Build();
    }

    private static Stream OpenAsset(string path)
    {
        using var source = FileSystem.OpenAppPackageFileAsync(path).GetAwaiter().GetResult();
        var copy = new MemoryStream();
        source.CopyTo(copy);
        copy.Position = 0;
        return copy;
    }

    private static void ApplySavedLanguage(IServiceProvider services)
    {
        try
        {
            var profile = services.GetRequiredService<ISender>().Send(new GetShopProfileQuery()).GetAwaiter().GetResult();
            if (profile is not null) services.GetRequiredService<ILocalizer>().Language = profile.Language;
        }
        catch (Exception ex)
        {
            GlobalExceptions.Report(ex, "startup-language");
        }
    }

    /// <summary>NFR-07: the app never hard-crashes at the counter; everything unexpected is logged with a correlation id.</summary>
    private static void HookUnhandledExceptions()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) GlobalExceptions.Report(ex, "AppDomain");
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            GlobalExceptions.Report(e.Exception, "UnobservedTask");
            e.SetObserved();
        };
#if ANDROID
        Android.Runtime.AndroidEnvironment.UnhandledExceptionRaiser += (_, e) =>
        {
            GlobalExceptions.Report(e.Exception, "Android");
            e.Handled = true;
        };
#endif
    }
}
