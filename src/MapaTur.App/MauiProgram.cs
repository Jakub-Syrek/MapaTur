using MapaTur.App.Services;
using MapaTur.App.ViewModels;
using MapaTur.App.Views;
using MapaTur.Application.Climbing;
using MapaTur.Application.Location;
using MapaTur.Application.Maps;
using MapaTur.Application.Packaging;
using MapaTur.Application.Pois;
using MapaTur.Application.Roads;
using MapaTur.Application.Routing;
using MapaTur.Application.Terrain;
using MapaTur.Application.Tracks;
using MapaTur.Application.Trails;
using MapaTur.Infrastructure.Climbing;
using MapaTur.Infrastructure.Maps.MBTiles;
using MapaTur.Infrastructure.Packaging;
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
        services.AddSingleton<IGpxParser, GpxParser>();
        services.AddTransient<ImportTcxFileUseCase>();
        services.AddTransient<ImportTrackFileUseCase>();

        // Store trails at FULL geometry (no store-time simplification). Simplifying on store silently dropped
        // junction vertices that two trails SHARE in OSM (e.g. where the Żleb Kulczyńskiego meets the green
        // valley trail — a 0 m shared node became a 35 m gap), which disconnected the routing graph and forced
        // absurd detours. Render consumers simplify on READ instead: the 2D layer via the (bounds, epsilon)
        // overload, the 3D overlay via SimplifyForOverlay3D — so routing keeps the connectivity it needs.
        services.AddSingleton<ITrailRepository>(_ =>
            new SqliteTrailRepository(
                Path.Combine(FileSystem.AppDataDirectory, "mapatur-trails.db"),
                simplificationEpsilonMeters: 0.0));
        // Elevation source for the router: lifts trail geometry onto the base DEM so "fastest time" sees real
        // slopes (in the mountains shortest != fastest). One instance, shared: the view-model sets its raster
        // once the base DEM loads; the planner reads it.
        services.AddSingleton<DemElevationSource>();
        services.AddSingleton<IElevationSource>(sp => sp.GetRequiredService<DemElevationSource>());
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
        // User-imported off-trail ("pozaszlaki") GPX/TCX tracks, persisted separately so they survive restarts.
        services.AddSingleton<ITrackRepository>(_ =>
            new SqliteTrackRepository(Path.Combine(FileSystem.AppDataDirectory, "mapatur-tracks.db")));
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

        services.AddHttpClient<MapaTur.Application.Waterways.IWaterwayOverpassClient,
            MapaTur.Infrastructure.Waterways.OverpassWaterwayHttpClient>(client =>
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
        // cacheDecodedTiles: the 1 m detail re-center re-loads the SAME z16 window on every camera move, so the
        // per-tile fetch+decode dominated each re-center. The decoded-tile cache makes a sliding window decode only
        // the newly-entered tiles and reuse the overlap (zoom-scoped eviction keeps the rare z13 base load separate).
        services.AddSingleton<OnlineRegionDemLoader>(sp =>
            new OnlineRegionDemLoader(sp.GetRequiredService<IDemTileSource>(), cacheDecodedTiles: true));
        services.AddSingleton<OfflineRegionDownloader>(sp =>
        {
            var gugik = sp.GetRequiredService<GugikNmtDemTileSource>();
            return new OfflineRegionDownloader(gugik, gugik.IsCached);
        });

        // Stage 2c: scan the pre-baked DEM pyramid (dem-cache/baked/{z}/{x}/{y}.bdt) ONCE at startup into an
        // availability index. It backs the baked-tile streaming path (MapPageViewModel.UseBakedTileStreaming):
        // its roots/IsBaked drive the quadtree selector and its Load reads tiles off-thread. An absent/empty
        // baked root yields an empty index, so the VM cleanly falls back to the runtime-build detail path.
        services.AddSingleton<MapaTur.Application.Terrain.BakedTileAvailabilityIndex>(_ =>
        {
            string bakedRoot = Path.Combine(FileSystem.AppDataDirectory, "dem-cache", "baked");
            var index = MapaTur.Application.Terrain.BakedTileAvailabilityIndex.Scan(bakedRoot);
            Log.Information(
                "Baked DEM pyramid: {Count} tiles under {Root} (roots={Roots} z{MinZoom})",
                index.Count, bakedRoot, index.Roots.Count, index.Count == 0 ? 0 : index.MinZoom);
            return index;
        });

        // Region-package download: pull pre-baked DEM/ortho packages from our server and unpack them into the
        // exact dirs the renderer already reads (DEM cache + maps), so a fresh user gets full offline data
        // without side-loading anything. The manifest URL points at the package server (Railway); the
        // per-package download URLs inside the manifest may point at a CDN without any code change.
        // Railway package server (override per-build via the MAPATUR_PACKAGES_BASEURL env var).
        const string defaultPackagesBaseUrl = "https://mapatur-production.up.railway.app";
        string packagesBaseUrl =
            (Environment.GetEnvironmentVariable("MAPATUR_PACKAGES_BASEURL") ?? defaultPackagesBaseUrl).TrimEnd('/');
        services.AddHttpClient("packages", c => c.Timeout = TimeSpan.FromMinutes(10));
        services.AddSingleton<IInstalledPackageStore>(_ =>
            new FileInstalledPackageStore(Path.Combine(FileSystem.AppDataDirectory, "packages", "installed")));
        services.AddSingleton<IPackageContentExtractor>(_ =>
            new PackageContentExtractor(
                Path.Combine(FileSystem.AppDataDirectory, "dem-cache", "gugik"),
                Path.Combine(FileSystem.AppDataDirectory, "maps"),
                Path.Combine(FileSystem.AppDataDirectory, "dem")));
        services.AddSingleton<IPackageCatalogSource>(sp =>
        {
            var httpFactory = sp.GetRequiredService<System.Net.Http.IHttpClientFactory>();
            return new HttpPackageCatalogSource(
                httpFactory.CreateClient("packages"), $"{packagesBaseUrl}/manifest.json");
        });
        services.AddSingleton<IPackageInstaller>(sp =>
        {
            var httpFactory = sp.GetRequiredService<System.Net.Http.IHttpClientFactory>();
            return new PackageInstaller(
                new HttpPackageFileFetcher(httpFactory.CreateClient("packages")),
                sp.GetRequiredService<IPackageContentExtractor>(),
                sp.GetRequiredService<IInstalledPackageStore>(),
                Path.Combine(FileSystem.AppDataDirectory, "packages", "work"));
        });
        services.AddSingleton<OfflinePackageService>(sp => new OfflinePackageService(
            sp.GetRequiredService<IPackageCatalogSource>(),
            sp.GetRequiredService<IInstalledPackageStore>(),
            sp.GetRequiredService<IPackageInstaller>()));

        services.AddTransient<MapPageViewModel>();
        services.AddTransient<MapPage>();
    }
}