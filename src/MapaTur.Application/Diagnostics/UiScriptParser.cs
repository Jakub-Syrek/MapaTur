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
}