using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Strażnik pozy kamery (2026-09-04, po „ścianie przed oczyma"): zapisana / harnessowa poza z celem
/// POD terenem (cel z=360 m przy minimum bazy 1424 m) była odtwarzana przy każdym starcie i kamera
/// patrzyła w dół przez przekrój terenu. Reguła: cel poniżej (min bazy − margines) albo absurdalnie
/// wysoko nad maksimum = poza NIEWIARYGODNA → odrzuć i auto-kadruj, zamiast utrwalać śmieć.
/// </summary>
public sealed class CameraPoseGuardTests
{
    private const float MinZ = 1424f;
    private const float MaxZ = 4613f;

    [Fact]
    public void target_on_terrain_is_plausible()
    {
        CameraPoseGuard.IsTargetPlausible(targetZ: 3953f, MinZ, MaxZ).Should().BeTrue();
    }

    [Fact]
    public void target_far_below_base_minimum_is_rejected()
    {
        // Dokładnie przypadek z 08-29: cel 360 m, min bazy 1424 m.
        CameraPoseGuard.IsTargetPlausible(targetZ: 360f, MinZ, MaxZ).Should().BeFalse();
    }

    [Fact]
    public void target_slightly_below_minimum_within_margin_is_plausible()
    {
        // Dolina poniżej minimum bazy o kilkadziesiąt metrów (np. jezioro w wyciętym voidzie) — nie karać.
        CameraPoseGuard.IsTargetPlausible(targetZ: MinZ - CameraPoseGuard.BelowMinMarginMeters + 1f, MinZ, MaxZ)
            .Should().BeTrue();
    }

    [Fact]
    public void target_absurdly_above_maximum_is_rejected()
    {
        CameraPoseGuard.IsTargetPlausible(targetZ: MaxZ + CameraPoseGuard.AboveMaxMarginMeters + 1f, MinZ, MaxZ)
            .Should().BeFalse();
    }

    [Fact]
    public void non_finite_target_is_rejected()
    {
        CameraPoseGuard.IsTargetPlausible(float.NaN, MinZ, MaxZ).Should().BeFalse();
        CameraPoseGuard.IsTargetPlausible(float.PositiveInfinity, MinZ, MaxZ).Should().BeFalse();
    }

    [Fact]
    public void degenerate_frame_without_elevations_accepts_everything()
    {
        // TerrainMesh3D ustawia Min/Max = 0 gdy brak wysokości — nie ma czego strzec.
        CameraPoseGuard.IsTargetPlausible(targetZ: -500f, minZ: 0f, maxZ: 0f).Should().BeTrue();
    }
}