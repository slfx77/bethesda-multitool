using BethesdaMultitool.Core.Formats.Daggerfall;
using BethesdaMultitool.Core.Formats.Dds;
using BethesdaMultitool.Core.Formats.Xngine.Flic;
using BethesdaMultitool.Core.Imaging;

namespace BethesdaMultitool.Core.AssetBrowse;

/// <summary>
///     A decoded classic movie, addressable by frame. Implementations hold indexed pixels rather
///     than RGBA so a few hundred frames stay affordable, converting only the frame asked for.
/// </summary>
internal interface IVideoFrameSource
{
    /// <summary>Frame width in pixels.</summary>
    int Width { get; }

    /// <summary>Frame height in pixels.</summary>
    int Height { get; }

    /// <summary>Frames available.</summary>
    int FrameCount { get; }

    /// <summary>Seconds each frame is shown for.</summary>
    double SecondsPerFrame { get; }

    /// <summary>Converts one frame to RGBA.</summary>
    DecodedTexture GetFrame(int index);
}

/// <summary>
///     The classic-games implementation of <see cref="IVideoFrameSource" />, covering Arena's
///     <c>.FLC</c>/<c>.CEL</c> (Autodesk FLIC) and Daggerfall's <c>.VID</c>.
///     <para>
///         ⚠ Frames are MATERIALISED at open, not decoded on demand, and that is forced by the
///         formats rather than chosen for convenience. A Daggerfall VID paints onto ONE persistent
///         canvas — an incremental frame's runs SKIP pixels rather than carrying values — so frame
///         N simply does not exist without replaying every frame before it. Seeking a
///         lazily-decoded VID would either be wrong or quietly restart the decode each time.
///     </para>
///     <para>
///         Indexed storage keeps that affordable: a 320×200 Daggerfall frame is 64 KB indexed
///         against 256 KB as RGBA, and the longest retail movie is a few hundred frames. Each frame
///         also carries its OWN palette, which both formats genuinely vary mid-clip.
///     </para>
/// </summary>
internal sealed class ClassicVideoClip : IVideoFrameSource
{
    private readonly IReadOnlyList<(IndexedBitmap Bitmap, Palette Palette)> _frames;

    private ClassicVideoClip(
        string name, int width, int height, double secondsPerFrame,
        IReadOnlyList<(IndexedBitmap, Palette)> frames)
    {
        Name = name;
        Width = width;
        Height = height;
        SecondsPerFrame = secondsPerFrame;
        _frames = frames;
    }

    /// <summary>Source file name, for display.</summary>
    public string Name { get; }

    public int Width { get; }

    public int Height { get; }

    public int FrameCount => _frames.Count;

    public double SecondsPerFrame { get; }

    public DecodedTexture GetFrame(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _frames.Count);

        var (bitmap, palette) = _frames[index];
        return bitmap.ToDecodedTexture(palette);
    }

    /// <summary>True for a file extension this class can open.</summary>
    public static bool CanOpen(AssetNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        var extension = Path.GetExtension(node.Name);
        return extension.Equals(".flc", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".cel", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".vid", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Decodes a movie out of the session, or returns null when it will not open.</summary>
    public static ClassicVideoClip? TryOpen(
        AssetBrowseSession session, AssetNode node, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(node);

        if (!CanOpen(node))
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

            return FlicFile.IsFlic(bytes)
                ? FromFlic(FlicFile.Parse(bytes, node.Name))
                : FromVid(DaggerfallVidFile.Parse(bytes, node.Name));
        }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException
                                     or IOException or ArgumentException)
        {
            return null;
        }
    }

    private static ClassicVideoClip FromFlic(FlicFile flic)
    {
        var frames = flic.Frames.Select(f => (f.Image, f.Palette)).ToList();
        return new ClassicVideoClip(flic.Name, flic.Width, flic.Height, flic.SecondsPerFrame, frames);
    }

    private static ClassicVideoClip FromVid(DaggerfallVidFile vid)
    {
        // EnumerateFrames replays the block stream, which is the only way to resolve the
        // incremental frames; the result is retained so playback does not replay per seek.
        var frames = vid.EnumerateFrames().Select(f => (f.Bitmap, f.Palette)).ToList();

        // The VID header's delay is in the same units the exporter uses for its frame timing.
        var secondsPerFrame = vid.GlobalDelay > 0 ? vid.GlobalDelay / 1000.0 : 1.0 / 15.0;
        return new ClassicVideoClip(vid.Name, vid.Width, vid.Height, secondsPerFrame, frames);
    }
}
