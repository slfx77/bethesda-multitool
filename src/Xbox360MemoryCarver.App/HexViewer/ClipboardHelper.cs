using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Xbox360MemoryCarver.App.HexViewer;

/// <summary>
///     Helper class for clipboard operations using Win32 interop.
///     WinUI 3 clipboard APIs can have COM threading issues, so we use native calls.
/// </summary>
internal static class ClipboardHelper
{
    private const uint CF_UNICODETEXT = 13;

    [DllImport("user32.dll")]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll")]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll")]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    /// <summary>
    ///     Copy text to clipboard using Win32 interop.
    /// </summary>
    public static void CopyText(string text)
    {
        try
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                try
                {
                    EmptyClipboard();
                    var hGlobal = Marshal.StringToHGlobalUni(text + '\0');
                    if (SetClipboardData(CF_UNICODETEXT, hGlobal) == IntPtr.Zero)
                    {
                        // SetClipboardData failed, free the memory
                        Marshal.FreeHGlobal(hGlobal);
                        Debug.WriteLine("[ClipboardHelper] SetClipboardData failed");
                    }
                    else
                    {
                        Debug.WriteLine($"[ClipboardHelper] Copied {text.Length} characters to clipboard");
                    }
                }
                finally
                {
                    CloseClipboard();
                }
            }
            else
            {
                Debug.WriteLine("[ClipboardHelper] OpenClipboard failed");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ClipboardHelper] Interop copy failed: {ex.Message}");
        }
    }
}
