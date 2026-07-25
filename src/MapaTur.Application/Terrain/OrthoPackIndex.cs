using System.Buffers.Binary;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Indeks warstwy pakietów `.opk` (ARCHITEKTURA-STREAMING §2.3): binarna lista pokrytych cel
/// {ci,cj,cellPx,pageCount,fileBytes} + bitmapa pokrycia KAFLI (klucz = (ti,tj) w siatce kafli warstwy).
/// Pisany przez bake, ładowany RAZ na starcie — planner i shaderowy coverage-gate odpowiadają na
/// „czy cela/kafel jest pokryty" bez dotykania systemu plików (zastępuje skan katalogów WebP).
/// Zapis atomowy, odczyt walidowany (magic/version/długość) — plik obcy albo rozdarty ⇒ null.
/// </summary>
public sealed class OrthoPackIndex
{
    private const uint Magic = 0x49544F4Du; // "MOTI"
    private const ushort Version = 1;
    private const int HeaderBytes = 24;
    private const int CellEntryBytes = 20;

    /// <summary>Wpis pokrytej celi — metadane dla plannera (bez otwierania `.opk`).</summary>
    public readonly record struct CellEntry(int Ci, int Cj, int CellPx, ushort PageCount, long FileBytes);

    private readonly CellEntry[] cells;
    private readonly HashSet<long> cellSet;
    private readonly HashSet<long> tileSet;

    /// <summary>Kafli na bok celi (det05: 16, det25/det1m: 8).</summary>
    public int TilesPerCell { get; }

    /// <summary>Wszystkie pokryte cele warstwy.</summary>
    public IReadOnlyList<CellEntry> Cells => cells;

    private OrthoPackIndex(int tilesPerCell, CellEntry[] cells, HashSet<long> tileSet)
    {
        TilesPerCell = tilesPerCell;
        this.cells = cells;
        this.tileSet = tileSet;
        cellSet = new HashSet<long>(cells.Length);
        foreach (CellEntry c in cells)
        {
            cellSet.Add(Key(c.Ci, c.Cj));
        }
    }

    private static long Key(int a, int b) => ((long)a << 32) | (uint)b;

    /// <summary>Czy cela (ci,cj) ma pakiet `.opk`.</summary>
    public bool IsCellCovered(int ci, int cj) => cellSet.Contains(Key(ci, cj));

    /// <summary>Czy kafel źródłowy (ti,tj) istniał w bake'u (bitmapa pokrycia — shaderowy coverage-gate).</summary>
    public bool IsTileCovered(int ti, int tj) => tileSet.Contains(Key(ti, tj));

    /// <summary>Atomowy zapis indeksu warstwy.</summary>
    public static void Write(
        string path, int tilesPerCell, IReadOnlyList<CellEntry> cells, IReadOnlyList<(int Ti, int Tj)> coveredTiles)
    {
        string? parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        string tmp = path + ".tmp-" + Environment.CurrentManagedThreadId;
        using (FileStream fs = new(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            Span<byte> header = stackalloc byte[HeaderBytes];
            BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
            BinaryPrimitives.WriteUInt16LittleEndian(header[4..], Version);
            BinaryPrimitives.WriteUInt16LittleEndian(header[6..], (ushort)tilesPerCell);
            BinaryPrimitives.WriteInt32LittleEndian(header[8..], cells.Count);
            BinaryPrimitives.WriteInt32LittleEndian(header[12..], coveredTiles.Count);
            BinaryPrimitives.WriteUInt64LittleEndian(header[16..], 0); // reserved
            fs.Write(header);

            Span<byte> row = stackalloc byte[CellEntryBytes];
            foreach (CellEntry c in cells)
            {
                BinaryPrimitives.WriteInt32LittleEndian(row, c.Ci);
                BinaryPrimitives.WriteInt32LittleEndian(row[4..], c.Cj);
                BinaryPrimitives.WriteInt32LittleEndian(row[8..], c.CellPx);
                BinaryPrimitives.WriteUInt16LittleEndian(row[12..], c.PageCount);
                row[14] = 0; row[15] = 0;
                BinaryPrimitives.WriteUInt32LittleEndian(row[16..], (uint)Math.Min(c.FileBytes, uint.MaxValue));
                fs.Write(row);
            }

            Span<byte> tile = stackalloc byte[8];
            foreach ((int ti, int tj) in coveredTiles)
            {
                BinaryPrimitives.WriteInt32LittleEndian(tile, ti);
                BinaryPrimitives.WriteInt32LittleEndian(tile[4..], tj);
                fs.Write(tile);
            }
        }

        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Ładuje i waliduje indeks; null przy braku/uszkodzeniu (czytelnik spada na skan FS albo brak warstwy).</summary>
    public static OrthoPackIndex? Load(string path)
    {
        try
        {
            using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (fs.Length < HeaderBytes)
            {
                return null;
            }

            Span<byte> header = stackalloc byte[HeaderBytes];
            fs.ReadExactly(header);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic
                || BinaryPrimitives.ReadUInt16LittleEndian(header[4..]) != Version)
            {
                return null;
            }

            int tilesPerCell = BinaryPrimitives.ReadUInt16LittleEndian(header[6..]);
            int cellCount = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
            int tileCount = BinaryPrimitives.ReadInt32LittleEndian(header[12..]);
            long expected = HeaderBytes + ((long)cellCount * CellEntryBytes) + ((long)tileCount * 8);
            if (cellCount < 0 || tileCount < 0 || fs.Length != expected)
            {
                return null;
            }

            var cells = new CellEntry[cellCount];
            Span<byte> row = stackalloc byte[CellEntryBytes];
            for (int i = 0; i < cellCount; i++)
            {
                fs.ReadExactly(row);
                cells[i] = new CellEntry(
                    BinaryPrimitives.ReadInt32LittleEndian(row),
                    BinaryPrimitives.ReadInt32LittleEndian(row[4..]),
                    BinaryPrimitives.ReadInt32LittleEndian(row[8..]),
                    BinaryPrimitives.ReadUInt16LittleEndian(row[12..]),
                    BinaryPrimitives.ReadUInt32LittleEndian(row[16..]));
            }

            var tiles = new HashSet<long>(tileCount);
            Span<byte> tile = stackalloc byte[8];
            for (int i = 0; i < tileCount; i++)
            {
                fs.ReadExactly(tile);
                tiles.Add(Key(
                    BinaryPrimitives.ReadInt32LittleEndian(tile),
                    BinaryPrimitives.ReadInt32LittleEndian(tile[4..])));
            }

            return new OrthoPackIndex(tilesPerCell, cells, tiles);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}