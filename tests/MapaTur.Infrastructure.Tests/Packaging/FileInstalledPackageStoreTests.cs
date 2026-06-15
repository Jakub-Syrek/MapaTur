using FluentAssertions;

using MapaTur.Application.Packaging;
using MapaTur.Infrastructure.Packaging;

namespace MapaTur.Infrastructure.Tests.Packaging;

public sealed class FileInstalledPackageStoreTests : IDisposable
{
    private readonly string dir;

    public FileInstalledPackageStoreTests()
    {
        dir = Path.Combine(Path.GetTempPath(), "mapatur-installed-tests", Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }

    [Fact]
    public void List_ReturnsEmpty_WhenNothingInstalled()
    {
        var store = new FileInstalledPackageStore(dir);

        store.List().Should().BeEmpty();
    }

    [Fact]
    public void MarkInstalled_ThenList_ReturnsTheRecord()
    {
        var store = new FileInstalledPackageStore(dir);
        var record = new InstalledPackage("tatry-dem-1m", 3, "aabbcc");

        store.MarkInstalled(record);

        store.List().Should().ContainSingle().Which.Should().Be(record);
    }

    [Fact]
    public void MarkInstalled_SurvivesAcrossInstances()
    {
        var record = new InstalledPackage("tatry-ortho", 2, "ddeeff");
        new FileInstalledPackageStore(dir).MarkInstalled(record);

        var reopened = new FileInstalledPackageStore(dir);

        reopened.List().Should().ContainSingle().Which.Should().Be(record);
    }

    [Fact]
    public void MarkInstalled_OverwritesSameId()
    {
        var store = new FileInstalledPackageStore(dir);
        store.MarkInstalled(new InstalledPackage("a", 1, "old"));

        store.MarkInstalled(new InstalledPackage("a", 4, "new"));

        store.List().Should().ContainSingle()
            .Which.Should().Be(new InstalledPackage("a", 4, "new"));
    }

    [Fact]
    public void Remove_DeletesTheMarker()
    {
        var store = new FileInstalledPackageStore(dir);
        store.MarkInstalled(new InstalledPackage("a", 1, "00"));

        store.Remove("a");

        store.List().Should().BeEmpty();
    }

    [Fact]
    public void Remove_IsNoOp_WhenMarkerAbsent()
    {
        var store = new FileInstalledPackageStore(dir);

        Action act = () => store.Remove("ghost");

        act.Should().NotThrow();
    }

    [Fact]
    public void List_SkipsCorruptMarkerFiles()
    {
        var store = new FileInstalledPackageStore(dir);
        store.MarkInstalled(new InstalledPackage("good", 1, "00"));
        File.WriteAllText(Path.Combine(dir, "broken.json"), "{ this is not valid json");

        store.List().Should().ContainSingle().Which.Id.Should().Be("good");
    }
}