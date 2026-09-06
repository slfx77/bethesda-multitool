using System.Buffers.Binary;

using BethesdaMultitool.Core.Imaging;

namespace BethesdaMultitool.Core.Formats.Fallout;

/// <summary>
///     The Fallout <c>.FRM</c> sprite — every piece of 2D art in Fallout 1 and 2, from interface
///     panels to the six-direction critter animations. Clean-roomed from the fodev format
///     documentation and measured against the retail archives 2026-09-05.
///     <para>
///         Everything is BIG-endian, the DOS-era convention this family keeps. The header is
///         <b>0x3E bytes</b>: u32 version (4 on all but one retail sprite), u16 frames per second, u16
///         action frame, u16 frames per direction, <c>i16 xShift[6]</c> at 0x0A, <c>i16 yShift[6]</c>
///         at 0x16, <c>u32 directionOffset[6]</c> at 0x22 and u32 frame-area size at 0x3A. Frame
///         data starts at 0x3E and each direction's offset is relative to that.
///     </para>
///     <para>
///         ⚠ The header is 0x3E, not the 0x3A its last field starts at — an easy off-by-four that
///         is measurable rather than arguable: reading frames from 0x3A fails on all 600 sampled
///         retail sprites, and from 0x3E all 600 walk with <c>width * height == the frame's own
///         declared size</c>.
///     </para>
///     <para>
///         A frame is u16 width, u16 height, u32 size, i16 xOffset, i16 yOffset, then <c>size</c>
///         bytes of 8-bit palette indices. Directions that share artwork share an offset, so the
///         six entries commonly point at fewer than six runs — a background like
///         <c>BACK1.FRM</c> has one direction of one frame.
///     </para>
/// </summary>
internal sealed class FalloutFrmFile
{
    /// <summary>Bytes of header before the frame area.</summary>
    public const int HeaderLength = 0x3E;

    /// <summary>Bytes of fixed fields at the head of each frame.</summary>
    public const int FrameHeaderLength = 12;

    /// <summary>Directions a sprite can carry; entries may share offsets.</summary>
    public const int DirectionCount = 6;

    /// <summary>Offset of the direction-offset array.</summary>
    public const int DirectionOffsetsPosition = 0x22;

    /// <summary>Offset of the frame-area size.</summary>
    public const int FrameAreaSizePosition = 0x3A;

    /// <summary>
    ///     The version all but one retail sprite declares. <c>ART\INVEN\OKNIFE.FRM</c> alone says 3,
    ///     and its frames tile exactly like the other 4,927 — so nothing dispatches on this word.
    /// </summary>
    public const uint RetailVersion = 4;

    private FalloutFrmFile(
        string name, uint version, int framesPerSecond, int actionFrame, int framesPerDirection,
        IReadOnlyList<int> xShifts, IReadOnlyList<int> yShifts, IReadOnlyList<FalloutFrmDirection> directions)
    {
        Name = name;
        Version = version;
        FramesPerSecond = framesPerSecond;
        ActionFrame = actionFrame;
        FramesPerDirection = framesPerDirection;
        XShifts = xShifts;
        YShifts = yShifts;
        Directions = directions;
    }

    /// <summary>Source file name, for messages.</summary>
    public string Name { get; }

    /// <summary>Format version; 4 across retail.</summary>
    public uint Version { get; }

    /// <summary>Playback rate. 0 means the engine's default.</summary>
    public int FramesPerSecond { get; }

    /// <summary>The frame at which the animation's action lands (a hit, a shot).</summary>
    public int ActionFrame { get; }

    /// <summary>Frames in each direction's run.</summary>
    public int FramesPerDirection { get; }

    /// <summary>Per-direction x shift.</summary>
    public IReadOnlyList<int> XShifts { get; }

    /// <summary>Per-direction y shift.</summary>
    public IReadOnlyList<int> YShifts { get; }

    /// <summary>The six directions, in order; those sharing artwork share frames.</summary>
    public IReadOnlyList<FalloutFrmDirection> Directions { get; }

    /// <summary>Every distinct frame, in file order.</summary>
    public IEnumerable<FalloutFrmFrame> DistinctFrames =>
        Directions.DistinctBy(d => d.DataOffset).SelectMany(d => d.Frames);

    /// <summary>True for a <c>.FRM</c> or one of the numbered <c>.FR0</c>..<c>.FR5</c> siblings.</summary>
    public static bool IsFrmFileName(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        var extension = Path.GetExtension(fileName);
        if (extension.Equals(".frm", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // CRITTER.DAT splits a critter's directions into .FR0 through .FR5 beside the .FRM.
        return extension.Length == 4
               && extension.StartsWith(".fr", StringComparison.OrdinalIgnoreCase)
               && char.IsAsciiDigit(extension[3]);
    }

    /// <summary>Parses a sprite, throwing <see cref="InvalidDataException" /> when a frame does not fit.</summary>
    public static FalloutFrmFile Parse(ReadOnlySpan<byte> bytes, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (bytes.Length < HeaderLength)
        {
            throw new InvalidDataException($"'{name}' is {bytes.Length} bytes, shorter than the {HeaderLength}-byte FRM header.");
        }

        var version = BinaryPrimitives.ReadUInt32BigEndian(bytes);
        var framesPerSecond = BinaryPrimitives.ReadUInt16BigEndian(bytes[4..]);
        var actionFrame = BinaryPrimitives.ReadUInt16BigEndian(bytes[6..]);
        var framesPerDirection = BinaryPrimitives.ReadUInt16BigEndian(bytes[8..]);
        if (framesPerDirection == 0)
        {
            throw new InvalidDataException($"'{name}' declares no frames per direction.");
        }

        var xShifts = new int[DirectionCount];
        var yShifts = new int[DirectionCount];
        var offsets = new uint[DirectionCount];
        for (var i = 0; i < DirectionCount; i++)
        {
            xShifts[i] = BinaryPrimitives.ReadInt16BigEndian(bytes[(0x0A + 2 * i)..]);
            yShifts[i] = BinaryPrimitives.ReadInt16BigEndian(bytes[(0x16 + 2 * i)..]);
            offsets[i] = BinaryPrimitives.ReadUInt32BigEndian(bytes[(DirectionOffsetsPosition + 4 * i)..]);
        }

        // Directions sharing artwork share an offset, so each run is read once and referenced.
        var runs = new Dictionary<uint, IReadOnlyList<FalloutFrmFrame>>();
        var directions = new FalloutFrmDirection[DirectionCount];
        for (var i = 0; i < DirectionCount; i++)
        {
            if (!runs.TryGetValue(offsets[i], out var frames))
            {
                frames = ReadFrames(bytes, name, i, offsets[i], framesPerDirection);
                runs[offsets[i]] = frames;
            }

            directions[i] = new FalloutFrmDirection(i, offsets[i], xShifts[i], yShifts[i], frames);
        }

        return new FalloutFrmFile(name, version, framesPerSecond, actionFrame, framesPerDirection, xShifts, yShifts, directions);
    }

    private static List<FalloutFrmFrame> ReadFrames(
        ReadOnlySpan<byte> bytes, string name, int direction, uint dataOffset, int frameCount)
    {
        var frames = new List<FalloutFrmFrame>(frameCount);
        var position = HeaderLength + (long)dataOffset;
        for (var i = 0; i < frameCount; i++)
        {
            if (position + FrameHeaderLength > bytes.Length)
            {
                throw new InvalidDataException(
                    $"'{name}' direction {direction} frame {i} starts at {position}, past its {bytes.Length} bytes.");
            }

            var at = (int)position;
            var width = BinaryPrimitives.ReadUInt16BigEndian(bytes[at..]);
            var height = BinaryPrimitives.ReadUInt16BigEndian(bytes[(at + 2)..]);
            var size = BinaryPrimitives.ReadUInt32BigEndian(bytes[(at + 4)..]);
            var xOffset = BinaryPrimitives.ReadInt16BigEndian(bytes[(at + 8)..]);
            var yOffset = BinaryPrimitives.ReadInt16BigEndian(bytes[(at + 10)..]);

            // The size field is redundant with the dimensions, which makes it a free integrity
            // check — and the check that fixed the header size.
            if ((long)width * height != size)
            {
                throw new InvalidDataException(
                    $"'{name}' direction {direction} frame {i} is {width}x{height} but declares {size} bytes.");
            }

            var pixelsAt = at + FrameHeaderLength;
            if (pixelsAt + (long)size > bytes.Length)
            {
                throw new InvalidDataException(
                    $"'{name}' direction {direction} frame {i} needs {size} pixel bytes, past its {bytes.Length} bytes.");
            }

            frames.Add(new FalloutFrmFrame(
                new IndexedBitmap(width, height, bytes.Slice(pixelsAt, (int)size).ToArray(), xOffset, yOffset)));
            position = pixelsAt + size;
        }

        return frames;
    }
}

/// <summary>One direction's run of frames. <see cref="DataOffset" /> is shared when directions share artwork.</summary>
internal sealed record FalloutFrmDirection(
    int Index, uint DataOffset, int XShift, int YShift, IReadOnlyList<FalloutFrmFrame> Frames);

/// <summary>One frame: 8-bit palette indices plus the shift the engine draws it at.</summary>
internal readonly record struct FalloutFrmFrame(IndexedBitmap Bitmap)
{
    public int Width => Bitmap.Width;

    public int Height => Bitmap.Height;
}
