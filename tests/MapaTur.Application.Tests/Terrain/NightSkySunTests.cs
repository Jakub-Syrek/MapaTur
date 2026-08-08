using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Task #3 (2026-08-08): realna efemeryda słońca dla dziennego toru <see cref="Atmosphere"/> — ten sam
/// rurociąg Meeusa, który od golden-hour epiku napędza gwiazdy i księżyc (SolarPosition +
/// AstronomicalTime + CelestialCoordinates), spięty w <c>NightSky.SunForLocalDate</c>. Wartości
/// odniesienia z geometrii sferycznej dla Tatr (49,23°N 20,0°E, czas lokalny CET/CEST):
/// kulminacja = 90° − φ ± δ (lato 64,2°, zima 17,4°, równonoc 40,8°), południe słoneczne na 20°E
/// w CEST ≈ 12:40, wschód letni na NE (azymut ~51°).
/// </summary>
public sealed class NightSkySunTests
{
    private const double Lat = 49.23;
    private const double Lon = 20.0;

    private static (double MaxElevDeg, double MaxHour) ScanMaxElevation(int year, int month, int day)
    {
        double best = -90.0;
        double bestHour = 0.0;
        for (double h = 0.0; h < 24.0; h += 0.05)
        {
            Vector3 dir = NightSky.SunForLocalDate(year, month, day, h, Lat, Lon);
            double elev = Math.Asin(Math.Clamp(dir.Z, -1f, 1f)) * 180.0 / Math.PI;
            if (elev > best)
            {
                best = elev;
                bestHour = h;
            }
        }

        return (best, bestHour);
    }

    [Fact]
    public void summer_solstice_culmination_matches_spherical_geometry()
    {
        (double maxElev, double maxHour) = ScanMaxElevation(2026, 6, 21);

        maxElev.Should().BeApproximately(64.2, 1.0, "kulminacja letnia = 90 − 49,23 + 23,44");
        maxHour.Should().BeInRange(12.2, 13.2, "południe słoneczne na 20°E w CEST wypada ~12:40, nie 12:00");
    }

    [Fact]
    public void winter_solstice_culmination_is_low()
    {
        (double maxElev, _) = ScanMaxElevation(2026, 12, 21);

        maxElev.Should().BeApproximately(17.4, 1.0, "kulminacja zimowa = 90 − 49,23 − 23,44");
    }

    [Fact]
    public void equinox_culmination_is_colatitude()
    {
        (double maxElev, _) = ScanMaxElevation(2026, 3, 20);

        maxElev.Should().BeApproximately(40.8, 1.2, "w równonoc kulminacja = 90 − szerokość");
    }

    [Fact]
    public void summer_sunrise_is_in_the_north_east()
    {
        // Znajdź przejście Z przez zero (wschód) i sprawdź azymut — latem słońce wstaje na NE, nie na E.
        double riseHour = -1.0;
        Vector3 riseDir = default;
        Vector3 prev = NightSky.SunForLocalDate(2026, 6, 21, 2.0, Lat, Lon);
        for (double h = 2.05; h < 12.0; h += 0.05)
        {
            Vector3 dir = NightSky.SunForLocalDate(2026, 6, 21, h, Lat, Lon);
            if (prev.Z <= 0f && dir.Z > 0f)
            {
                riseHour = h;
                riseDir = dir;
                break;
            }

            prev = dir;
        }

        riseHour.Should().BeInRange(4.2, 5.0, "letni wschód w Tatrach to ~4:34 CEST");
        double azimuthDeg = Math.Atan2(riseDir.X, riseDir.Y) * 180.0 / Math.PI; // 0=N, 90=E
        azimuthDeg.Should().BeInRange(40.0, 75.0, "latem słońce wstaje na północnym wschodzie");
    }

    [Fact]
    public void winter_midday_sun_is_up_and_midnight_down()
    {
        NightSky.SunForLocalDate(2026, 12, 21, 12.0, Lat, Lon).Z.Should().BeGreaterThan(0f);
        NightSky.SunForLocalDate(2026, 12, 21, 0.0, Lat, Lon).Z.Should().BeLessThan(0f);
    }

    [Fact]
    public void agrees_with_the_moon_pipeline_sun_direction()
    {
        // Księżyc już liczy kierunek słońca (orientacja terminatora) — obie ścieżki muszą być JEDNĄ ścieżką.
        Vector3 sun = NightSky.SunForLocalDate(2026, 8, 8, 15.0, Lat, Lon);
        MoonSky moon = NightSky.MoonForLocalDate(2026, 8, 8, 15.0, Lat, Lon);

        Vector3.Dot(sun, moon.SunDirection).Should().BeGreaterThan(0.9999f);
    }
}