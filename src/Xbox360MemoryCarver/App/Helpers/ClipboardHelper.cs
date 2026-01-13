using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Xbox360MemoryCarver;

/// <summary>
///     Helper class for clipboard operations using Win32 interop.
///     WinUI 3 clipboard APIs can have COM threading issues, so we use native calls.
/// </summary>
internal static class ClipboardHelper
{
    private const uint CF_UNICODETEXT = 13;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr hMem);

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

    /// <summary>
    ///     Get text from clipboard using Win32 interop.
    /// </summary>
    /// <returns>The clipboard text, or null if no text is available.</returns>
    public static string? GetText()
    {
        try
        {
            if (!IsClipboardFormatAvailable(CF_UNICODETEXT))
            {
                Debug.WriteLine("[ClipboardHelper] No text format available in clipboard");
                return null;
            }

            if (OpenClipboard(IntPtr.Zero))
            {
                try
                {
                    var hGlobal = GetClipboardData(CF_UNICODETEXT);
                    if (hGlobal == IntPtr.Zero)
                    {
                        Debug.WriteLine("[ClipboardHelper] GetClipboardData returned null");
                        return null;
                    }

                    var lpStr = GlobalLock(hGlobal);
                    if (lpStr == IntPtr.Zero)
                    {
                        Debug.WriteLine("[ClipboardHelper] GlobalLock failed");
                        return null;
                    }

                    try
                    {
                        var text = Marshal.PtrToStringUni(lpStr);
                        Debug.WriteLine($"[ClipboardHelper] Retrieved {text?.Length ?? 0} characters from clipboard");
                        return text;
                    }
                    finally
                    {
                        GlobalUnlock(hGlobal);
                    }
                }
                finally
                {
                    CloseClipboard();
                }
            }

            Debug.WriteLine("[ClipboardHelper] OpenClipboard failed");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ClipboardHelper] Interop get failed: {ex.Message}");
        }

        return null;
    }

    /// <summary>
    ///     Check if there is text available in the clipboard.
    /// </summary>
    public static bool HasText()
    {
        try
        {
            return IsClipboardFormatAvailable(CF_UNICODETEXT);
        }
        catch
        {
            return false;
        }
    }
}
