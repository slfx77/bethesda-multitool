// Conversion rules ported from JimmyPCTool / AweMultitool (https://github.com/slfx77/JimmyPCTool),
// MIT licence — src/AweMultitool/App/Controls/BitmapHelper.cs. Split differently here: that project
// keeps the arithmetic beside its WriteableBitmap wrapper, but App/** is excluded from this repo's
// net10.0 target framework, so the pure half lives in Core/ where it can actually be tested.

namespace BethesdaMultitool.Core.Imaging;

/// <summary>
///     Converts straight RGBA to the premultiplied BGRA a XAML composition surface expects.
///     <para>
///         ⚠ Colour is zeroed wherever alpha is zero, and that is not cosmetic. Palettized sources
///         here keep their key colour in RGB and only clear alpha — <see cref="Palette.WithTransparentIndex" />
///         literally writes <c>rgba[index * 4 + 3] = 0</c> and leaves the triplet alone. Fallout's
///         COLOR.PAL holds <c>FF FF FF</c> at index 0, the transparent index, so on a premultiplied
///         surface every transparent pixel of every FRM would ghost WHITE without this step.
///     </para>
///     <para>
///         Zeroing is a no-op under straight alpha and correct under premultiplied, so it is done
///         unconditionally rather than after establishing which one the framework wants.
///     </para>
/// </summary>
internal static class PremultipliedBgra
{
    /// <summary>Bytes per pixel in both the input and the output.</summary>
    public const int BytesPerPixel = 4;

    /// <summary>
    ///     Converts straight RGBA to premultiplied BGRA. Pure arithmetic — call it off the UI
    ///     thread; only the bitmap wrapper that consumes the result is thread-affine.
    /// </summary>
    public static byte[] FromRgba(ReadOnlySpan<byte> rgba)
    {
        if (rgba.Length % BytesPerPixel != 0)
        {
            throw new ArgumentException(
                $"An RGBA buffer must be a whole number of {BytesPerPixel}-byte pixels; got {rgba.Length}.",
                nameof(rgba));
        }

        var bgra = new byte[rgba.Length];
        for (var i = 0; i < rgba.Length; i += BytesPerPixel)
        {
            var alpha = rgba[i + 3];
            if (alpha == 0)
            {
                // Left as zero: premultiplying by zero gives zero, and this is the case that would
                // otherwise leak the key colour.
                continue;
            }

            if (alpha == 0xFF)
            {
                bgra[i] = rgba[i + 2];
                bgra[i + 1] = rgba[i + 1];
                bgra[i + 2] = rgba[i];
                bgra[i + 3] = 0xFF;
                continue;
            }

            bgra[i] = (byte)(rgba[i + 2] * alpha / 255);
            bgra[i + 1] = (byte)(rgba[i + 1] * alpha / 255);
            bgra[i + 2] = (byte)(rgba[i] * alpha / 255);
            bgra[i + 3] = alpha;
        }

        return bgra;
    }
}
