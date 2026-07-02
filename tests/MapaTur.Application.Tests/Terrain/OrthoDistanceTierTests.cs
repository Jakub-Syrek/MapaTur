using FluentAssertions;

using MapaTur.Application.Terrain;

using Xunit;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Ortho cells are geographically huge (~16 km) and were all downsampled to one FLAT cap regardless of camera
/// distance — a cell the camera is standing right on top of got the same coarse resolution as one 50 km away,
/// which reads as blocky pixelation up close. <see cref="OrthoDistanceTier.DesiredCapPx"/> decides a per-cell
/// resolution tier from camera distance instead: near stays sharp, far is downsampled harder. Hysteresis (a
/// dead-band between the enter and exit thresholds) stops the tier flapping — and re-triggering an expensive
/// re-downsample + GPU re-upload — every frame the camera hovers near the boundary.
/// </summary>
public sealed class OrthoDistanceTierTests
{
    [Fact]
    public void DesiredCapPx_FarCell_CameraClose_EntersNearTier()
    {
        int cap = OrthoDistanceTier.DesiredCapPx(currentCapPx: OrthoDistanceTier.FarCapPx, distanceMeters: 5_000f);

        cap.Should().Be(OrthoDistanceTier.NearCapPx);
    }

    [Fact]
    public void DesiredCapPx_FarCell_CameraInHysteresisBand_StaysFar()
    {
        // 12 km is beyond the ENTER threshold (10 km) but within the EXIT threshold (14 km) — a cell that
        // hasn't already earned the near tier must not enter it from this distance (only exit-side hysteresis
        // applies once near).
        int cap = OrthoDistanceTier.DesiredCapPx(currentCapPx: OrthoDistanceTier.FarCapPx, distanceMeters: 12_000f);

        cap.Should().Be(OrthoDistanceTier.FarCapPx);
    }

    [Fact]
    public void DesiredCapPx_NearCell_CameraInHysteresisBand_StaysNear()
    {
        // Same 12 km distance, but the cell is ALREADY near — the dead-band must hold it there so hovering at
        // this distance doesn't flap the tier (and re-upload) every frame.
        int cap = OrthoDistanceTier.DesiredCapPx(currentCapPx: OrthoDistanceTier.NearCapPx, distanceMeters: 12_000f);

        cap.Should().Be(OrthoDistanceTier.NearCapPx);
    }

    [Fact]
    public void DesiredCapPx_NearCell_CameraMovesPastExitThreshold_DemotesToFar()
    {
        int cap = OrthoDistanceTier.DesiredCapPx(currentCapPx: OrthoDistanceTier.NearCapPx, distanceMeters: 15_000f);

        cap.Should().Be(OrthoDistanceTier.FarCapPx);
    }

    [Fact]
    public void DesiredCapPx_UnsetInitialCap_TreatedAsFarUntilCloseEnough()
    {
        // 0 = "never uploaded yet" — must behave like the far tier (only promotes once inside the ENTER
        // distance), so a cell's first-ever upload never wastes a near-tier download for ground that turns
        // out to be far away.
        OrthoDistanceTier.DesiredCapPx(currentCapPx: 0, distanceMeters: 20_000f).Should().Be(OrthoDistanceTier.FarCapPx);
        OrthoDistanceTier.DesiredCapPx(currentCapPx: 0, distanceMeters: 1_000f).Should().Be(OrthoDistanceTier.NearCapPx);
    }
}