using System.Buffers.Binary;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Pakiet stron `.opk` — jednostka PLIKU streamingu orto (ARCHITEKTURA-STREAMING §2.2). Plik = jedna cela;
/// wewnątrz strony BC1: zwykłe (kafel 512 px, mipy 0-1) i jedna strona TAIL (poziomy celi 2048↓), fizycznie
/// PIERWSZA w payloadzie, żeby sekwencyjny odczyt nagłówek→tail dawał celę w jakości ~0,2 m bez seeków.
/// Zapis wyłącznie w narzędziu bake (runtime NIGDY nie pisze), atomowy (tmp + File.Move). Odczyt waliduje
/// magic/version/px/długość przy otwarciu i crc32 per strona przy odczycie — plik rozdarty, obcy lub
/// przekłamany jest ODRZUCANY w całości albo per strona, nigdy użyty częściowo. Pole zstdBytes jest w
/// formacie od v1; ta wersja pisze strony bez kompresji (zstdBytes = 0) — kompresja to optymalizacja
/// rozmiaru dysku, nie warunek bramki.
/// </summary>
public sealed class OrthoPagePack : IDisposable
{
    /// <summary>Identyfikator strony TAIL w TOC (spec: 0xFFFF).</summary>
    public const ushort TailPageId = 0xFFFF;

    private const uint Magic = 0x504F544D; // "MTOP" little-endian
    private const ushort Version = 1;
    private const ushort FormatBc1 = 1;
    private const int HeaderBytes = 32;
    private const int TocEntryBytes = 32;
    private const int PagePx = 512;

    /// <summary>Jedna strona do zapisu: payload BC1 + hash źródła (przyrostowy bake porównuje per strona).</summary>
    public readonly record struct PageData(ushort PageId, byte Level, byte[] Payload, ulong SrcHash);

    /// <summary>Wpis TOC po odczycie.</summary>
    public readonly record struct Entry(ushort PageId, byte Level, long Offset, int RawBytes, int ZstdBytes, uint Crc32, ulong SrcHash);

    private readonly FileStream stream;
    private readonly Entry[] entries;
    private readonly Dictionary<ushort, int> byPageId;

    /// <summary>Rozdzielczość celi w px (8192 det05 / 4096 det25|det1m).</summary>
    public int CellPx { get; }

    /// <summary>Liczba stron w pakiecie.</summary>
    public int PageCount => entries.Length;

    /// <summary>TOC w kolejności fizycznej payloadu (tail pierwszy).</summary>
    public IReadOnlyList<Entry> Entries => entries;

    private OrthoPagePack(FileStream stream, int cellPx, Entry[] entries)
    {
        this.stream = stream;
        CellPx = cellPx;
        this.entries = entries;
        byPageId = new Dictionary<ushort, int>(entries.Length);
        for (int i = 0; i < entries.Length; i++)
        {
            byPageId[entries[i].PageId] = i;
        }
    }

    /// <summary>Atomowy zapis pakietu: tail wymuszony na początek payloadu niezależnie od kolejności wejścia.</summary>
    public static void Write(string path, int cellPx, IEnumerable<PageData> pages)
    {
        List<PageData> ordered = pages.OrderBy(p => p.PageId == TailPageId ? 0 : 1).ToList();
        ArgumentOutOfRangeException.ThrowIfGreaterThan(ordered.Count, ushort.MaxValue - 1);
        string? parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        long payloadStart = HeaderBytes + ((long)ordered.Count * TocEntryBytes);
        string tmp = path + ".tmp-" + Environment.CurrentManagedThreadId;
        using (FileStream fs = new(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            Span<byte> header = stackalloc byte[HeaderBytes];
            BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
            BinaryPrimitives.WriteUInt16LittleEndian(header[4..], Version);
            BinaryPrimitives.WriteUInt16LittleEndian(header[6..], FormatBc1);
            BinaryPrimitives.WriteInt32LittleEndian(header[8..], cellPx);
            BinaryPrimitives.WriteInt32LittleEndian(header[12..], PagePx);
            BinaryPrimitives.WriteUInt16LittleEndian(header[16..], (ushort)ordered.Count);
            BinaryPrimitives.WriteUInt16LittleEndian(header[18..], 0); // flags
            BinaryPrimitives.WriteUInt64LittleEndian(header[20..], 0); // srcManifestHash (bake wpisuje później)
            BinaryPrimitives.WriteUInt32LittleEndian(header[28..], 0); // reserved
            fs.Write(header);

            Span<byte> toc = stackalloc byte[TocEntryBytes];
            long offset = payloadStart;
            foreach (PageData p in ordered)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(toc, p.PageId);
                toc[2] = p.Level;
                toc[3] = 0;
                BinaryPrimitives.WriteInt64LittleEndian(toc[4..], offset);
                BinaryPrimitives.WriteInt32LittleEndian(toc[12..], p.Payload.Length);
                BinaryPrimitives.WriteInt32LittleEndian(toc[16..], 0); // zstdBytes = 0 → bez kompresji
                BinaryPrimitives.WriteUInt32LittleEndian(toc[20..], Crc32(p.Payload));
                BinaryPrimitives.WriteUInt64LittleEndian(toc[24..], p.SrcHash);
                fs.Write(toc);
                offset += p.Payload.Length;
            }

            foreach (PageData p in ordered)
            {
                fs.Write(p.Payload);
            }
        }

        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Otwiera i waliduje pakiet. Null przy JAKIMKOLWIEK niedopasowaniu (brak, obcy plik, zła
    /// rozdzielczość, obcięty payload) — czytelnik spada na niższą warstwę, nigdy nie używa częściowo.</summary>
    public static OrthoPagePack? Open(string path, int expectedCellPx)
    {
        FileStream? fs = null;
        try
        {
            fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Span<byte> header = stackalloc byte[HeaderBytes];
            if (fs.Length < HeaderBytes)
            {
                fs.Dispose();
                return null;
            }

            fs.ReadExactly(header);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic
                || BinaryPrimitives.ReadUInt16LittleEndian(header[4..]) != Version
                || BinaryPrimitives.ReadUInt16LittleEndian(header[6..]) != FormatBc1
                || BinaryPrimitives.ReadInt32LittleEndian(header[8..]) != expectedCellPx
                || BinaryPrimitives.ReadInt32LittleEndian(header[12..]) != PagePx)
            {
                fs.Dispose();
                return null;
            }

            int count = BinaryPrimitives.ReadUInt16LittleEndian(header[16..]);
            long tocBytes = (long)count * TocEntryBytes;
            if (fs.Length < HeaderBytes + tocBytes)
            {
                fs.Dispose();
                return null;
            }

            var entries = new Entry[count];
            Span<byte> toc = stackalloc byte[TocEntryBytes];
            long payloadEnd = HeaderBytes + tocBytes;
            for (int i = 0; i < count; i++)
            {
                fs.ReadExactly(toc);
                entries[i] = new Entry(
                    BinaryPrimitives.ReadUInt16LittleEndian(toc),
                    toc[2],
                    BinaryPrimitives.ReadInt64LittleEndian(toc[4..]),
                    BinaryPrimitives.ReadInt32LittleEndian(toc[12..]),
                    BinaryPrimitives.ReadInt32LittleEndian(toc[16..]),
                    BinaryPrimitives.ReadUInt32LittleEndian(toc[20..]),
                    BinaryPrimitives.ReadUInt64LittleEndian(toc[24..]));
                long end = entries[i].Offset + (entries[i].ZstdBytes > 0 ? entries[i].ZstdBytes : entries[i].RawBytes);
                if (end > payloadEnd)
                {
                    payloadEnd = end;
                }
            }

            if (fs.Length < payloadEnd)
            {
                fs.Dispose();
                return null; // rozdarty zapis — payload krótszy niż deklaruje TOC
            }

            return new OrthoPagePack(fs, expectedCellPx, entries);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            fs?.Dispose();
            return null;
        }
    }

    /// <summary>Czyta i waliduje (crc32) jedną stronę. False = strona nieobecna w TOC albo przekłamana —
    /// wywołujący traktuje jak brak pokrycia; przekłamana strona NIGDY nie trafia do GPU.</summary>
    public bool TryReadPage(ushort pageId, out byte[] payload)
    {
        payload = Array.Empty<byte>();
        if (!byPageId.TryGetValue(pageId, out int idx))
        {
            return false;
        }

        Entry e = entries[idx];
        byte[] buf = new byte[e.RawBytes];
        try
        {
            stream.Seek(e.Offset, SeekOrigin.Begin);
            stream.ReadExactly(buf);
        }
        catch (IOException)
        {
            return false;
        }

        if (Crc32(buf) != e.Crc32)
        {
            return false;
        }

        payload = buf;
        return true;
    }

    public void Dispose() => stream.Dispose();

    // CRC-32 (IEEE 802.3, odbity wielomian 0xEDB88320) — tablicowy, bez zależności NuGet.
    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[i] = c;
        }

        return table;
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFu;
    }
}
