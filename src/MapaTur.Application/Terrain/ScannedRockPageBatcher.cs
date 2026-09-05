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
/// zmiana składu = grupa brudna → rysowana pojedynczymi stronami, aż warstwa GL ją przebuduje i oznaczy MarkBuilt.
/// Przegląd 09-05: przebudowa dopiero po USTANIU zmian (minAgeFrames — podczas dociągania 2 stron/klatkę grupa 16 stron
/// scalała się do 16 razy), budżet w STRONACH (nie grupach), priorytet = widoczne grupy, potem najstarsze zmiany.
/// Grupy bez stron znikają i są zgłaszane do sprzątania GPU. Bez alokacji per klatka przy niezmienionym składzie.
/// </summary>
public sealed class ScannedRockPageBatchTracker
{
    private readonly int groupCells;
    private readonly Dictionary<ScannedRockGroupKey, Group> groups = new();
    private readonly Dictionary<ScannedRockGroupKey, List<ScannedRockPageStub>> wanted = new();
    private readonly List<ScannedRockGroupKey> removed = new();
    private readonly List<ScannedRockGroupKey> scratchKeys = new();
    private readonly List<(ScannedRockGroupKey Key, bool Visible, int Changed, int Pages)> candidates = new();
    private int frameCounter;

    public ScannedRockPageBatchTracker(int groupCells)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(groupCells, 1);
        this.groupCells = groupCells;
    }

    public int GroupCells => groupCells;

    public IReadOnlyCollection<ScannedRockGroupKey> Groups => groups.Keys;

    public void Update(IReadOnlyList<ScannedRockPageStub> drawable) => Update(drawable, ++frameCounter);

    public void Update(IReadOnlyList<ScannedRockPageStub> drawable, int frame)
    {
        ArgumentNullException.ThrowIfNull(drawable);
        frameCounter = frame;
        foreach (List<ScannedRockPageStub> list in wanted.Values)
        {
            list.Clear();
        }

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

        scratchKeys.Clear();
        foreach (ScannedRockGroupKey gk in groups.Keys)
        {
            if (!wanted.TryGetValue(gk, out List<ScannedRockPageStub>? list) || list.Count == 0)
            {
                scratchKeys.Add(gk);
            }
        }

        foreach (ScannedRockGroupKey gk in scratchKeys)
        {
            groups.Remove(gk);
            removed.Add(gk);
        }

        foreach ((ScannedRockGroupKey gk, List<ScannedRockPageStub> stubs) in wanted)
        {
            if (stubs.Count == 0)
            {
                continue;
            }

            if (!groups.TryGetValue(gk, out Group? group))
            {
                groups[gk] = new Group(stubs, frame);
                continue;
            }

            if (!group.SameMembers(stubs))
            {
                group.Replace(stubs, frame);
            }
        }
    }

    public IReadOnlyList<ScannedRockPageKey> MembersOf(ScannedRockGroupKey key) =>
        groups.TryGetValue(key, out Group? g) ? g.Keys : Array.Empty<ScannedRockPageKey>();

    public int MemberCount(ScannedRockGroupKey key) => groups.TryGetValue(key, out Group? g) ? g.Stubs.Count : 0;

    public bool IsDirty(ScannedRockGroupKey key) => groups.TryGetValue(key, out Group? g) && g.Dirty;

    public bool IsBuilt(ScannedRockGroupKey key) => groups.TryGetValue(key, out Group? g) && g.Built && !g.Dirty;

    /// <summary>Do `max` brudnych grup (kolejność słownikowa, bez debounce) — flaga zostaje do MarkBuilt.</summary>
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

    /// <summary>
    /// Brudne grupy do przebudowy: tylko te, których skład nie zmienił się od ≥ minAgeFrames klatek (debounce),
    /// najpierw widoczne (isVisible), w obrębie tego najstarsza zmiana pierwsza; budżet liczony w STRONACH członków
    /// (maxPages), ale zawsze ≥ 1 grupa. Flaga brudna zostaje do MarkBuilt.
    /// </summary>
    public IReadOnlyList<ScannedRockGroupKey> TakeDirty(int maxPages, int minAgeFrames, int frame, Func<ScannedRockGroupKey, bool>? isVisible = null)
    {
        candidates.Clear();
        foreach ((ScannedRockGroupKey gk, Group g) in groups)
        {
            if (!g.Dirty || frame - g.LastChangedFrame < minAgeFrames)
            {
                continue;
            }

            candidates.Add((gk, isVisible?.Invoke(gk) ?? true, g.LastChangedFrame, g.Stubs.Count));
        }

        if (candidates.Count == 0)
        {
            return Array.Empty<ScannedRockGroupKey>();
        }

        candidates.Sort(static (a, b) =>
        {
            int v = b.Visible.CompareTo(a.Visible); // widoczne (true) przed niewidocznymi
            return v != 0 ? v : a.Changed.CompareTo(b.Changed);
        });

        var result = new List<ScannedRockGroupKey>();
        int pages = 0;
        foreach ((ScannedRockGroupKey key, bool _, int _, int count) in candidates)
        {
            if (result.Count > 0 && pages + count > maxPages)
            {
                break;
            }

            result.Add(key);
            pages += count;
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
        foreach (List<ScannedRockPageStub> list in wanted.Values)
        {
            list.Clear();
        }
    }

    private sealed class Group
    {
        private readonly HashSet<ScannedRockPageKey> keySet = new();

        public Group(List<ScannedRockPageStub> stubs, int frame)
        {
            Stubs = new List<ScannedRockPageStub>(stubs.Count);
            Keys = Array.Empty<ScannedRockPageKey>();
            Replace(stubs, frame);
            Built = false;
        }

        public List<ScannedRockPageStub> Stubs { get; }

        public ScannedRockPageKey[] Keys { get; private set; }

        public bool Dirty { get; set; }

        public bool Built { get; set; }

        public int LastChangedFrame { get; private set; }

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

        public void Replace(List<ScannedRockPageStub> stubs, int frame)
        {
            Stubs.Clear();
            Stubs.AddRange(stubs);
            keySet.Clear();
            var keys = new ScannedRockPageKey[stubs.Count];
            for (int i = 0; i < stubs.Count; i++)
            {
                keys[i] = stubs[i].Key;
                keySet.Add(stubs[i].Key);
            }

            Keys = keys;
            Dirty = true;
            LastChangedFrame = frame;
        }
    }
}
