using System.Numerics;

namespace MapaTur.Application.Terrain;

/// <summary>Stan zaćmienia Słońca dla jednej chwili: kierunki świata (X wschód, Y północ, Z góra),
/// promienie kątowe tarcz (radiany) i ułamek zakrytej POWIERZCHNI tarczy słonecznej [0,1].
/// Konsumenci: pass nieba (dysk z „wgryzem") i Atmosphere (przyciemnienie światła).</summary>
public readonly record struct SolarEclipseState(
    Vector3 SunDirection,
    Vector3 MoonDirection,
    float SunAngularRadiusRadians,
    float MoonAngularRadiusRadians,
    float Obscuration);

/// <summary>
/// Task #9 (2026-08-09): geometria zaćmienia Słońca. Ułamek powierzchni tarczy słonecznej zakryty
/// przez tarczę Księżyca — soczewka dwóch kół w płaskiej aproksymacji (kąty ~0,5°, błąd pomijalny).
/// Pozycję Księżyca dostarcza <see cref="LunarPosition.EquatorialTopocentric"/> (paralaksa!), spięcie
/// w chwilę lokalnego zegara robi <see cref="NightSky.EclipseForLocalDate"/>.
/// </summary>
public static class SolarEclipse
{
    /// <summary>Kątowy promień tarczy słonecznej (radiany) — stała 0,2666° (zmienność roczna ±1,7%
    /// bez znaczenia wizualnego).</summary>
    public const double SunAngularRadiusRadians = 0.26667 * Math.PI / 180.0;

    /// <summary>Promień Księżyca w promieniach Ziemi — do promienia kątowego z odległości topocentrycznej.</summary>
    public const double MoonRadiusEarthRadii = 0.27240;

    /// <summary>
    /// Ułamek POWIERZCHNI tarczy słonecznej (promień <paramref name="sunRadius"/>) zakryty przez tarczę
    /// Księżyca (promień <paramref name="moonRadius"/>) przy separacji środków <paramref name="separation"/>.
    /// Jednostki dowolne, byle spójne (u nas radiany). Płaska geometria dwóch kół.
    /// </summary>
    public static double ObscuredFraction(double separation, double sunRadius, double moonRadius)
    {
        if (separation >= sunRadius + moonRadius)
        {
            return 0.0; // tarcze rozłączne
        }

        if (separation + sunRadius <= moonRadius)
        {
            return 1.0; // tarcza słońca w całości pod księżycem (faza całkowita)
        }

        if (separation + moonRadius <= sunRadius)
        {
            // Księżyc w całości NA tarczy słońca (przypadek obrączkowy): zakrywa stosunek pól.
            return (moonRadius * moonRadius) / (sunRadius * sunRadius);
        }

        // Soczewka częściowego przekrycia (standardowa forma zamknięta).
        double d = separation;
        double lens = (sunRadius * sunRadius * Math.Acos(((d * d) + (sunRadius * sunRadius) - (moonRadius * moonRadius)) / (2.0 * d * sunRadius)))
            + (moonRadius * moonRadius * Math.Acos(((d * d) + (moonRadius * moonRadius) - (sunRadius * sunRadius)) / (2.0 * d * moonRadius)))
            - (0.5 * Math.Sqrt(Math.Max(0.0,
                (-d + sunRadius + moonRadius) * (d + sunRadius - moonRadius)
                * (d - sunRadius + moonRadius) * (d + sunRadius + moonRadius))));
        return lens / (Math.PI * sunRadius * sunRadius);
    }
}