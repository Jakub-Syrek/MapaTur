using FluentAssertions;

using MapaTur.Application.Packaging;

namespace MapaTur.Application.Tests.Packaging;

public sealed class OfflinePackageServiceTests
{
    private static RegionPackage Pkg(string id, int version) =>
        new(id, id, PackageLayer.Dem, PackageFormat.ZipTileCache, version, 1, "00", $"https://x/{id}");

    [Fact]
    public async Task GetCatalogAsync_MergesManifestWithInstalledMarkers()
    {
        var source = new FakeSource(new PackageManifest(new[] { Pkg("a", 2), Pkg("b", 1) }));
        var store = new FakeStore(new InstalledPackage("a", 1, "00")); // 'a' is out of date
        var service = new OfflinePackageService(source, store, new FakeInstaller());

        IReadOnlyList<PackageStatus> catalog = await service.GetCatalogAsync();

        catalog.Should().HaveCount(2);
        catalog[0].State.Should().Be(PackageState.UpdateAvailable);
        catalog[1].State.Should().Be(PackageState.NotInstalled);
    }

    [Fact]
    public async Task InstallAsync_DelegatesToTheInstaller()
    {
        var installer = new FakeInstaller();
        var service = new OfflinePackageService(
            new FakeSource(new PackageManifest(Array.Empty<RegionPackage>())), new FakeStore(), installer);
        RegionPackage target = Pkg("a", 1);

        await service.InstallAsync(target);

        installer.Installed.Should().ContainSingle().Which.Id.Should().Be("a");
    }

    private sealed class FakeSource : IPackageCatalogSource
    {
        private readonly PackageManifest manifest;

        public FakeSource(PackageManifest manifest) => this.manifest = manifest;

        public Task<PackageManifest> GetManifestAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(manifest);
    }

    private sealed class FakeStore : IInstalledPackageStore
    {
        private readonly List<InstalledPackage> records;

        public FakeStore(params InstalledPackage[] records) => this.records = records.ToList();

        public IReadOnlyCollection<InstalledPackage> List() => records;

        public void MarkInstalled(InstalledPackage package) => records.Add(package);

        public void Remove(string id) => records.RemoveAll(r => r.Id == id);
    }

    private sealed class FakeInstaller : IPackageInstaller
    {
        public List<RegionPackage> Installed { get; } = new();

        public Task<PackageInstallResult> InstallAsync(
            RegionPackage package,
            IProgress<PackageDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Installed.Add(package);
            return Task.FromResult(new PackageInstallResult(package.Id, 0, false));
        }
    }
}