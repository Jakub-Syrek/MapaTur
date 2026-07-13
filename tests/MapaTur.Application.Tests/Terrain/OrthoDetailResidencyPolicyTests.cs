using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Decides which detail CELLS should be resident this frame (PLAN R2): the ring of cells around the camera
/// focus, ordered nearest-first, biased FORWARD along the camera velocity (prefetch so a walked boundary
/// crossing finds the next cell already composed), capped at a near-budget count, and SUPPRESSED entirely
/// above a fast-motion speed (dragon flight — the ring would only churn behind the camera). Pure: the GL
/// compose/upload is the streaming manager's job. The cell UNDER the camera is always included (never a hole).
/// </summary>
public sealed class OrthoDetailResidencyPolicyTests
{
    private static readonly OrthoDetailGrid Grid = new(); // det25, pitch 6 = 768 m cells
    private static readonly OrthoDetailResidencyPolicy Policy = new(
        Grid, ringRadiusMeters: 2500.0, fastMotionSpeedMps: 30.0, prefetchLeadMeters: 500.0);

    // A focus well inside the massif (near Morskie Oko) so the ring is full of valid cells.
    private static readonly GeoPoint Focus = new(49.235, 20.010);

    [Fact]
    public void DesiredCells_Still_ReturnsFocusCellFirstThenNearestWithinCap()
    {
        var cells = Policy.DesiredCells(Focus, velEastMps: 0, velNorthMps: 0, nearCap: 6);

        cells.Should().HaveCount(6);
        cells.Should().OnlyHaveUniqueItems();
        cells[0].Should().Be(Grid.CellKey(Grid.CellForPoint(Focus).Ci, Grid.CellForPoint(Focus).Cj));
    }

    [Fact]
    public void DesiredCells_NearCapOne_ReturnsOnlyTheCellUnderTheCamera()
    {
        var cells = Policy.DesiredCells(Focus, 0, 0, nearCap: 1);
        var (ci, cj) = Grid.CellForPoint(Focus);
        cells.Should().Equal(Grid.CellKey(ci, cj));
    }

    [Fact]
    public void DesiredCells_UnderFastMotion_ReturnsEmpty()
    {
        // 50 m/s > 30 m/s threshold (dragon flight) → suppress; base ortho carries the frame.
        Policy.DesiredCells(Focus, velEastMps: 50, velNorthMps: 0, nearCap: 9).Should().BeEmpty();
    }

    [Fact]
    public void DesiredCells_WithEastwardVelocity_LeansEast()
    {
        int focusCi = Grid.CellForPoint(Focus).Ci;
        var cells = Policy.DesiredCells(Focus, velEastMps: 20, velNorthMps: 0, nearCap: 6);

        int east = cells.Count(k => Grid.CellFromKey(k).Ci > focusCi);
        int west = cells.Count(k => Grid.CellFromKey(k).Ci < focusCi);
        east.Should().BeGreaterThan(west, "prefetch biases the resident set forward along the velocity");
    }

    [Fact]
    public void DesiredCells_AllWithinTheRing()
    {
        var cells = Policy.DesiredCells(Focus, 0, 0, nearCap: 30);
        foreach (int key in cells)
        {
            var (ci, cj) = Grid.CellFromKey(key);
            MapBounds b = Grid.CellBounds(ci, cj);
            double midLat = (b.SouthWest.Latitude + b.NorthEast.Latitude) / 2;
            double midLon = (b.SouthWest.Longitude + b.NorthEast.Longitude) / 2;
            double dLat = (midLat - Focus.Latitude) * 111320.0;
            double dLon = (midLon - Focus.Longitude) * 111320.0 * Math.Cos(Focus.Latitude * Math.PI / 180);
            Math.Sqrt((dLat * dLat) + (dLon * dLon)).Should().BeLessThan(2500.0 + 1024.0); // ring + cell span
        }
    }
}
