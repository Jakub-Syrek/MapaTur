#if WINDOWS
using System.Runtime.InteropServices;

using Silk.NET.Core.Contexts;
using Silk.NET.OpenGLES;

namespace MapaTur.App.Platforms.Windows;

/// <summary>
/// Acquires a Silk.NET <see cref="GL"/> for the OpenGL ES context that SkiaSharp's <c>SKGLView</c> makes
/// current during its paint callback (ANGLE on Windows). Function pointers are resolved via
/// <c>eglGetProcAddress</c>, with a direct export fallback from <c>libGLESv2.dll</c> for any core entry
/// point ANGLE doesn't return through EGL. The same shipped ANGLE DLLs that back SkiaSharp are reused.
/// </summary>
internal static class AngleGl
{
    [DllImport("libEGL.dll", CharSet = CharSet.Ansi)]
    private static extern nint eglGetProcAddress(string name);

    private static GL? gl;
    private static nint glesV2Lib;

    /// <summary>Lazily builds (and caches) the GL API bound to the current ANGLE context.</summary>
    public static GL Get() => gl ??= GL.GetApi(new LamdaNativeContext(Resolve));

    private static nint Resolve(string name)
    {
        nint p = eglGetProcAddress(name);
        if (p != 0)
        {
            return p;
        }

        if (glesV2Lib == 0 && !NativeLibrary.TryLoad("libGLESv2.dll", out glesV2Lib))
        {
            return 0;
        }

        return NativeLibrary.TryGetExport(glesV2Lib, name, out nint ex) ? ex : 0;
    }
}
#endif