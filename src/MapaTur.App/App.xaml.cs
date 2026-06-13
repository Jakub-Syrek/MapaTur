namespace MapaTur.App;

/// <summary>
/// Root MAUI application object.
/// </summary>
public partial class App : Microsoft.Maui.Controls.Application
{
    /// <summary>Initializes the application and loads its resource dictionary.</summary>
    public App()
    {
        // MapaTur is a Polish Tatra app: every hand-written string (panel headers, chips, status) is Polish
        // and the localized resources carry a full Polish set. Pin the UI culture to Polish so the app reads
        // consistently on ANY machine — without this an English-locale desktop showed the four download
        // buttons in English while everything around them stayed Polish. Only the UI (resource) culture is
        // pinned; number/date formatting is left to the OS, and coordinates already use InvariantCulture.
        var polish = new System.Globalization.CultureInfo("pl-PL");
        System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = polish;
        System.Threading.Thread.CurrentThread.CurrentUICulture = polish;

        InitializeComponent();
    }

    /// <inheritdoc />
    protected override Window CreateWindow(IActivationState? activationState)
    {
        return new Window(new AppShell());
    }
}