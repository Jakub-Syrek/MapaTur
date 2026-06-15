using System.Text;

using FluentAssertions;

using MapaTur.Application.Packaging;

namespace MapaTur.Application.Tests.Packaging;

public sealed class PackageManifestParserTests
{
    private static ReadOnlySpan<byte> Utf8(string json) => Encoding.UTF8.GetBytes(json);

    private const string TwoPackages = """
    {
      "packages": [
        {
          "id": "tatry-dem-1m",
          "name": "Tatry — detal 1 m",
          "layer": "Dem",
          "format": "ZipTileCache",
          "version": 3,
          "sizeBytes": 524288000,
          "sha256": "aabbcc",
          "url": "https://pkg.example/packages/tatry-dem-1m-v3.zip"
        },
        {
          "id": "tatry-ortho",
          "name": "Tatry — ortofoto",
          "layer": "Ortho",
          "format": "MBTiles",
          "version": 1,
          "sizeBytes": 1610612736,
          "sha256": "ddeeff",
          "url": "https://pkg.example/packages/tatry-ortho-v1.mbtiles"
        }
      ]
    }
    """;

    [Fact]
    public void Parse_ReturnsAllPackages_WhenManifestIsValid()
    {
        PackageManifest manifest = PackageManifestParser.Parse(Utf8(TwoPackages));

        manifest.Packages.Should().HaveCount(2);
    }

    [Fact]
    public void Parse_ReadsEveryField_OfFirstPackage()
    {
        PackageManifest manifest = PackageManifestParser.Parse(Utf8(TwoPackages));

        RegionPackage dem = manifest.Packages[0];
        dem.Id.Should().Be("tatry-dem-1m");
        dem.Name.Should().Be("Tatry — detal 1 m");
        dem.Layer.Should().Be(PackageLayer.Dem);
        dem.Format.Should().Be(PackageFormat.ZipTileCache);
        dem.Version.Should().Be(3);
        dem.SizeBytes.Should().Be(524288000L);
        dem.Sha256.Should().Be("aabbcc");
        dem.Url.Should().Be("https://pkg.example/packages/tatry-dem-1m-v3.zip");
    }

    [Fact]
    public void Parse_MapsOrthoLayerAndMBTilesFormat()
    {
        PackageManifest manifest = PackageManifestParser.Parse(Utf8(TwoPackages));

        RegionPackage ortho = manifest.Packages[1];
        ortho.Layer.Should().Be(PackageLayer.Ortho);
        ortho.Format.Should().Be(PackageFormat.MBTiles);
    }

    [Fact]
    public void Parse_MapsEnumStrings_CaseInsensitively()
    {
        const string lower = """
        { "packages": [ {
            "id": "x", "name": "X", "layer": "dem", "format": "ziptilecache",
            "version": 1, "sizeBytes": 1, "sha256": "00", "url": "https://x" } ] }
        """;

        PackageManifest manifest = PackageManifestParser.Parse(Utf8(lower));

        manifest.Packages[0].Layer.Should().Be(PackageLayer.Dem);
        manifest.Packages[0].Format.Should().Be(PackageFormat.ZipTileCache);
    }

    [Fact]
    public void Parse_ReturnsEmpty_WhenPackagesArrayIsEmpty()
    {
        PackageManifest manifest = PackageManifestParser.Parse(Utf8("""{ "packages": [] }"""));

        manifest.Packages.Should().BeEmpty();
    }

    [Fact]
    public void Parse_Throws_WhenJsonIsMalformed()
    {
        Action act = () => PackageManifestParser.Parse(Utf8("{ not json"));

        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Parse_Throws_WhenPackagesArrayIsMissing()
    {
        Action act = () => PackageManifestParser.Parse(Utf8("""{ "version": 1 }"""));

        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Parse_Throws_WhenAPackageEntryIsMissingARequiredField()
    {
        // No "url" field on the single entry.
        const string missingUrl = """
        { "packages": [ {
            "id": "x", "name": "X", "layer": "Dem", "format": "ZipTileCache",
            "version": 1, "sizeBytes": 1, "sha256": "00" } ] }
        """;

        Action act = () => PackageManifestParser.Parse(Utf8(missingUrl));

        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Parse_SkipsPackage_WhenLayerValueIsUnknown()
    {
        // Forward compatibility: a layer this build doesn't know (e.g. added in a newer app) is skipped,
        // so a newer manifest never breaks an older app. The known package still loads.
        const string mixed = """
        { "packages": [
          { "id": "future", "name": "F", "layer": "Bathymetry", "format": "ZipTileCache",
            "version": 1, "sizeBytes": 1, "sha256": "00", "url": "https://x" },
          { "id": "ortho", "name": "O", "layer": "Ortho", "format": "ZipTileCache",
            "version": 1, "sizeBytes": 1, "sha256": "00", "url": "https://y" } ] }
        """;

        PackageManifest manifest = PackageManifestParser.Parse(Utf8(mixed));

        manifest.Packages.Should().ContainSingle().Which.Id.Should().Be("ortho");
    }

    [Fact]
    public void Parse_Throws_WhenLayerFieldIsMissing()
    {
        // A missing layer field is structural (malformed), not a forward-compat case → still fatal.
        const string noLayer = """
        { "packages": [ {
            "id": "x", "name": "X", "format": "ZipTileCache",
            "version": 1, "sizeBytes": 1, "sha256": "00", "url": "https://x" } ] }
        """;

        Action act = () => PackageManifestParser.Parse(Utf8(noLayer));

        act.Should().Throw<InvalidDataException>();
    }
}