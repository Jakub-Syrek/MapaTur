using System.Numerics;

namespace MapaTur.Application.Terrain;

/// <summary>Klucz grupy batchingu: komórka orto bazowej (tekstura bindowana per draw) × kwadrat GroupCells×GroupCells komórek stron.</summary>
public readonly record struct ScannedRockGroupKey(int OrthoTileIndex, int Gx, int Gy);

/// <summary>Rysowalna strona w trybie „kolor z orto": klucz, komórka orto i AABB świata (wejście trackera batchingu).</summary>
public readonly record struct ScannedRockPageStub(ScannedRockPageKey Key, int OrthoTileIndex, Vector3 Min, Vector3 Max);

/// <summary>Jednostka rysowania: albo cała zbudowana grupa (Group), albo pojedyncza strona (Page) grupy brudnej/niezbudowanej.</summary>
public readonly record struct ScannedRockDrawUnit(ScannedRockGroupKey? Group, ScannedRockPageKey? Page);

/// <summary>
/// Batching stron RMP2 rysowanych programem terenu (pilot „kolor z orto", 2026-09-05). Pomiar ON→OFF w jednej sesji
/// (Rysy 150 m, 391 stron): CPU +11 ms/klatkę = 1173 draw calli (3 passy) + bindy per strona. Strony tej samej komórki
/// orto i tego samego kwadratu komórek są scalane w jeden bufor (jeden draw na pass). Czysta logika — bez GL.
/// </summary>
public static class ScannedRockPageBatcher
{
    public static ScannedRockGroupKey GroupKeyFor(ScannedRockPageKey key, int orthoTileIndex, int groupCells)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(groupCells, 1);
        return new ScannedRockGroupKey(orthoTileIndex, FloorDiv(key.PageX, groupCells), FloorDiv(key.PageY, groupCells));
    }

    /// <summary>Scala paczki w jedną: atrybuty konkatenowane, indeksy przesunięte o bazę wierzchołków każdej paczki.</summary>
    public static TerrainVertexPack Merge(IReadOnlyList<TerrainVertexPack> packs)
    {
        ArgumentNullException.ThrowIfNull(packs);
        ArgumentOutOfRangeException.ThrowIfZero(packs.Count);

        int vertices = 0;
        int indices = 0;
        foreach (TerrainVertexPack p in packs)
        {
            vertices += p.Positions.Length;
            indices += p.Indices.Length;
        }

        var positions = new Vector3[vertices];
        var colors = new uint[vertices];
        var normals = new Vector3[vertices];
        var tex = new float[vertices * 2];
        var detail = new float[vertices];
        var index = new uint[indices];

        int vBase = 0;
        int iBase = 0;
        foreach (TerrainVertexPack p in packs)
        {
            int n = p.Positions.Length;
            Array.Copy(p.Positions, 0, positions, vBase, n);
            Array.Copy(p.Colors, 0, colors, vBase, n);
            Array.Copy(p.Normals, 0, normals, vBase, n);
            Array.Copy(p.TexCoords, 0, tex, vBase * 2, n * 2);
            Array.Copy(p.Detail, 0, detail, vBase, n);
            uint offset = (uint)vBase;
            for (int i = 0; i < p.Indices.Length; i++)
            {
                index[iBase + i] = p.Indices[i] + offset;
            }

            vBase += n;
            iBase += p.Indices.Length;
        }

        return new TerrainVertexPack(positions, colors, normals, tex, detail, index);
    }

    private static int FloorDiv(int value, int divisor) =>
        (int)Math.Floor(value / (double)divisor);
}

/// <summary>
/// Śledzi skład grup na podstawie ZESTAWU RYSOWALNYCH stron (jedna strona na komórkę, wybrany LOD) klatka po klatce:
/// zmiana składu grupy = grupa brudna → rysowana pojedynczymi stronami, aż warstwa GL ją przebuduje (budżet
/// przebudów na klatkę przez TakeDirty) i oznaczy MarkBuilt. Grupy bez stron znikają i są zgłaszane do sprzątania GPU.
/// </summary>
public sealed class ScannedRockPageBatchTracker
{
    private readonly int groupCells;
    private readonly Dictionary<ScannedRockGroupKey, Group> groups = new();
    private readonly List<ScannedRockGroupKey> removed = new();

    public ScannedRockPageBatchTracker(int groupCells)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(groupCells, 1);
        this.groupCells = groupCells;
    }

    public int GroupCells => groupCells;

    public IReadOnlyCollection<ScannedRockGroupKey> Groups => groups.Keys;

    public void Update(IReadOnlyList<ScannedRockPageStub> drawable)
    {
        ArgumentNullException.ThrowIfNull(drawable);
        var wanted = new Dictionary<ScannedRockGroupKey, List<ScannedRockPageStub>>();
        foreach (ScannedRockPageStub stub in drawable)
        {
            ScannedRockGroupKey gk = ScannedRockPageBatcher.GroupKeyFor(stub.Key, stub.OrthoTileIndex, groupCells);
            if (!wanted.TryGetValue(gk, out List<ScannedRockPageStub>? list))
            {
                list = new List<ScannedRockPageStub>();
                wanted[gk] = list;
            }

            list.Add(stub);
        }

        foreach (ScannedRockGroupKey gk in groups.Keys.ToList())
        {
            if (!wanted.ContainsKey(gk))
            {
                groups.Remove(gk);
                removed.Add(gk);
            }
        }

        foreach ((ScannedRockGroupKey gk, List<ScannedRockPageStub> stubs) in wanted)
        {
            if (!groups.TryGetValue(gk, out Group? group))
            {
                groups[gk] = new Group(stubs);
                continue;
            }

            if (!group.SameMembers(stubs))
            {
                group.Replace(stubs);
            }
        }
    }

    public IReadOnlyList<ScannedRockPageKey> MembersOf(ScannedRockGroupKey key) =>
        groups.TryGetValue(key, out Group? g) ? g.Keys : Array.Empty<ScannedRockPageKey>();

    public bool IsDirty(ScannedRockGroupKey key) => groups.TryGetValue(key, out Group? g) && g.Dirty;

    public bool IsBuilt(ScannedRockGroupKey key) => groups.TryGetValue(key, out Group? g) && g.Built && !g.Dirty;

    /// <summary>Do `max` brudnych grup do przebudowy (flaga zostaje do MarkBuilt — nieudana przebudowa wraca w następnej klatce).</summary>
    public IReadOnlyList<ScannedRockGroupKey> TakeDirty(int max)
    {
        var result = new List<ScannedRockGroupKey>();
        foreach ((ScannedRockGroupKey gk, Group g) in groups)
        {
            if (result.Count >= max)
            {
                break;
            }

            if (g.Dirty)
            {
                result.Add(gk);
            }
        }

        return result;
    }

    public void MarkBuilt(ScannedRockGroupKey key)
    {
        if (groups.TryGetValue(key, out Group? g))
        {
            g.Dirty = false;
            g.Built = true;
        }
    }

    /// <summary>Grupy usunięte od ostatniego wywołania (warstwa GL kasuje ich bufory); lista jest czyszczona.</summary>
    public IReadOnlyList<ScannedRockGroupKey> TakeRemoved()
    {
        if (removed.Count == 0)
        {
            return Array.Empty<ScannedRockGroupKey>();
        }

        var copy = removed.ToArray();
        removed.Clear();
        return copy;
    }

    public (Vector3 Min, Vector3 Max) BoundsOf(ScannedRockGroupKey key)
    {
        if (!groups.TryGetValue(key, out Group? g) || g.Stubs.Count == 0)
        {
            return (Vector3.Zero, Vector3.Zero);
        }

        Vector3 min = g.Stubs[0].Min;
        Vector3 max = g.Stubs[0].Max;
        for (int i = 1; i < g.Stubs.Count; i++)
        {
            min = Vector3.Min(min, g.Stubs[i].Min);
            max = Vector3.Max(max, g.Stubs[i].Max);
        }

        return (min, max);
    }

    /// <summary>Zbudowane i czyste grupy w całości; brudne/niezbudowane — ich strony pojedynczo (poprawność podczas streamingu).</summary>
    public IEnumerable<ScannedRockDrawUnit> DrawUnits()
    {
        foreach ((ScannedRockGroupKey gk, Group g) in groups)
        {
            if (g.Built && !g.Dirty)
            {
                yield return new ScannedRockDrawUnit(gk, null);
                continue;
            }

            foreach (ScannedRockPageStub stub in g.Stubs)
            {
                yield return new ScannedRockDrawUnit(null, stub.Key);
            }
        }
    }

    public void Clear()
    {
        foreach (ScannedRockGroupKey gk in groups.Keys)
        {
            removed.Add(gk);
        }

        groups.Clear();
    }

    private sealed class Group
    {
        private HashSet<ScannedRockPageKey> keySet;

        public Group(List<ScannedRockPageStub> stubs)
        {
            Stubs = stubs;
            keySet = new HashSet<ScannedRockPageKey>(stubs.Select(s => s.Key));
            Keys = stubs.Select(s => s.Key).ToArray();
            Dirty = true;
            Built = false;
        }

        public List<ScannedRockPageStub> Stubs { get; private set; }

        public ScannedRockPageKey[] Keys { get; private set; }

        public bool Dirty { get; set; }

        public bool Built { get; set; }

        public bool SameMembers(List<ScannedRockPageStub> stubs)
        {
            if (stubs.Count != keySet.Count)
            {
                return false;
            }

            foreach (ScannedRockPageStub s in stubs)
            {
                if (!keySet.Contains(s.Key))
                {
                    return false;
                }
            }

            return true;
        }

        public void Replace(List<ScannedRockPageStub> stubs)
        {
            Stubs = stubs;
            keySet = new HashSet<ScannedRockPageKey>(stubs.Select(s => s.Key));
            Keys = stubs.Select(s => s.Key).ToArray();
            Dirty = true;
        }
    }
}
