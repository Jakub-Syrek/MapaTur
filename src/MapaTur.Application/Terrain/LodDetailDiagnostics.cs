using System.Globalization;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Formats a one-line, on-screen diagnostic of the LOD detail decision so the live 1 m-detail state can be read
/// straight off the device — logcat/Serilog has repeatedly come back empty on the phone, so the ground truth has
/// to be visible in the UI. Pure and culture-invariant: unit-pinnable, and it renders identically regardless of
/// the device locale (a Polish phone must still show <c>1.2 km</c>, not <c>1,2 km</c>).
///
/// The decisive signal is whether the chosen <c>detailZoom</c> fell at/below <c>baseDetailZoomFloor</c> — that is
/// the gate (MapPageViewModel.OnDetailFocusAsync) that nulls the 1 m patch and leaves the bare stride-decimated
/// base showing, i.e. the dominant cause of the mobile "plasticine" terrain.
/// </summary>
public static class LodDetailDiagnostics
{
    /// <summary>Builds the one-line LOD detail readout for <c>StatusMessage</c>.</summary>
    /// <param name="source">where the look-at focus came from (e.g. "look-at", "target").</param>
    /// <param name="distanceMeters">camera → look-at distance that drives the screen-space zoom pick.</param>
    /// <param name="viewportHeight">viewport height in DIPs — the multiplier that makes a small phone viewport pick a coarser zoom than the desktop at the same distance.</param>
    /// <param name="detailZoom">the zoom <see cref="ScreenSpaceLod"/> chose for this frame.</param>
    /// <param name="baseDetailZoomFloor">at/below this zoom the detail patch is dropped (bare base shows).</param>
    /// <param name="requestedTiles">planned z16 tiles, or null when the floor gate skipped the per-tile build.</param>
    /// <param name="cachedTiles">how many of those are present in the offline cache, or null when skipped.</param>
    /// <param name="avgStep">average per-tile subsample step of the built mesh (≈1 = true 1 m; ≥2 = budget-demoted/coarse), or null when the detail mesh did not render.</param>
    /// <param name="finestStep">finest per-tile subsample step in the built mesh, or null when the detail mesh did not render.</param>
    /// <param name="note">why the detail did/didn't render ("ok" | "no-raster" | "no-terrain"); shown as →BASE(note) when the mesh came back null so the bare base is on screen.</param>
    /// <param name="building">true while a detail rebuild is in flight — what is CURRENTLY drawn is still the
    /// previous detail / bare base, not the target zoom, so the badge shows "⌛bld" instead of a step that
    /// hasn't rendered yet (the achieved step replaces it once the build completes).</param>
    public static string Format(
        string source,
        double distanceMeters,
        double viewportHeight,
        int detailZoom,
        int baseDetailZoomFloor,
        int? requestedTiles,
        int? cachedTiles,
        double? avgStep = null,
        int? finestStep = null,
        string? note = null,
        bool building = false)
    {
        bool skipped = detailZoom <= baseDetailZoomFloor;
        string dist = FormatDistance(distanceMeters);
        int vh = (int)Math.Round(viewportHeight, MidpointRounding.AwayFromZero);

        // Front-load the three discriminators (zoom, ON/OFF, cache ratio) — the StatusMessage label tail-truncates
        // at ~300px, so what matters must come first; dist/vh/source trail and may truncate harmlessly.
        if (skipped)
        {
            return $"LOD z{detailZoom}≤fl{baseDetailZoomFloor} OFF · {dist} · vh{vh} · {source}";
        }

        string counts = requestedTiles is { } requested && cachedTiles is { } cached
            ? $" {cached}/{requested}"
            : string.Empty;

        // A rebuild is running: the target zoom is NOT on screen yet (base / previous detail is). Say so instead of
        // showing a step the user isn't actually looking at — directly answers "what resolution do I have NOW?".
        if (building)
        {
            return $"LOD →z{detailZoom}{counts} ⌛bld · {dist} · vh{vh} · {source}";
        }

        // Outcome of the per-tile build. avgStep≈1 = true 1 m, s≥2 = budget-demoted (blocky). When no step came
        // back the mesh did NOT render and the bare base is on screen — flag it explicitly with the reason note,
        // because "ON" alone (zoom selected, tiles cached) does not mean a 1 m surface actually drew.
        string outcome = avgStep is { } a && finestStep is { } f
            ? " s" + a.ToString("F1", CultureInfo.InvariantCulture) + "/" + f.ToString(CultureInfo.InvariantCulture)
            : string.IsNullOrEmpty(note) ? string.Empty : $" →BASE({note})";

        return $"LOD z{detailZoom} ON{counts}{outcome} · {dist} · vh{vh} · {source}";
    }

    private static string FormatDistance(double meters)
    {
        return meters >= 1000.0
            ? (meters / 1000.0).ToString("F1", CultureInfo.InvariantCulture) + " km"
            : meters.ToString("F0", CultureInfo.InvariantCulture) + " m";
    }
}