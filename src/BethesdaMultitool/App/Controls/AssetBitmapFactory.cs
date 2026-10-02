// The WriteableBitmap half of JimmyPCTool / AweMultitool's BitmapHelper
//. The arithmetic half lives in
// Core/Imaging/PremultipliedBgra.cs, because App/** is excluded from this repo's net10.0 target
// and would otherwise be untestable.

using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media.Imaging;

namespace BethesdaMultitool;

/// <summary>
///     Wraps premultiplied BGRA bytes in a bitmap a XAML <c>Image</c> can show.
///     <para>
///         Deliberately thin. <see cref="WriteableBitmap" /> is a <c>DependencyObject</c>, so every
///         method here is UI-thread-only; the expensive conversion it consumes
///         (<see cref="Core.Imaging.PremultipliedBgra.FromRgba" />) is pure and belongs on a worker.
///         Keeping the split means UI-thread cost per gallery tile is one allocation and one buffer
///         copy.
///     </para>
/// </summary>
internal static class AssetBitmapFactory
{
    /// <summary>Wraps premultiplied BGRA in a bitmap. UI thread only.</summary>
    public static WriteableBitmap FromPremultipliedBgra(int width, int height, byte[] bgra)
    {
        ArgumentNullException.ThrowIfNull(bgra);

        var bitmap = new WriteableBitmap(width, height);
        using (var stream = bitmap.PixelBuffer.AsStream())
        {
            stream.Write(bgra, 0, bgra.Length);
        }

        bitmap.Invalidate();
        return bitmap;
    }
}
