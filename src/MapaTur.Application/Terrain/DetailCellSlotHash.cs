namespace MapaTur.Application.Terrain;

/// <summary>
/// One resident detail-grid cell and the GPU-array slot that owns its texture.
/// Alpha is quantized because it is only the short promote fade (0..1 in the shader).
/// </summary>
public readonly record struct DetailCellSlot(int Ci, int Cj, int Slot, byte Alpha);

/// <summary>
/// A bounded open-addressed hash table uploaded directly as <c>ivec4[]</c>:
/// <c>(ci, cj, textureSlot, alphaByte)</c>. It replaces the fragment shader's linear scan over every
/// resident cell. The table builder searches for a seed whose longest probe chain fits the fixed shader
/// bound, so lookup cost cannot grow with the residency cap.
/// </summary>
public sealed class DetailCellSlotHash
{
    private const int SeedAttempts = 4096;

    private DetailCellSlotHash(int[] packedEntries, int count, int maxProbe, uint seed)
    {
        PackedEntries = packedEntries;
        Count = count;
        MaxProbeUsed = maxProbe;
        Seed = seed;
    }

    /// <summary>Flat <c>ivec4</c>-ready storage: ci, cj, slot, alphaByte.</summary>
    public int[] PackedEntries { get; }

    public int Count { get; }

    /// <summary>Maximum number of table entries inspected for any inserted key.</summary>
    public int MaxProbeUsed { get; }

    public uint Seed { get; }

    public int TableSize => PackedEntries.Length / 4;

    public static DetailCellSlotHash Build(
        IReadOnlyCollection<DetailCellSlot> cells,
        int tableSize,
        int maxProbe)
    {
        ArgumentNullException.ThrowIfNull(cells);
        if (tableSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tableSize));
        }

        if (maxProbe <= 0 || maxProbe > tableSize)
        {
            throw new ArgumentOutOfRangeException(nameof(maxProbe));
        }

        if (cells.Count > tableSize)
        {
            throw new ArgumentException("The hash table cannot hold more cells than entries.", nameof(cells));
        }

        var unique = new HashSet<(int Ci, int Cj)>();
        foreach (DetailCellSlot cell in cells)
        {
            if (cell.Slot < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(cells), "Texture slots must be non-negative.");
            }

            if (!unique.Add((cell.Ci, cell.Cj)))
            {
                throw new ArgumentException($"Duplicate detail cell ({cell.Ci},{cell.Cj}).", nameof(cells));
            }
        }

        DetailCellSlot[] source = cells.ToArray();
        var entries = new int[tableSize * 4];
        (uint seed, int longest) = Fill(source, entries, maxProbe);
        return new DetailCellSlotHash(entries, cells.Count, longest, seed);
    }

    /// <summary>
    /// Allocation-free renderer path. <paramref name="packedEntries"/> must contain four integers per table
    /// entry. The method clears it and searches for a bounded-probe seed.
    /// </summary>
    public static (uint Seed, int MaxProbeUsed) Fill(
        ReadOnlySpan<DetailCellSlot> cells,
        Span<int> packedEntries,
        int maxProbe)
    {
        if (packedEntries.Length == 0 || packedEntries.Length % 4 != 0)
        {
            throw new ArgumentException("Packed table storage must contain four integers per entry.", nameof(packedEntries));
        }

        int tableSize = packedEntries.Length / 4;
        if (maxProbe <= 0 || maxProbe > tableSize)
        {
            throw new ArgumentOutOfRangeException(nameof(maxProbe));
        }

        if (cells.Length > tableSize)
        {
            throw new ArgumentException("The hash table cannot hold more cells than entries.", nameof(cells));
        }

        for (uint seed = 0; seed < SeedAttempts; seed++)
        {
            Clear(packedEntries);
            int longest = 0;
            bool fits = true;
            foreach (DetailCellSlot cell in cells)
            {
                int start = (int)(Hash(cell.Ci, cell.Cj, seed) % (uint)tableSize);
                bool placed = false;
                for (int probe = 0; probe < maxProbe; probe++)
                {
                    int entry = (start + probe) % tableSize;
                    int offset = entry * 4;
                    if (packedEntries[offset + 2] >= 0)
                    {
                        continue;
                    }

                    packedEntries[offset] = cell.Ci;
                    packedEntries[offset + 1] = cell.Cj;
                    packedEntries[offset + 2] = cell.Slot;
                    packedEntries[offset + 3] = cell.Alpha;
                    longest = Math.Max(longest, probe + 1);
                    placed = true;
                    break;
                }

                if (!placed)
                {
                    fits = false;
                    break;
                }
            }

            if (fits)
            {
                return (seed, longest);
            }
        }

        throw new InvalidOperationException(
            $"Could not place {cells.Length} detail cells in {tableSize} entries with max probe {maxProbe}.");
    }

    public bool TryGet(int ci, int cj, out int slot, out byte alpha)
    {
        int start = (int)(Hash(ci, cj, Seed) % (uint)TableSize);
        for (int probe = 0; probe < MaxProbeUsed; probe++)
        {
            int offset = ((start + probe) % TableSize) * 4;
            int storedSlot = PackedEntries[offset + 2];
            if (storedSlot < 0)
            {
                break;
            }

            if (PackedEntries[offset] == ci && PackedEntries[offset + 1] == cj)
            {
                slot = storedSlot;
                alpha = (byte)PackedEntries[offset + 3];
                return true;
            }
        }

        slot = -1;
        alpha = 0;
        return false;
    }

    /// <summary>Must stay bit-identical to <c>detailCellHash</c> in the terrain fragment shader.</summary>
    public static uint Hash(int ci, int cj, uint seed)
    {
        unchecked
        {
            uint h = ((uint)ci * 0x9E3779B1u)
                   ^ ((uint)cj * 0x85EBCA77u)
                   ^ (seed * 0xC2B2AE3Du);
            h ^= h >> 16;
            h *= 0x7FEB352Du;
            h ^= h >> 15;
            h *= 0x846CA68Bu;
            h ^= h >> 16;
            return h;
        }
    }

    private static void Clear(Span<int> entries)
    {
        entries.Clear();
        for (int i = 0; i < entries.Length; i += 4)
        {
            entries[i + 2] = -1;
        }
    }
}
