using System.Globalization;

namespace MapaTur.Application.Diagnostics;

/// <summary>
/// Task #7 (2026-08-08): tryb nagrywania — planuje rozmiar KLIENTA okna pod kadr publikacji.
/// Spec: proporcja "a:b" (np. "4:5", "9:16") → największy prostokąt o tej proporcji mieszczący się
/// w obszarze roboczym monitora, albo jawne "WxH" (np. "1080x1350") → dosłownie, skalowane w dół
/// z zachowaniem proporcji gdy nie mieści się na monitorze. Wymiary zawsze PARZYSTE (H.264).
/// Konsumenci: MauiProgram (env MAPATUR_RECORD_FRAME przy starcie) i F11 w Terrain3DView (cykl).
/// </summary>
public static class RecordFramePlanner
{
    public static (int Width, int Height)? Plan(string? spec, int workW, int workH)
    {
        if (string.IsNullOrWhiteSpace(spec) || workW <= 0 || workH <= 0)
        {
            return null;
        }

        string s = spec.Trim().ToLowerInvariant();
        double w, h;
        int sep;
        if ((sep = s.IndexOf(':')) > 0)
        {
            if (!TryPositive(s[..sep], out int aspW) || !TryPositive(s[(sep + 1)..], out int aspH))
            {
                return null;
            }

            double scale = Math.Min((double)workW / aspW, (double)workH / aspH);
            w = aspW * scale;
            h = aspH * scale;
        }
        else if ((sep = s.IndexOf('x')) > 0)
        {
            if (!TryPositive(s[..sep], out int exactW) || !TryPositive(s[(sep + 1)..], out int exactH))
            {
                return null;
            }

            double scale = Math.Min(1.0, Math.Min((double)workW / exactW, (double)workH / exactH));
            w = exactW * scale;
            h = exactH * scale;
        }
        else
        {
            return null;
        }

        int wi = (int)w & ~1;
        int hi = (int)h & ~1;
        return wi >= 2 && hi >= 2 ? (wi, hi) : null;
    }

    private static bool TryPositive(string token, out int value)
        => int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value > 0;
}