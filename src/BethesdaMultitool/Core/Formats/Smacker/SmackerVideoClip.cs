// ORIGINAL CLEAN-ROOM IMPLEMENTATION.
//
// Written from our own reverse engineering of RAD Game Tools' Smacker decoder as statically linked
// into two shipped games — Redguard's RG.EXE ("*** Smacker Version: 3.2b***") and Battlespire's
// GAME.EXE ("*** Smacker Version: 3.0k***") — via our Ghidra decompilations at
// tools/GhidraProject/ClassicRE/RG.EXE.decompiled.txt and GAME.EXE.decompiled.txt and our own
// capstone disassembly of the unpacked LE images, as written up in the specification document
//   scratchpad .../gap2/smacker/SPEC.md  ("Smacker Video (SMK2) - Format Specification").
//
// NO FFmpeg- or libav-derived code, and no other third-party Smacker implementation, was consulted,
// read, copied or paraphrased.

using BethesdaMultitool.Core.AssetBrowse;
using BethesdaMultitool.Core.Formats.Dds;
using BethesdaMultitool.Core.Imaging;

namespace BethesdaMultitool.Core.Formats.Smacker;

/// <summary>
///     The Smacker implementation of <see cref="IVideoFrameSource" />, so an <c>.smk</c> plays
///     through the same seam as Arena's FLIC, Daggerfall's VID and Bink.
///     <para>
///         Frames are NOT materialised at open: Redguard's <c>Intro.smk</c> is 6,895 frames and,
///         as RGBA at its displayed 640x480, would be 8.5 GB. Like Bink, <see cref="GetFrame" />
///         decodes forward from the current position and replays from frame 0 on a backward seek;
///         the canvas is one indexed byte per pixel so a replay is cheap.
///     </para>
///     <para>
///         ⚠ <see cref="Height" /> is the DISPLAY height. Every Redguard movie and three
///         Battlespire ones are stored at 640x240 with the Y-doubled or Y-interlaced flag, and the
///         game doubles the surface it draws them on; this clip does the same by repeating each
///         stored row, so what the viewer shows has the game's aspect. The stored frame is what
///         the ffmpeg oracle was matched against — see <see cref="SmackerVideoDecoder" />.
///     </para>
/// </summary>
internal sealed class SmackerVideoClip : IVideoFrameSource
{
    private readonly SmackerVideoDecoder _decoder;

    private SmackerVideoClip(SmackerFile file)
    {
        File = file;
        _decoder = new SmackerVideoDecoder(file);
    }

    /// <summary>The parsed container, for callers that want the header or the frame table.</summary>
    internal SmackerFile File { get; }

    /// <summary>Source file name, for display.</summary>
    internal string Name => File.Name;

    public int Width => File.Width;

    public int Height => File.DisplayHeight;

    public int FrameCount => File.FrameCount;

    public double SecondsPerFrame => File.SecondsPerFrame;

    public DecodedTexture GetFrame(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, FrameCount);

        _decoder.DecodeFrame(index);
        var stored = _decoder.GetFrameIndices();
        var palette = Palette.FromRgb8(_decoder.PaletteRgb);
        var bitmap = new IndexedBitmap(Width, Height, ExpandRows(stored, File.Width, File.Height, Height));
        return bitmap.ToDecodedTexture(palette);
    }

    /// <summary>Repeats each stored row when the display height is double the stored height.</summary>
    internal static byte[] ExpandRows(byte[] stored, int width, int storedHeight, int displayHeight)
    {
        ArgumentNullException.ThrowIfNull(stored);

        if (displayHeight == storedHeight)
        {
            return stored;
        }

        var expanded = new byte[width * displayHeight];
        for (var y = 0; y < displayHeight; y++)
        {
            Buffer.BlockCopy(stored, (y * storedHeight / displayHeight) * width, expanded, y * width, width);
        }

        return expanded;
    }

    /// <summary>True for a file extension this class can open: <c>.smk</c>.</summary>
    internal static bool CanOpen(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);

        return Path.GetExtension(fileName).Equals(".smk", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Opens a movie from its bytes, or returns null when it will not open.</summary>
    internal static SmackerVideoClip? TryOpen(byte[] bytes, string name)
    {
        if (bytes is null || bytes.Length == 0 || !SmackerFile.IsSmacker(bytes))
        {
            return null;
        }

        try
        {
            return new SmackerVideoClip(SmackerFile.Parse(bytes, name));
        }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException
                                      or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Opens a movie whose container has already been parsed.</summary>
    internal static SmackerVideoClip Open(SmackerFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        return new SmackerVideoClip(file);
    }
}
