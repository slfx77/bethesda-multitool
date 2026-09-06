using BethesdaMultitool.Core.Formats.Daggerfall;
using BethesdaMultitool.Core.Formats.Dds;

namespace BethesdaMultitool.Core.AssetBrowse;

/// <summary>
///     Supplies a classic game's sky as a BACKDROP IMAGE for the level view.
///     <para>
///         ⚠ A classic sky is not geometry, and must not be routed through the viewer's native sky
///         pass — <c>BethesdaViewerNativeSkyPolicy</c> admits only raw-NIF sky layers that
///         <c>SkyGeometryRenderer12</c> understands. Daggerfall's sky is 32 sets of 64 frames
///         (512x220 each, one palette PER FRAME), drawn as a flat scrolling backdrop behind the
///         world. So an image layer behind the 3D view is the faithful model here, not a
///         simplification of one.
///     </para>
///     <para>
///         Which set corresponds to which region and weather is NOT established, so a caller picks
///         by index and the UI says so. Claiming a mapping we have not measured would be worse than
///         offering the choice.
///     </para>
/// </summary>
internal static class ClassicSkySource
{
    /// <summary>Frames in one half of a sky set; index 0 is the horizon-forward view.</summary>
    public const int FramesPerHalf = DaggerfallSkyFile.FramesPerHalf;

    /// <summary>True when a node is a Daggerfall sky set.</summary>
    public static bool IsSky(AssetNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return DaggerfallSkyFile.IsSkyFileName(node.Name);
    }

    /// <summary>
    ///     Decodes one frame of a sky set to RGBA, or returns null when the node is not a sky or
    ///     will not parse.
    /// </summary>
    /// <param name="half">Which half of the day the set holds (0 or 1).</param>
    /// <param name="step">Frame within that half.</param>
    public static DecodedTexture? TryLoadFrame(
        AssetBrowseSession session, AssetNode node, int half = 0, int step = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(node);

        if (!IsSky(node))
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var bytes = session.FileSystem.TryReadAllBytes(node.VirtualPath);
            if (bytes is null || bytes.Length == 0)
            {
                return null;
            }

            var sky = DaggerfallSkyFile.Parse(bytes, node.Name);
            var frameIndex = (Math.Clamp(half, 0, 1) * FramesPerHalf) + Math.Clamp(step, 0, FramesPerHalf - 1);

            // Each frame carries its OWN palette — the set is a day cycle, so sharing one palette
            // across frames would flatten dawn and dusk into the same colours.
            return sky.GetFrame(Math.Clamp(half, 0, 1), Math.Clamp(step, 0, FramesPerHalf - 1))
                .ToDecodedTexture(sky.PaletteFor(frameIndex));
        }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException
                                     or IOException or ArgumentException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
