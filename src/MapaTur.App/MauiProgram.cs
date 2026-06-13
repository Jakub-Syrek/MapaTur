using MapaTur.App.Services;
using MapaTur.App.ViewModels;
using MapaTur.App.Views;
using MapaTur.Application.Climbing;
using MapaTur.Application.Location;
using MapaTur.Application.Maps;
using MapaTur.Application.Pois;
using MapaTur.Application.Roads;
using MapaTur.Application.Routing;
using MapaTur.Application.Terrain;
using MapaTur.Application.Tracks;
using MapaTur.Application.Trails;
using MapaTur.Infrastructure.Climbing;
using MapaTur.Infrastructure.Maps.MBTiles;
using MapaTur.Infrastructure.Pois;
using MapaTur.Infrastructure.Roads;
using MapaTur.Infrastructure.Routing;
using MapaTur.Infrastructure.Terrain;
using MapaTur.Infrastructure.Tracks;
using MapaTur.Infrastructure.Trails;
using MapaTur.Infrastructure.Trails.Overpass;

using Microsoft.Extensions.Logging;

using Serilog;
using Serilog.Events;

using SkiaSharp.Views.Maui.Controls.Hosting;

namespace MapaTur.App;

/// <summary>
/// MAUI application bootstrap. Configures hosting, fonts, logging and the
/// dependency injection container.
/// </summary>
public static class MauiProgram
{
    /// <summary>
    /// Creates the configured <see cref="MauiApp"/> for the platform host.
    /// </summary>
    /// <returns>Configured MAUI app.</returns>
    public static MauiApp CreateMauiApp()
    {
        ConfigureSerilog();
        InitializeNativeSqlite();
        InstallUnhandledExceptionLogging();

        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .UseSkiaSharp()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

        RegisterServices(builder.Services);

        builder.Logging.ClearProviders();
        builder.Logging.AddSerilog(dispose: true);

#if DEBUG
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }

    /// <summary>
    /// Runtime-adjustable Serilog level. Default Information; the Ustawienia "Szczegółowe logi" toggle
    /// flips it to Verbose for in-field diagnostics without a rebuild.
    /// </summary>
    public static readonly Serilog.Core.LoggingLevelSwitch LogLevelSwitch = new(LogEventLevel.Information);

    private static void ConfigureSerilog()
    {
        // Write logs next to the exe so they're trivial to locate and immune to
        // FileSystem.AppDataDirectory quirks on unpackaged MAUI Windows apps.
        string logDirectory = Path.Combine(AppContext.BaseDirectory, "logs");

        try
        {
            Directory.CreateDirectory(logDirectory);
        }
        catch (Exception)
        {
            logDirectory = Path.Combine(Path.GetTempPath(), "MapaTur", "logs");
            Directory.CreateDirectory(logDirectory);
        }

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.ControlledBy(LogLevelSwitch)
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .WriteTo.File(
                path: Path.Combine(logDirectory, "mapatur-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                flushToDiskInterval: TimeSpan.FromMilliseconds(250),
                shared: true)
            .CreateLogger();

        Log.Information("Logger initialised. Log directory: {LogDirectory}", logDirectory);
    }

    /// <summary>
    /// Forces the SQLite native provider to load eagerly. Microsoft.Data.Sqlite has its own
    /// auto-init, but BruTile.MbTiles uses sqlite-net-pcl which expects a Batteries call.
    /// Initialising once at startup keeps both stacks happy and surfaces native-load errors
    /// here rather than mid-feature.
    /// </summary>
    private static void InitializeNativeSqlite()
    {
        try
        {
            SQLitePCL.Batteries_V2.Init();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "SQLitePCL native initialisation failed");
        }
    }

    /// <summary>
    /// Routes unhandled exceptions from every dispatcher to Serilog so we can diagnose
    /// crashes that happen outside a try/catch block.
    /// </summary>
    private static void InstallUnhandledExceptionLogging()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Log.Fatal(args.ExceptionObject as Exception, "AppDomain unhandled exception (terminating: {Terminating})", args.IsTerminating);
            Log.CloseAndFlush();
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(args.Exception, "Unobserved task exception");
            args.SetObserved();
        };
    }

    private static void RegisterServices(IServiceCollection services)
    {
#if WINDOWS
        services.AddSingleton<IFilePickerService, Platforms.Windows.WindowsFilePickerService>();
#else
        services.AddSingleton<IFilePickerService, MauiFilePickerService>();
#endif
        services.AddSingleton<IFileSaverService, AppDataFileSaverService>();
        services.AddSingleton<IOfflineMapLoader, MBTilesMapLoader>();
        services.AddSingleton<IMapAutoLoader, FileSystemMapAutoLoader>();
        services.AddSingleton<I3DSettingsStore, MauiPreferences3DSettingsStore>();
        services.AddSingleton<ITileSourceFactory, MBTilesTileSourceFactory>();
        services.AddSingleton<IRasterTileDecoder, SkiaRasterTileDecoder>();
        services.AddSingleton<MBTilesOrthoCompositor>();
        services.AddSingleton<IUserLocationService, MauiUserLocationService>();
        services.AddSingleton<IUserLocationLayerRenderer, MapsuiUserLocationLayerRenderer>();
        services.AddSingleton<ITrackLayerRenderer, MapsuiTrackLayerRenderer>();
        services.AddSingleton<ITrailLayerRenderer, MapsuiTrailLayerRenderer>();
        services.AddSingleton<IRouteLayerRenderer, MapsuiRouteLayerRenderer>();
        services.AddSingleton<IClimbingLayerRenderer, MapsuiClimbingLayerRenderer>();
        services.AddSingleton<IPoiLayerRenderer, MapsuiPoiLayerRenderer>();
        services.AddSingleton<IRoadLayerRenderer, MapsuiRoadLayerRenderer>();
        services.AddSingleton<ITcxParser, TcxParser>();
        services.AddTransient<ImportTcxFileUseCase>();

        services.AddSingleton<ITrailRepository>(_ =>
            new SqliteTrailRepository(
                Path.Combine(FileSystem.AppDataDirectory, "mapatur-trails.db"),
                simplificationEpsilonMeters: 10.0));
        services.AddSingleton<IRoutePlanner, TrailRoutePlanner>();
        services.AddSingleton<IGpxWriter, GpxWriter>();
        services.AddTransient<PlanRouteUseCase>();
        services.AddTransient<MultiStopRoutePlanner>();
        services.AddTransient<ExportRouteToGpxUseCase>();

        services.AddHttpClient<IOverpassClient, OverpassHttpClient>(client =>
        {
            // Overpass main endpoint is rate-limited; 90s covers most regional queries.
            client.Timeout = TimeSpan.FromSeconds(90);
        });

        services.AddSingleton<IClimbingRepository>(_ =>
            new SqliteClimbingRepository(Path.Combine(FileSystem.AppDataDirectory, "mapatur-climbing.db")));
        services.AddSingleton<IPoiRepository>(_ =>
            new SqlitePoiRepository(Path.Combine(FileSystem.AppDataDirectory, "mapatur-pois.db")));
        services.AddHttpClient<IClimbingOverpassClient, OverpassClimbingHttpClient>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(90);
        });

        services.AddHttpClient<IPoiOverpassClient, OverpassPoiHttpClient>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(90);
        });

        services.AddHttpClient<IRoadOverpassClient, OverpassRoadHttpClient>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(90);
        });

        // Online elevation: GUGiK NMT 1 m (Poland LiDAR) in front of the global Terrarium fallback,
        // composed behind one IDemTileSource. GUGiK short-circuits outside Poland so the composite falls
        // through to Terrarium worldwide. OnlineRegionDemLoader stitches a region's tiles into one mesh.
        services.AddHttpClient("dem-gugik", c => c.Timeout = TimeSpan.FromSeconds(60));
        services.AddHttpClient("dem-terrarium", c => c.Timeout = TimeSpan.FromSeconds(30));

        // GUGiK source registered on its own so the offline downloader can fetch + probe its cache
        // directly (bypassing the Terrarium fallback — an offline pull wants 1 m or nothing, not
        // 30 m filler tiles in the cache).
        services.AddSingleton<GugikNmtDemTileSource>(sp =>
        {
            var httpFactory = sp.GetRequiredService<System.Net.Http.IHttpClientFactory>();
            string cacheRoot = Path.Combine(FileSystem.AppDataDirectory, "dem-cache");
            // The cache can be SEEDED out-of-band (phone ↔ desktop tile copies), so log where this
            // process actually resolves it — a path mismatch here silently disables all 1 m detail.
            Log.Information("GUGiK DEM cache root: {Root}", Path.Combine(cacheRoot, "gugik"));
            return new GugikNmtDemTileSource(
                httpFactory.CreateClient("dem-gugik"),
                Path.Combine(cacheRoot, "gugik"),
                tileSize: 256);
        });
        services.AddSingleton<IDemTileSource>(sp =>
        {
            var httpFactory = sp.GetRequiredService<System.Net.Http.IHttpClientFactory>();
            var decoder = sp.GetRequiredService<IRasterTileDecoder>();
            string cacheRoot = Path.Combine(FileSystem.AppDataDirectory, "dem-cache");
            var terrarium = new OnlineDemTileSource(
                httpFactory.CreateClient("dem-terrarium"),
                decoder,
                Path.Combine(cacheRoot, "terrarium"));
            return new CompositeDemTileSource(sp.GetRequiredService<GugikNmtDemTileSource>(), terrarium);
        });
        services.AddSingleton<OnlineRegionDemLoader>(sp =>
            new OnlineRegionDemLoader(sp.GetRequiredService<IDemTileSource>()));
        services.AddSingleton<OfflineRegionDownloader>(sp =>
        {
            var gugik = sp.GetRequiredService<GugikNmtDemTileSource>();
            return new OfflineRegionDownloader(gugik, gugik.IsCached);
        });

        services.AddTransient<MapPageViewModel>();
        services.AddTransient<MapPage>();
    }
}