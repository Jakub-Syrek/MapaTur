using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Task #5 (2026-08-08): analityczny model nieba Hoska–Wilkiego (SIGGRAPH 2012) — lustro referencyjnego
/// ArHosekSkyModel.cc (cook: kwintyczny Bezier po elevation^(1/3), bilinear turbidity/albedo; radiancja:
/// rozszerzona formuła Pereza z członem Mie). UWAGA na indeksy referencji: człon zenitalny mnoży c[7],
/// a anizotropia Mie w mianowniku to c[8]. Testy pinują własności fizyczne modelu, nie golden-values —
/// dataset jest przekonwertowany mechanicznie, a formuły są lustrem 1:1.
/// </summary>
public sealed class HosekSkyTests
{
    private const double T = 3.0;      // czyste alpejskie niebo
    private const double Albedo = 0.3; // mieszanka skała/łąka

    [Fact]
    public void dataset_arrays_have_reference_dimensions()
    {
        foreach (double[] set in new[] { HosekSkyDataset.DatasetRGB1, HosekSkyDataset.DatasetRGB2, HosekSkyDataset.DatasetRGB3 })
        {
            set.Length.Should().Be(2 * 10 * 6 * 9);
        }

        foreach (double[] rad in new[] { HosekSkyDataset.DatasetRGBRad1, HosekSkyDataset.DatasetRGBRad2, HosekSkyDataset.DatasetRGBRad3 })
        {
            rad.Length.Should().Be(2 * 10 * 6);
        }
    }

    [Fact]
    public void radiance_is_positive_and_finite_over_the_dome()
    {
        foreach (double elevDeg in new[] { 2.0, 15.0, 40.0, 64.0 })
        {
            HosekSkyState state = HosekSky.Create(T, Albedo, elevDeg * Math.PI / 180.0);
            for (double thetaDeg = 0; thetaDeg <= 88; thetaDeg += 8)
            {
                for (double gammaDeg = 0; gammaDeg <= 180; gammaDeg += 20)
                {
                    (double r, double g, double b) = state.Radiance(thetaDeg * Math.PI / 180.0, gammaDeg * Math.PI / 180.0);
                    foreach (double v in new[] { r, g, b })
                    {
                        double.IsFinite(v).Should().BeTrue($"elev {elevDeg} theta {thetaDeg} gamma {gammaDeg}");
                        v.Should().BeGreaterThan(0.0, $"elev {elevDeg} theta {thetaDeg} gamma {gammaDeg}");
                    }
                }
            }
        }
    }

    [Fact]
    public void is_continuous_across_integer_turbidity_boundary()
    {
        HosekSkyState below = HosekSky.Create(3.999, Albedo, 0.5);
        HosekSkyState above = HosekSky.Create(4.001, Albedo, 0.5);

        (double rB, double gB, double bB) = below.Radiance(1.0, 0.8);
        (double rA, double gA, double bA) = above.Radiance(1.0, 0.8);
        rA.Should().BeApproximately(rB, Math.Abs(rB) * 0.02);
        gA.Should().BeApproximately(gB, Math.Abs(gB) * 0.02);
        bA.Should().BeApproximately(bB, Math.Abs(bB) * 0.02);
    }

    [Fact]
    public void circumsolar_region_is_brighter_than_antisolar()
    {
        HosekSkyState state = HosekSky.Create(T, Albedo, 30.0 * Math.PI / 180.0);
        double theta = 60.0 * Math.PI / 180.0;

        (_, double gNear, _) = state.Radiance(theta, 5.0 * Math.PI / 180.0);
        (_, double gFar, _) = state.Radiance(theta, 140.0 * Math.PI / 180.0);
        gNear.Should().BeGreaterThan(gFar * 1.5, "poświata okołosłoneczna to znak firmowy modelu HW");
    }

    [Fact]
    public void low_sun_sky_is_redder_near_the_sun_than_high_sun()
    {
        HosekSkyState low = HosekSky.Create(T, Albedo, 4.0 * Math.PI / 180.0);
        HosekSkyState high = HosekSky.Create(T, Albedo, 60.0 * Math.PI / 180.0);
        double theta = 80.0 * Math.PI / 180.0;
        double gamma = 10.0 * Math.PI / 180.0;

        (double rLow, _, double bLow) = low.Radiance(theta, gamma);
        (double rHigh, _, double bHigh) = high.Radiance(theta, gamma);
        (rLow / bLow).Should().BeGreaterThan((rHigh / bHigh) * 1.3, "niskie słońce = czerwieńsze niebo przy horyzoncie");
    }

    [Fact]
    public void horizon_is_brighter_than_zenith_at_mid_elevation()
    {
        // Klasyka fizyki nieba (i słabość 2-kolorowego lerpa): jasny pas przy horyzoncie.
        HosekSkyState state = HosekSky.Create(T, Albedo, 40.0 * Math.PI / 180.0);

        (_, double gZenith, _) = state.Radiance(0.0, 40.0 * Math.PI / 180.0);
        (_, double gHorizon, _) = state.Radiance(85.0 * Math.PI / 180.0, 60.0 * Math.PI / 180.0);
        gHorizon.Should().BeGreaterThan(gZenith, "pas horyzontu jaśniejszy od zenitu");
    }

    [Fact]
    public void radiance_at_and_below_horizon_equals_the_floor_sample()
    {
        // Formuła HW eksploduje przy cosθ→0 (exp(k1/(cosθ+0.01))): bez podłogi ostatni ~1° nad
        // horyzontem ściska się na ekranie do 1-2 px (twarda krawędź — zmierzone 08-08, zachód),
        // a wypełnienie pod horyzontem dziedziczy garść nasyconego ekstremum. Podłoga 0.05 (~3°)
        // czyni przejście przez horyzont ciągłym.
        HosekSkyState state = HosekSky.Create(T, Albedo, 4.0 * Math.PI / 180.0);
        double gamma = 30.0 * Math.PI / 180.0;

        (double rH, double gH, double bH) = state.Radiance(Math.PI / 2.0, gamma);          // dokładnie horyzont
        (double rB, double gB, double bB) = state.Radiance(100.0 * Math.PI / 180.0, gamma); // pod horyzontem
        (double rF, double gF, double bF) = state.Radiance(Math.Acos(0.05), gamma);         // próbka podłogi

        rH.Should().Be(rF); gH.Should().Be(gF); bH.Should().Be(bF);
        rB.Should().Be(rF); gB.Should().Be(gF); bB.Should().Be(bF);
    }

    [Fact]
    public void clamps_inputs_to_supported_ranges()
    {
        // Turbidity spoza [1,10] i ujemna elewacja nie mogą wywrócić modelu (shader woła co klatkę).
        HosekSkyState state = HosekSky.Create(turbidity: 0.5, albedo: -0.2, solarElevationRadians: -0.1);

        (double r, double g, double b) = state.Radiance(0.5, 0.5);
        double.IsFinite(r + g + b).Should().BeTrue();
    }
}