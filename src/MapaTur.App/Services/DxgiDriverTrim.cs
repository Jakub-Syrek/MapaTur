#if WINDOWS
using System.Runtime.InteropServices;

using Serilog;

namespace MapaTur.App.Services;

/// <summary>
/// P0 commit GPU (2026-08-08): sterownik/ANGLE odkłada wewnętrzne tymczasowe alokacje (staging,
/// upload-heaps) proporcjonalnie do wolumenu transferów — zmierzone jako `GPU Process Memory\Dedicated
/// Usage` rosnące ~+0,5–0,8 GB/min lotu przy STAŁYCH licznikach GlTrack, we wszystkich konfiguracjach
/// pul (benche A–B5, dev/p0-pooling). Jedyne publiczne API każące sterownikowi ODDAĆ ten osad to
/// <c>IDXGIDevice3::Trim()</c> (aplikacje UWP wołają je przy suspend). ANGLE wystawia swój
/// <c>ID3D11Device</c> przez EGL (EGL_EXT_device_query + EGL_ANGLE_device_d3d) — wyciągamy go RAZ,
/// QI do IDXGIDevice3 i wołamy Trim na kadencji renderera (wątek GL, kontekst current).
/// Każdy błąd zatrzaskuje serwis na OFF — brak Trim nie może zepsuć renderowania.
/// </summary>
internal static unsafe class DxgiDriverTrim
{
    private const string EglLibName = "libEGL.dll";
    private const int EglDeviceExt = 0x322C;        // EGL_DEVICE_EXT (EGL_EXT_device_query)
    private const int EglD3D11DeviceAngle = 0x33A1; // EGL_D3D11_DEVICE_ANGLE (EGL_ANGLE_device_d3d)

    // dxgi1_3.h:2258: DEFINE_GUID(IID_IDXGIDevice3, 0x6007896c, 0x3244, 0x4afd, ...) — zweryfikowane
    // w lokalnym SDK 10.0.22621 po tym, jak GUID z pamięci dał E_NOINTERFACE (T1 08-07 23:45).
    private static readonly Guid IidDxgiDevice3 = new("6007896c-3244-4afd-bf18-a6d3beda5023");

    [DllImport(EglLibName, EntryPoint = "eglGetProcAddress", CharSet = CharSet.Ansi)]
    private static extern nint EglGetProcAddress(string name);

    [DllImport(EglLibName, EntryPoint = "eglGetCurrentDisplay")]
    private static extern nint EglGetCurrentDisplay();

    private static bool broken;
    private static nint dxgiDevice3; // COM ptr z naszym ref (QI) — trzymany do końca procesu

    /// <summary>Woła IDXGIDevice3::Trim na urządzeniu D3D11 ANGLE. False = niedostępne (zatrzaśnięte).</summary>
    public static bool TryTrim()
    {
        if (broken)
        {
            return false;
        }

        try
        {
            if (dxgiDevice3 == 0 && !TryResolveDevice())
            {
                broken = true;
                return false;
            }

            // vtable IDXGIDevice3: IUnknown(3) + IDXGIObject(4) + IDXGIDevice(5) + IDXGIDevice1(2)
            //                      + IDXGIDevice2(3) = sloty 0–16, Trim = slot 17.
            void** vtbl = *(void***)dxgiDevice3;
            ((delegate* unmanaged[Stdcall]<nint, void>)vtbl[17])(dxgiDevice3);
            return true;
        }
        catch (Exception ex)
        {
            broken = true;
            Log.Warning(ex, "[Trim] IDXGIDevice3::Trim niedostępny — serwis wyłączony do końca sesji");
            return false;
        }
    }

    private static bool TryResolveDevice()
    {
        nint qDisplay = EglGetProcAddress("eglQueryDisplayAttribEXT");
        nint qDevice = EglGetProcAddress("eglQueryDeviceAttribEXT");
        if (qDisplay == 0 || qDevice == 0)
        {
            Log.Warning("[Trim] brak EGL_EXT_device_query w tym ANGLE — Trim niedostępny");
            return false;
        }

        nint display = EglGetCurrentDisplay();
        if (display == 0)
        {
            Log.Warning("[Trim] eglGetCurrentDisplay=0 (wołane poza wątkiem GL?) — Trim niedostępny");
            return false;
        }

        nint eglDevice = 0;
        if (((delegate* unmanaged[Stdcall]<nint, int, nint*, int>)qDisplay)(display, EglDeviceExt, &eglDevice) == 0
            || eglDevice == 0)
        {
            Log.Warning("[Trim] eglQueryDisplayAttribEXT(EGL_DEVICE_EXT) odmówił — Trim niedostępny");
            return false;
        }

        nint d3dDevice = 0;
        if (((delegate* unmanaged[Stdcall]<nint, int, nint*, int>)qDevice)(eglDevice, EglD3D11DeviceAngle, &d3dDevice) == 0
            || d3dDevice == 0)
        {
            Log.Warning("[Trim] EGL_D3D11_DEVICE_ANGLE niedostępne (backend nie-D3D11?) — Trim niedostępny");
            return false;
        }

        // d3dDevice to ID3D11Device BEZ naszego AddRef — QueryInterface daje nam własną referencję.
        Guid iid = IidDxgiDevice3;
        int hr = Marshal.QueryInterface(d3dDevice, in iid, out nint dev3);
        if (hr != 0 || dev3 == 0)
        {
            Log.Warning("[Trim] QueryInterface(IDXGIDevice3) hr=0x{Hr:X8} — Trim niedostępny", hr);
            return false;
        }

        dxgiDevice3 = dev3;
        Log.Information("[Trim] IDXGIDevice3 z urządzenia ANGLE pozyskany — Trim aktywny");
        return true;
    }
}
#endif
