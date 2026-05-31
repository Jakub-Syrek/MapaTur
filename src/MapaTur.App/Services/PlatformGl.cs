using System.Runtime.InteropServices;

using Silk.NET.Core.Contexts;
using Silk.NET.OpenGLES;

namespace MapaTur.App.Services;

/// <summary>
/// Builds a Silk.NET <see cref="GL"/> bound to the OpenGL ES context that SkiaSharp's <c>SKGLView</c>
/// makes current during its paint callback. Function pointers are resolved via the platform's EGL
/// loader (<c>eglGetProcAddress</c>) with a direct export fallback from the system's GLES2 library
/// — necessary because some core ES entry points are absent from <c>eglGetProcAddress</c> on certain
/// drivers (ANGLE on Windows, Mali on Android).
///
/// Library names by platform:
///   • Windows: <c>libEGL.dll</c> + <c>libGLESv2.dll</c> (ANGLE — shipped with SkiaSharp.Views.Maui).
///   • Android: <c>libEGL.so</c> + <c>libGLESv2.so</c> (system libraries, present since API 21).
/// </summary>
internal static class PlatformGl
{
#if WINDOWS
    private const string EglLibName = "libEGL.dll";
    private const string GlesV2LibName = "libGLESv2.dll";
#elif ANDROID
    private const string EglLibName = "libEGL.so";
    private const string GlesV2LibName = "libGLESv2.so";
#elif IOS || MACCATALYST
    // iOS / Mac Catalyst ship GLES through OpenGLES.framework — flat function exports rather than
    // an EGL loader. SkiaSharp on Apple targets uses the same framework, so direct exports work.
    private const string EglLibName = "/System/Library/Frameworks/OpenGLES.framework/OpenGLES";
    private const string GlesV2LibName = "/System/Library/Frameworks/OpenGLES.framework/OpenGLES";
#else
    private const string EglLibName = "";
    private const string GlesV2LibName = "";
#endif

#if WINDOWS || ANDROID
    [DllImport(EglLibName, EntryPoint = "eglGetProcAddress", CharSet = CharSet.Ansi)]
    private static extern nint EglGetProcAddress(string name);
#endif

    private static GL? gl;
    private static nint glesV2Lib;

    /// <summary>Lazily builds (and caches) the GL API bound to the current GLES context.</summary>
    public static GL Get() => gl ??= GL.GetApi(new LamdaNativeContext(Resolve));

    private static nint Resolve(string name)
    {
#if WINDOWS || ANDROID
        nint p = EglGetProcAddress(name);
        if (p != 0)
        {
            return p;
        }
#endif

        if (glesV2Lib == 0)
        {
            if (string.IsNullOrEmpty(GlesV2LibName) || !NativeLibrary.TryLoad(GlesV2LibName, out glesV2Lib))
            {
                return 0;
            }
        }

        return NativeLibrary.TryGetExport(glesV2Lib, name, out nint ex) ? ex : 0;
    }
}