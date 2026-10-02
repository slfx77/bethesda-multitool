// Ported from OpenTESArena (MIT License), https://github.com/afritz1/OpenTESArena
//   OpenTESArena/src/Assets/FLCFile.cpp / FLCFile.h. License texts are collected centrally in
//   THIRD_PARTY_LICENSES.
//
// Divergence from the reference: every read here is bounds-checked and reports an
// InvalidDataException naming the file. The reference walks the chunk data with raw pointers and
// no length guards because it only ever opens shipped assets; this decoder is reachable from the
// CLI with arbitrary input.

using System.Buffers.Binary;
using BethesdaMultitool.Core.Imaging;

namespace BethesdaMultitool.Core.Formats.Xngine.Flic;

/// <summary>
///     An Autodesk Animator FLIC animation — the <c>.FLC</c> and <c>.CEL</c> cutscenes and
///     animated menus used by the XnGine-era games (Arena's intro, endings and class portraits;
///     Daggerfall and Battlespire reuse the format).
///     <para>
///         A 128-byte header is followed by frame blocks, each holding typed chunks. Only three
///         chunk types carry picture data in these games: a 256-entry palette, a whole-frame RLE
///         image, and a delta against the previous frame. Because delta frames mutate a running
///         image, a FLIC must be decoded from the start — there is no random access to frame N.
///     </para>
/// </summary>
internal sealed class FlicFile
{
    /// <summary>Bytes in the FLIC file header.</summary>
    public const int HeaderLength = 128;

    /// <summary>Magic for the 8-bit FLC variant, the only one these games ship.</summary>
    public const ushort FlcMagic = 0xAF12;

    /// <summary>Magic for the older, smaller FLI variant — recognized so it can be rejected clearly.</summary>
    public const ushort FliMagic = 0xAF11;

    private const ushort FrameTypeChunk = 0xF1FA;
    private const ushort PrefixChunk = 0xF100;

    private const ushort ChunkColor256 = 4;
    private const ushort ChunkDeltaFlc = 7;
    private const ushort ChunkByteRun = 15;

    private const int FrameHeaderLength = 16;
    private const int ChunkHeaderLength = 6;

    private FlicFile(
        string name,
        int width,
        int height,
        uint millisecondsPerFrame,
        int declaredFrameCount,
        IReadOnlyList<FlicFrame> frames)
    {
        Name = name;
        Width = width;
        Height = height;
        MillisecondsPerFrame = millisecondsPerFrame;
        DeclaredFrameCount = declaredFrameCount;
        Frames = frames;
    }

    /// <summary>
    ///     Frame count as stated in the header. <see cref="Frames" /> should match it: the file
    ///     stores one extra block for the loop-back frame, which is dropped.
    /// </summary>
    public int DeclaredFrameCount { get; }

    /// <summary>Logical file name this animation was parsed from.</summary>
    public string Name { get; }

    /// <summary>Frame width in pixels — 320 for every Arena animation.</summary>
    public int Width { get; }

    /// <summary>Frame height in pixels — 200 for every Arena animation.</summary>
    public int Height { get; }

    /// <summary>Frame duration, from the header's millisecond delay.</summary>
    public double SecondsPerFrame => MillisecondsPerFrame / 1000.0;

    /// <summary>The original unsigned millisecond delay, without floating-point conversion.</summary>
    public uint MillisecondsPerFrame { get; }

    /// <summary>Decoded frames, each already paired with the palette in force when it was drawn.</summary>
    public IReadOnlyList<FlicFrame> Frames { get; }

    /// <summary>Total running time.</summary>
    public double DurationSeconds => Frames.Count * SecondsPerFrame;

    /// <summary>True when the header carries a recognized FLIC magic.</summary>
    public static bool IsFlic(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 6)
        {
            return false;
        }

        var magic = BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]);
        return magic is FlcMagic or FliMagic;
    }

    /// <summary>Decodes every frame of a FLIC.</summary>
    public static FlicFile Parse(ReadOnlySpan<byte> bytes, string name) => ParseCore(bytes, name, null, default);

    /// <summary>Decodes only the explicitly supported media profile within preallocation limits.</summary>
    /// <param name="bytes">Borrowed encoded bytes, stable until this synchronous call returns.</param>
    /// <param name="name">Source label used in diagnostics.</param>
    /// <param name="limits">Encoded, canvas, actual frame-block and decoded allocation admission limits.</param>
    /// <param name="cancellationToken">Cancels frame, chunk, row and packet work before return.</param>
    /// <returns>Detached indexed frames; no encoded input is retained.</returns>
    internal static FlicFile ParseBounded(ReadOnlySpan<byte> bytes, string name, FlicDecodeLimits limits,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(limits);
        limits.Validate();
        return ParseCore(bytes, name, limits, cancellationToken);
    }

    /// <summary>Shares the existing codec while optional admission checks protect the new media adapter.</summary>
    private static FlicFile ParseCore(ReadOnlySpan<byte> bytes, string name, FlicDecodeLimits? limits,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (limits is not null && bytes.Length > limits.MaximumEncodedBytes)
        {
            throw new NotSupportedException($"'{name}' exceeds its encoded FLC byte limit.");
        }
        if (bytes.Length < HeaderLength)
        {
            throw new InvalidDataException($"'{name}' is too small to be a FLIC ({bytes.Length} bytes).");
        }

        var magic = BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]);
        if (magic == FliMagic)
        {
            throw new NotSupportedException(
                $"'{name}' is an FLI (0x{FliMagic:X4}); only the FLC variant (0x{FlcMagic:X4}) is supported. " +
                "No XnGine-era game ships FLI.");
        }

        if (magic != FlcMagic)
        {
            throw new InvalidDataException(
                $"'{name}' is not a FLIC: magic 0x{magic:X4}, expected 0x{FlcMagic:X4}.");
        }

        var declaredFrames = BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]);
        var width = BinaryPrimitives.ReadUInt16LittleEndian(bytes[8..]);
        var height = BinaryPrimitives.ReadUInt16LittleEndian(bytes[10..]);
        var depth = BinaryPrimitives.ReadUInt16LittleEndian(bytes[12..]);
        var speed = BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]);

        if (width <= 0 || height <= 0)
        {
            throw new InvalidDataException($"'{name}' declares an empty frame ({width}x{height}).");
        }

        if (depth != 8)
        {
            throw new NotSupportedException($"'{name}' is {depth}-bit; FLIC picture data is 8-bit indexed.");
        }

        long admittedBytes = 0;
        if (limits is not null)
        {
            if (speed == 0)
            {
                throw new NotSupportedException($"'{name}' has no positive FLC millisecond delay.");
            }
            if (declaredFrames == 0 || declaredFrames >= limits.MaximumFrameBlocks ||
                (long)width * height > limits.MaximumCanvasPixels)
            {
                throw new NotSupportedException($"'{name}' exceeds its FLC frame or canvas limit.");
            }
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes) != bytes.Length)
            {
                throw new InvalidDataException($"'{name}' has a mismatched FLC encoded length.");
            }
            // Reserve the working index canvas and the adapter's one RGBA conversion buffer.
            AdmitBytes(ref admittedBytes, (long)width * height * 5, limits, name);
        }

        // The running frame: whole-frame chunks replace it, delta chunks patch it in place.
        var canvas = new byte[width * height];
        var frames = new List<FlicFrame>();
        Palette? palette = null;

        var offset = HeaderLength;
        while (offset + FrameHeaderLength <= bytes.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frameSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
            var frameType = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(offset + 4)..]);
            var chunkCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(offset + 6)..]);

            if (frameSize < FrameHeaderLength || frameSize > bytes.Length - offset)
            {
                throw new InvalidDataException(
                    $"'{name}' has a frame at {offset} claiming {frameSize} bytes, which does not fit the file.");
            }

            switch (frameType)
            {
                case FrameTypeChunk:
                {
                    if (limits is not null)
                    {
                        if (frames.Count >= limits.MaximumFrameBlocks)
                        {
                            throw new NotSupportedException($"'{name}' exceeds its actual FLC frame-block limit.");
                        }
                        if (BinaryPrimitives.ReadUInt64LittleEndian(bytes[(offset + 8)..]) != 0)
                        {
                            throw new NotSupportedException($"'{name}' uses unsupported frame-header extension metadata.");
                        }
                        AdmitBytes(ref admittedBytes, canvas.Length, limits, name);
                    }
                    var chunkOffset = offset + FrameHeaderLength;
                    for (var i = 0; i < chunkCount; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (chunkOffset + ChunkHeaderLength > offset + frameSize)
                        {
                            throw new InvalidDataException($"'{name}' has a frame whose chunks overrun it.");
                        }

                        var chunkSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes[chunkOffset..]);
                        var chunkType = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(chunkOffset + 4)..]);
                        if (chunkSize < ChunkHeaderLength || chunkSize > bytes.Length - chunkOffset ||
                            (limits is not null && chunkSize > offset + frameSize - chunkOffset))
                        {
                            throw new InvalidDataException(
                                $"'{name}' has a chunk of type {chunkType} claiming {chunkSize} bytes, past end of file.");
                        }

                        var data = bytes.Slice(chunkOffset + ChunkHeaderLength, chunkSize - ChunkHeaderLength);
                        switch (chunkType)
                        {
                            case ChunkColor256:
                                if (limits is not null)
                                {
                                    AdmitBytes(ref admittedBytes, Palette.EntryCount * 4, limits, name);
                                }
                                palette = ReadPalette(data, name, limits is not null);
                                break;
                            case ChunkByteRun:
                                DecodeFullFrame(data, canvas, width, height, name, limits is not null, cancellationToken);
                                break;
                            case ChunkDeltaFlc:
                                DecodeDeltaFrame(data, canvas, width, height, name, limits is not null, cancellationToken);
                                break;
                            case 18: // Known non-display postage-stamp thumbnail, not a picture update.
                                break;
                            default:
                                if (limits is not null)
                                {
                                    throw new NotSupportedException($"'{name}' has unsupported FLC picture chunk {chunkType}.");
                                }
                                break;
                        }

                        chunkOffset += chunkSize;
                    }

                    // One frame block is one displayed frame, whether or not it changed the
                    // picture. Blocks that carry only a palette (or nothing at all) hold the
                    // previous image and still occupy their slot in time — 8 of KING.FLC's 90
                    // frames are like this, and skipping them would shorten the animation.
                    frames.Add(NewFrame(canvas, width, height, palette, name));
                    break;
                }

                case PrefixChunk:
                    // A .CEL prefix block holds authoring metadata, never picture data.
                    break;

                default:
                    throw new InvalidDataException($"'{name}' has an unrecognized frame type 0x{frameType:X4}.");
            }

            offset += frameSize;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (limits is not null && (offset != bytes.Length || frames.Count != declaredFrames + 1))
        {
            throw new InvalidDataException($"'{name}' needs its declared display blocks and one loop-back block.");
        }

        if (frames.Count == 0)
        {
            throw new InvalidDataException($"'{name}' contains no picture chunks.");
        }

        // A FLIC always stores one more frame block than its header declares: the extra one is a
        // delta back to frame 0 so playback can loop seamlessly. Dropping it makes the decoded
        // count equal the declared count — verified across all 20 retail animations, where the
        // block count is exactly header + 1 every time.
        if (frames.Count > 1)
        {
            if (limits is not null &&
                (!frames[0].Image.Indices.AsSpan().SequenceEqual(frames[^1].Image.Indices) ||
                 !frames[0].Palette.Rgba.SequenceEqual(frames[^1].Palette.Rgba)))
            {
                throw new NotSupportedException($"'{name}' has no verified palette-and-index loop-back block.");
            }
            frames.RemoveAt(frames.Count - 1);
        }

        return new FlicFile(name, width, height, speed, declaredFrames, frames);
    }

    /// <summary>Charges actual allocations, including discarded ring frames and superseded palettes, before allocation.</summary>
    private static void AdmitBytes(ref long admittedBytes, long bytes, FlicDecodeLimits limits, string name)
    {
        if (bytes > limits.MaximumDecodedBytes - admittedBytes)
        {
            throw new NotSupportedException($"'{name}' exceeds its decoded FLC allocation limit.");
        }
        admittedBytes += bytes;
    }

    private static FlicFrame NewFrame(byte[] canvas, int width, int height, Palette? palette, string name)
    {
        if (palette is null)
        {
            throw new InvalidDataException($"'{name}' has a picture chunk before any palette chunk.");
        }

        return new FlicFrame(new IndexedBitmap(width, height, (byte[])canvas.Clone()), palette);
    }

    /// <summary>
    ///     A COLOR_256 chunk: a packet count (always 1 here), then a skip count and colour count
    ///     that both go unused, then 256 full-range RGB triplets.
    /// </summary>
    private static Palette ReadPalette(ReadOnlySpan<byte> data, string name, bool strict)
    {
        const int packetHeader = 4;
        if (data.Length < packetHeader + Palette.RgbByteCount)
        {
            throw new InvalidDataException($"'{name}' has a truncated palette chunk.");
        }

        var packetCount = BinaryPrimitives.ReadUInt16LittleEndian(data);
        if (packetCount != 1 || (strict && (data[2] != 0 || data[3] != 0)))
        {
            throw new NotSupportedException(
                $"'{name}' has a palette chunk with {packetCount} packets; only whole-palette updates are supported.");
        }

        return Palette.FromRgb8(data.Slice(packetHeader, Palette.RgbByteCount));
    }

    /// <summary>
    ///     A BYTE_RUN chunk: one whole frame, row by row. Each row opens with a packet count that
    ///     is ignored (the row ends when the frame's width is filled), then packets whose signed
    ///     lead byte selects the mode — positive repeats the next byte that many times, negative
    ///     copies that many literal bytes.
    /// </summary>
    private static void DecodeFullFrame(
        ReadOnlySpan<byte> data,
        byte[] canvas,
        int width,
        int height,
        string name,
        bool strict,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        for (var row = 0; row < height; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (offset >= data.Length)
            {
                throw new InvalidDataException($"'{name}' has a whole-frame chunk that ends at row {row}.");
            }

            // The stored packet count is unreliable in FLC files; the row width is authoritative.
            offset++;

            var column = 0;
            while (column < width)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (offset >= data.Length)
                {
                    throw new InvalidDataException($"'{name}' has a whole-frame chunk that ends mid-row.");
                }

                var type = (sbyte)data[offset];
                if (strict && Math.Abs((int)type) > width - column)
                {
                    throw new InvalidDataException($"'{name}' has a byte-run packet outside its row.");
                }
                if (type > 0)
                {
                    if (offset + 2 > data.Length)
                    {
                        throw new InvalidDataException($"'{name}' has a truncated run packet.");
                    }

                    var pixel = data[offset + 1];
                    var run = Math.Min(type, width - column);
                    canvas.AsSpan(row * width + column, run).Fill(pixel);
                    column += type;
                    offset += 2;
                }
                else if (type < 0)
                {
                    var count = -type;
                    if (offset + 1 + count > data.Length)
                    {
                        throw new InvalidDataException($"'{name}' has a truncated literal packet.");
                    }

                    var copy = Math.Min(count, width - column);
                    data.Slice(offset + 1, copy).CopyTo(canvas.AsSpan(row * width + column));
                    column += count;
                    offset += 1 + count;
                }
                else
                {
                    throw new InvalidDataException($"'{name}' has a zero-length byte-run packet.");
                }
            }
        }
    }

    /// <summary>
    ///     A DELTA_FLC chunk: a count of encoded rows, then per row a sequence of u16 control
    ///     words. A word with either high bit set is an instruction rather than a count — both set
    ///     skips rows, bit 15 alone writes the row's last pixel — and the first word with both
    ///     clear is the packet count for that row. Packets then carry a column skip and a signed
    ///     count of PIXEL PAIRS, positive for literals and negative for a repeated pair.
    /// </summary>
    private static void DecodeDeltaFrame(
        ReadOnlySpan<byte> data,
        byte[] canvas,
        int width,
        int height,
        string name,
        bool strict,
        CancellationToken cancellationToken)
    {
        if (data.Length < 2)
        {
            throw new InvalidDataException($"'{name}' has a truncated delta chunk.");
        }

        var lineCount = BinaryPrimitives.ReadUInt16LittleEndian(data);
        var offset = 2;
        var y = 0;

        for (var line = 0; line < lineCount; line++, y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var packetCount = 0;
            var hasPacketCount = false;
            var lastPixelReachedEnd = false;
            if (strict && (uint)y >= (uint)height)
            {
                throw new InvalidDataException($"'{name}' starts a delta row outside its canvas.");
            }
            while (offset + 2 <= data.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var packet = BinaryPrimitives.ReadInt16LittleEndian(data[offset..]);
                offset += 2;

                var bit15 = (packet & unchecked((short)0x8000)) != 0;
                var bit14 = (packet & 0x4000) != 0;

                if (!bit15)
                {
                    if (strict && bit14)
                    {
                        throw new NotSupportedException($"'{name}' uses an unsupported delta control word.");
                    }
                    packetCount = packet;
                    hasPacketCount = true;
                    break;
                }

                if (bit14)
                {
                    // Both high bits set: a negative row skip.
                    if (strict && -packet >= height - y)
                    {
                        throw new InvalidDataException($"'{name}' has a delta row skip outside its canvas.");
                    }
                    y += -packet;
                }
                else
                {
                    // Bit 15 alone: the low byte is the row's final pixel.
                    if (strict && (uint)y >= (uint)height)
                    {
                        throw new InvalidDataException($"'{name}' has a delta last-pixel write outside its canvas.");
                    }
                    lastPixelReachedEnd = y == height - 1;
                    if ((uint)y < (uint)height)
                    {
                        canvas[y * width + width - 1] = (byte)(packet & 0xFF);
                    }

                    y++;
                }
            }

            if (strict && (!hasPacketCount ||
                ((uint)y >= (uint)height && !(y == height && packetCount == 0 && lastPixelReachedEnd))))
            {
                throw new InvalidDataException($"'{name}' has an incomplete or out-of-canvas delta row.");
            }
            var x = 0;
            for (var i = 0; i < packetCount; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (offset + 2 > data.Length)
                {
                    throw new InvalidDataException($"'{name}' has a truncated delta packet.");
                }

                x += data[offset];
                var count = (sbyte)data[offset + 1];
                offset += 2;
                if (strict && (count == 0 || Math.Abs((int)count) * 2 > width - x))
                {
                    throw new InvalidDataException($"'{name}' has a delta packet outside its row or with zero length.");
                }

                if (count > 0)
                {
                    if (offset + count * 2 > data.Length)
                    {
                        throw new InvalidDataException($"'{name}' has a truncated delta literal run.");
                    }

                    for (var j = 0; j < count && x < width; j++)
                    {
                        WritePair(canvas, width, height, y, ref x, data[offset], data[offset + 1]);
                        offset += 2;
                    }
                }
                else if (count < 0)
                {
                    if (offset + 2 > data.Length)
                    {
                        throw new InvalidDataException($"'{name}' has a truncated delta repeat run.");
                    }

                    var first = data[offset];
                    var second = data[offset + 1];
                    for (var j = 0; j < -count && x < width; j++)
                    {
                        WritePair(canvas, width, height, y, ref x, first, second);
                    }

                    offset += 2;
                }
            }
        }
    }

    /// <summary>Writes a pixel pair, advancing the column and clipping at the row's end.</summary>
    private static void WritePair(
        byte[] canvas,
        int width,
        int height,
        int y,
        ref int x,
        byte first,
        byte second)
    {
        if ((uint)y >= (uint)height)
        {
            x += 2;
            return;
        }

        if (x < width)
        {
            canvas[y * width + x] = first;
            x++;
        }

        if (x < width)
        {
            canvas[y * width + x] = second;
            x++;
        }
    }
}

/// <summary>One decoded FLIC frame: its indexed pixels and the palette in force when it was drawn.</summary>
internal sealed record FlicFrame(IndexedBitmap Image, Palette Palette);
