using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Task #4 (2026-08-08): mgła WYSOKOŚCIOWA — gęstość maleje wykładniczo z wysokością
/// (ρ(z) = ρ0·exp(−(z−ref)/H), z klapą ×3 poniżej referencji, żeby Podhale nie tonęło w zupie),
/// a głębia optyczna wzdłuż promienia liczy się analitycznie (kawałkami przez strefę klapy).
/// Shader terenu LUSTRZE tę matematykę 1:1 — ta klasa jest jedynym źródłem prawdy i miejscem testów.
/// Wartości domyślne: ref 900 m (dna dolin), H 450 m — granie ~2 H nad referencją mają ~13% gęstości dolin.
/// </summary>
public sealed class HeightFogTests
{
    private const double Rho = 1.5e-5;
    private const double RefZ = 900.0;
    private const double H = 450.0;

    [Fact]
    public void horizontal_ray_at_reference_height_matches_uniform_fog()
    {
        double depth = HeightFog.OpticalDepth(camZ: RefZ, fragZ: RefZ, dist: 20_000, Rho, RefZ, H);

        depth.Should().BeApproximately(Rho * 20_000, Rho * 20_000 * 1e-6,
            "na wysokości referencyjnej mgła wysokościowa degeneruje się do dotychczasowej jednorodnej");
    }

    [Fact]
    public void ray_one_scale_height_up_is_e_times_thinner()
    {
        double depth = HeightFog.OpticalDepth(RefZ + H, RefZ + H, 20_000, Rho, RefZ, H);

        depth.Should().BeApproximately(Rho * 20_000 * Math.Exp(-1.0), Rho * 20_000 * 1e-4);
    }

    [Fact]
    public void is_symmetric_in_endpoints()
    {
        double down = HeightFog.OpticalDepth(2200.0 * 1.5, 950.0 * 1.5, 12_000, Rho, RefZ * 1.5, H * 1.5);
        double up = HeightFog.OpticalDepth(950.0 * 1.5, 2200.0 * 1.5, 12_000, Rho, RefZ * 1.5, H * 1.5);

        down.Should().BeApproximately(up, Math.Abs(up) * 1e-9, "całka po odcinku nie zależy od kierunku");
    }

    [Fact]
    public void valley_ray_is_denser_than_ridge_ray()
    {
        double valley = HeightFog.OpticalDepth(RefZ + 50, RefZ + 50, 10_000, Rho, RefZ, H);
        double ridge = HeightFog.OpticalDepth(RefZ + 1200, RefZ + 1200, 10_000, Rho, RefZ, H);

        valley.Should().BeGreaterThan(ridge * 5, "doliny mgliste, granie czyste — sedno taska #4");
    }

    [Fact]
    public void density_boost_below_reference_is_capped()
    {
        // Kamera i fragment głęboko pod referencją (Podhale/kotliny): bez klapy exp(+3) = ×20 gęstości
        // i cała scena tonie; klapa trzyma maksimum ×3.
        double depth = HeightFog.OpticalDepth(RefZ - (3 * H), RefZ - (3 * H), 10_000, Rho, RefZ, H);

        depth.Should().BeApproximately(HeightFog.MaxBoostBelowReference * Rho * 10_000, Rho * 10_000 * 1e-6);
    }

    [Fact]
    public void slanted_ray_matches_numerical_integration()
    {
        // Promień z grani (2200) w dno doliny (950) — przecina strefę klapy? Nie (950 > zCap ≈ 406),
        // ale sprawdzamy też wariant nurkujący pod klapę. Suma Riemanna 20k kroków vs forma zamknięta.
        foreach ((double camZ, double fragZ) in new[] { (2200.0, 950.0), (1800.0, 250.0), (300.0, 2400.0) })
        {
            double dist = 15_000;
            int n = 20_000;
            double sum = 0;
            for (int i = 0; i < n; i++)
            {
                double z = camZ + ((fragZ - camZ) * ((i + 0.5) / n));
                sum += HeightFog.Density(z, Rho, RefZ, H) * (dist / n);
            }

            double closed = HeightFog.OpticalDepth(camZ, fragZ, dist, Rho, RefZ, H);
            closed.Should().BeApproximately(sum, Math.Abs(sum) * 1e-4,
                $"forma zamknięta musi zgadzać się z całką numeryczną (cam {camZ} → frag {fragZ})");
        }
    }

    [Fact]
    public void fog_factor_is_bounded_and_monotonic_with_distance()
    {
        double near = HeightFog.FogFactor(RefZ, RefZ, 2_000, Rho, RefZ, H);
        double far = HeightFog.FogFactor(RefZ, RefZ, 40_000, Rho, RefZ, H);

        near.Should().BeInRange(0.0, 1.0);
        far.Should().BeInRange(0.0, 1.0);
        far.Should().BeGreaterThan(near);
    }
}