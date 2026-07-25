using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Indeks warstwy `.opk` (ARCHITEKTURA-STREAMING §2.3): jedna binarna lista pokrytych cel + bitmapa
/// pokrycia KAFLI, ładowana raz na starcie — planner i coverage-gate znają dostępność bez dotykania
/// systemu plików. Kontrakt: roundtrip zapis/odczyt, szybkie zapytania (cela pokryta? kafel pokryty?),
/// odrzut pliku obcego/rozdartego, zapis atomowy.
/// </summary>
public sealed class OrthoPackIndexTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "opkidx-tests-" + Guid.NewGuid().ToString("N"));

    public OrthoPackIndexTests() => Directory.CreateDirectory(dir);

    public void Dispose()
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
    }

    private string IndexPath => Path.Combine(dir, "index.bin");

    private static OrthoPackIndex.CellEntry Cell(int ci, int cj, int pages, long bytes)
        => new(ci, cj, CellPx: 4096, PageCount: (ushort)pages, FileBytes: bytes);

    [Fact]
    public void WriteThenLoad_RoundTripsCellsAndTileBitmap()
    {
        var cells = new[] { Cell(10, 20, 65, 1_000_000), Cell(11, 20, 64, 900_000) };
        var coveredTiles = new (int Ti, int Tj)[] { (80, 160), (81, 160), (88, 165) };

        OrthoPackIndex.Write(IndexPath, tilesPerCell: 8, cells, coveredTiles);
        OrthoPackIndex idx = OrthoPackIndex.Load(IndexPath)!;

        idx.Cells.Should().HaveCount(2);
        idx.IsCellCovered(10, 20).Should().BeTrue();
        idx.IsCellCovered(12, 20).Should().BeFalse();
        idx.IsTileCovered(81, 160).Should().BeTrue();
        idx.IsTileCovered(82, 160).Should().BeFalse();
    }

    [Fact]
    public void Load_MissingFile_ReturnsNull()
    {
        OrthoPackIndex.Load(Path.Combine(dir, "absent.bin")).Should().BeNull();
    }

    [Fact]
    public void Load_Truncated_IsRejected()
    {
        OrthoPackIndex.Write(IndexPath, 8, new[] { Cell(1, 2, 10, 100) }, new (int, int)[] { (8, 16) });
        using (FileStream fs = File.OpenWrite(IndexPath))
        {
            fs.SetLength(fs.Length - 3);
        }

        OrthoPackIndex.Load(IndexPath).Should().BeNull();
    }

    [Fact]
    public void Write_LeavesNoTempFileBehind()
    {
        OrthoPackIndex.Write(IndexPath, 8, new[] { Cell(1, 2, 10, 100) }, Array.Empty<(int, int)>());

        Directory.GetFiles(dir).Should().ContainSingle(f => f.EndsWith("index.bin", StringComparison.Ordinal));
    }

    [Fact]
    public void CellEntries_PreserveMetadataForPlanner()
    {
        OrthoPackIndex.Write(IndexPath, 16, new[] { Cell(226, 120, 257, 45_000_000) }, new (int, int)[] { (3616, 1920) });
        OrthoPackIndex idx = OrthoPackIndex.Load(IndexPath)!;

        OrthoPackIndex.CellEntry e = idx.Cells.Single();
        e.Ci.Should().Be(226);
        e.Cj.Should().Be(120);
        e.PageCount.Should().Be(257);
        e.FileBytes.Should().Be(45_000_000);
    }
}
