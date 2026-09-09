// ORIGINAL CLEAN-ROOM IMPLEMENTATION.
//
// Written from our own reverse engineering of RAD Game Tools' Smacker decoder as statically linked
// into two shipped games — Redguard's RG.EXE ("*** Smacker Version: 3.2b***") and Battlespire's
// GAME.EXE ("*** Smacker Version: 3.0k***") — via our Ghidra decompilations at
// tools/GhidraProject/ClassicRE/RG.EXE.decompiled.txt and GAME.EXE.decompiled.txt and our own
// capstone disassembly of the unpacked LE images, as written up in the specification document
//   scratchpad .../gap2/smacker/SPEC.md  ("Smacker Video (SMK2) - Format Specification"),
//   sections 1 and 2 (SmackOpen = RG.EXE FUN_000cd730 / GAME.EXE FUN_00085af0; the frame chunk
//   layout = FUN_000ce270).
//
// NO FFmpeg- or libav-derived code, and no other third-party Smacker implementation, was consulted,
// read, copied or paraphrased. ffmpeg was used only as a sealed black box: run as an executable,
// with its output pixels and samples compared to ours.

using System.Buffers.Binary;

namespace BethesdaMultitool.Core.Formats.Smacker;

/// <summary>
///     A Smacker movie container: the 104-byte header, the frame size/type tables, the four
///     16-bit Huffman trees (read at open — every frame needs them) and the byte range of every
///     frame. Parsing is separate from decoding so <c>video info</c> and the asset census can
///     describe a file without paying for a frame.
///     <para>
///         ⚠ <see cref="Height" /> is the STORED height. When <see cref="IsYDoubled" /> or
///         <see cref="IsYInterlaced" /> is set the game doubles it before it allocates a surface
///         (SmackOpen: <c>param_3[2] = param_3[2] * 2</c> once bit 4 or 5 of its private flag byte
///         is set) — see <see cref="DisplayHeight" />. Every Redguard movie and three of the six
///         Battlespire ones are stored at 640x240 and shown at 640x480.
///     </para>
/// </summary>
internal sealed class SmackerFile
{
    /// <summary>Bytes of the fixed file header.</summary>
    internal const int HeaderSize = 104;

    /// <summary>Audio tracks a file can carry.</summary>
    internal const int TrackCount = 7;

    /// <summary>Header flag bit 0: the file carries one extra "ring" frame after the last frame.</summary>
    internal const uint FlagRingFrame = 1;

    /// <summary>Header flag bit 1: every stored row is shown twice.</summary>
    internal const uint FlagYDoubled = 2;

    /// <summary>Header flag bit 2: stored rows are shown on alternate lines.</summary>
    internal const uint FlagYInterlaced = 4;

    private SmackerFile(byte[] bytes, string name)
    {
        Bytes = bytes;
        Name = name;
    }

    /// <summary>Source file name, for display.</summary>
    internal string Name { get; }

    /// <summary>The whole file, which the decoders read bit-wise.</summary>
    internal byte[] Bytes { get; }

    /// <summary>The four-character magic, <c>SMK2</c> or <c>SMK4</c>.</summary>
    internal string Magic { get; private init; } = string.Empty;

    /// <summary>Stored frame width in pixels.</summary>
    internal int Width { get; private init; }

    /// <summary>Stored frame height in pixels (see <see cref="DisplayHeight" />).</summary>
    internal int Height { get; private init; }

    /// <summary>The height the game shows the movie at: stored height, doubled when Y-doubled or Y-interlaced.</summary>
    internal int DisplayHeight => IsYDoubled || IsYInterlaced ? Height * 2 : Height;

    /// <summary>Playable frames (the ring frame, when present, is NOT counted).</summary>
    internal int FrameCount { get; private init; }

    /// <summary>
    ///     Header <c>+0x10</c>, signed. Negative: <c>100000 / -value</c> frames per second; positive:
    ///     <c>1000 / value</c> frames per second (the game scales a positive value by 100 and then
    ///     divides 100000 by it, which is the same thing).
    /// </summary>
    internal int FrameRateField { get; private init; }

    /// <summary>Header <c>+0x14</c> flags.</summary>
    internal uint Flags { get; private init; }

    /// <summary>True when the file carries a ring frame after the last frame.</summary>
    internal bool HasRingFrame => (Flags & FlagRingFrame) != 0;

    /// <summary>True when rows are shown twice.</summary>
    internal bool IsYDoubled => (Flags & FlagYDoubled) != 0;

    /// <summary>True when rows are shown on alternate lines.</summary>
    internal bool IsYInterlaced => (Flags & FlagYInterlaced) != 0;

    /// <summary>Header <c>+0x18</c>: the largest unpacked audio chunk per track, in bytes.</summary>
    internal IReadOnlyList<uint> AudioSizes { get; private init; } = [];

    /// <summary>Header <c>+0x34</c>: bytes of Huffman tree data that follow the frame tables.</summary>
    internal uint TreesSize { get; private init; }

    /// <summary>Header <c>+0x38..+0x44</c>: the decoder's allocation sizes for the four trees (MMAP, MCLR, FULL, TYPE).</summary>
    internal IReadOnlyList<uint> TreeAllocationSizes { get; private init; } = [];

    /// <summary>The seven audio track descriptors, in header order (absent tracks have <see cref="SmackerAudioTrack.IsPresent" /> false).</summary>
    internal IReadOnlyList<SmackerAudioTrack> AudioTracks { get; private init; } = [];

    /// <summary>
    ///     Raw frame-size entries, one per frame plus one for the ring frame when present. Bit 0
    ///     is the keyframe flag; bits 0-1 are masked off for the byte size.
    /// </summary>
    internal IReadOnlyList<uint> RawFrameSizes { get; private init; } = [];

    /// <summary>Frame type bytes: bit 0 = a palette chunk opens the frame; bit 1 + t = audio track t is present.</summary>
    internal IReadOnlyList<byte> FrameTypes { get; private init; } = [];

    /// <summary>Byte offset of each frame's data (frames, then the ring frame when present).</summary>
    internal IReadOnlyList<int> FrameOffsets { get; private init; } = [];

    /// <summary>Byte offset where the first frame begins (the header, tables and trees precede it).</summary>
    internal int FirstFrameOffset { get; private init; }

    /// <summary>Bits of the tree block actually consumed by the four trees.</summary>
    internal long TreeBitsUsed { get; private init; }

    internal SmackerTree16 MapTree { get; private init; } = SmackerTree16.Empty;

    internal SmackerTree16 ColorTree { get; private init; } = SmackerTree16.Empty;

    internal SmackerTree16 FullTree { get; private init; } = SmackerTree16.Empty;

    internal SmackerTree16 TypeTree { get; private init; } = SmackerTree16.Empty;

    /// <summary>Frames per second from <see cref="FrameRateField" />; 10 when the field is 0 (a convention — the game's own arithmetic degenerates there).</summary>
    internal double FramesPerSecond => FrameRateField switch
    {
        < 0 => 100000.0 / -FrameRateField,
        > 0 => 1000.0 / FrameRateField,
        _ => 10.0
    };

    /// <summary>Seconds one frame is shown for.</summary>
    internal double SecondsPerFrame => 1.0 / FramesPerSecond;

    /// <summary>Total run time in seconds.</summary>
    internal double DurationSeconds => FrameCount * SecondsPerFrame;

    /// <summary>True when frame <paramref name="index" />'s size entry carries the keyframe bit.</summary>
    internal bool IsKeyFrame(int index)
    {
        return (RawFrameSizes[index] & 1) != 0;
    }

    /// <summary>Byte size of frame <paramref name="index" /> (flag bits masked off).</summary>
    internal int FrameSize(int index)
    {
        return (int)(RawFrameSizes[index] & ~3u);
    }

    /// <summary>True when frame <paramref name="index" /> opens with a palette chunk.</summary>
    internal bool HasPalette(int index)
    {
        return (FrameTypes[index] & 1) != 0;
    }

    /// <summary>True when frame <paramref name="index" /> carries a chunk for audio track <paramref name="track" />.</summary>
    internal bool HasAudio(int index, int track)
    {
        return (FrameTypes[index] & (2 << track)) != 0;
    }

    /// <summary>
    ///     Content probe: the magic AND the arithmetic a real container must satisfy — the header,
    ///     the two frame tables, the declared tree bytes and the masked frame sizes tile the file
    ///     EXACTLY. Measured on all 17 retail movies (11 Redguard + 6 Battlespire): 17/17 tile.
    /// </summary>
    internal static bool IsSmacker(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderSize)
        {
            return false;
        }

        if (bytes[0] != (byte)'S' || bytes[1] != (byte)'M' || bytes[2] != (byte)'K'
            || bytes[3] is not ((byte)'2' or (byte)'4'))
        {
            return false;
        }

        var width = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]);
        var height = BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]);
        var frames = BinaryPrimitives.ReadInt32LittleEndian(bytes[12..]);
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(bytes[20..]);
        var treesSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes[52..]);
        if (width == 0 || height == 0 || width > 16384 || height > 16384 || frames <= 0)
        {
            return false;
        }

        var tableFrames = (long)frames + (flags & FlagRingFrame);
        var tablesEnd = HeaderSize + tableFrames * 5;
        if (tablesEnd + treesSize > bytes.Length)
        {
            return false;
        }

        long total = tablesEnd + treesSize;
        for (var i = 0; i < tableFrames; i++)
        {
            total += BinaryPrimitives.ReadUInt32LittleEndian(bytes[(HeaderSize + i * 4)..]) & ~3u;
            if (total > bytes.Length)
            {
                return false;
            }
        }

        return total == bytes.Length;
    }

    /// <summary>Parses the container, including its four Huffman trees.</summary>
    internal static SmackerFile Parse(byte[] bytes, string name)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(name);

        if (!IsSmacker(bytes))
        {
            throw new InvalidDataException($"{name} is not a Smacker movie (bad magic or the frames do not tile the file).");
        }

        var span = bytes.AsSpan();
        var frames = BinaryPrimitives.ReadInt32LittleEndian(span[12..]);
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(span[20..]);
        var audioSizes = new uint[TrackCount];
        for (var t = 0; t < TrackCount; t++)
        {
            audioSizes[t] = BinaryPrimitives.ReadUInt32LittleEndian(span[(24 + t * 4)..]);
        }

        var treesSize = BinaryPrimitives.ReadUInt32LittleEndian(span[52..]);
        var treeAlloc = new uint[4];
        for (var t = 0; t < 4; t++)
        {
            treeAlloc[t] = BinaryPrimitives.ReadUInt32LittleEndian(span[(56 + t * 4)..]);
        }

        var tracks = new SmackerAudioTrack[TrackCount];
        for (var t = 0; t < TrackCount; t++)
        {
            tracks[t] = new SmackerAudioTrack(t, BinaryPrimitives.ReadUInt32LittleEndian(span[(72 + t * 4)..]), audioSizes[t]);
        }

        var tableFrames = frames + (int)(flags & FlagRingFrame);
        var sizes = new uint[tableFrames];
        var types = new byte[tableFrames];
        for (var i = 0; i < tableFrames; i++)
        {
            sizes[i] = BinaryPrimitives.ReadUInt32LittleEndian(span[(HeaderSize + i * 4)..]);
            types[i] = bytes[HeaderSize + tableFrames * 4 + i];
        }

        var treesOffset = HeaderSize + tableFrames * 5;
        var reader = new SmackerBitReader(bytes, treesOffset, (int)treesSize);
        var map = SmackerTree16.Read(reader);
        var color = SmackerTree16.Read(reader);
        var full = SmackerTree16.Read(reader);
        var type = SmackerTree16.Read(reader);

        var firstFrame = treesOffset + (int)treesSize;
        var offsets = new int[tableFrames];
        var offset = firstFrame;
        for (var i = 0; i < tableFrames; i++)
        {
            offsets[i] = offset;
            offset += (int)(sizes[i] & ~3u);
        }

        return new SmackerFile(bytes, name)
        {
            Magic = System.Text.Encoding.ASCII.GetString(bytes, 0, 4),
            Width = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[4..]),
            Height = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[8..]),
            FrameCount = frames,
            FrameRateField = BinaryPrimitives.ReadInt32LittleEndian(span[16..]),
            Flags = flags,
            AudioSizes = audioSizes,
            TreesSize = treesSize,
            TreeAllocationSizes = treeAlloc,
            AudioTracks = tracks,
            RawFrameSizes = sizes,
            FrameTypes = types,
            FrameOffsets = offsets,
            FirstFrameOffset = firstFrame,
            TreeBitsUsed = reader.BitPosition,
            MapTree = map,
            ColorTree = color,
            FullTree = full,
            TypeTree = type
        };
    }

    /// <summary>
    ///     Splits frame <paramref name="index" /> (a playable frame, or the ring frame at
    ///     <see cref="FrameCount" /> when present) into its chunks, in the order the game walks
    ///     them (FUN_000ce270): an optional palette chunk whose first byte is its length in
    ///     4-byte units, then one chunk per present audio track, each opening with its own u32
    ///     length (that length INCLUDES the four bytes of the field), then the video block stream
    ///     to the end of the frame.
    /// </summary>
    internal SmackerFrameLayout GetFrameLayout(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, FrameOffsets.Count);

        var start = FrameOffsets[index];
        var size = FrameSize(index);
        var end = start + size;
        var p = start;
        var type = FrameTypes[index];

        var palette = default(SmackerByteRange);
        if ((type & 1) != 0)
        {
            if (p >= end)
            {
                throw new InvalidDataException($"{Name}: frame {index} declares a palette chunk but is empty.");
            }

            var length = Bytes[p] * 4;
            if (length == 0 || p + length > end)
            {
                throw new InvalidDataException($"{Name}: frame {index}'s palette chunk ({length} bytes) overruns the frame.");
            }

            palette = new SmackerByteRange(p, length);
            p += length;
        }

        var audio = new SmackerByteRange[TrackCount];
        for (var t = 0; t < TrackCount; t++)
        {
            if ((type & (2 << t)) == 0)
            {
                continue;
            }

            if (p + 4 > end)
            {
                throw new InvalidDataException($"{Name}: frame {index}'s audio track {t} has no length field.");
            }

            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(Bytes.AsSpan(p));
            if (length < 4 || p + length > end)
            {
                throw new InvalidDataException($"{Name}: frame {index}'s audio track {t} ({length} bytes) overruns the frame.");
            }

            audio[t] = new SmackerByteRange(p, length);
            p += length;
        }

        return new SmackerFrameLayout(palette, audio, new SmackerByteRange(p, end - p));
    }
}

/// <summary>A byte window into <see cref="SmackerFile.Bytes" />; <see cref="Length" /> 0 means absent.</summary>
internal readonly record struct SmackerByteRange(int Offset, int Length)
{
    /// <summary>True when the chunk exists.</summary>
    internal bool IsPresent => Length > 0;
}

/// <summary>The chunks of one frame, in file order.</summary>
internal sealed record SmackerFrameLayout(
    SmackerByteRange Palette, IReadOnlyList<SmackerByteRange> Audio, SmackerByteRange Video);

/// <summary>
///     One of the seven audio track descriptors from the header (<c>+0x48 + 4t</c>). Bit 31 =
///     the track's chunks are compressed (the game's SmackOpen tests that bit before allocating a
///     decoder for the track; bit 30 = the track is present (the descriptor is otherwise 0);
///     bit 29 = 16-bit; bit 28 = stereo; bits 0-23 = the sample rate in Hz. The per-chunk flag bits
///     (stereo, 16-bit) are what the decoder actually follows; the header's are what a player
///     allocates from.
/// </summary>
internal sealed class SmackerAudioTrack
{
    internal SmackerAudioTrack(int index, uint descriptor, uint largestChunk)
    {
        Index = index;
        Descriptor = descriptor;
        LargestUnpackedChunk = largestChunk;
    }

    /// <summary>Track number, 0..6.</summary>
    internal int Index { get; }

    /// <summary>The raw header word.</summary>
    internal uint Descriptor { get; }

    /// <summary>Header <c>+0x18 + 4t</c>: the largest unpacked chunk, in bytes.</summary>
    internal uint LargestUnpackedChunk { get; }

    /// <summary>True when the track exists (descriptor bit 30, or a non-zero descriptor).</summary>
    internal bool IsPresent => Descriptor != 0 && (Descriptor & 0x4000_0000) != 0;

    /// <summary>True when the track's chunks are Huffman/DPCM compressed rather than raw PCM.</summary>
    internal bool IsCompressed => (Descriptor & 0x8000_0000) != 0;

    /// <summary>True for 16-bit samples.</summary>
    internal bool Is16Bit => (Descriptor & 0x2000_0000) != 0;

    /// <summary>True for two channels.</summary>
    internal bool IsStereo => (Descriptor & 0x1000_0000) != 0;

    /// <summary>Channels, 1 or 2.</summary>
    internal int Channels => IsStereo ? 2 : 1;

    /// <summary>Bits per sample, 8 or 16.</summary>
    internal int BitsPerSample => Is16Bit ? 16 : 8;

    /// <summary>Sample rate in Hz.</summary>
    internal int SampleRate => (int)(Descriptor & 0x00FF_FFFF);
}
