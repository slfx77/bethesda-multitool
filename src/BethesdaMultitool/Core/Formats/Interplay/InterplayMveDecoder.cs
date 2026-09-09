// ORIGINAL CLEAN-ROOM IMPLEMENTATION.
//
// Written from our own reverse engineering of Interplay's shipped Fallout player FALLOUTW.EXE
// (Steam release, 1,243,136 bytes): the opcode dispatcher FUN_004d7710, the 8-bit block decoder
// FUN_004d9ba9 (self-modifying, hand-written assembly — read through our own capstone
// disassembly, tools/GhidraProject/ClassicRE holds the Ghidra dump), the audio fill FUN_004d83c0
// and the DPCM loops FUN_004d91cc / FUN_004d91fd, as written up in the specification document
//   scratchpad .../gap2/fallout-mve/SPEC.md  ("Interplay MVE - Format Specification").
//
// NO FFmpeg- or libav-derived code, and no other third-party MVE implementation, was consulted,
// read, copied or paraphrased. ffmpeg was used only as a sealed executable whose output pixels and
// samples were compared with this decoder's.

using System.Buffers.Binary;

namespace BethesdaMultitool.Core.Formats.Interplay;

/// <summary>The audio stream an <see cref="InterplayMveDecoder" /> found in opcode 0x03.</summary>
/// <param name="SampleRate">Samples per second per channel.</param>
/// <param name="Channels">1 or 2 (flag bit 0).</param>
/// <param name="BitsPerSample">8 or 16 (flag bit 1).</param>
/// <param name="Compressed">True when the frames carry DPCM deltas (flag bit 2, version 1+).</param>
/// <param name="BufferBytes">The player's ring-buffer size the file asked for.</param>
internal readonly record struct MveAudioFormat(
    int SampleRate, int Channels, int BitsPerSample, bool Compressed, uint BufferBytes);

/// <summary>
///     Decodes an Interplay <c>.MVE</c> movie's video (8-bit indexed, 8x8 block encodings) and audio
///     (DPCM) exactly as Fallout's own player does. <see cref="InterplayMveFile" /> stays the
///     container walk; this class replays its opcodes.
///     <para>
///         <b>The buffer rule.</b> The player owns TWO frame buffers. A video-data opcode (0x11)
///         whose flag bit 0 is set SWAPS them before decoding (<c>FUN_004d8770</c>), so the buffer
///         decoded into is the one shown two frames ago and the other holds the previous frame.
///         Encodings 0, 4 and 5 copy from that previous frame; 2 and 3 copy from the buffer being
///         decoded — including, for 2, from blocks not yet reached this frame, i.e. from the frame
///         before last. Encoding 1 writes nothing, so the block keeps that older content too. The
///         first frame carries flag 0 and all later retail frames carry flag 1.
///     </para>
///     <para>
///         <b>Frames versus display ticks.</b> A decode (0x11) and a display (0x07, "send buffer
///         to screen") are separate opcodes and the retail movies have MORE displays than decodes
///         (CATHEXP.MVE: 301 ticks, 218 decodes — the audio-only pre-roll shows the still buffer).
///         <see cref="DecodedFrameCount" /> counts decodes, which is what ffmpeg emits;
///         <see cref="DisplayTicks" /> lists, per tick, the decoded frame on screen, which is what
///         the game shows and what a player should step through at <see cref="SecondsPerFrame" />.
///     </para>
///     <para>
///         <b>Robustness.</b> The player checks nothing — a bad vector reads whatever memory is
///         there. This decoder never throws once constructed: a copy that would leave the buffer
///         is skipped and counted in <see cref="OutOfRangeCopies" />, and a frame whose map or data
///         runs out ends early and counts in <see cref="TruncatedFrames" />. The retail corpus
///         scores zero on both, which the retail tests pin; a caller that decodes on a UI timer
///         (the asset-browser preview) therefore cannot be thrown at mid-clip.
///     </para>
/// </summary>
internal sealed class InterplayMveDecoder
{
    /// <summary>Opcode: end of stream.</summary>
    public const byte EndOfStreamOpcode = 0x00;

    /// <summary>Opcode: create timer — u32 rate, u16 subdivision; a tick is rate*subdivision microseconds.</summary>
    public const byte TimerOpcode = 0x02;

    /// <summary>Opcode: init audio buffers — u16, u16 flags, u16 sample rate, u32 (v1) / u16 (v0) buffer length.</summary>
    public const byte AudioInitOpcode = 0x03;

    /// <summary>Opcode: init video buffers — u16 width and height in 8-pixel blocks, u16 count (v1+), u16 true-colour (v2+).</summary>
    public const byte VideoBuffersOpcode = 0x05;

    /// <summary>Opcode: send the current buffer to the display — u16 palette start, u16 palette count.</summary>
    public const byte SendBufferOpcode = 0x07;

    /// <summary>Opcode: audio frame — u16 sequence, u16 stream mask, u16 PCM byte length, payload.</summary>
    public const byte AudioDataOpcode = 0x08;

    /// <summary>Opcode: audio silence — the same header, no payload; the player fills zeros (or 0x80 for 8-bit).</summary>
    public const byte AudioSilenceOpcode = 0x09;

    /// <summary>Opcode: the block-encoding map, one nibble per 8x8 block, low nibble first.</summary>
    public const byte DecodingMapOpcode = 0x0F;

    /// <summary>Opcode: video data (version 3) — 14-byte header then the block data.</summary>
    public const byte VideoDataOpcode = 0x11;

    /// <summary>The audio stream the player selects by default (<c>1 &lt;&lt; 0</c> in <c>FUN_004d7440</c>).</summary>
    public const ushort DefaultAudioStreamMask = 1;

    private const int BlockSize = 8;
    private const int VideoDataHeaderLength = 14;

    private readonly byte[] _bytes;
    private readonly InterplayMveFile _file;
    private readonly int[] _displayTicks;
    private readonly int[] _frameOpcodeOffsets;

    private byte[] _current;
    private byte[] _previous;
    private readonly byte[] _palette;
    private byte[]? _map;
    private int _cursor;
    private int _currentFrame;

    /// <summary>
    ///     Parses the container and scans its opcodes for the stream parameters; no frame is decoded.
    ///     Throws <see cref="InvalidDataException" /> when the container does not walk or declares no
    ///     8-bit video buffer.
    /// </summary>
    public InterplayMveDecoder(byte[] bytes, string name)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        _bytes = bytes;
        _file = InterplayMveFile.Parse(bytes, name);

        var frames = new List<int>();
        var ticks = new List<int>();
        var width = 0;
        var height = 0;
        var trueColour = 0;
        MveAudioFormat? audio = null;
        double secondsPerFrame = 0;

        foreach (var (opcode, version, offset, length) in EnumerateOpcodes())
        {
            switch (opcode)
            {
                case TimerOpcode when length >= 6:
                    var rate = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
                    var subdivision = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 4));
                    secondsPerFrame = rate * (double)subdivision / 1_000_000.0;
                    break;
                case AudioInitOpcode when length >= 8:
                    var flags = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 2));
                    var sampleRate = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 4));
                    var bufferBytes = version >= 1 && length >= 10
                        ? BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 6))
                        : BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 6));
                    audio = new MveAudioFormat(
                        sampleRate,
                        (flags & 1) != 0 ? 2 : 1,
                        (flags & 2) != 0 ? 16 : 8,
                        version >= 1 && (flags & 4) != 0,
                        bufferBytes);
                    break;
                case VideoBuffersOpcode when length >= 4:
                    width = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset)) * BlockSize;
                    height = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 2)) * BlockSize;
                    trueColour = version >= 2 && length >= 8
                        ? BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 6))
                        : 0;
                    break;
                case SendBufferOpcode:
                    ticks.Add(frames.Count - 1);
                    break;
                case VideoDataOpcode:
                    if (version < 3)
                    {
                        throw new InvalidDataException(
                            $"{name}: video data opcode version {version} at {offset}; the player rejects anything below 3.");
                    }

                    frames.Add(offset);
                    break;
            }
        }

        if (width == 0 || height == 0)
        {
            throw new InvalidDataException($"{name}: no video buffer (opcode 0x05) declared.");
        }

        if (trueColour != 0)
        {
            throw new NotSupportedException(
                $"{name}: declares a 16-bit (true-colour) video buffer; only the 8-bit decoder is implemented.");
        }

        Name = name;
        Width = width;
        Height = height;
        Audio = audio;
        SecondsPerFrame = secondsPerFrame;
        _frameOpcodeOffsets = [.. frames];
        _displayTicks = [.. ticks];
        _current = new byte[width * height];
        _previous = new byte[width * height];
        _palette = new byte[768];
        _currentFrame = -1;
    }

    /// <summary>Source file name, for messages.</summary>
    public string Name { get; }

    /// <summary>The parsed container.</summary>
    public InterplayMveFile File => _file;

    /// <summary>Frame width in pixels (the 0x05 block width times 8).</summary>
    public int Width { get; }

    /// <summary>Frame height in pixels.</summary>
    public int Height { get; }

    /// <summary>The audio stream, or null when the movie declares none.</summary>
    public MveAudioFormat? Audio { get; }

    /// <summary>
    ///     Seconds per display tick from the timer opcode: <c>rate * subdivision</c> microseconds
    ///     (<c>FUN_004d7d40</c>; retail is 8341 x 8 = 66,728 us, 14.985 fps). 0 when absent.
    /// </summary>
    public double SecondsPerFrame { get; }

    /// <summary>How many video-data opcodes (0x11) the movie carries — the frames ffmpeg emits.</summary>
    public int DecodedFrameCount => _frameOpcodeOffsets.Length;

    /// <summary>
    ///     One entry per send-buffer opcode (0x07): the index of the decoded frame on screen at
    ///     that tick, or -1 while the buffer is still the initial blank one.
    /// </summary>
    public IReadOnlyList<int> DisplayTicks => _displayTicks;

    /// <summary>Index of the frame currently in <see cref="CurrentIndices" />, -1 before the first decode.</summary>
    public int CurrentFrame => _currentFrame;

    /// <summary>The decoded frame as palette indices, row-major, <see cref="Width" /> bytes per row.</summary>
    public ReadOnlySpan<byte> CurrentIndices => _current;

    /// <summary>
    ///     The palette in force when <see cref="CurrentFrame" /> was decoded, 256 RGB triples of
    ///     6-bit VGA components (the gradient opcode 0x0B writes values up to 63 and nothing in the
    ///     player promotes them; promotion is the display driver's).
    /// </summary>
    public ReadOnlySpan<byte> CurrentPaletteRgb => _palette;

    /// <summary>Block copies that would have read outside the buffer, skipped. Zero on retail.</summary>
    public int OutOfRangeCopies { get; private set; }

    /// <summary>Frames whose map or data ran out before the last block. Zero on retail.</summary>
    public int TruncatedFrames { get; private set; }

    /// <summary>
    ///     Video-data opcodes whose data was not consumed to the byte. Retail leaves ONE spare
    ///     byte on about half its frames (the data is padded to an even length); this counts them.
    /// </summary>
    public int FramesWithSpareBytes { get; private set; }

    /// <summary>Rewinds to before the first frame with both buffers blank and the palette black.</summary>
    public void Reset()
    {
        Array.Clear(_current);
        Array.Clear(_previous);
        Array.Clear(_palette);
        _map = null;
        _cursor = 0;
        _currentFrame = -1;
    }

    /// <summary>
    ///     Decodes the next frame (the next 0x11 opcode), replaying every palette and map opcode
    ///     before it. Returns false at the end of the movie.
    /// </summary>
    public bool DecodeNextFrame()
    {
        var target = _currentFrame + 1;
        if (target >= _frameOpcodeOffsets.Length)
        {
            return false;
        }

        var stop = _frameOpcodeOffsets[target];
        foreach (var (opcode, _, offset, length) in EnumerateOpcodes(_cursor))
        {
            if (offset > stop)
            {
                break;
            }

            switch (opcode)
            {
                case InterplayMveFile.PaletteOpcode:
                    ApplyPalette(offset, length);
                    break;
                case DecodingMapOpcode:
                    _map = _bytes.AsSpan(offset, length).ToArray();
                    break;
                case VideoDataOpcode when offset == stop:
                    DecodeVideoData(offset, length);
                    _cursor = offset + length;
                    _currentFrame = target;
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     Positions the decoder on frame <paramref name="index" />, decoding forward from where it
    ///     is or from the start when asked to go backwards — every frame is coded against its
    ///     predecessor and there are no keyframes, so that replay is the only way to seek.
    /// </summary>
    public void SeekToFrame(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, DecodedFrameCount);

        if (index < _currentFrame)
        {
            Reset();
        }

        while (_currentFrame < index)
        {
            if (!DecodeNextFrame())
            {
                break;
            }
        }
    }

    /// <summary>Decodes frame <paramref name="index" /> (seeking as needed) and returns it as RGBA.</summary>
    public byte[] DecodeFrameRgba(int index)
    {
        SeekToFrame(index);
        return CurrentFrameRgba();
    }

    /// <summary>The current frame through the current palette, promoted from 6-bit VGA.</summary>
    public byte[] CurrentFrameRgba()
    {
        var rgba = new byte[Width * Height * 4];
        var promoted = new byte[768];
        for (var i = 0; i < 768; i++)
        {
            var v = _palette[i];
            promoted[i] = (byte)((v << 2) | (v >> 4));
        }

        for (int p = 0, o = 0; p < _current.Length; p++, o += 4)
        {
            var e = _current[p] * 3;
            rgba[o] = promoted[e];
            rgba[o + 1] = promoted[e + 1];
            rgba[o + 2] = promoted[e + 2];
            rgba[o + 3] = 255;
        }

        return rgba;
    }

    /// <summary>
    ///     Decodes the whole audio track of stream <paramref name="streamMask" /> (the player's
    ///     default is bit 0) to interleaved little-endian PCM at <see cref="Audio" />'s format, in
    ///     file order, silence opcodes included — the player fills its ring buffer with zeros for
    ///     those, so they are part of the timing. Returns an empty array when there is no audio.
    /// </summary>
    public byte[] DecodeAudioPcm(ushort streamMask = DefaultAudioStreamMask)
    {
        if (Audio is not { } format)
        {
            return [];
        }

        var output = new MemoryStream();
        foreach (var (opcode, _, offset, length) in EnumerateOpcodes())
        {
            if ((opcode != AudioDataOpcode && opcode != AudioSilenceOpcode) || length < 6)
            {
                continue;
            }

            var mask = BinaryPrimitives.ReadUInt16LittleEndian(_bytes.AsSpan(offset + 2));
            if ((mask & streamMask) == 0)
            {
                continue;
            }

            var pcmLength = BinaryPrimitives.ReadUInt16LittleEndian(_bytes.AsSpan(offset + 4));
            var pcm = new byte[pcmLength];
            if (opcode == AudioSilenceOpcode)
            {
                // FUN_004d83c0 with no source: 0x80 for 8-bit (unsigned), 0 for 16-bit.
                if (format.BitsPerSample == 8)
                {
                    Array.Fill(pcm, (byte)0x80);
                }
            }
            else
            {
                DecodeAudioFrame(_bytes.AsSpan(offset + 6, length - 6), pcm, format);
            }

            output.Write(pcm);
        }

        return output.ToArray();
    }

    /// <summary>
    ///     One audio frame as <c>FUN_004d83c0</c> fills it: raw PCM is copied; DPCM starts with the
    ///     first sample per channel stored verbatim (2 bytes mono, 4 stereo), then one delta byte
    ///     per sample, <c>predictor += DpcmDeltas[b]</c> wrapping in 16 bits, stereo bytes
    ///     alternating left, right.
    /// </summary>
    internal static void DecodeAudioFrame(ReadOnlySpan<byte> payload, Span<byte> pcm, MveAudioFormat format)
    {
        if (!format.Compressed)
        {
            var n = Math.Min(payload.Length, pcm.Length);
            payload[..n].CopyTo(pcm);
            return;
        }

        var deltas = InterplayMveTables.DpcmDeltas;
        if (format.Channels == 2)
        {
            if (payload.Length < 4 || pcm.Length < 4)
            {
                return;
            }

            var left = BinaryPrimitives.ReadInt16LittleEndian(payload);
            var right = BinaryPrimitives.ReadInt16LittleEndian(payload[2..]);
            BinaryPrimitives.WriteInt16LittleEndian(pcm, left);
            BinaryPrimitives.WriteInt16LittleEndian(pcm[2..], right);
            var source = 4;
            for (var o = 4; o + 4 <= pcm.Length && source + 2 <= payload.Length; o += 4, source += 2)
            {
                left = unchecked((short)(left + deltas[payload[source]]));
                right = unchecked((short)(right + deltas[payload[source + 1]]));
                BinaryPrimitives.WriteInt16LittleEndian(pcm[o..], left);
                BinaryPrimitives.WriteInt16LittleEndian(pcm[(o + 2)..], right);
            }
        }
        else
        {
            if (payload.Length < 2 || pcm.Length < 2)
            {
                return;
            }

            var value = BinaryPrimitives.ReadInt16LittleEndian(payload);
            BinaryPrimitives.WriteInt16LittleEndian(pcm, value);
            var source = 2;
            for (var o = 2; o + 2 <= pcm.Length && source < payload.Length; o += 2, source++)
            {
                value = unchecked((short)(value + deltas[payload[source]]));
                BinaryPrimitives.WriteInt16LittleEndian(pcm[o..], value);
            }
        }
    }

    private IEnumerable<(byte Opcode, byte Version, int DataOffset, int DataLength)> EnumerateOpcodes(int from = 0)
    {
        foreach (var chunk in _file.Chunks)
        {
            var end = chunk.Offset + 4 + chunk.Length;
            if (end <= from)
            {
                continue;
            }

            var cursor = chunk.Offset + 4;
            while (cursor + 4 <= end)
            {
                var length = BinaryPrimitives.ReadUInt16LittleEndian(_bytes.AsSpan(cursor));
                var opcode = _bytes[cursor + 2];
                var version = _bytes[cursor + 3];
                if (cursor + 4 + length > end)
                {
                    break;
                }

                if (cursor + 4 >= from)
                {
                    yield return (opcode, version, cursor + 4, length);
                }

                cursor += 4 + length;
            }
        }
    }

    private void ApplyPalette(int offset, int length)
    {
        if (length < 4)
        {
            return;
        }

        var start = BinaryPrimitives.ReadUInt16LittleEndian(_bytes.AsSpan(offset));
        var count = BinaryPrimitives.ReadUInt16LittleEndian(_bytes.AsSpan(offset + 2));
        var available = Math.Min(count * 3, length - 4);
        var room = 768 - start * 3;
        var n = Math.Clamp(Math.Min(available, room), 0, 768);
        if (n > 0)
        {
            _bytes.AsSpan(offset + 4, n).CopyTo(_palette.AsSpan(start * 3));
        }
    }

    private void DecodeVideoData(int offset, int length)
    {
        if (length < VideoDataHeaderLength)
        {
            TruncatedFrames++;
            return;
        }

        var span = _bytes.AsSpan(offset, length);
        var blockX = BinaryPrimitives.ReadUInt16LittleEndian(span[4..]);
        var blockY = BinaryPrimitives.ReadUInt16LittleEndian(span[6..]);
        var blocksWide = BinaryPrimitives.ReadUInt16LittleEndian(span[8..]);
        var blocksHigh = BinaryPrimitives.ReadUInt16LittleEndian(span[10..]);
        var flags = BinaryPrimitives.ReadUInt16LittleEndian(span[12..]);

        if ((flags & 1) != 0)
        {
            (_current, _previous) = (_previous, _current);
        }

        var map = _map ?? [];
        var data = span[VideoDataHeaderLength..];
        var consumed = DecodeBlocks(map, data, blockX, blockY, blocksWide, blocksHigh, out var truncated);
        if (truncated)
        {
            TruncatedFrames++;
        }
        else if (consumed != data.Length)
        {
            FramesWithSpareBytes++;
        }
    }

    /// <summary>
    ///     The block loop of <c>FUN_004d9ba9</c>: one map byte per PAIR of blocks, low nibble first,
    ///     each nibble dispatching through the 16-entry handler table at <c>0x53BE40</c>. Encoding 6
    ///     is the exception — it uses the whole byte, skipping <c>hi + 2</c> pairs. Returns the data
    ///     bytes consumed.
    /// </summary>
    private int DecodeBlocks(
        ReadOnlySpan<byte> map, ReadOnlySpan<byte> data, int blockX, int blockY, int blocksWide, int blocksHigh,
        out bool truncated)
    {
        truncated = false;
        var pairsPerRow = blocksWide >> 1;
        var totalPairs = pairsPerRow * blocksHigh;
        var mapIndex = 0;
        var dataIndex = 0;
        var pair = 0;

        while (pair < totalPairs)
        {
            if (mapIndex >= map.Length)
            {
                truncated = true;
                break;
            }

            var mapByte = map[mapIndex++];
            var low = mapByte & 15;
            var high = mapByte >> 4;
            if (low == 6)
            {
                pair += high + 2;
                continue;
            }

            var row = pair / pairsPerRow;
            var column = pair % pairsPerRow;
            var position = (blockY + row) * BlockSize * Width + (blockX + column * 2) * BlockSize;
            if (position + 7 * Width + 16 > _current.Length)
            {
                truncated = true;
                break;
            }

            if (!DecodeBlock(low, position, data, ref dataIndex) ||
                !DecodeBlock(high, position + BlockSize, data, ref dataIndex))
            {
                truncated = true;
                break;
            }

            pair++;
        }

        return dataIndex;
    }

    /// <summary>One 8x8 block; returns false when the data ran out.</summary>
    private bool DecodeBlock(int encoding, int position, ReadOnlySpan<byte> data, ref int dataIndex)
    {
        switch (encoding)
        {
            case 0:
                // Handler 0x4D9C88: the block as it was in the previous frame.
                CopyBlock(position, position, _previous);
                return true;
            case 1:
                // Handler 0x4D9C90 is the shared "advance 8 pixels" epilogue alone: nothing written.
                return true;
            case 2:
            case 3:
            {
                // Handlers 0x4D9C94 / 0x4D9CB4: a vector from the 0x53B400 table into the CURRENT
                // buffer, encoding 3 with both components negated.
                if (dataIndex + 1 > data.Length)
                {
                    return false;
                }

                var b = data[dataIndex++];
                int dx = InterplayMveTables.CurrentFrameVectors[b * 2];
                int dy = InterplayMveTables.CurrentFrameVectors[b * 2 + 1];
                if (encoding == 3)
                {
                    dx = -dx;
                    dy = -dy;
                }

                CopyBlock(position, position + dx + dy * Width, _current);
                return true;
            }
            case 4:
            {
                // Handler 0x4D9CC8: a nibble-packed vector (0x53B200 table) into the previous frame.
                if (dataIndex + 1 > data.Length)
                {
                    return false;
                }

                var (dx, dy) = InterplayMveTables.PreviousFrameVector(data[dataIndex++]);
                CopyBlock(position, position + dx + dy * Width, _previous);
                return true;
            }
            case 5:
            {
                // Handler 0x4D9CD8: two signed bytes, dx then dy, into the previous frame.
                if (dataIndex + 2 > data.Length)
                {
                    return false;
                }

                int dx = (sbyte)data[dataIndex];
                int dy = (sbyte)data[dataIndex + 1];
                dataIndex += 2;
                CopyBlock(position, position + dx + dy * Width, _previous);
                return true;
            }
            case 7:
                return DecodeTwoColour(position, data, ref dataIndex);
            case 8:
                return DecodeTwoColourQuadrants(position, data, ref dataIndex);
            case 9:
                return DecodeFourColour(position, data, ref dataIndex);
            case 10:
                return DecodeFourColourQuadrants(position, data, ref dataIndex);
            case 11:
            {
                // Handler 0x4DB414: 64 raw bytes, row-major.
                if (dataIndex + 64 > data.Length)
                {
                    return false;
                }

                for (var r = 0; r < 8; r++)
                {
                    data.Slice(dataIndex + r * 8, 8).CopyTo(_current.AsSpan(position + r * Width));
                }

                dataIndex += 64;
                return true;
            }
            case 12:
            {
                // Handler 0x4DB488: 16 bytes, each a 2x2 block, row-major over the 4x4 grid of them.
                if (dataIndex + 16 > data.Length)
                {
                    return false;
                }

                for (var q = 0; q < 4; q++)
                {
                    for (var j = 0; j < 4; j++)
                    {
                        FillRect(position + (2 * q) * Width + 2 * j, 2, 2, data[dataIndex + 4 * q + j]);
                    }
                }

                dataIndex += 16;
                return true;
            }
            case 13:
            {
                // Handler 0x4DB540: 4 bytes, one per 4x4 quadrant — top-left, top-right, bottom-left, bottom-right.
                if (dataIndex + 4 > data.Length)
                {
                    return false;
                }

                FillRect(position, 4, 4, data[dataIndex]);
                FillRect(position + 4, 4, 4, data[dataIndex + 1]);
                FillRect(position + 4 * Width, 4, 4, data[dataIndex + 2]);
                FillRect(position + 4 * Width + 4, 4, 4, data[dataIndex + 3]);
                dataIndex += 4;
                return true;
            }
            case 14:
            {
                // Handler 0x4DB5BC: one byte fills the block.
                if (dataIndex + 1 > data.Length)
                {
                    return false;
                }

                FillRect(position, 8, 8, data[dataIndex++]);
                return true;
            }
            case 15:
            {
                // Handler 0x4DB5D0: two bytes as a checkerboard — even rows start with the first,
                // odd rows with the second (rol ebx, 8 flips the pair).
                if (dataIndex + 2 > data.Length)
                {
                    return false;
                }

                var a = data[dataIndex];
                var b = data[dataIndex + 1];
                dataIndex += 2;
                for (var r = 0; r < 8; r++)
                {
                    var rowStart = position + r * Width;
                    var (even, odd) = (r & 1) == 0 ? (a, b) : (b, a);
                    for (var x = 0; x < 8; x += 2)
                    {
                        _current[rowStart + x] = even;
                        _current[rowStart + x + 1] = odd;
                    }
                }

                return true;
            }
            default:
                // 6 is handled at the map level and never reaches here.
                return true;
        }
    }

    /// <summary>
    ///     Encoding 7 (handler 0x4D9D9C). Two colours; when <c>c0 &lt;= c1</c> eight flag bytes follow,
    ///     one per row, bit x selecting c1 for pixel x. When <c>c0 &gt; c1</c> two flag bytes follow
    ///     and each NIBBLE (low first) covers two rows as four 2x2 cells, bit j selecting c1 for
    ///     columns 2j..2j+1.
    /// </summary>
    private bool DecodeTwoColour(int position, ReadOnlySpan<byte> data, ref int dataIndex)
    {
        if (dataIndex + 2 > data.Length)
        {
            return false;
        }

        var c0 = data[dataIndex];
        var c1 = data[dataIndex + 1];
        if (c0 <= c1)
        {
            if (dataIndex + 10 > data.Length)
            {
                return false;
            }

            for (var r = 0; r < 8; r++)
            {
                FillTwoColourRow(position + r * Width, 8, data[dataIndex + 2 + r], c0, c1);
            }

            dataIndex += 10;
            return true;
        }

        if (dataIndex + 4 > data.Length)
        {
            return false;
        }

        for (var q = 0; q < 4; q++)
        {
            var nibble = (data[dataIndex + 2 + (q >> 1)] >> (4 * (q & 1))) & 15;
            for (var j = 0; j < 4; j++)
            {
                FillRect(position + 2 * q * Width + 2 * j, 2, 2, (nibble >> j & 1) != 0 ? c1 : c0);
            }
        }

        dataIndex += 4;
        return true;
    }

    /// <summary>
    ///     Encoding 8 (handler 0x4DA050), two colours per region. <c>c0 &lt;= c1</c> in the first
    ///     pair: four 4x4 quadrants of <c>[c0 c1 f f]</c> in the order top-left, bottom-left,
    ///     top-right, bottom-right, each flag byte two rows of four pixels (16 bytes). Otherwise the
    ///     pair at +6 decides: <c>&lt;=</c> gives the left then the right 4x8 half as
    ///     <c>[c0 c1 f f f f]</c> with the same two-rows-per-byte flags; <c>&gt;</c> gives the top
    ///     then the bottom 8x4 half with one row of eight per flag byte (12 bytes either way).
    /// </summary>
    private bool DecodeTwoColourQuadrants(int position, ReadOnlySpan<byte> data, ref int dataIndex)
    {
        if (dataIndex + 8 > data.Length)
        {
            return false;
        }

        if (data[dataIndex] <= data[dataIndex + 1])
        {
            if (dataIndex + 16 > data.Length)
            {
                return false;
            }

            ReadOnlySpan<(int Y, int X)> quadrants = [(0, 0), (4, 0), (0, 4), (4, 4)];
            for (var q = 0; q < 4; q++)
            {
                var region = data.Slice(dataIndex + 4 * q, 4);
                var origin = position + quadrants[q].Y * Width + quadrants[q].X;
                for (var r = 0; r < 4; r++)
                {
                    var nibble = (region[2 + (r >> 1)] >> (4 * (r & 1))) & 15;
                    FillTwoColourRow(origin + r * Width, 4, nibble, region[0], region[1]);
                }
            }

            dataIndex += 16;
            return true;
        }

        if (dataIndex + 12 > data.Length)
        {
            return false;
        }

        if (data[dataIndex + 6] <= data[dataIndex + 7])
        {
            for (var half = 0; half < 2; half++)
            {
                var region = data.Slice(dataIndex + 6 * half, 6);
                var origin = position + 4 * half;
                for (var r = 0; r < 8; r++)
                {
                    var nibble = (region[2 + (r >> 1)] >> (4 * (r & 1))) & 15;
                    FillTwoColourRow(origin + r * Width, 4, nibble, region[0], region[1]);
                }
            }
        }
        else
        {
            for (var half = 0; half < 2; half++)
            {
                var region = data.Slice(dataIndex + 6 * half, 6);
                var origin = position + 4 * half * Width;
                for (var r = 0; r < 4; r++)
                {
                    FillTwoColourRow(origin + r * Width, 8, region[2 + r], region[0], region[1]);
                }
            }
        }

        dataIndex += 12;
        return true;
    }

    /// <summary>
    ///     Encoding 9 (handler 0x4DA5E4). Four colours c0..c3 then flags of two bits per cell,
    ///     bits 2k..2k+1 selecting the colour of cell k left to right. The two comparisons pick
    ///     the cell shape: <c>c0 &lt;= c1, c2 &lt;= c3</c> 1x1 cells, two bytes per row (20 bytes);
    ///     <c>c0 &lt;= c1, c2 &gt; c3</c> 2x2 cells, one byte per two rows (8 bytes);
    ///     <c>c0 &gt; c1, c2 &lt;= c3</c> 2x1 cells, one byte per row (12 bytes);
    ///     <c>c0 &gt; c1, c2 &gt; c3</c> 1x2 cells, two bytes per two rows (12 bytes).
    /// </summary>
    private bool DecodeFourColour(int position, ReadOnlySpan<byte> data, ref int dataIndex)
    {
        if (dataIndex + 8 > data.Length)
        {
            return false;
        }

        var colours = data.Slice(dataIndex, 4);
        var flags = data[(dataIndex + 4)..];
        if (colours[0] <= colours[1])
        {
            if (colours[2] <= colours[3])
            {
                if (flags.Length < 16)
                {
                    return false;
                }

                for (var r = 0; r < 8; r++)
                {
                    FillFourColourRow(position + r * Width, flags[2 * r], colours, 1, 1);
                    FillFourColourRow(position + r * Width + 4, flags[2 * r + 1], colours, 1, 1);
                }

                dataIndex += 20;
                return true;
            }

            for (var q = 0; q < 4; q++)
            {
                FillFourColourRow(position + 2 * q * Width, flags[q], colours, 2, 2);
            }

            dataIndex += 8;
            return true;
        }

        if (flags.Length < 8)
        {
            return false;
        }

        if (colours[2] <= colours[3])
        {
            for (var r = 0; r < 8; r++)
            {
                FillFourColourRow(position + r * Width, flags[r], colours, 2, 1);
            }
        }
        else
        {
            for (var q = 0; q < 4; q++)
            {
                FillFourColourRow(position + 2 * q * Width, flags[2 * q], colours, 1, 2);
                FillFourColourRow(position + 2 * q * Width + 4, flags[2 * q + 1], colours, 1, 2);
            }
        }

        dataIndex += 12;
        return true;
    }

    /// <summary>
    ///     Encoding 10 (handler 0x4DAC90), four colours per region with 1x1 cells. <c>c0 &lt;= c1</c>
    ///     in the first region: four 4x4 quadrants of <c>[c0 c1 c2 c3 f f f f]</c>, top-left,
    ///     bottom-left, top-right, bottom-right, one flag byte per row of four (32 bytes). Otherwise
    ///     the pair at +12 decides: <c>&lt;=</c> gives the left then the right 4x8 half as
    ///     <c>[4 colours + 8 flags]</c>, one byte per row; <c>&gt;</c> gives the top then the bottom
    ///     8x4 half with two bytes per row (24 bytes either way).
    /// </summary>
    private bool DecodeFourColourQuadrants(int position, ReadOnlySpan<byte> data, ref int dataIndex)
    {
        if (dataIndex + 16 > data.Length)
        {
            return false;
        }

        if (data[dataIndex] <= data[dataIndex + 1])
        {
            if (dataIndex + 32 > data.Length)
            {
                return false;
            }

            ReadOnlySpan<(int Y, int X)> quadrants = [(0, 0), (4, 0), (0, 4), (4, 4)];
            for (var q = 0; q < 4; q++)
            {
                var region = data.Slice(dataIndex + 8 * q, 8);
                var origin = position + quadrants[q].Y * Width + quadrants[q].X;
                for (var r = 0; r < 4; r++)
                {
                    FillFourColourRow(origin + r * Width, region[4 + r], region, 1, 1);
                }
            }

            dataIndex += 32;
            return true;
        }

        if (dataIndex + 24 > data.Length)
        {
            return false;
        }

        if (data[dataIndex + 12] <= data[dataIndex + 13])
        {
            for (var half = 0; half < 2; half++)
            {
                var region = data.Slice(dataIndex + 12 * half, 12);
                var origin = position + 4 * half;
                for (var r = 0; r < 8; r++)
                {
                    FillFourColourRow(origin + r * Width, region[4 + r], region, 1, 1);
                }
            }
        }
        else
        {
            for (var half = 0; half < 2; half++)
            {
                var region = data.Slice(dataIndex + 12 * half, 12);
                var origin = position + 4 * half * Width;
                for (var r = 0; r < 4; r++)
                {
                    FillFourColourRow(origin + r * Width, region[4 + 2 * r], region, 1, 1);
                    FillFourColourRow(origin + r * Width + 4, region[4 + 2 * r + 1], region, 1, 1);
                }
            }
        }

        dataIndex += 24;
        return true;
    }

    /// <summary>Writes <paramref name="pixels" /> pixels from bit 0 up: set selects c1.</summary>
    private void FillTwoColourRow(int rowStart, int pixels, int flags, byte c0, byte c1)
    {
        for (var x = 0; x < pixels; x++)
        {
            _current[rowStart + x] = (flags >> x & 1) != 0 ? c1 : c0;
        }
    }

    /// <summary>
    ///     Four cells of <paramref name="cellWidth" /> x <paramref name="cellHeight" /> from one flag
    ///     byte, two bits per cell from bit 0 up.
    /// </summary>
    private void FillFourColourRow(int origin, int flags, ReadOnlySpan<byte> colours, int cellWidth, int cellHeight)
    {
        for (var k = 0; k < 4; k++)
        {
            FillRect(origin + k * cellWidth, cellWidth, cellHeight, colours[flags >> (2 * k) & 3]);
        }
    }

    private void FillRect(int origin, int width, int height, byte value)
    {
        for (var r = 0; r < height; r++)
        {
            _current.AsSpan(origin + r * Width, width).Fill(value);
        }
    }

    /// <summary>
    ///     The 8x8 copy at 0x4D9D1C: eight rows of eight bytes from <paramref name="source" /> in
    ///     <paramref name="from" />, linear addressing (a vector that crosses a row edge wraps into
    ///     the neighbouring row exactly as the player's pointer arithmetic does). A source that
    ///     leaves the buffer is skipped and counted.
    /// </summary>
    private void CopyBlock(int destination, int source, byte[] from)
    {
        if (source < 0 || source + 7 * Width + 8 > from.Length)
        {
            OutOfRangeCopies++;
            return;
        }

        if (ReferenceEquals(from, _current))
        {
            // Rows are read then written one at a time in the player; a temporary keeps that
            // order exact should a source ever overlap its destination.
            Span<byte> temp = stackalloc byte[64];
            for (var r = 0; r < 8; r++)
            {
                from.AsSpan(source + r * Width, 8).CopyTo(temp[(r * 8)..]);
            }

            for (var r = 0; r < 8; r++)
            {
                temp.Slice(r * 8, 8).CopyTo(_current.AsSpan(destination + r * Width));
            }

            return;
        }

        for (var r = 0; r < 8; r++)
        {
            from.AsSpan(source + r * Width, 8).CopyTo(_current.AsSpan(destination + r * Width));
        }
    }
}
