using System.Globalization;
using System.Linq;

using MapaTur.App.Services;
using MapaTur.Application.Localization;

namespace MapaTur.App;

/// <summary>
/// Root MAUI application object.
/// </summary>
public partial class App : Microsoft.Maui.Controls.Application
{
    /// <summary>Initializes the application and loads its resource dictionary.</summary>
    public App()
    {
        // MapaTur defaults to Polish (a Polish Tatra app) but the user can switch to English in
        // Ustawienia. The choice is persisted in Preferences; read it here, before any page is built,
        // so every {x:Static loc:AppStrings.X} resolves under the chosen UI culture. Unknown / missing
        // falls back to Polish. Only the UI (resource) culture is pinned; number/date formatting is left
        // to the OS, and coordinates already use InvariantCulture.
        ApplyCulture(new MauiPreferences3DSettingsStore().Language);

        InitializeComponent();
    }

    /// <inheritdoc />
    protected override Window CreateWindow(IActivationState? activationState)
    {
        return new Window(new AppShell());
    }

    /// <summary>
    /// Switches the live UI language: persists nothing (the caller owns the store) but applies the new
    /// culture and rebuilds the root page so every compile-time-resolved <c>{x:Static}</c> string
    /// re-evaluates under it. A soft restart — no process kill, camera/route state survive via Preferences.
    /// </summary>
    public static void SwitchLanguage(string? languageCode)
    {
        ApplyCulture(languageCode);
        (Current as App)?.RecreateRoot();
    }

    private static void ApplyCulture(string? languageCode)
    {
        var culture = new CultureInfo(AppLanguage.ToCultureName(languageCode));
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
    }

    private void RecreateRoot()
    {
        Window? window = Windows.FirstOrDefault();
        if (window is not null)
        {
            window.Page = new AppShell();
        }
    }
}