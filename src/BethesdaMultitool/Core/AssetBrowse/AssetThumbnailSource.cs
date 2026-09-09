using BethesdaMultitool.CLI.Rendering.Sprite;
using BethesdaMultitool.Core.Formats.Dds;

namespace BethesdaMultitool.Core.AssetBrowse;

/// <summary>
///     Turns an <see cref="AssetNode" /> into a gallery-sized RGBA thumbnail.
///     <para>
///         Two decode families converge on the same <see cref="DecodedTexture" />: the modern
///         texture path (<see cref="DdsTextureDecoder" />) and the classic palettized path
///         (<see cref="SpriteRenderPipeline" />). Going through the sprite pipeline rather than
///         calling the per-game readers directly is deliberate — the pipeline owns palette
///         resolution, and palette choice is exactly where these formats go wrong. A Fallout slide
///         needs its own <c>&lt;STEM&gt;.PAL</c>, a Daggerfall map screen names <c>MAP.PAL</c>, and a
///         critter in CRITTER.DAT can only find COLOR.PAL in a sibling archive. Re-deriving any of
///         that here would let the browser and the CLI disagree.
///     </para>
///     <para>
///         Multi-frame sources (a Daggerfall TEXTURE set, a six-direction FRM) thumbnail as their
///         FIRST frame. The gallery shows one cell per file, not per frame; the preview pane is
///         where every frame is reachable.
///     </para>
/// </summary>
internal static class AssetThumbnailSource
{
    /// <summary>Kinds this class can produce a picture for. Anything else gets an icon, not a thumbnail.</summary>
    public static bool CanRender(AssetNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return node.Kind is AssetNodeKind.Texture or AssetNodeKind.Sprite;
    }

    /// <summary>
    ///     Decodes and scales one node to fit <paramref name="cellPixels" />, or returns null when the
    ///     node has no picture in it.
    ///     <para>
    ///         ⚠ Returns null rather than throwing for a file this build cannot decode yet. A gallery
    ///         over a retail install will always contain formats a given milestone has not reached,
    ///         and one undecodable file must not take out the whole view.
    ///     </para>
    /// </summary>
    public static RgbaThumbnail? TryRender(
        AssetBrowseSession session, AssetNode node, int cellPixels, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(node);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cellPixels);

        if (!CanRender(node))
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        var texture = TryDecode(session, node);
        if (texture is null || texture.Width <= 0 || texture.Height <= 0)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return ThumbnailScaler.Fit(new RgbaThumbnail(texture.Width, texture.Height, texture.Pixels), cellPixels);
    }

    private static DecodedTexture? TryDecode(AssetBrowseSession session, AssetNode node)
    {
        try
        {
            if (node.Kind == AssetNodeKind.Texture && IsDdsName(node.Name))
            {
                var dds = session.FileSystem.TryReadAllBytes(node.VirtualPath);
                return dds is null ? null : DdsTextureDecoder.Decode(dds);
            }

            var bytes = session.FileSystem.TryReadAllBytes(node.VirtualPath);
            if (bytes is null)
            {
                return null;
            }

            var decoded = SpriteRenderPipeline.DecodeBytes(bytes, node.Name, name => FindPalette(session, name));
            return decoded.Frames.Count == 0 ? null : decoded.Frames[0].Texture;
        }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException or IOException
                                      or FileNotFoundException or ArgumentException)
        {
            // Undecodable-for-now is the normal case over a retail install, not an error worth
            // surfacing per file. The node still lists and still extracts.
            return null;
        }
    }

    /// <summary>
    ///     Finds a companion palette anywhere in the opened source. The merged filesystem already
    ///     layers loose files over the game's archives in the profile's override order, so one
    ///     lookup covers Fallout's COLOR.PAL in MASTER.DAT, a slide's own PAL beside it, and a
    ///     modder's loose replacement — without this class knowing which of those it found.
    /// </summary>
    private static byte[]? FindPalette(AssetBrowseSession session, string fileName)
    {
        // Cheap exact hit first: most palettes sit at a path that IS their file name.
        if (session.FileSystem.TryReadAllBytes(fileName) is { } direct)
        {
            return direct;
        }

        foreach (var entry in session.FileSystem.EnumerateFiles())
        {
            var name = Path.GetFileName(entry.Path);
            if (name.Equals(fileName, StringComparison.OrdinalIgnoreCase) &&
                session.FileSystem.TryReadAllBytes(entry.Path) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static bool IsDdsName(string name)
    {
        var extension = Path.GetExtension(name);
        return extension.Equals(".dds", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".ddx", StringComparison.OrdinalIgnoreCase);
    }
}
