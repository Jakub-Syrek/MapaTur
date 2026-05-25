using MapaTur.App.Services;
using MapaTur.App.ViewModels;
using MapaTur.App.Views;
using MapaTur.Application.Maps;
using MapaTur.Application.Tracks;
using MapaTur.Application.Trails;
using MapaTur.Infrastructure.Maps.MBTiles;
using MapaTur.Infrastructure.Tracks;
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

    private static void ConfigureSerilog()
    {
        string logDirectory = Path.Combine(FileSystem.AppDataDirectory, "logs");
        Directory.CreateDirectory(logDirectory);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .WriteTo.File(
                path: Path.Combine(logDirectory, "mapatur-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7)
            .CreateLogger();
    }

    private static void RegisterServices(IServiceCollection services)
    {
        services.AddSingleton<IFilePickerService, MauiFilePickerService>();
        services.AddSingleton<IOfflineMapLoader, MBTilesMapLoader>();
        services.AddSingleton<ITileSourceFactory, MBTilesTileSourceFactory>();
        services.AddSingleton<ITrackLayerRenderer, MapsuiTrackLayerRenderer>();
        services.AddSingleton<ITrailLayerRenderer, MapsuiTrailLayerRenderer>();
        services.AddSingleton<ITcxParser, TcxParser>();
        services.AddTransient<ImportTcxFileUseCase>();

        services.AddHttpClient<IOverpassClient, OverpassHttpClient>();

        services.AddTransient<MapPageViewModel>();
        services.AddTransient<MapPage>();
    }
}
