// Ported from daggerfall-unity's DaggerfallConnect API (MIT License),
//   https://github.com/Interkarma/daggerfall-unity — Assets/Scripts/API/VidFile.cs (header,
//   block types, palette scaling and the frame run-length grammar). License texts are collected
//   centrally in THIRD_PARTY_LICENSES.

using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Imaging;

namespace BethesdaMultitool.Core.Formats.Daggerfall;

/// <summary>Block kinds in a Daggerfall <c>.VID</c> movie.</summary>
internal enum DaggerfallVidBlockType : byte
{
    Null = 0,
    VideoIncrementalFrame = 1,
    Palette = 2,
    VideoStartFrame = 3,
    VideoIncrementalRowOffsetFrame = 4,
    EndOfFile = 20,
    AudioStartFrame = 124,
    AudioIncrementalFrame = 125
}

/// <summary>One decoded movie frame: the full canvas after this block was applied.</summary>
internal sealed record DaggerfallVidFrame(int Index, DaggerfallVidBlockType BlockType, int Delay, IndexedBitmap Bitmap, Palette Palette);

/// <summary>
///     A Daggerfall movie, <c>*.VID</c> in ARENA2: a 15-byte header, a 768-byte palette block, then
///     a stream of blocks — a full first frame, incremental frames (optionally starting at a row),
///     interleaved 11,025 Hz 8-bit unsigned PCM, and an end marker. Frames paint onto one persistent
///     canvas, so an incremental block leaves untouched pixels alone.
///     <para>
///         Measured on retail (2026-09-03): 17 files, 320x200 except DAG2.VID at 256x200; every one
///         walks to the byte, ends on the end-of-file block, and decodes EXACTLY its declared frame
///         count (35-1,164). None carries a palette block after the header — the reference's handling
///         of a later palette block would desynchronise its stream, so this reader applies one
///         instead.
///     </para>
/// </summary>
internal sealed class DaggerfallVidFile
{
    /// <summary>Sample rate of the interleaved audio.</summary>
    public const int SampleRate = 11025;

    /// <summary>Bytes in a palette block's payload.</summary>
    public const int PaletteLength = 768;

    /// <summary>Header bytes before the first block.</summary>
    public const int HeaderLength = 15;

    private const string Tag = "VID";

    private readonly byte[] _bytes;

    private DaggerfallVidFile(byte[] bytes)
    {
        _bytes = bytes;
    }

    /// <summary>Logical file name.</summary>
    public required string Name { get; init; }

    /// <summary>Frame count declared in the header.</summary>
    public required int DeclaredFrameCount { get; init; }

    /// <summary>Frames actually decoded from the block stream.</summary>
    public required int FrameCount { get; init; }

    /// <summary>Canvas width.</summary>
    public required int Width { get; init; }

    /// <summary>Canvas height.</summary>
    public required int Height { get; init; }

    /// <summary>Header delay field, shared by the whole movie.</summary>
    public required int GlobalDelay { get; init; }

    /// <summary>Header word after the tag (512 on every retail file).</summary>
    public required int Unknown1 { get; init; }

    /// <summary>Header word after the delay (14 on every retail file).</summary>
    public required int Unknown2 { get; init; }

    /// <summary>The palette from the header's palette block.</summary>
    public required Palette Palette { get; init; }

    /// <summary>Interleaved audio, concatenated in stream order.</summary>
    public required ReadOnlyMemory<byte> Audio { get; init; }

    /// <summary>
    ///     How many blocks of each kind the stream holds, the header's own palette block included
    ///     (so a movie that never changes palette still reports one).
    /// </summary>
    public required IReadOnlyDictionary<DaggerfallVidBlockType, int> BlockCounts { get; init; }

    /// <summary>True when the stream ended on the end-of-file block.</summary>
    public required bool EndOfFileSeen { get; init; }

    /// <summary>Playing time implied by the audio track.</summary>
    public double AudioSeconds => Audio.Length / (double)SampleRate;

    /// <summary>True for a <c>.VID</c> name.</summary>
    public static bool IsVidFileName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return name.EndsWith(".VID", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when the bytes open with the movie tag.</summary>
    public static bool IsVid(ReadOnlySpan<byte> bytes)
    {
        return bytes.Length >= 4 && bytes[0] == (byte)'V' && bytes[1] == (byte)'I' && bytes[2] == (byte)'D';
    }

    /// <summary>Parses the header and walks the stream for its audio and block census.</summary>
    public static DaggerfallVidFile Parse(byte[] bytes, string name)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(name);

        if (!IsVid(bytes))
        {
            throw new InvalidDataException($"{name} does not start with '{Tag}'.");
        }

        if (bytes.Length < HeaderLength + 1 + PaletteLength)
        {
            throw new InvalidDataException($"{name}: {bytes.Length} bytes is shorter than a header plus its palette block.");
        }

        var unknown1 = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(3));
        var declaredFrames = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(5));
        var width = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(7));
        var height = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(9));
        var globalDelay = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(11));
        var unknown2 = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(13));

        if (width <= 0 || height <= 0 || (long)width * height > 4_000_000)
        {
            throw new InvalidDataException($"{name}: implausible canvas {width}x{height}.");
        }

        if (bytes[HeaderLength] != (byte)DaggerfallVidBlockType.Palette)
        {
            throw new InvalidDataException($"{name}: no palette block follows the header.");
        }

        var palette = Palette.FromVga6Bit(bytes.AsSpan(HeaderLength + 1, PaletteLength));

        var file = new DaggerfallVidFile(bytes)
        {
            Name = name,
            DeclaredFrameCount = declaredFrames,
            FrameCount = 0,
            Width = width,
            Height = height,
            GlobalDelay = globalDelay,
            Unknown1 = unknown1,
            Unknown2 = unknown2,
            Palette = palette,
            Audio = ReadOnlyMemory<byte>.Empty,
            BlockCounts = new Dictionary<DaggerfallVidBlockType, int>(),
            EndOfFileSeen = false
        };

        var counts = new Dictionary<DaggerfallVidBlockType, int>();
        var audio = new List<byte>();
        var frames = 0;
        var endOfFile = false;
        foreach (var block in file.Walk(null))
        {
            counts[block.Type] = counts.GetValueOrDefault(block.Type) + 1;
            switch (block.Type)
            {
                case DaggerfallVidBlockType.AudioStartFrame:
                case DaggerfallVidBlockType.AudioIncrementalFrame:
                    audio.AddRange(bytes.AsSpan(block.AudioOffset, block.AudioLength));
                    break;
                case DaggerfallVidBlockType.VideoStartFrame:
                case DaggerfallVidBlockType.VideoIncrementalFrame:
                case DaggerfallVidBlockType.VideoIncrementalRowOffsetFrame:
                    frames++;
                    break;
                case DaggerfallVidBlockType.EndOfFile:
                    endOfFile = true;
                    break;
                default:
                    break;
            }
        }

        return new DaggerfallVidFile(bytes)
        {
            Name = name,
            DeclaredFrameCount = declaredFrames,
            FrameCount = frames,
            Width = width,
            Height = height,
            GlobalDelay = globalDelay,
            Unknown1 = unknown1,
            Unknown2 = unknown2,
            Palette = palette,
            Audio = audio.ToArray(),
            BlockCounts = counts,
            EndOfFileSeen = endOfFile
        };
    }

    /// <summary>
    ///     Decodes every frame in order. The canvas persists between frames, so each yielded bitmap
    ///     is a snapshot copy of the whole canvas after that block.
    /// </summary>
    public IEnumerable<DaggerfallVidFrame> EnumerateFrames()
    {
        var canvas = new byte[Width * Height];
        var palette = Palette;
        var index = 0;
        foreach (var block in Walk(canvas))
        {
            if (block.Type == DaggerfallVidBlockType.Palette)
            {
                palette = Palette.FromVga6Bit(_bytes.AsSpan(block.AudioOffset, PaletteLength));
                continue;
            }

            if (block.Type is not (DaggerfallVidBlockType.VideoStartFrame
                or DaggerfallVidBlockType.VideoIncrementalFrame
                or DaggerfallVidBlockType.VideoIncrementalRowOffsetFrame))
            {
                continue;
            }

            yield return new DaggerfallVidFrame(
                index++,
                block.Type,
                block.Delay,
                new IndexedBitmap(Width, Height, canvas.AsSpan().ToArray()),
                palette);
        }
    }

    /// <summary>
    ///     Walks the block stream once. When <paramref name="canvas" /> is null, frame payloads are
    ///     skipped rather than painted, which is what the header/audio pass needs.
    /// </summary>
    private IEnumerable<BlockInfo> Walk(byte[]? canvas)
    {
        var position = HeaderLength;
        while (position < _bytes.Length)
        {
            var type = (DaggerfallVidBlockType)_bytes[position];
            position++;

            switch (type)
            {
                case DaggerfallVidBlockType.Null:
                    yield return new BlockInfo(type, 0, 0, 0);
                    continue;
                case DaggerfallVidBlockType.EndOfFile:
                    yield return new BlockInfo(type, 0, 0, 0);
                    yield break;
                case DaggerfallVidBlockType.Palette:
                    Require(position, PaletteLength, "palette block");
                    yield return new BlockInfo(type, position, PaletteLength, 0);
                    position += PaletteLength;
                    continue;
                case DaggerfallVidBlockType.AudioStartFrame:
                {
                    Require(position, 5, "audio start block");
                    var rate = _bytes[position + 2];
                    if (rate != 166)
                    {
                        throw new InvalidDataException($"{Name}: audio playback rate {rate} is not the 11,025 Hz value 166.");
                    }

                    var length = BinaryPrimitives.ReadUInt16LittleEndian(_bytes.AsSpan(position + 3));
                    Require(position + 5, length, "audio start data");
                    yield return new BlockInfo(type, position + 5, length, 0);
                    position += 5 + length;
                    continue;
                }

                case DaggerfallVidBlockType.AudioIncrementalFrame:
                {
                    Require(position, 2, "audio block");
                    var length = BinaryPrimitives.ReadUInt16LittleEndian(_bytes.AsSpan(position));
                    Require(position + 2, length, "audio data");
                    yield return new BlockInfo(type, position + 2, length, 0);
                    position += 2 + length;
                    continue;
                }

                case DaggerfallVidBlockType.VideoStartFrame:
                case DaggerfallVidBlockType.VideoIncrementalFrame:
                case DaggerfallVidBlockType.VideoIncrementalRowOffsetFrame:
                {
                    Require(position, 2, "frame delay");
                    var delay = BinaryPrimitives.ReadUInt16LittleEndian(_bytes.AsSpan(position));
                    position += 2;

                    var start = 0;
                    if (type == DaggerfallVidBlockType.VideoIncrementalRowOffsetFrame)
                    {
                        Require(position, 2, "frame row offset");
                        start = BinaryPrimitives.ReadUInt16LittleEndian(_bytes.AsSpan(position)) * Width;
                        position += 2;
                    }

                    position = ReadFrame(type, position, start, canvas);
                    yield return new BlockInfo(type, 0, 0, delay);
                    continue;
                }

                default:
                    throw new InvalidDataException($"{Name}: unknown block type {(byte)type} at {position - 1}.");
            }
        }
    }

    /// <summary>
    ///     Paints (or skips) one frame's run-length payload. A full frame's run carries a value byte
    ///     after the count; an incremental frame's run instead SKIPS that many pixels, leaving the
    ///     canvas as it was.
    /// </summary>
    private int ReadFrame(DaggerfallVidBlockType type, int position, int start, byte[]? canvas)
    {
        var full = type == DaggerfallVidBlockType.VideoStartFrame;
        var pixels = Width * Height;
        var cursor = start;
        while (cursor < pixels)
        {
            Require(position, 1, "frame data");
            var control = _bytes[position];
            position++;

            if (control == 0)
            {
                break;
            }

            if (control >= 0x80)
            {
                var count = control - 0x80;
                if (full)
                {
                    Require(position, 1, "frame run value");
                    var value = _bytes[position];
                    position++;
                    if (canvas is not null)
                    {
                        canvas.AsSpan(cursor, Math.Min(count, pixels - cursor)).Fill(value);
                    }
                }

                cursor += count;
                continue;
            }

            Require(position, control, "frame literal");
            if (canvas is not null)
            {
                _bytes.AsSpan(position, Math.Min(control, pixels - cursor)).CopyTo(canvas.AsSpan(cursor));
            }

            position += control;
            cursor += control;
        }

        return position;
    }

    private void Require(int position, int length, string what)
    {
        if (position < 0 || length < 0 || position + length > _bytes.Length)
        {
            throw new InvalidDataException($"{Name}: {what} needs {length} bytes at {position}, the file has {_bytes.Length}.");
        }
    }

    private readonly record struct BlockInfo(DaggerfallVidBlockType Type, int AudioOffset, int AudioLength, int Delay);

    /// <summary>The header tag as text, for diagnostics.</summary>
    public string HeaderTag => Encoding.ASCII.GetString(_bytes, 0, 3);
}
