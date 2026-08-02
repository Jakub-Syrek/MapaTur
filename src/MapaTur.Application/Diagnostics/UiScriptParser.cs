using System.Globalization;

namespace MapaTur.Application.Diagnostics;

/// <summary>One scripted UI action: open (or close) a premium-menu section at a given time from startup.</summary>
/// <param name="AtSeconds">Seconds from app start at which to trigger the action.</param>
/// <param name="Section">Target section index (1-6); 0 closes any open section.</param>
public readonly record struct UiScriptStep(double AtSeconds, int Section);

/// <summary>
/// Parser for the <c>MAPATUR_UI_SCRIPT</c> test-harness variable: <c>"20:6,30:0"</c> = open section 6
/// twenty seconds after startup, close it at thirty. Drives the SAME command path as the top-bar chips,
/// so UI responsiveness ("menu 1 FPS") can be measured without a mouse. Invalid entries are skipped —
/// a diagnostics hook must never take the app down.
/// </summary>
public static class UiScriptParser
{
    private const int MaxSection = 6;

    /// <summary>Parses the script; returns steps sorted by time. Null/blank/garbage yields an empty list.</summary>
    public static IReadOnlyList<UiScriptStep> Parse(string? script)
    {
        if (string.IsNullOrWhiteSpace(script))
        {
            return [];
        }

        var steps = new List<UiScriptStep>();
        foreach (string entry in script.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] parts = entry.Split(':');
            if (parts.Length != 2
                || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double atSeconds)
                || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int section)
                || atSeconds < 0 || section is < 0 or > MaxSection)
            {
                continue;
            }

            steps.Add(new UiScriptStep(atSeconds, section));
        }

        steps.Sort((a, b) => a.AtSeconds.CompareTo(b.AtSeconds));
        return steps;
    }

    /// <summary>
    /// Expands <c>MAPATUR_UI_STRESS="startSec:count:intervalMs"</c> into a rapid open/close burst
    /// (1,0,2,0,3,0,…). The user's report is "po kilku kliknięciach przestaje odpowiadać" — a slow,
    /// well-spaced script never reproduced it, so the harness has to click FAST and many times.
    /// </summary>
    public static IReadOnlyList<UiScriptStep> ParseStress(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec))
        {
            return [];
        }

        string[] parts = spec.Split(':');
        var ci = CultureInfo.InvariantCulture;
        if (parts.Length != 3
            || !double.TryParse(parts[0], NumberStyles.Float, ci, out double startSec)
            || !int.TryParse(parts[1], NumberStyles.Integer, ci, out int count)
            || !int.TryParse(parts[2], NumberStyles.Integer, ci, out int intervalMs)
            || startSec < 0 || count <= 0 || intervalMs <= 0)
        {
            return [];
        }

        var steps = new List<UiScriptStep>(count);
        int nextSection = 1;
        for (int i = 0; i < count; i++)
        {
            int section = i % 2 == 0 ? nextSection : 0;
            if (i % 2 == 1)
            {
                nextSection = nextSection % MaxSection + 1;
            }

            steps.Add(new UiScriptStep(startSec + (i * intervalMs / 1000.0), section));
        }

        return steps;
    }
}