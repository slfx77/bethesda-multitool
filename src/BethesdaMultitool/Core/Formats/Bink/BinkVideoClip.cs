// ORIGINAL CLEAN-ROOM IMPLEMENTATION.
//
// Written from our own reverse engineering of RAD Game Tools' shipped decoder binkw32.dll
// (Fallout Tactics, md5 ecbd8213e89f8afde368f8eb05ff5a9c), via our Ghidra decompilation at
// tools/GhidraProject/ClassicRE/binkw32.dll.decompiled.txt and our own capstone disassembly of the
// same file, as written up in the specification document
//   scratchpad .../cleanroom/bink/SPEC.md  ("Bink Video (BIKi) - Format Specification").
//
// NO FFmpeg- or libav-derived code, and no other third-party Bink implementation, was consulted,
// read, copied or paraphrased.

using BethesdaMultitool.Core.AssetBrowse;
using BethesdaMultitool.Core.Formats.Dds;

namespace BethesdaMultitool.Core.Formats.Bink;

/// <summary>
///     The Bink implementation of <see cref="IVideoFrameSource" />, so a <c>.bik</c> plays through
///     the same seam as Arena's FLIC and Daggerfall's VID.
///     <para>
///         ⚠ Unlike <c>ClassicVideoClip</c>, frames are NOT materialised at open. The corpus's
///         longest movie is 8,987 frames of 720x486; holding those as RGBA would be 12 GB. Bink is
///         also cheap to replay — every frame is inter-coded against exactly one predecessor and the
///         container marks keyframes — so <see cref="GetFrame" /> decodes forward from the current
///         position, or replays from the preceding keyframe when asked to seek backwards.
///     </para>
/// </summary>
internal sealed class BinkVideoClip : IVideoFrameSource
{
    private readonly BinkVideoDecoder _decoder;

    private BinkVideoClip(BinkFile file)
    {
        File = file;
        _decoder = new BinkVideoDecoder(file);
    }

    /// <summary>The parsed container, for callers that want the header or the frame table.</summary>
    internal BinkFile File { get; }

    /// <summary>Source file name, for display.</summary>
    internal string Name => File.Name;

    public int Width => File.Width;

    public int Height => File.Height;

    public int FrameCount => File.FrameCount;

    public double SecondsPerFrame => File.SecondsPerFrame;

    public DecodedTexture GetFrame(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, FrameCount);

        var rgba = _decoder.DecodeFrameRgba(index);
        return DecodedTexture.FromBaseLevel(rgba, Width, Height, false);
    }

    /// <summary>
    ///     True for a file extension this class can open: <c>.bik</c>, and <c>.mve</c> because the
    ///     cancelled Van Buren prototype's two <c>Movies\*.mve</c> are <c>BIKi</c> under that name
    ///     (measured 2026-09-08: <c>BISlogo.mve</c> 185 frames and <c>IPlogo.mve</c> 230, both
    ///     640x320, every frame byte-exact against the ffmpeg oracle). Fallout's genuine Interplay
    ///     MVEs share the extension and fail <see cref="BinkFile.IsBink" />, so
    ///     <see cref="TryOpen" /> returns null for them rather than throwing.
    /// </summary>
    internal static bool CanOpen(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);

        var extension = Path.GetExtension(fileName);
        return extension.Equals(".bik", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".mve", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Opens a movie from its bytes, or returns null when it will not open.</summary>
    internal static BinkVideoClip? TryOpen(byte[] bytes, string name)
    {
        if (bytes is null || bytes.Length == 0 || !BinkFile.IsBink(bytes))
        {
            return null;
        }

        try
        {
            var file = BinkFile.Parse(bytes, name);
            return file.IsDecodableVideo ? new BinkVideoClip(file) : null;
        }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException
                                      or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Opens a movie whose container has already been parsed.</summary>
    internal static BinkVideoClip Open(BinkFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        return new BinkVideoClip(file);
    }
}
