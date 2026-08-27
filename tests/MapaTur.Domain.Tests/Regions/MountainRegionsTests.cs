using FluentAssertions;

using MapaTur.Domain.Geography;
using MapaTur.Domain.Regions;

namespace MapaTur.Domain.Tests.Regions;

/// <summary>
/// Pins the "tatry" registry entry BIT-FOR-BIT to the values the app shipped with before the region
/// registry existed (P-A, PLAN-ALPY §3: Tatry = entry #1, zero regression). Every literal here is the
/// pre-registry constant — if a refactor drifts any of them, these tests are the tripwire. The old
/// static classes (TatraDemRegion, TatraOfflineRegion, KarpatRegions.Tatry, TrailAutoSyncPolicy)
/// became facades over this entry, so their own tests keep guarding the facade path.
/// </summary>
public sealed class MountainRegionsTests
{
    [Fact]
    public void Tatry_DemLoad_PinsPreRegistryValues()
    {
        RegionDemLoad dem = MountainRegions.Tatry.DemLoad;

        dem.Bounds.Should().Be(new MapBounds(new GeoPoint(49.183, 20.050), new GeoPoint(49.207, 20.093)));
        dem.MaxTiles.Should().Be(76);
        dem.MinZoom.Should().Be(11);
        dem.MaxZoom.Should().Be(16);
    }

    [Fact]
    public void Tatry_OfflineDownload_PinsPreRegistryValues()
    {
        RegionOfflineDownload off = MountainRegions.Tatry.Offline;

        off.Bounds.Should().Be(new MapBounds(new GeoPoint(49.17, 19.73), new GeoPoint(49.30, 20.15)));
        off.DownloadZoom.Should().Be(16);
        off.ApproxBytesPerTile.Should().Be(256L * 256 * 4);
    }

    [Fact]
    public void Tatry_TrailFilterBounds_PinsKarpatRegionsTatry()
    {
        MountainRegions.Tatry.TrailFilterBounds
            .Should().Be(new MapBounds(new GeoPoint(49.05, 19.55), new GeoPoint(49.40, 20.30)));
    }

    [Fact]
    public void Tatry_TrailSyncBounds_PinsRegionC()
    {
        MountainRegions.Tatry.TrailSyncBounds
            .Should().Be(new MapBounds(new GeoPoint(49.10, 19.50), new GeoPoint(49.40, 20.40)));
    }

    [Fact]
    public void Tatry_PoiCoreBounds_PinsTatraCoreRegion()
    {
        MountainRegions.Tatry.PoiCoreBounds
            .Should().Be(new MapBounds(new GeoPoint(49.08, 19.78), new GeoPoint(49.32, 20.35)));
    }

    [Fact]
    public void Tatry_MapStart_PinsKasprowyViewport()
    {
        RegionMapStart start = MountainRegions.Tatry.MapStart;

        start.Latitude.Should().Be(49.2326);
        start.Longitude.Should().Be(19.9819);
        start.Resolution.Should().Be(152.0);
    }

    [Fact]
    public void Tatry_DetailLattice_PinsFetcherAnchors()
    {
        RegionDetailLattice lattice = MountainRegions.Tatry.DetailLattice;

        // MUSI zgadzac sie z testdata/maps/fetch-ortho-detail.py — inwariant kraty na dysku.
        lattice.Lon0.Should().Be(19.50);
        lattice.Lat0.Should().Be(49.40);
        lattice.RefLat.Should().Be(49.25);
        lattice.PathSegment.Should().Be("tatry");
    }

    [Fact]
    public void Tatry_DemCacheSubdir_PinsGugik()
    {
        MountainRegions.Tatry.DemCacheSubdir.Should().Be("gugik");
    }

    [Fact]
    public void Default_IsTatry()
    {
        MountainRegions.Default.Should().BeSameAs(MountainRegions.Tatry);
    }

    [Fact]
    public void ById_FindsTatryAndRejectsUnknown()
    {
        MountainRegions.ById("tatry").Should().BeSameAs(MountainRegions.Tatry);
        MountainRegions.ById("zermatt").Should().BeSameAs(MountainRegions.Zermatt);
        MountainRegions.ById("atlantyda").Should().BeNull();
    }

    [Fact]
    public void All_ListsTatryFirstThenZermatt()
    {
        MountainRegions.All.Select(r => r.Id).Should().Equal("tatry", "zermatt");
    }

    [Fact]
    public void Zermatt_PinsPilotWindowAndOwnNamespaces()
    {
        MountainRegion z = MountainRegions.ById("zermatt")!;

        // Okno pilota P-B (TILE-PRODUCTION-ALPY par.A0) — wszystkie bboxy celowo IDENTYCZNE na start
        // (Tatry mialy piec roznych, bo narosly historycznie; Zermatt zaczyna od jednego okna).
        var window = new MapBounds(new GeoPoint(45.92, 7.58), new GeoPoint(46.08, 7.88));
        z.DemLoad.Bounds.Should().Be(window);
        z.Offline.Bounds.Should().Be(window);
        z.TrailFilterBounds.Should().Be(window);
        z.TrailSyncBounds.Should().Be(window);
        z.PoiCoreBounds.Should().Be(window);
        // Wlasne przestrzenie nazw danych — NIGDY wspoldzielone z tatry:
        z.DemCacheSubdir.Should().Be("swisstopo");
        z.DetailLattice.PathSegment.Should().Be("zermatt");
        // Kotwica kraty detali = NW naroznik okna, RefLat = srodek (wlasna krata, nie tatrzanska).
        z.DetailLattice.Lon0.Should().Be(7.58);
        z.DetailLattice.Lat0.Should().Be(46.08);
        z.DetailLattice.RefLat.Should().Be(46.0);
        // Start 2D: wies Zermatt.
        z.MapStart.Latitude.Should().Be(46.0207);
        z.MapStart.Longitude.Should().Be(7.7491);
    }

    [Fact]
    public void Default_HonoursRegionEnvOverride()
    {
        // Harness P-B: MAPATUR_REGION=zermatt przelacza region bez UI (P-A3 dostarczy przelaczanie
        // produktowe); nieznane id albo brak env = tatry (zero regresji).
        MountainRegions.ResolveDefault("zermatt")!.Id.Should().Be("zermatt");
        MountainRegions.ResolveDefault("TATRY")!.Id.Should().Be("tatry");
        MountainRegions.ResolveDefault("atlantyda").Should().BeNull();
        MountainRegions.ResolveDefault(null).Should().BeNull();
    }
}