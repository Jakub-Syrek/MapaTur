using FluentAssertions;

using MapaTur.Application.Packaging;

namespace MapaTur.Application.Tests.Packaging;

public sealed class PackageCatalogTests
{
    private static RegionPackage Pkg(string id, int version) =>
        new(id, id, PackageLayer.Dem, PackageFormat.ZipTileCache, version, 1, "00", $"https://x/{id}");

    private static PackageManifest Manifest(params RegionPackage[] packages) => new(packages);

    [Fact]
    public void Merge_MarksNotInstalled_WhenNoMarkerExists()
    {
        PackageManifest manifest = Manifest(Pkg("a", 1));

        IReadOnlyList<PackageStatus> status = PackageCatalog.Merge(manifest, Array.Empty<InstalledPackage>());

        status.Single().State.Should().Be(PackageState.NotInstalled);
    }

    [Fact]
    public void Merge_MarksInstalled_WhenMarkerVersionMatches()
    {
        PackageManifest manifest = Manifest(Pkg("a", 2));
        var installed = new[] { new InstalledPackage("a", 2, "00") };

        IReadOnlyList<PackageStatus> status = PackageCatalog.Merge(manifest, installed);

        status.Single().State.Should().Be(PackageState.Installed);
    }

    [Fact]
    public void Merge_MarksUpdateAvailable_WhenManifestVersionIsNewer()
    {
        PackageManifest manifest = Manifest(Pkg("a", 5));
        var installed = new[] { new InstalledPackage("a", 3, "00") };

        IReadOnlyList<PackageStatus> status = PackageCatalog.Merge(manifest, installed);

        status.Single().State.Should().Be(PackageState.UpdateAvailable);
    }

    [Fact]
    public void Merge_MarksInstalled_WhenInstalledVersionIsNewerThanManifest()
    {
        // Defensive: a downgraded manifest must not nag for a "downgrade".
        PackageManifest manifest = Manifest(Pkg("a", 1));
        var installed = new[] { new InstalledPackage("a", 4, "00") };

        IReadOnlyList<PackageStatus> status = PackageCatalog.Merge(manifest, installed);

        status.Single().State.Should().Be(PackageState.Installed);
    }

    [Fact]
    public void Merge_PreservesManifestOrder()
    {
        PackageManifest manifest = Manifest(Pkg("a", 1), Pkg("b", 1), Pkg("c", 1));

        IReadOnlyList<PackageStatus> status = PackageCatalog.Merge(manifest, Array.Empty<InstalledPackage>());

        status.Select(s => s.Package.Id).Should().ContainInOrder("a", "b", "c");
    }

    [Fact]
    public void Merge_IgnoresInstalledMarkers_NotAdvertisedInManifest()
    {
        // A stale marker for a withdrawn package must not appear in the catalogue.
        PackageManifest manifest = Manifest(Pkg("a", 1));
        var installed = new[] { new InstalledPackage("ghost", 1, "00") };

        IReadOnlyList<PackageStatus> status = PackageCatalog.Merge(manifest, installed);

        status.Should().ContainSingle().Which.Package.Id.Should().Be("a");
    }
}