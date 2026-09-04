using System.Diagnostics;

using MapaTur.Domain.Regions;

namespace MapaTur.App.Views;

/// <summary>
/// P-A3 (2026-09-04, na prośbę usera): panel wyboru regionu pokazywany NA DESKTOPIE przed mapą.
/// Wybór = RESTART procesu z <c>MAPATUR_REGION=&lt;id&gt;</c> (+ <c>MAPATUR_REGION_CHOSEN=1</c>, żeby
/// nowy proces nie pokazał panelu ponownie). Dlaczego restart, a nie przełączenie w locie:
/// <see cref="MountainRegions.Default"/> i jego konsumenci (<c>OrthoDetailGrid</c>, statyki
/// <c>MapPageViewModel</c>) inicjalizują się przy ładowaniu typów — przełączanie po starcie oznaczałoby
/// przepisanie tej architektury i ryzyko regresji Tatr (wpis #1 pinowany bit w bit). Restart kosztuje
/// ~1 s i używa dokładnie tej samej ścieżki co <c>run-tatry.cmd</c>/<c>run-zermatt.cmd</c>.
/// Na mobile panel się nie pojawia (zawsze Tatry) — bramka w <c>App.CreateWindow</c>.
/// </summary>
public partial class RegionChooserPage : ContentPage
{
    /// <summary>Env marker set by the launchers and by the relaunch: the region is already chosen.</summary>
    public const string ChosenEnvVar = "MAPATUR_REGION_CHOSEN";

    private const string RegionEnvVar = "MAPATUR_REGION";
    private const string LastRegionKey = "App.LastRegion";

    public RegionChooserPage()
    {
        InitializeComponent();

        string? last = Preferences.Default.Get<string?>(LastRegionKey, null);
        foreach (MountainRegion region in MountainRegions.All)
        {
            bool isLast = string.Equals(region.Id, last, StringComparison.OrdinalIgnoreCase);
            var button = new Button
            {
                Text = region.DisplayName,
                FontSize = 20,
                HeightRequest = 64,
                CornerRadius = 10,
                BackgroundColor = isLast ? Color.FromArgb("#2F6FEB") : Color.FromArgb("#1E2630"),
                TextColor = Colors.White,
            };
            string id = region.Id;
            button.Clicked += (_, _) => Launch(id);
            RegionButtons.Children.Add(button);
        }

        LastHint.Text = last is null
            ? string.Empty
            : $"Ostatnio: {MountainRegions.ById(last)?.DisplayName ?? last}";
        Serilog.Log.Information("[Region] panel wyboru regionu: {Count} wpisów, ostatnio '{Last}'",
            MountainRegions.All.Count, last ?? "—");

        // Harness (protokół testów bez myszki): MAPATUR_REGION_AUTOPICK=<id> wybiera wpis tak, jakby
        // kliknięto przycisk — testuje pełną ścieżkę restartu (Process.Start + Quit) bez kradzieży okna.
        string? autopick = Environment.GetEnvironmentVariable("MAPATUR_REGION_AUTOPICK");
        if (!string.IsNullOrWhiteSpace(autopick) && MountainRegions.ById(autopick.Trim().ToLowerInvariant()) is { } picked)
        {
            Serilog.Log.Information("[Region] AUTOPICK '{Id}' (harness)", picked.Id);
            Dispatcher.Dispatch(() => Launch(picked.Id));
        }
    }

    private static void Launch(string regionId)
    {
        Preferences.Default.Set(LastRegionKey, regionId);

        string? exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            Serilog.Log.Error("[Region] brak Environment.ProcessPath — nie mogę zrestartować w regionie {Id}", regionId);
            return;
        }

        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        // Tatry = wpis #1: brak env => domyślny region, dokładnie jak run-tatry.cmd (nic nie dziedziczymy).
        psi.Environment.Remove(RegionEnvVar);
        if (!string.Equals(regionId, MountainRegions.Tatry.Id, StringComparison.OrdinalIgnoreCase))
        {
            psi.Environment[RegionEnvVar] = regionId;
        }
        psi.Environment[ChosenEnvVar] = "1";
        psi.Environment.Remove("MAPATUR_REGION_AUTOPICK"); // harness: dziecko nie ma powtarzać wyboru

        Serilog.Log.Information("[Region] wybór '{Id}' — restart procesu w tym regionie", regionId);
        Process.Start(psi);
        Microsoft.Maui.Controls.Application.Current?.Quit();
    }
}
