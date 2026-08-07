using Silk.NET.OpenGLES;

namespace MapaTur.App.Services;

/// <summary>
/// Live GL-resource counters (diagnoza wycieku natywnego 08-02: commit D3D rósł ~2,5 GB na lot F9,
/// a księgowość [Mem] tego nie widziała). Every Gen*/Delete* in the renderer routes through here, so
/// <c>%TEMP%\mapatur-status.json</c> can show LIVE object counts — rosnący licznik = niesparowany create,
/// stały licznik przy rosnącym commicie = osad po stronie sterownika (ghosting/deferred destruction).
/// Deleting id 0 is a GL no-op and is not counted, mirroring driver semantics.
/// </summary>
internal static class GlTrack
{
    private static long texAlive;
    private static long bufAlive;
    private static long vaoAlive;
    private static long fboAlive;
    private static long rboAlive;
    private static long vboBytes;

    internal static long TexAlive => Interlocked.Read(ref texAlive);
    internal static long BufAlive => Interlocked.Read(ref bufAlive);

    /// <summary>Bytes of GL buffer storage the renderer believes it has allocated (tile VBO/EBO classes).</summary>
    internal static long VboBytes => Interlocked.Read(ref vboBytes);

    internal static void AddVboBytes(long delta) => Interlocked.Add(ref vboBytes, delta);

    /// <summary>Utrata kontekstu GL: wszystkie zaksięgowane jednostki pooled umarły z kontekstem bez
    /// Delete*Unit (słusznie — martwy kontekst), więc licznik trzeba wyzerować wprost. Bez tego glVboMB
    /// dubluje się po każdej rekreacji kontekstu (finding weryfikacji 08-06) i fałszuje bench P0.
    /// Poprawne, bo KAŻDE użycie AddVboBytes pochodzi z jednostek pooled żyjących w tym kontekście.</summary>
    internal static void ResetVboBytes() => Interlocked.Exchange(ref vboBytes, 0);

    // P0 pooling (2026-08-06): migawka statystyk pul GL renderera (tile+line+staging) publikowana per
    // klatka do status.json — hit-rate i wolne bajty puli to kryterium „pooling faktycznie pracuje"
    // w benchu przed/po (obok płaskiej krzywej ws/commit D3D).
    private static long poolFreeBytes;
    private static long poolHits;
    private static long poolMisses;
    private static long pboFenceWaits;

    internal static long PoolFreeBytes => Interlocked.Read(ref poolFreeBytes);
    internal static long PoolHits => Interlocked.Read(ref poolHits);
    internal static long PoolMisses => Interlocked.Read(ref poolMisses);
    internal static long PboFenceWaits => Interlocked.Read(ref pboFenceWaits);

    internal static void PublishPoolStats(long freeBytes, long hits, long misses, long fenceWaits)
    {
        Interlocked.Exchange(ref poolFreeBytes, freeBytes);
        Interlocked.Exchange(ref poolHits, hits);
        Interlocked.Exchange(ref poolMisses, misses);
        Interlocked.Exchange(ref pboFenceWaits, fenceWaits);
    }

    // P0 gpuDed (08-08): skumulowane bajty uploadu per konsument — bisekcja pełzania commitu GPU.
    private static long upDet1m;
    private static long upDet05;
    private static long upDet25;
    private static long upOrtho;
    private static long upMask;
    private static long upMesh;

    internal static long UpDet1m => Interlocked.Read(ref upDet1m);
    internal static long UpDet05 => Interlocked.Read(ref upDet05);
    internal static long UpDet25 => Interlocked.Read(ref upDet25);
    internal static long UpOrtho => Interlocked.Read(ref upOrtho);
    internal static long UpMask => Interlocked.Read(ref upMask);
    internal static long UpMesh => Interlocked.Read(ref upMesh);

    internal static void PublishUploadStats(long det1m, long det05, long det25, long ortho, long mask, long mesh)
    {
        Interlocked.Exchange(ref upDet1m, det1m);
        Interlocked.Exchange(ref upDet05, det05);
        Interlocked.Exchange(ref upDet25, det25);
        Interlocked.Exchange(ref upOrtho, ortho);
        Interlocked.Exchange(ref upMask, mask);
        Interlocked.Exchange(ref upMesh, mesh);
    }

    internal static long VaoAlive => Interlocked.Read(ref vaoAlive);
    internal static long FboAlive => Interlocked.Read(ref fboAlive);
    internal static long RboAlive => Interlocked.Read(ref rboAlive);

    internal static uint GenTexture(GL g)
    {
        Interlocked.Increment(ref texAlive);
        return g.GenTexture();
    }

    internal static void DeleteTexture(GL g, uint id)
    {
        if (id != 0)
        {
            Interlocked.Decrement(ref texAlive);
            g.DeleteTexture(id);
        }
    }

    internal static uint GenBuffer(GL g)
    {
        Interlocked.Increment(ref bufAlive);
        return g.GenBuffer();
    }

    internal static void DeleteBuffer(GL g, uint id)
    {
        if (id != 0)
        {
            Interlocked.Decrement(ref bufAlive);
            g.DeleteBuffer(id);
        }
    }

    internal static uint GenVertexArray(GL g)
    {
        Interlocked.Increment(ref vaoAlive);
        return g.GenVertexArray();
    }

    internal static void DeleteVertexArray(GL g, uint id)
    {
        if (id != 0)
        {
            Interlocked.Decrement(ref vaoAlive);
            g.DeleteVertexArray(id);
        }
    }

    internal static uint GenFramebuffer(GL g)
    {
        Interlocked.Increment(ref fboAlive);
        return g.GenFramebuffer();
    }

    internal static void DeleteFramebuffer(GL g, uint id)
    {
        if (id != 0)
        {
            Interlocked.Decrement(ref fboAlive);
            g.DeleteFramebuffer(id);
        }
    }

    internal static uint GenRenderbuffer(GL g)
    {
        Interlocked.Increment(ref rboAlive);
        return g.GenRenderbuffer();
    }

    internal static void DeleteRenderbuffer(GL g, uint id)
    {
        if (id != 0)
        {
            Interlocked.Decrement(ref rboAlive);
            g.DeleteRenderbuffer(id);
        }
    }
}