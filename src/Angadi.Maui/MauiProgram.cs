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
using BarcodeScanning;
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
            .UseBarcodeScanning()
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
        builder.Services.Configure<SupportOptions>(o => config.GetSection("Support").Bind(o));
        builder.Services.Configure<DuesOptions>(o => config.GetSection("Dues").Bind(o));

        // NFR-07/FR-36: rolling file logs on the device, exported from Settings.
        var logs = new LogFiles(Path.Combine(FileSystem.AppDataDirectory, "logs"));
#if DEBUG
        var logProvider = logs.CreateProvider(debug: true);
#else
        var logProvider = logs.CreateProvider(debug: false);
#endif
        builder.Logging.AddProvider(logProvider);
        // FIX(LOG-3b, 2026-10-20, FIX-3): Flaw: GlobalExceptions.Logger was only assigned AFTER builder.Build(),
        // so anything reported during startup (e.g. a malformed appsettings.json, LOG-2) never reached the file
        // log. Fix: a bootstrap logger over the SAME provider instance (one file writer) is assigned now and
        // replaced by the app's factory logger after Build. The factory is intentionally not disposed — it
        // shares the provider, whose lifetime is the app's. Logged in docs/AGENT_FIX_LOG.md.
        GlobalExceptions.Logger = new LoggerFactory([logProvider]).CreateLogger("Angadi.Unhandled");
        // FIX(LOG-2, 2026-10-20, FIX-3): the settings fallback taken in LoadConfiguration is silent no longer —
        // one startup warning, now that a logger exists (see the catch in LoadConfiguration). Logged in
        // docs/AGENT_FIX_LOG.md.
        if (_configProblem is { } problem)
            GlobalExceptions.Logger.LogWarning("appsettings.json was not used ({Problem}); built-in defaults are in force", problem);

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
        SafeUi.Services = app.Services;
        TranslateExtension.Localizer = app.Services.GetRequiredService<ILocalizer>();
        HookUnhandledExceptions();
        ApplySavedLanguage(app.Services);
        return app;
    }

    /// <summary>Why appsettings.json was not used (exception type name), when it wasn't. Warned once logging exists.</summary>
    private static string? _configProblem;

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
        // FIX(LOG-2, 2026-10-20, FIX-3): Flaw: the filter caught only FileNotFound/IOException, so a MALFORMED
        // appsettings.json threw FormatException out of the config provider — an uncaught hard startup crash
        // with nothing in the file log. Fix: FormatException joins the filter (scoped to this settings load),
        // the app falls back to the built-in defaults, and the reason is remembered so CreateMauiApp can log a
        // startup warning once the bootstrap logger exists. Why: a settings typo must degrade to defaults,
        // never brick the shop's tablet at opening time. Logged in docs/AGENT_FIX_LOG.md.
        catch (Exception ex) when (ex is FileNotFoundException or IOException or FormatException)
        {
            // Missing or malformed settings must never stop the app: the Options defaults match the SRS.
            _configProblem = ex.GetType().Name;
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
            var text = services.GetRequiredService<ILocalizer>();
            // A1-05: database work continues on the thread pool; blocking the UI thread on it directly could deadlock
            // (a continuation posted back to the blocked UI thread). Task.Run keeps the whole chain off it.
            var sender = services.GetRequiredService<ISender>();
            var profile = Task.Run(() => sender.Send(new GetShopProfileQuery())).GetAwaiter().GetResult();
            if (profile is not null) text.Language = profile.Language;
            else if (Enum.TryParse<Angadi.Domain.Shop.UiLanguage>(Preferences.Default.Get(AppShell.LanguageKey, ""), out var chosen))
                text.Language = chosen;   // first run: chosen before the shop was set up
            Theme.UseLatin = text.Language == Angadi.Domain.Shop.UiLanguage.English;
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
            e.Handled = true;   // keep the counter open; the shopkeeper gets a message, the log gets the details
            _ = SafeUi.ShowUnexpectedAsync();
        };
#endif
    }
}
