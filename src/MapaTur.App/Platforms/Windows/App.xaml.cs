using Microsoft.UI.Xaml;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace MapaTur.App.WinUI;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : MauiWinUIApplication
{
    /// <summary>
    /// Initializes the singleton application object.  This is the first line of authored code
    /// executed, and as such is the logical equivalent of main() or WinMain().
    /// </summary>
    public App()
    {
        this.InitializeComponent();

        // WinUI dispatcher exceptions (async-void UI handlers, XAML callbacks) do NOT reach the
        // AppDomain/TaskScheduler hooks in MauiProgram — they die as a stowed exception 0xc000027b in
        // Microsoft.UI.Xaml.dll with NOTHING in the log (measured: two APPCRASHes on 2026-07-03 while the
        // user planned a multi-stop route; Serilog was silent both times). This hook is the only place
        // that sees them with a stack trace. Handled=false: after logging we still let the process die —
        // the app state after a swallowed dispatcher exception is not trustworthy.
        this.UnhandledException += (_, e) =>
        {
            Serilog.Log.Fatal(e.Exception, "WinUI unhandled exception: {Message}", e.Message);
            Serilog.Log.CloseAndFlush();
        };
    }

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}