using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Disk cache of GPU-ready (BC1 + full mip chain) ortho cells (2026-07-23): written once by the compose
/// worker / offline bake, read back in ~15 ms on every later visit instead of a 3–5 s WebP decode storm.
/// Contract under test: chain sizing matches BC1 mip arithmetic, a write/read round-trip is bit-exact,
/// corrupted or mismatched files are REJECTED (loader falls back to compose — never uploads garbage),
/// and writes are atomic (no partial file is ever visible under the final name).
/// </summary>
public sealed class GpuCellCacheTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "mtgc-tests-" + Guid.NewGuid().ToString("N"));

    public GpuCellCacheTests() => Directory.CreateDirectory(dir);

    public void Dispose()
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void ChainSize_16px_SumsAllMipLevels()
    {
        // 16→8→4→2→1: blocks 16+4+1+1+1 = 23 × 8 B.
        GpuCellCache.ChainSize(16).Should().Be(23 * 8);
    }

    [Fact]
    public void WriteThenTryRead_RoundTripsBitExact()
    {
        int size = GpuCellCache.ChainSize(16);
        byte[] chain = new byte[size];
        new Random(7).NextBytes(chain);
        string path = Path.Combine(dir, "3_5.mtgc");

        GpuCellCache.Write(path, 16, chain);
        byte[] dest = new byte[size];
        bool ok = GpuCellCache.TryRead(path, 16, dest, out int length);

        ok.Should().BeTrue();
        length.Should().Be(size);
        dest.AsSpan(0, length).SequenceEqual(chain).Should().BeTrue();
    }

    [Fact]
    public void TryRead_MissingFile_IsFalse()
    {
        GpuCellCache.TryRead(Path.Combine(dir, "absent.mtgc"), 16, new byte[64], out _).Should().BeFalse();
    }

    [Fact]
    public void TryRead_PxMismatch_IsRejected()
    {
        string path = Path.Combine(dir, "px.mtgc");
        GpuCellCache.Write(path, 16, new byte[GpuCellCache.ChainSize(16)]);

        GpuCellCache.TryRead(path, 32, new byte[GpuCellCache.ChainSize(32)], out _)
            .Should().BeFalse("a cell baked at another resolution must fall back to compose, not upload garbage");
    }

    [Fact]
    public void TryRead_TruncatedFile_IsRejected()
    {
        string path = Path.Combine(dir, "trunc.mtgc");
        GpuCellCache.Write(path, 16, new byte[GpuCellCache.ChainSize(16)]);
        using (FileStream fs = File.OpenWrite(path))
        {
            fs.SetLength(fs.Length - 5); // simulate a torn write / disk-full stub
        }

        GpuCellCache.TryRead(path, 16, new byte[GpuCellCache.ChainSize(16)], out _).Should().BeFalse();
    }

    [Fact]
    public void Write_LeavesNoTempFileBehind()
    {
        string path = Path.Combine(dir, "atomic.mtgc");
        GpuCellCache.Write(path, 16, new byte[GpuCellCache.ChainSize(16)]);

        Directory.GetFiles(dir).Should().ContainSingle(f => f.EndsWith("atomic.mtgc", StringComparison.Ordinal));
    }
}
