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

    internal static long TexAlive => Interlocked.Read(ref texAlive);
    internal static long BufAlive => Interlocked.Read(ref bufAlive);
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