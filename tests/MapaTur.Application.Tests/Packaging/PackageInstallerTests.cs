using System.Security.Cryptography;

using FluentAssertions;

using MapaTur.Application.Packaging;

namespace MapaTur.Application.Tests.Packaging;

public sealed class PackageInstallerTests : IDisposable
{
    private readonly string workDir;

    public PackageInstallerTests()
    {
        workDir = Path.Combine(Path.GetTempPath(), "mapatur-pkg-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(workDir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup; a held handle on a transient run is not a test failure.
        }
    }

    private static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static byte[] SampleBytes(int length)
    {
        var data = new byte[length];
        for (int i = 0; i < length; i++)
        {
            data[i] = (byte)(i * 7 + 13);
        }

        return data;
    }

    private static RegionPackage DemPackage(byte[] bytes, string? shaOverride = null) =>
        new(
            Id: "tatry-dem-1m",
            Name: "Tatry — detal 1 m",
            Layer: PackageLayer.Dem,
            Format: PackageFormat.ZipTileCache,
            Version: 3,
            SizeBytes: bytes.Length,
            Sha256: shaOverride ?? Sha256Hex(bytes),
            Url: "https://pkg.example/tatry-dem-1m.zip");

    private string PartialPath(RegionPackage pkg) => Path.Combine(workDir, pkg.Id + ".part");

    private PackageInstaller NewInstaller(
        FakeFetcher fetcher, RecordingExtractor extractor, InMemoryStore store) =>
        new(fetcher, extractor, store, workDir);

    [Fact]
    public async Task InstallAsync_DownloadsVerifiesExtractsAndMarks_OnHappyPath()
    {
        byte[] bytes = SampleBytes(1000);
        RegionPackage pkg = DemPackage(bytes);
        var fetcher = new FakeFetcher(bytes);
        var extractor = new RecordingExtractor();
        var store = new InMemoryStore();
        PackageInstaller installer = NewInstaller(fetcher, extractor, store);

        PackageInstallResult result = await installer.InstallAsync(pkg);

        extractor.Package!.Id.Should().Be(pkg.Id);
        extractor.ArchiveExistedAtCall.Should().BeTrue("the verified archive must be present when extracting");
        store.List().Should().ContainSingle()
            .Which.Should().Be(new InstalledPackage(pkg.Id, pkg.Version, pkg.Sha256));
        result.BytesDownloaded.Should().Be(bytes.Length);
        result.ResumedFromPartial.Should().BeFalse();
    }

    [Fact]
    public async Task InstallAsync_RemovesPartialFile_AfterSuccess()
    {
        byte[] bytes = SampleBytes(500);
        RegionPackage pkg = DemPackage(bytes);
        PackageInstaller installer = NewInstaller(new FakeFetcher(bytes), new RecordingExtractor(), new InMemoryStore());

        await installer.InstallAsync(pkg);

        File.Exists(PartialPath(pkg)).Should().BeFalse("a completed install must not leave a .part file behind");
    }

    [Fact]
    public async Task InstallAsync_ResumesFromPartial_AndOnlyFetchesRemainder()
    {
        byte[] bytes = SampleBytes(1000);
        RegionPackage pkg = DemPackage(bytes);
        await File.WriteAllBytesAsync(PartialPath(pkg), bytes[..400]); // 400 already on disk
        var fetcher = new FakeFetcher(bytes);
        PackageInstaller installer = NewInstaller(fetcher, new RecordingExtractor(), new InMemoryStore());

        PackageInstallResult result = await installer.InstallAsync(pkg);

        fetcher.RangeRequests.Should().ContainSingle().Which.Should().Be(400L, "resume must start where the partial ended");
        result.ResumedFromPartial.Should().BeTrue();
        result.BytesDownloaded.Should().Be(600L);
    }

    [Fact]
    public async Task InstallAsync_DoesNotFetch_WhenPartialIsAlreadyComplete()
    {
        byte[] bytes = SampleBytes(800);
        RegionPackage pkg = DemPackage(bytes);
        await File.WriteAllBytesAsync(PartialPath(pkg), bytes); // whole thing already downloaded
        var fetcher = new FakeFetcher(bytes);
        var store = new InMemoryStore();
        PackageInstaller installer = NewInstaller(fetcher, new RecordingExtractor(), store);

        PackageInstallResult result = await installer.InstallAsync(pkg);

        fetcher.RangeRequests.Should().BeEmpty("a complete partial needs no further network fetch");
        result.BytesDownloaded.Should().Be(0L);
        store.List().Should().ContainSingle();
    }

    [Fact]
    public async Task InstallAsync_Throws_AndLeavesNoMarker_WhenHashMismatches()
    {
        byte[] bytes = SampleBytes(600);
        RegionPackage pkg = DemPackage(bytes, shaOverride: "deadbeef"); // wrong hash
        var extractor = new RecordingExtractor();
        var store = new InMemoryStore();
        PackageInstaller installer = NewInstaller(new FakeFetcher(bytes), extractor, store);

        Func<Task> act = () => installer.InstallAsync(pkg);

        await act.Should().ThrowAsync<InvalidDataException>();
        store.List().Should().BeEmpty("a corrupt download must not be marked installed");
        extractor.Package.Should().BeNull("a corrupt download must never be extracted");
        File.Exists(PartialPath(pkg)).Should().BeFalse("the corrupt partial must be discarded so a retry re-downloads");
    }

    [Fact]
    public async Task InstallAsync_ReportsProgress_EndingAtTotal()
    {
        byte[] bytes = SampleBytes(1024);
        RegionPackage pkg = DemPackage(bytes);
        var progress = new RecordingProgress();
        PackageInstaller installer = NewInstaller(new FakeFetcher(bytes), new RecordingExtractor(), new InMemoryStore());

        await installer.InstallAsync(pkg, progress);

        progress.Reports.Should().NotBeEmpty();
        PackageDownloadProgress last = progress.Reports[^1];
        last.BytesReceived.Should().Be(1024L);
        last.TotalBytes.Should().Be(1024L);
    }

    [Fact]
    public async Task InstallAsync_HonoursCancellation()
    {
        byte[] bytes = SampleBytes(1000);
        RegionPackage pkg = DemPackage(bytes);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        PackageInstaller installer = NewInstaller(new FakeFetcher(bytes), new RecordingExtractor(), new InMemoryStore());

        Func<Task> act = () => installer.InstallAsync(pkg, cancellationToken: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private sealed class FakeFetcher : IPackageFileFetcher
    {
        private readonly byte[] data;

        public FakeFetcher(byte[] data) => this.data = data;

        public List<long> RangeRequests { get; } = new();

        public Task<long> GetContentLengthAsync(string url, CancellationToken cancellationToken = default) =>
            Task.FromResult((long)data.Length);

        public Task<Stream> OpenRangeAsync(string url, long fromByte, CancellationToken cancellationToken = default)
        {
            RangeRequests.Add(fromByte);
            int offset = (int)fromByte;
            Stream stream = new MemoryStream(data, offset, data.Length - offset, writable: false);
            return Task.FromResult(stream);
        }
    }

    private sealed class RecordingExtractor : IPackageContentExtractor
    {
        public string? ArchivePath { get; private set; }

        public RegionPackage? Package { get; private set; }

        public bool ArchiveExistedAtCall { get; private set; }

        public Task ExtractAsync(string archivePath, RegionPackage package, CancellationToken cancellationToken = default)
        {
            ArchivePath = archivePath;
            Package = package;
            ArchiveExistedAtCall = File.Exists(archivePath);
            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryStore : IInstalledPackageStore
    {
        private readonly Dictionary<string, InstalledPackage> map = new(StringComparer.Ordinal);

        public IReadOnlyCollection<InstalledPackage> List() => map.Values.ToList();

        public void MarkInstalled(InstalledPackage package) => map[package.Id] = package;

        public void Remove(string id) => map.Remove(id);
    }

    private sealed class RecordingProgress : IProgress<PackageDownloadProgress>
    {
        public List<PackageDownloadProgress> Reports { get; } = new();

        public void Report(PackageDownloadProgress value) => Reports.Add(value);
    }
}