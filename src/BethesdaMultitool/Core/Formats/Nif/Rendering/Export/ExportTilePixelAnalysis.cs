namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Export;

/// <summary>Pixel predicates shared by stitched and manifest-based 3D export.</summary>
internal static class ExportTilePixelAnalysis
{
    /// <summary>
    ///     Returns true only for a complete BGRA pixel buffer containing the renderer's transparent-
    ///     black clear value. Uniformity alone is not an emptiness proof: flat terrain, water, or a
    ///     full-screen plane can legitimately produce one non-clear colour.
    /// </summary>
    public static bool IsTransparentClear(ReadOnlySpan<byte> bgra)
    {
        if (bgra.Length < 4 || bgra.Length % 4 != 0)
        {
            return false;
        }

        foreach (var component in bgra)
        {
            if (component != 0)
            {
                return false;
            }
        }

        return true;
    }
}
