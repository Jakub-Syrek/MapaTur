using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Behaviour pinning for <see cref="DetailTileRing"/>, the residency selector for streaming-LOD detail
/// tiles (Etap 3 of the [[lod-terrain-architecture]]): around the camera's ground focus, keep an N×N ring
/// of high-resolution tiles resident over the always-on 30 m base. Pure tile math — picks WHICH tiles;
/// loading / building / the persistent base live elsewhere.
/// </summary>
public sealed class DetailTileRingTests
{
    private const int Zoom = 16;
    private static readonly GeoPoint MorskieOko = new(49.195, 20.07);

    [Fact]
    public void Around_RadiusZero_ReturnsOnlyTheFocusTile()
    {
        var (fx, fy) = SlippyTileMath.LonLatToTile(MorskieOko.Longitude, MorskieOko.Latitude, Zoom);

        var ring = DetailTileRing.Around(MorskieOko, Zoom, radiusTiles: 0);

        ring.Should().ContainSingle()
            .Which.Should().Be(new DemTileKey(Zoom, fx, fy));
    }

    [Fact]
    public void Around_RadiusOne_ReturnsThreeByThree_CentredOnFocus()
    {
        var (fx, fy) = SlippyTileMath.LonLatToTile(MorskieOko.Longitude, MorskieOko.Latitude, Zoom);

        var ring = DetailTileRing.Around(MorskieOko, Zoom, radiusTiles: 1);

        ring.Should().HaveCount(9);
        ring.Should().Contain(new DemTileKey(Zoom, fx, fy), "the focus tile is the centre");
        ring.Should().Contain(new DemTileKey(Zoom, fx - 1, fy - 1));
        ring.Should().Contain(new DemTileKey(Zoom, fx + 1, fy + 1));
    }

    [Fact]
    public void Around_RadiusTwo_ReturnsFiveByFive()
    {
        var ring = DetailTileRing.Around(MorskieOko, Zoom, radiusTiles: 2);

        ring.Should().HaveCount(25);
    }

    [Fact]
    public void Around_AllTiles_AreAtTheRequestedZoom()
    {
        var ring = DetailTileRing.Around(MorskieOko, Zoom, radiusTiles: 2);

        ring.Should().OnlyContain(t => t.Zoom == Zoom);
    }

    [Fact]
    public void Around_NegativeRadius_Throws()
    {
        var act = () => DetailTileRing.Around(MorskieOko, Zoom, radiusTiles: -1);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}