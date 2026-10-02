using BethesdaMultitool.Core.Formats.Dds;
using BethesdaMultitool.Core.Media.Sprite;

namespace BethesdaMultitool.Core.AssetBrowse;

/// <summary>
///     The one decode path behind both the gallery thumbnail and the full-size preview.
///     <para>
///         Two decode families converge on the same <see cref="DecodedTexture" />: the modern texture
///         path (<see cref="DdsTextureDecoder" />) and the classic palettized path
///         (<see cref="SpriteRenderPipeline" />). Going through the sprite pipeline rather than calling
///         the per-game readers directly is deliberate: the pipeline owns palette resolution, and
///         palette choice is exactly where these formats go wrong. A Fallout slide needs its own
///         <c>&lt;STEM&gt;.PAL</c>, a Daggerfall map screen names <c>MAP.PAL</c>, and a critter in
///         CRITTER.DAT can only find COLOR.PAL in a sibling archive. Re-deriving any of that here would
///         let the browser and the CLI disagree, and decoding the thumbnail and the preview through two
///         different paths would let the gallery and the preview pane disagree.
///     </para>
/// </summary>
internal static class AssetImageDecoding
{
    /// <summary>
    ///     Decodes every frame of a picture node at its stored size, or returns null when the node has
    ///     no picture in it: a kind this class cannot draw, missing bytes, or bytes this build cannot
    ///     decode yet. A DDS/DDX texture yields one frame; a sprite yields every frame the pipeline
    ///     decodes, in its order, each sharing the decoder's own pixel array.
    ///     <para>
    ///         Returns null rather than throwing for a file this build cannot decode. A gallery over a
    ///         retail install will always contain formats a given milestone has not reached, and one
    ///         undecodable file must not take out the whole view.
    ///     </para>
    /// </summary>
    /// <param name="session">The opened source the node belongs to.</param>
    /// <param name="node">A texture or sprite leaf.</param>
    internal static IReadOnlyList<AssetImageFrame>? TryDecodeFrames(AssetBrowseSession session, AssetNode node)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(node);
        if (!AssetThumbnailSource.CanRender(node))
        {
            return null;
        }

        try
        {
            var bytes = session.FileSystem.TryReadAllBytes(node.VirtualPath);
            if (bytes is null)
            {
                return null;
            }

            if (node.Kind == AssetNodeKind.Texture && IsDdsName(node.Name))
            {
                var texture = DdsTextureDecoder.Decode(bytes);
                return texture is null || texture.Width <= 0 || texture.Height <= 0
                    ? null
                    : Array.AsReadOnly(new[] { new AssetImageFrame(node.Name, texture.Width, texture.Height, texture.Pixels) });
            }

            var decoded = SpriteRenderPipeline.DecodeBytes(bytes, node.Name, name => FindPalette(session, name));
            if (decoded.Frames.Count == 0)
            {
                return null;
            }

            var frames = new AssetImageFrame[decoded.Frames.Count];
            for (var index = 0; index < frames.Length; index++)
            {
                var frame = decoded.Frames[index];
                frames[index] = new AssetImageFrame(frame.Label, frame.Texture.Width, frame.Texture.Height, frame.Texture.Pixels);
            }

            return frames[0].Width <= 0 || frames[0].Height <= 0 ? null : Array.AsReadOnly(frames);
        }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException or IOException
                                      or FileNotFoundException or ArgumentException)
        {
            // Undecodable-for-now is the normal case over a retail install, not an error worth
            // surfacing per file. The node still lists and still extracts.
            return null;
        }
    }

    /// <summary>Whether a texture name takes the DDS decoder (<c>.dds</c> or Xbox <c>.ddx</c>) rather than the sprite pipeline.</summary>
    /// <param name="name">The node's file name.</param>
    internal static bool IsDdsName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var extension = Path.GetExtension(name);
        return extension.Equals(".dds", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".ddx", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Finds a companion palette anywhere in the opened source. The merged filesystem already
    ///     layers loose files over the game's archives in the profile's override order, so one
    ///     lookup covers Fallout's COLOR.PAL in MASTER.DAT, a slide's own PAL beside it, and a
    ///     modder's loose replacement, without this class knowing which of those it found.
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
}
