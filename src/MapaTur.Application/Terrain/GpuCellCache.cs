using System.Buffers.Binary;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Disk cache of GPU-ready ortho cells — BC1 with the FULL mip chain in one file (2026-07-23, game-grade
/// streaming). The compose worker (or an offline bake) writes a cell once; every later visit reads it back
/// in ~15 ms and uploads compressed, instead of re-decoding hundreds of WebP tiles (measured 3–5 s a cell).
/// Format: 16-byte header {magic "MTGC", version u16, format u16 (1 = BC1), px i32, reserved i32} + the
/// packed per-level BC1 payloads, level 0 first, down to 1×1. Writes are atomic (tmp + File.Move) so a torn
/// write never becomes visible; reads validate magic/version/px/length and REJECT on any mismatch — the
/// caller falls back to the compose path rather than uploading garbage.
/// </summary>
public static class GpuCellCache
{
    private const uint Magic = 0x4347544D; // "MTGC" little-endian
    private const ushort Version = 1;
    private const ushort FormatBc1 = 1;
    private const int HeaderBytes = 16;

    /// <summary>Total BC1 bytes for a square <paramref name="px"/> cell including every mip level down to 1×1.</summary>
    public static int ChainSize(int px)
    {
        int total = 0;
        for (int p = px; p >= 1; p >>= 1)
        {
            total += Bc1Encoder.EncodedSize(p, p);
        }

        return total;
    }

    /// <summary>Atomically writes a cell's BC1 chain: tmp file + move, so readers never see a partial file.</summary>
    public static void Write(string path, int px, ReadOnlySpan<byte> chain)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(chain.Length, ChainSize(px));
        string? parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        Span<byte> header = stackalloc byte[HeaderBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..], Version);
        BinaryPrimitives.WriteUInt16LittleEndian(header[6..], FormatBc1);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], px);
        BinaryPrimitives.WriteInt32LittleEndian(header[12..], 0);

        string tmp = path + ".tmp-" + Environment.CurrentManagedThreadId;
        using (FileStream fs = new(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(header);
            fs.Write(chain[..ChainSize(px)]);
        }

        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Reads a cell's BC1 chain into <paramref name="dest"/>. False on ANY mismatch (missing,
    /// truncated, wrong px/format/version) — the caller composes instead; never partial data.</summary>
    public static bool TryRead(string path, int expectedPx, byte[] dest, out int length)
    {
        length = 0;
        int chain = ChainSize(expectedPx);
        if (dest.Length < chain)
        {
            return false;
        }

        try
        {
            using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (fs.Length != HeaderBytes + chain)
            {
                return false;
            }

            Span<byte> header = stackalloc byte[HeaderBytes];
            fs.ReadExactly(header);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic
                || BinaryPrimitives.ReadUInt16LittleEndian(header[4..]) != Version
                || BinaryPrimitives.ReadUInt16LittleEndian(header[6..]) != FormatBc1
                || BinaryPrimitives.ReadInt32LittleEndian(header[8..]) != expectedPx)
            {
                return false;
            }

            fs.ReadExactly(dest.AsSpan(0, chain));
            length = chain;
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
