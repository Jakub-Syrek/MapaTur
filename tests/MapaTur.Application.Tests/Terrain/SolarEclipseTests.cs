using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Task #9 (2026-08-09): zaćmienie Słońca. Geometria: ułamek POWIERZCHNI tarczy słonecznej zakryty
/// przez tarczę Księżyca (soczewka dwóch kół, płaska aproksymacja — kąty ~0,5°), pozycja Księżyca
/// TOPOCENTRYCZNA (paralaksa do ~0,95° przy horyzoncie — geocentryczna „gubi" zaćmienie), realny
/// przypadek: częściowe zaćmienie 2026-08-12 nad Tatrami przy zachodzie (referencja: zdjęcie usera
/// z Kasprowego). Wartości pinowane z tej implementacji (Schlyter ~1-2′ + topo wektorowo).
/// </summary>
public sealed class SolarEclipseTests
{
    private const double KasprowyLat = 49.232;
    private const double KasprowyLon = 19.982;

    // ── czysta geometria przekrycia ──────────────────────────────────────────────────────────────

    [Fact]
    public void no_obscuration_when_discs_do_not_touch()
    {
        SolarEclipse.ObscuredFraction(separation: 0.010, sunRadius: 0.0045, moonRadius: 0.0050)
            .Should().Be(0.0);
    }

    [Fact]
    public void full_obscuration_when_moon_disc_contains_sun_disc()
    {
        SolarEclipse.ObscuredFraction(separation: 0.0004, sunRadius: 0.0045, moonRadius: 0.0050)
            .Should().Be(1.0);
    }

    [Fact]
    public void annular_case_caps_at_area_ratio()
    {
        // Księżyc MNIEJSZY od słońca, centrycznie: zakrywa (r/R)² powierzchni — obrączka zostaje.
        double f = SolarEclipse.ObscuredFraction(separation: 0.0, sunRadius: 0.0050, moonRadius: 0.0045);
        f.Should().BeApproximately(Math.Pow(0.0045 / 0.0050, 2.0), 1e-9);
    }

    [Fact]
    public void obscuration_is_monotonic_in_separation()
    {
        double prev = 2.0;
        for (double sep = 0.0; sep <= 0.011; sep += 0.0005)
        {
            double f = SolarEclipse.ObscuredFraction(sep, 0.0045, 0.0050);
            f.Should().BeLessThanOrEqualTo(prev + 1e-12);
            prev = f;
        }
    }

    [Fact]
    public void obscuration_matches_numeric_integration()
    {
        // Suma po siatce 2000² punktów tarczy słońca vs forma zamknięta soczewki, kilka konfiguracji.
        foreach ((double sep, double rS, double rM) in new[]
                 { (0.003, 0.0045, 0.0050), (0.0058, 0.0045, 0.0050), (0.002, 0.0050, 0.0040) })
        {
            int n = 2000, inside = 0, total = 0;
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < n; j++)
                {
                    double x = ((i + 0.5) / n * 2.0 - 1.0) * rS;
                    double y = ((j + 0.5) / n * 2.0 - 1.0) * rS;
                    if ((x * x) + (y * y) > rS * rS)
                    {
                        continue;
                    }

                    total++;
                    double dx = x - sep;
                    if ((dx * dx) + (y * y) <= rM * rM)
                    {
                        inside++;
                    }
                }
            }

            double closed = SolarEclipse.ObscuredFraction(sep, rS, rM);
            closed.Should().BeApproximately((double)inside / total, 2e-3, $"sep {sep}");
        }
    }

    // ── paralaksa topocentryczna ─────────────────────────────────────────────────────────────────

    [Fact]
    public void topocentric_moon_near_horizon_shifts_by_most_of_a_degree()
    {
        // 2026-08-12 ~20:00 CEST: Księżyc tuż nad zachodnim horyzontem — paralaksa blisko maksimum.
        double hourUtc = 20.0 - 2.0;
        double jd = AstronomicalTime.JulianDate(2026, 8, 12, hourUtc);
        double lst = AstronomicalTime.LocalSiderealTimeHours(jd, KasprowyLon);

        (double raG, double decG) = LunarPosition.Equatorial(jd);
        (double raT, double decT, _) = LunarPosition.EquatorialTopocentric(jd, lst, KasprowyLat);

        Vector3 geo = CelestialCoordinates.EquatorialToWorld(raG, decG, lst, KasprowyLat);
        Vector3 topo = CelestialCoordinates.EquatorialToWorld(raT, decT, lst, KasprowyLat);
        double shiftDeg = Math.Acos(Math.Clamp(Vector3.Dot(geo, topo), -1f, 1f)) * 180.0 / Math.PI;

        shiftDeg.Should().BeInRange(0.55, 1.05, "przy horyzoncie paralaksa ~0,9°; topo NIŻEJ niż geo");
        topo.Z.Should().BeLessThan(geo.Z, "paralaksa spycha Księżyc KU horyzontowi");
    }

    // ── realne zaćmienie 2026-08-12 nad Tatrami ──────────────────────────────────────────────────

    [Fact]
    public void partial_eclipse_over_tatras_on_2026_08_12_peaks_near_sunset()
    {
        double maxObs = 0.0, maxHour = 0.0;
        for (double h = 18.0; h <= 21.0; h += 1.0 / 30.0)
        {
            SolarEclipseState e = NightSky.EclipseForLocalDate(2026, 8, 12, h, KasprowyLat, KasprowyLon);
            if (e.Obscuration > maxObs)
            {
                maxObs = e.Obscuration;
                maxHour = h;
            }
        }

        maxObs.Should().BeGreaterThan(0.5, "częściowe zaćmienie 12.08.2026 nad Polską jest głębokie");
        maxHour.Should().BeInRange(19.0, 20.75, "maksimum przy zachodzie (CEST)");

        // Kontrola ujemna: tydzień wcześniej — zero, o każdej porze tego wieczoru.
        for (double h = 18.0; h <= 21.0; h += 0.25)
        {
            NightSky.EclipseForLocalDate(2026, 8, 5, h, KasprowyLat, KasprowyLon)
                .Obscuration.Should().Be(0.0f, $"2026-08-05 {h:F2}");
        }
    }

    [Fact]
    public void eclipse_state_carries_consistent_disc_geometry()
    {
        SolarEclipseState e = NightSky.EclipseForLocalDate(2026, 8, 12, 19.9, KasprowyLat, KasprowyLon);

        // Promienie kątowe tarcz: słońce ~0,266°, księżyc 0,24-0,29° (zależnie od odległości topo).
        (e.SunAngularRadiusRadians * 180.0 / Math.PI).Should().BeApproximately(0.266, 0.01);
        (e.MoonAngularRadiusRadians * 180.0 / Math.PI).Should().BeInRange(0.235, 0.295);
        e.SunDirection.Length().Should().BeApproximately(1f, 1e-3f);
        e.MoonDirection.Length().Should().BeApproximately(1f, 1e-3f);
        // Skoro obscuracja > 0, separacja kątowa musi być mniejsza od sumy promieni.
        if (e.Obscuration > 0f)
        {
            double sep = Math.Acos(Math.Clamp(Vector3.Dot(e.SunDirection, e.MoonDirection), -1f, 1f));
            sep.Should().BeLessThan(e.SunAngularRadiusRadians + e.MoonAngularRadiusRadians);
        }
    }
}