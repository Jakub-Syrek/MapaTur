using System.Collections.Concurrent;
using System.Diagnostics;

using MapaTur.Application.Terrain;

using SkiaSharp;

// MapaTur.OrthoBake (ARCHITEKTURA-STREAMING §8 + ANEKS A): prebake warstwy orto do pakietów .opk.
//   dotnet run --project src/MapaTur.OrthoBake -- --layer det25 --src <dir-webp> --out <dir-opk> [--det1m-out <dir>]
// Warstwa det25: pakiet = dysjunktywna grupa 8×8 kafli (4096 px @ 0,25 m). Strona = kafel 1:1 WebP→BC1
// (mip 512+256), tail = kompozyt grupy 2048↓. Równolegle det1m: fragmenty 4×-downsample grup det25 →
// pakiety 4096 px @ 1 m/px. Przyrostowość: srcHash (mtime+size) per strona; istniejący .opk z tym samym
// zestawem hashy jest pomijany. Wyjście: {out}/{gi}_{gj}.opk + index.bin; weryfikacja liczbowa na końcu.

const int TilePx = 512;
const int Det1mPackFragments = 4;    // 4×4 fragmenty (każdy = grupa det25 zdownsamplowana 4× do 1024 px) = pakiet det1m 4096 px

string? GetArg(string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

string layer = GetArg("--layer") ?? "det25";
string? srcArg = GetArg("--src"); // wymagane tylko dla bake'u (walidowane za gałęzią --verify-full)
string outDir = GetArg("--out") ?? throw new ArgumentException("--out wymagane");
string? det1mOut = GetArg("--det1m-out");
int parallelism = int.TryParse(GetArg("--parallel"), out int p) ? p : Math.Max(2, Environment.ProcessorCount - 2);

// ── Tryb --verify-full: PEŁNA walidacja warstwy (bez bake'u) ─────────────────────────────────────────
// CRC KAŻDEJ strony, offsety/długości (rozłączne, w granicach pliku, payload za TOC), unikalność pageId
// w pakiecie i kluczy (gi,gj) w indeksie, bijekcja indeks ↔ pliki .opk na dysku.
if (args.Contains("--verify-full"))
{
    string dir = GetArg("--out") ?? throw new ArgumentException("--out wymagane dla --verify-full");
    OrthoPackIndex? idx = OrthoPackIndex.Load(Path.Combine(dir, "index.bin"));
    if (idx is null) { Console.Error.WriteLine("[verify-full] index.bin nie ładuje się"); return 1; }

    var swv = Stopwatch.StartNew();
    var keySet = new HashSet<(int, int)>();
    long pagesOk = 0, pagesBad = 0, layoutBad = 0, dupIds = 0;
    foreach (OrthoPackIndex.CellEntry c in idx.Cells)
    {
        if (!keySet.Add((c.Ci, c.Cj)))
        {
            Console.Error.WriteLine($"[verify-full] DUPLIKAT klucza w indeksie: ({c.Ci},{c.Cj})");
            return 1;
        }

        string pk = Path.Combine(dir, $"{c.Ci}_{c.Cj}.opk");
        using OrthoPagePack? pack = OrthoPagePack.Open(pk, c.CellPx);
        if (pack is null) { Console.Error.WriteLine($"[verify-full] pakiet nie otwiera się: {pk}"); return 1; }
        if (pack.PageCount != c.PageCount) { Console.Error.WriteLine($"[verify-full] pageCount mismatch: {pk}"); return 1; }

        long fileLen = new FileInfo(pk).Length;
        var ids = new HashSet<ushort>();
        var regions = new List<(long Start, long End)>();
        foreach (OrthoPagePack.Entry e in pack.Entries)
        {
            if (!ids.Add(e.PageId)) { dupIds++; }
            long len = e.ZstdBytes > 0 ? e.ZstdBytes : e.RawBytes;
            if (e.Offset < 32 || e.Offset + len > fileLen || e.RawBytes <= 0) { layoutBad++; }
            regions.Add((e.Offset, e.Offset + len));
            if (pack.TryReadPage(e.PageId, out _)) { pagesOk++; } else { pagesBad++; }
        }

        regions.Sort();
        for (int i = 1; i < regions.Count; i++)
        {
            if (regions[i].Start < regions[i - 1].End) { layoutBad++; }
        }
    }

    int orphanFiles = Directory.EnumerateFiles(dir, "*.opk").Count() - idx.Cells.Count;
    Console.WriteLine($"[verify-full] pakiety={idx.Cells.Count} strony: OK={pagesOk} BAD={pagesBad} | layoutBad={layoutBad} dupPageId={dupIds} | pliki-poza-indeksem={orphanFiles} | czas={swv.Elapsed.TotalMinutes:F1} min");
    return pagesBad == 0 && layoutBad == 0 && dupIds == 0 && orphanFiles == 0 ? 0 : 1;
}

if (layer != "det25" && layer != "det05")
{
    Console.Error.WriteLine($"Warstwa '{layer}' nieobsługiwana (det25|det05).");
    return 2;
}

// det05: dysjunktywna grupa 16×16 kafli = 8192 px (ANEKS A); det25: 8×8 = 4096 px. det1m tylko z det25.
int GroupTiles = layer == "det05" ? 16 : 8;
int GroupPx = TilePx * GroupTiles;
if (layer == "det05" && det1mOut is not null)
{
    Console.Error.WriteLine("--det1m-out działa tylko z --layer det25");
    return 2;
}

string src = srcArg ?? throw new ArgumentException("--src wymagane");
var sw = Stopwatch.StartNew();

// ── 1. Skan źródła: kafle {ti}/{tj}.webp → grupy dysjunktywne ────────────────────────────────────────
Console.WriteLine($"[bake] skan {src} ...");
var tiles = new Dictionary<(int Ti, int Tj), (string Path, ulong SrcHash)>();
foreach (string colDir in Directory.EnumerateDirectories(src))
{
    if (!int.TryParse(Path.GetFileName(colDir), out int ti)) { continue; }
    foreach (string f in Directory.EnumerateFiles(colDir, "*.webp"))
    {
        if (!int.TryParse(Path.GetFileNameWithoutExtension(f), out int tj)) { continue; }
        var fi = new FileInfo(f);
        ulong hash = unchecked(((ulong)fi.Length * 0x9E3779B97F4A7C15UL) ^ (ulong)fi.LastWriteTimeUtc.Ticks);
        tiles[(ti, tj)] = (f, hash);
    }
}

var groups = tiles.Keys
    .GroupBy(t => (Gi: Math.DivRem(t.Ti, GroupTiles, out _) is var _ ? t.Ti / GroupTiles : 0, Gj: t.Tj / GroupTiles))
    .ToDictionary(g => g.Key, g => g.ToList());
Console.WriteLine($"[bake] kafli: {tiles.Count}, grup (pakietów det25): {groups.Count}, parallel={parallelism}");

// ── 2. Bake per grupa (równolegle, bounded) ──────────────────────────────────────────────────────────
Directory.CreateDirectory(outDir);
var det1mFragments = new ConcurrentDictionary<(int Fi, int Fj), byte[]>(); // fragment 1024px @1m, klucz = indeks grupy det25
long baked = 0, skipped = 0, failedTiles = 0;
long pageCountTotal = 0;

byte[]? DecodeWebp(string path)
{
    using SKBitmap? bmp = SKBitmap.Decode(path);
    if (bmp is null || bmp.Width != TilePx || bmp.Height != TilePx) { return null; }
    using SKBitmap rgba = bmp.ColorType == SKColorType.Rgba8888
        ? bmp.Copy(SKColorType.Rgba8888) ?? bmp
        : bmp.Copy(SKColorType.Rgba8888)!;
    byte[] dst = new byte[TilePx * TilePx * 4];
    System.Runtime.InteropServices.Marshal.Copy(rgba.GetPixels(), dst, 0, dst.Length);
    OrthoNodata.ZeroAlphaOnBlack(dst); // nodata GUGiK (kryjąca czerń na granicy PL) → punch-through
    return dst;
}

// Box 2×2 — downsample RGBA o połowę (mip strony i tail liczone spójnie z runtime'owym BuildMipChain).
static byte[] Half(byte[] rgbaIn, int pxIn)
{
    int pxOut = pxIn / 2;
    byte[] outB = new byte[pxOut * pxOut * 4];
    for (int y = 0; y < pxOut; y++)
    {
        int sy = y * 2;
        for (int x = 0; x < pxOut; x++)
        {
            int sx = x * 2;
            int a = ((sy * pxIn) + sx) * 4, b = a + 4, c = a + (pxIn * 4), d = c + 4, o = ((y * pxOut) + x) * 4;
            // ALFA-WAZONE usrednianie (czarne trojkaty przy szwie orto): przezroczyste piksele NIE zaciemniaja
            // koloru — kolor tylko z pokrytych, alfa usredniana osobno (inaczej gleboke mipy maja czarna
            // obwodke, ktora przy binarnym progu DXT1a wychodzi KRYJACA).
            int aa = rgbaIn[a + 3], ab = rgbaIn[b + 3], ac = rgbaIn[c + 3], ad = rgbaIn[d + 3];
            int sumA = aa + ab + ac + ad;
            if (sumA == 0)
            {
                outB[o] = 0; outB[o + 1] = 0; outB[o + 2] = 0; outB[o + 3] = 0;
            }
            else
            {
                outB[o] = (byte)(((rgbaIn[a] * aa) + (rgbaIn[b] * ab) + (rgbaIn[c] * ac) + (rgbaIn[d] * ad) + (sumA >> 1)) / sumA);
                outB[o + 1] = (byte)(((rgbaIn[a + 1] * aa) + (rgbaIn[b + 1] * ab) + (rgbaIn[c + 1] * ac) + (rgbaIn[d + 1] * ad) + (sumA >> 1)) / sumA);
                outB[o + 2] = (byte)(((rgbaIn[a + 2] * aa) + (rgbaIn[b + 2] * ab) + (rgbaIn[c + 2] * ac) + (rgbaIn[d + 2] * ad) + (sumA >> 1)) / sumA);
                outB[o + 3] = (byte)((sumA + 2) >> 2);
            }
        }
    }

    return outB;
}

static byte[] EncodeBc1(byte[] rgba, int px)
{
    byte[] dst = new byte[Bc1Encoder.EncodedSize(px, px)];
    Bc1Encoder.Encode(rgba, px, px, dst);
    return dst;
}

var indexCells = new ConcurrentBag<OrthoPackIndex.CellEntry>();
var coveredTiles = new ConcurrentBag<(int, int)>();

Parallel.ForEach(groups, new ParallelOptions { MaxDegreeOfParallelism = parallelism }, kv =>
{
    ((int gi, int gj), List<(int Ti, int Tj)> members) = (kv.Key, kv.Value);
    string packPath = Path.Combine(outDir, $"{gi}_{gj}.opk");

    // Przyrostowość: pakiet z identycznym zestawem (pageId → srcHash) pomijamy.
    using (OrthoPagePack? existing = OrthoPagePack.Open(packPath, GroupPx))
    {
        if (existing is not null)
        {
            var want = members.ToDictionary(
                t => (ushort)(((t.Ti - (gi * GroupTiles)) * GroupTiles) + (t.Tj - (gj * GroupTiles))),
                t => tiles[t].SrcHash);
            var have = existing.Entries.Where(e => e.PageId != OrthoPagePack.TailPageId)
                .ToDictionary(e => e.PageId, e => e.SrcHash);
            if (want.Count == have.Count && want.All(w => have.TryGetValue(w.Key, out ulong h) && h == w.Value))
            {
                Interlocked.Increment(ref skipped);
                Interlocked.Add(ref pageCountTotal, existing.PageCount);
                indexCells.Add(new OrthoPackIndex.CellEntry(gi, gj, GroupPx, (ushort)existing.PageCount, new FileInfo(packPath).Length));
                foreach ((int ti, int tj) in members) { coveredTiles.Add((ti, tj)); }

                // Fragment det1m jest pochodną ŹRÓDŁA (WebP), nie pakietu — przy skipie i tak go budujemy
                // (dekod + kompozyt + downsample, bez re-enkodu stron det25). Bez tego przyrostowy bieg
                // z --det1m-out dawał 0 pakietów det1m (luka wykryta 2026-07-23).
                if (det1mOut is not null)
                {
                    byte[] gRgba = new byte[GroupPx * GroupPx * 4];
                    foreach ((int ti, int tj) in members)
                    {
                        byte[]? t = DecodeWebp(tiles[(ti, tj)].Path);
                        if (t is null) { continue; }
                        int lx = ti - (gi * GroupTiles), ly = tj - (gj * GroupTiles);
                        for (int row = 0; row < TilePx; row++)
                        {
                            System.Buffer.BlockCopy(t, row * TilePx * 4, gRgba,
                                ((((ly * TilePx) + row) * GroupPx) + (lx * TilePx)) * 4, TilePx * 4);
                        }
                    }

                    det1mFragments[(gi, gj)] = Half(Half(gRgba, GroupPx), GroupPx / 2);
                }

                return;
            }
        }
    }

    // Kompozyt grupy (dysjunktywny — proste wklejenie kafli) + strony.
    byte[] groupRgba = new byte[GroupPx * GroupPx * 4];
    var pages = new List<OrthoPagePack.PageData>(members.Count + 1);
    foreach ((int ti, int tj) in members)
    {
        byte[]? tile = DecodeWebp(tiles[(ti, tj)].Path);
        if (tile is null)
        {
            Interlocked.Increment(ref failedTiles);
            continue;
        }

        int lx = ti - (gi * GroupTiles), ly = tj - (gj * GroupTiles);
        // Wklej kafel do kompozytu grupy (wiersz po wierszu).
        for (int row = 0; row < TilePx; row++)
        {
            System.Buffer.BlockCopy(tile, row * TilePx * 4, groupRgba,
                ((((ly * TilePx) + row) * GroupPx) + (lx * TilePx)) * 4, TilePx * 4);
        }

        // Strona: BC1 mip0 (512) + mip1 (256) spakowane sekwencyjnie.
        byte[] mip1 = Half(tile, TilePx);
        byte[] payload = new byte[Bc1Encoder.EncodedSize(TilePx, TilePx) + Bc1Encoder.EncodedSize(TilePx / 2, TilePx / 2)];
        Bc1Encoder.Encode(tile, TilePx, TilePx, payload);
        Bc1Encoder.Encode(mip1, TilePx / 2, TilePx / 2, payload.AsSpan(Bc1Encoder.EncodedSize(TilePx, TilePx)));
        ushort pageId = (ushort)((lx * GroupTiles) + ly);
        pages.Add(new OrthoPagePack.PageData(pageId, 0, payload, tiles[(ti, tj)].SrcHash));
        coveredTiles.Add((ti, tj));
    }

    if (pages.Count == 0)
    {
        return;
    }

    // Tail: kompozyt grupy 2048↓ ... 1 (poziomy celi 1..N) — jeden bufor sekwencyjny.
    var tailParts = new List<byte[]>();
    byte[] level = Half(groupRgba, GroupPx); // 2048
    int px = GroupPx / 2;
    while (px >= 1)
    {
        tailParts.Add(EncodeBc1(level, px));
        if (px == 1) { break; }
        level = Half(level, px);
        px /= 2;
    }

    byte[] tail = new byte[tailParts.Sum(t => t.Length)];
    int off = 0;
    foreach (byte[] t in tailParts) { System.Buffer.BlockCopy(t, 0, tail, off, t.Length); off += t.Length; }
    pages.Insert(0, new OrthoPagePack.PageData(OrthoPagePack.TailPageId, 1, tail, 0));

    OrthoPagePack.Write(packPath, GroupPx, pages);
    Interlocked.Add(ref pageCountTotal, pages.Count);
    indexCells.Add(new OrthoPackIndex.CellEntry(gi, gj, GroupPx, (ushort)pages.Count, new FileInfo(packPath).Length));

    // Fragment det1m: grupa 4096 @0,25 m → 1024 px @1 m.
    if (det1mOut is not null)
    {
        byte[] frag = Half(Half(groupRgba, GroupPx), GroupPx / 2); // 4× down = 1024
        det1mFragments[(gi, gj)] = frag;
    }

    long done = Interlocked.Increment(ref baked);
    if (done % 50 == 0)
    {
        Console.WriteLine($"[bake] {done + skipped}/{groups.Count} pakietów ({sw.Elapsed.TotalSeconds:F0}s)");
    }
});

// ── 3. Indeks det25 ──────────────────────────────────────────────────────────────────────────────────
OrthoPackIndex.Write(Path.Combine(outDir, "index.bin"), GroupTiles, indexCells.ToList(), coveredTiles.Distinct().ToList());
Console.WriteLine($"[bake] det25 GOTOWE: {baked} wypieczonych + {skipped} pominiętych (przyrostowo), stron={pageCountTotal}, kafli źle={failedTiles}, czas={sw.Elapsed.TotalMinutes:F1} min");

// ── 4. det1m z fragmentów ────────────────────────────────────────────────────────────────────────────
if (det1mOut is not null)
{
    Directory.CreateDirectory(det1mOut);
    var packs = det1mFragments.Keys.GroupBy(k => (Pi: FloorDiv(k.Fi, Det1mPackFragments), Pj: FloorDiv(k.Fj, Det1mPackFragments)));
    var idx1m = new List<OrthoPackIndex.CellEntry>();
    var tiles1m = new List<(int, int)>();
    int det1mCount = 0;
    foreach (var pack in packs)
    {
        (int pi, int pj) = (pack.Key.Pi, pack.Key.Pj);
        const int PackPx = 4096;
        const int FragPx = 1024;
        byte[] rgba = new byte[PackPx * PackPx * 4];
        foreach ((int fi, int fj) in pack)
        {
            byte[] frag = det1mFragments[(fi, fj)];
            int lx = fi - (pi * Det1mPackFragments), ly = fj - (pj * Det1mPackFragments);
            for (int row = 0; row < FragPx; row++)
            {
                System.Buffer.BlockCopy(frag, row * FragPx * 4, rgba,
                    ((((ly * FragPx) + row) * PackPx) + (lx * FragPx)) * 4, FragPx * 4);
            }
        }

        // det1m: strony 512 px (8×8) z pełnego rgba + tail. Strona powstaje TYLKO nad realnym pokryciem
        // (fragment det25 istniał) — czarna strona spoza pokrycia w pakiecie = „puste pola" na ekranie
        // (bramka P0); brak strony w TOC = shaderowy fallback do bazy przez maskę pokrycia.
        bool PageCovered(int lx, int ly)
        {
            // Strona 512 px @1 m = 512 m = ćwiartka fragmentu (fragment 1024 px = grupa det25).
            int fi = (pi * Det1mPackFragments) + (lx / 2), fj = (pj * Det1mPackFragments) + (ly / 2);
            return det1mFragments.ContainsKey((fi, fj));
        }

        var pages = new List<OrthoPagePack.PageData>();
        byte[] pageBuf = new byte[TilePx * TilePx * 4];
        for (int lx = 0; lx < 8; lx++)
        {
            for (int ly = 0; ly < 8; ly++)
            {
                if (!PageCovered(lx, ly)) { continue; }
                for (int row = 0; row < TilePx; row++)
                {
                    System.Buffer.BlockCopy(rgba, ((((ly * TilePx) + row) * PackPx) + (lx * TilePx)) * 4,
                        pageBuf, row * TilePx * 4, TilePx * 4);
                }

                byte[] mip1 = Half(pageBuf, TilePx);
                byte[] payload = new byte[Bc1Encoder.EncodedSize(TilePx, TilePx) + Bc1Encoder.EncodedSize(256, 256)];
                Bc1Encoder.Encode(pageBuf, TilePx, TilePx, payload);
                Bc1Encoder.Encode(mip1, 256, 256, payload.AsSpan(Bc1Encoder.EncodedSize(TilePx, TilePx)));
                pages.Add(new OrthoPagePack.PageData((ushort)((lx * 8) + ly), 0, payload, 0));
                tiles1m.Add(((pi * 8) + lx, (pj * 8) + ly));
            }
        }

        var tailParts = new List<byte[]>();
        byte[] level = Half(rgba, PackPx);
        int px = PackPx / 2;
        while (px >= 1)
        {
            tailParts.Add(EncodeBc1(level, px));
            if (px == 1) { break; }
            level = Half(level, px);
            px /= 2;
        }

        byte[] tail = new byte[tailParts.Sum(t => t.Length)];
        int off = 0;
        foreach (byte[] t in tailParts) { System.Buffer.BlockCopy(t, 0, tail, off, t.Length); off += t.Length; }
        pages.Insert(0, new OrthoPagePack.PageData(OrthoPagePack.TailPageId, 1, tail, 0));

        string packPath = Path.Combine(det1mOut, $"{pi}_{pj}.opk");
        OrthoPagePack.Write(packPath, PackPx, pages);
        idx1m.Add(new OrthoPackIndex.CellEntry(pi, pj, PackPx, (ushort)pages.Count, new FileInfo(packPath).Length));
        det1mCount++;
    }

    OrthoPackIndex.Write(Path.Combine(det1mOut, "index.bin"), 8, idx1m, tiles1m);
    Console.WriteLine($"[bake] det1m GOTOWE: {det1mCount} pakietów, czas łączny={sw.Elapsed.TotalMinutes:F1} min");
}

// ── 5. Weryfikacja liczbowa (obowiązkowa — §8) ───────────────────────────────────────────────────────
{
    OrthoPackIndex? idx = OrthoPackIndex.Load(Path.Combine(outDir, "index.bin"));
    if (idx is null) { Console.Error.WriteLine("[verify] BŁĄD: index.bin nie ładuje się"); return 1; }
    long tocPages = 0;
    var rnd = new Random(12345);
    var sample = idx.Cells.OrderBy(_ => rnd.Next()).Take(32).ToList();
    int crcOk = 0, crcBad = 0;
    foreach (OrthoPackIndex.CellEntry c in idx.Cells)
    {
        tocPages += c.PageCount;
    }

    foreach (OrthoPackIndex.CellEntry c in sample)
    {
        using OrthoPagePack? pk = OrthoPagePack.Open(Path.Combine(outDir, $"{c.Ci}_{c.Cj}.opk"), c.CellPx);
        if (pk is null) { crcBad++; continue; }
        foreach (OrthoPagePack.Entry e in pk.Entries.Take(4))
        {
            if (pk.TryReadPage(e.PageId, out _)) { crcOk++; } else { crcBad++; }
        }
    }

    long tocMinusTails = tocPages - idx.Cells.Count; // 1 tail na pakiet
    bool pagesMatch = tocMinusTails == tiles.Count - failedTiles;
    Console.WriteLine($"[verify] strony(TOC)-taile={tocMinusTails} vs kafle źródłowe OK={tiles.Count - failedTiles} → {(pagesMatch ? "ZGODNE" : "NIEZGODNE!")}");
    Console.WriteLine($"[verify] próbka crc: OK={crcOk}, BAD={crcBad}");
    Console.WriteLine($"[verify] rozmiar wyjścia: {DirSizeGb(outDir):F2} GB det25" + (det1mOut is not null ? $" + {DirSizeGb(det1mOut):F2} GB det1m" : string.Empty));
    if (!pagesMatch || crcBad > 0) { return 1; }
}

Console.WriteLine($"[bake] CAŁOŚĆ OK, czas zmierzony: {sw.Elapsed.TotalMinutes:F1} min");
return 0;

static int FloorDiv(int a, int b) => (int)Math.Floor((double)a / b);

static double DirSizeGb(string dir)
    => Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) / (1024.0 * 1024 * 1024);
