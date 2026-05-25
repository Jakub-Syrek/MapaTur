using System.Runtime.InteropServices;
using MapaTur.App.Services;

namespace MapaTur.App.Platforms.Windows;

/// <summary>
/// Windows-specific file picker using the classic Win32 GetOpenFileNameW common dialog.
/// The WinUI 3 FileOpenPicker fails with COMException 0x80004005 (E_FAIL) in unpackaged
/// MAUI apps even when InitializeWithWindow is called; the Win32 dialog has no such
/// requirements and works reliably on every Windows desktop session.
/// </summary>
public sealed class WindowsFilePickerService : IFilePickerService
{
    private const int MaxPathChars = 32_768;

    /// <inheritdoc />
    public Task<string?> PickFileAsync(string title)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        nint fileBuffer = Marshal.AllocCoTaskMem(MaxPathChars * sizeof(char));
        try
        {
            // Zero-initialise the buffer; GetOpenFileNameW requires a NUL-terminated empty
            // string when no initial filename is suggested.
            for (int i = 0; i < MaxPathChars * sizeof(char); i++)
            {
                Marshal.WriteByte(fileBuffer, i, 0);
            }

            var ofn = new OpenFileName
            {
                structSize = Marshal.SizeOf<OpenFileName>(),
                hwndOwner = GetActiveWindowHandle(),
                file = fileBuffer,
                maxFile = MaxPathChars,
                title = title,
                flags = FlagFileMustExist | FlagPathMustExist | FlagExplorer | FlagNoChangeDir,
            };

            if (!GetOpenFileNameW(ref ofn))
            {
                // User cancelled (or CommDlgExtendedError() != 0 for real failure).
                return Task.FromResult<string?>(null);
            }

            string? path = Marshal.PtrToStringUni(fileBuffer);
            return Task.FromResult(string.IsNullOrEmpty(path) ? null : path);
        }
        finally
        {
            Marshal.FreeCoTaskMem(fileBuffer);
        }
    }

    private static nint GetActiveWindowHandle()
    {
        var window = Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault();
        if (window?.Handler?.PlatformView is Microsoft.UI.Xaml.Window winuiWindow)
        {
            return WinRT.Interop.WindowNative.GetWindowHandle(winuiWindow);
        }
        return nint.Zero;
    }

    private const int FlagFileMustExist = 0x00001000;
    private const int FlagPathMustExist = 0x00000800;
    private const int FlagExplorer = 0x00080000;
    private const int FlagNoChangeDir = 0x00000008;

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetOpenFileNameW(ref OpenFileName ofn);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenFileName
    {
        public int structSize;
        public nint hwndOwner;
        public nint instance;
        [MarshalAs(UnmanagedType.LPWStr)] public string? filter;
        [MarshalAs(UnmanagedType.LPWStr)] public string? customFilter;
        public int maxCustFilter;
        public int filterIndex;
        public nint file;
        public int maxFile;
        public nint fileTitle;
        public int maxFileTitle;
        [MarshalAs(UnmanagedType.LPWStr)] public string? initialDir;
        [MarshalAs(UnmanagedType.LPWStr)] public string? title;
        public int flags;
        public short fileOffset;
        public short fileExtension;
        [MarshalAs(UnmanagedType.LPWStr)] public string? defExt;
        public nint custData;
        public nint hook;
        [MarshalAs(UnmanagedType.LPWStr)] public string? templateName;
        public nint pvReserved;
        public int dwReserved;
        public int flagsEx;
    }
}
