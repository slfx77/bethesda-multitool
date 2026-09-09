// ORIGINAL CLEAN-ROOM IMPLEMENTATION.
//
// Written from our own reverse engineering of RAD Game Tools' shipped decoder binkw32.dll
// (Fallout Tactics, md5 ecbd8213e89f8afde368f8eb05ff5a9c), via our Ghidra decompilation at
// tools/GhidraProject/ClassicRE/binkw32.dll.decompiled.txt and our own capstone disassembly of the
// same file, as written up in the specification document
//   scratchpad .../cleanroom/bink/SPEC.md  ("Bink Video (BIKi) - Format Specification"), sections
//   1 and 2.1 (_BinkOpen@8 at 0x30003A50, _BinkDoFrame@4 at 0x30005740).
//
// NO FFmpeg- or libav-derived code, and no other third-party Bink implementation, was consulted,
// read, copied or paraphrased.

using System.Buffers.Binary;
using System.Text;

namespace BethesdaMultitool.Core.Formats.Bink;

/// <summary>
///     A Bink movie container: the 0x2C header, the audio-track tables and the frame offset table.
///     Parsing is separate from decoding so <c>video info</c>, an asset census and the probe can
///     describe a file without paying for a frame.
///     <para>
///         Only <c>BIKi</c> is decodable here. <c>BIKf</c>/<c>BIKg</c>/<c>BIKh</c> parse as
///         containers (their headers are identical) but select an older colour-bundle coder and drop
///         the Y-plane length prefix, which this decoder does not implement — see
///         <see cref="IsDecodableVideo" />. No file in our 59-movie retail corpus is one.
///     </para>
/// </summary>
internal sealed class BinkFile
{
    /// <summary>Bytes of the fixed file header.</summary>
    internal const int HeaderSize = 0x2C;

    /// <summary>Header flag: the frame carries an alpha plane before Y.</summary>
    internal const uint FlagAlphaPlane = 0x0010_0000;

    /// <summary>Header flag: grayscale — the frame carries no chroma planes.</summary>
    internal const uint FlagGrayscale = 0x0002_0000;

    private BinkFile(byte[] bytes, string name)
    {
        Bytes = bytes;
        Name = name;
    }

    /// <summary>Source file name, for display.</summary>
    internal string Name { get; }

    /// <summary>The four-character magic, one of <c>BIKf BIKg BIKh BIKi</c>.</summary>
    internal string Magic { get; private init; } = string.Empty;

    /// <summary>Header <c>+0x04</c>: file length minus 8.</summary>
    internal uint DeclaredSize { get; private init; }

    /// <summary>Header <c>+0x08</c>, repeated at <c>+0x10</c>.</summary>
    internal int FrameCount { get; private init; }

    /// <summary>Header <c>+0x0C</c>: the largest frame in the file, in bytes.</summary>
    internal uint LargestFrameSize { get; private init; }

    internal int Width { get; private init; }

    internal int Height { get; private init; }

    /// <summary>Header <c>+0x1C</c>: frames per second dividend.</summary>
    internal uint FrameRateDividend { get; private init; }

    /// <summary>Header <c>+0x20</c>: frames per second divisor.</summary>
    internal uint FrameRateDivisor { get; private init; }

    /// <summary>Header <c>+0x24</c>: video flags.</summary>
    internal uint Flags { get; private init; }

    /// <summary>The audio tracks, in header order.</summary>
    internal IReadOnlyList<BinkAudioTrack> AudioTracks { get; private init; } = [];

    /// <summary>
    ///     <c>FrameCount + 1</c> raw offset-table entries. Bit 0 of each is the keyframe flag; the
    ///     final entry is the file length and carries no frame.
    /// </summary>
    internal IReadOnlyList<uint> RawFrameOffsets { get; private init; } = [];

    /// <summary>Frames per second, dividend over divisor.</summary>
    internal double FramesPerSecond =>
        FrameRateDivisor == 0 ? 0 : (double)FrameRateDividend / FrameRateDivisor;

    /// <summary>Seconds one frame is shown for.</summary>
    internal double SecondsPerFrame => FramesPerSecond > 0 ? 1.0 / FramesPerSecond : 0;

    /// <summary>Total run time in seconds.</summary>
    internal double DurationSeconds => FrameCount * SecondsPerFrame;

    /// <summary>True when the video payload is the <c>BIKi</c> coding this decoder implements.</summary>
    internal bool IsDecodableVideo =>
        string.Equals(Magic, "BIKi", StringComparison.Ordinal)
        && (Flags & FlagGrayscale) == 0
        && (Flags & FlagAlphaPlane) == 0;

    /// <summary>The whole file, which the decoder reads bit-wise.</summary>
    internal byte[] Bytes { get; }

    /// <summary>
    ///     Content probe. Checks the magic AND the arithmetic that must hold for a real container —
    ///     the size field, the repeated frame count, the offset table's monotonicity, its first
    ///     entry landing exactly on the byte after the table and its last entry equalling the file
    ///     length. A four-byte magic alone would claim any file that happens to start "BIKi"; every
    ///     one of these relations holds on 59/59 of our retail corpus.
    /// </summary>
    internal static bool IsBink(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderSize + 4)
        {
            return false;
        }

        if (bytes[0] != (byte)'B' || bytes[1] != (byte)'I' || bytes[2] != (byte)'K'
            || bytes[3] is not ((byte)'f' or (byte)'g' or (byte)'h' or (byte)'i'))
        {
            return false;
        }

        var declaredSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]);
        if (declaredSize != (uint)bytes.Length - 8)
        {
            return false;
        }

        var frames = BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]);
        var framesRepeat = BinaryPrimitives.ReadUInt32LittleEndian(bytes[0x10..]);
        if (frames == 0 || frames != framesRepeat || frames > 1_000_000)
        {
            return false;
        }

        var width = BinaryPrimitives.ReadUInt32LittleEndian(bytes[0x14..]);
        var height = BinaryPrimitives.ReadUInt32LittleEndian(bytes[0x18..]);
        if (width is 0 or > 16384 || height is 0 or > 16384)
        {
            return false;
        }

        var tracks = BinaryPrimitives.ReadUInt32LittleEndian(bytes[0x28..]);
        if (tracks > 256)
        {
            return false;
        }

        var tableStart = HeaderSize + (int)tracks * 12;
        var afterTable = tableStart + ((int)frames + 1) * 4;
        if (afterTable > bytes.Length)
        {
            return false;
        }

        var previous = 0u;
        for (var i = 0; i <= (int)frames; i++)
        {
            var entry = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(tableStart + i * 4)..]) & ~1u;
            if (i == 0 && entry != (uint)afterTable)
            {
                return false;
            }

            if (entry < previous || entry > (uint)bytes.Length)
            {
                return false;
            }

            previous = entry;
        }

        return previous == (uint)bytes.Length;
    }

    /// <summary>Parses a container. Throws <see cref="InvalidDataException" /> on a malformed file.</summary>
    internal static BinkFile Parse(byte[] bytes, string name)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        if (bytes.Length < HeaderSize)
        {
            throw new InvalidDataException($"{name}: too short to be a Bink file ({bytes.Length} bytes).");
        }

        var span = bytes.AsSpan();
        var magic = Encoding.ASCII.GetString(span[..4]);
        if (magic is not ("BIKf" or "BIKg" or "BIKh" or "BIKi"))
        {
            // The DLL's own wording for this rejection.
            throw new InvalidDataException($"{name}: Not a Bink file.");
        }

        var frameCount = BinaryPrimitives.ReadUInt32LittleEndian(span[8..]);
        if (frameCount == 0)
        {
            throw new InvalidDataException(
                $"{name}: The file doesn't contain any compressed frames yet.");
        }

        var trackCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[0x28..]);
        var tableStart = HeaderSize + trackCount * 12;
        var offsetCount = (int)frameCount + 1;
        if (tableStart + offsetCount * 4 > bytes.Length)
        {
            throw new InvalidDataException(
                $"{name}: frame offset table ({offsetCount} entries) runs past the end of the file.");
        }

        var tracks = new BinkAudioTrack[trackCount];
        for (var t = 0; t < trackCount; t++)
        {
            tracks[t] = new BinkAudioTrack(
                BinaryPrimitives.ReadUInt32LittleEndian(span[(HeaderSize + t * 4)..]),
                BinaryPrimitives.ReadUInt32LittleEndian(span[(HeaderSize + trackCount * 4 + t * 4)..]),
                BinaryPrimitives.ReadUInt32LittleEndian(span[(HeaderSize + trackCount * 8 + t * 4)..]));
        }

        var offsets = new uint[offsetCount];
        for (var i = 0; i < offsetCount; i++)
        {
            offsets[i] = BinaryPrimitives.ReadUInt32LittleEndian(span[(tableStart + i * 4)..]);
        }

        return new BinkFile(bytes, name)
        {
            Magic = magic,
            DeclaredSize = BinaryPrimitives.ReadUInt32LittleEndian(span[4..]),
            FrameCount = (int)frameCount,
            LargestFrameSize = BinaryPrimitives.ReadUInt32LittleEndian(span[0x0C..]),
            Width = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[0x14..]),
            Height = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[0x18..]),
            FrameRateDividend = BinaryPrimitives.ReadUInt32LittleEndian(span[0x1C..]),
            FrameRateDivisor = BinaryPrimitives.ReadUInt32LittleEndian(span[0x20..]),
            Flags = BinaryPrimitives.ReadUInt32LittleEndian(span[0x24..]),
            AudioTracks = tracks,
            RawFrameOffsets = offsets
        };
    }

    /// <summary>True when frame <paramref name="index" /> is a keyframe (offset-table bit 0).</summary>
    internal bool IsKeyFrame(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, FrameCount);

        return (RawFrameOffsets[index] & 1) != 0;
    }

    /// <summary>Byte offset of frame <paramref name="index" /> from the start of the file.</summary>
    internal int FrameStart(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, FrameCount);

        return (int)(RawFrameOffsets[index] & ~1u);
    }

    /// <summary>One past the last byte of frame <paramref name="index" />.</summary>
    internal int FrameEnd(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, FrameCount);

        return (int)(RawFrameOffsets[index + 1] & ~1u);
    }

    /// <summary>
    ///     Byte offset at which frame <paramref name="index" />'s VIDEO payload starts: the frame
    ///     start advanced past one length-prefixed packet per audio track. The length field counts
    ///     the bytes that FOLLOW it, so each track costs <c>4 + length</c>. Proven on all 109,985
    ///     frames of the 59-file corpus — every packet tiles inside the frame's byte range and the
    ///     Y-plane length that follows lands inside it too.
    /// </summary>
    internal int VideoStart(int index)
    {
        var position = FrameStart(index);
        var end = FrameEnd(index);
        foreach (var _ in AudioTracks)
        {
            if (position + 4 > end)
            {
                throw new InvalidDataException(
                    $"{Name}: frame {index}'s audio packet header runs past the frame.");
            }

            var length = BinaryPrimitives.ReadUInt32LittleEndian(Bytes.AsSpan(position));
            position += 4 + (int)length;
            if (position > end)
            {
                throw new InvalidDataException(
                    $"{Name}: frame {index}'s audio packet ({length} bytes) runs past the frame.");
            }
        }

        return position;
    }
}

/// <summary>
///     One audio track's header triple. Audio DECODING is out of scope for this pass — the video
///     decoder needs these only to skip packets correctly and to describe the file.
///     <para>
///         ⚑ WHAT THE DLL READS OUT OF THE DESCRIPTOR — measured 2026-09-08 by enumerating every
///         read of the descriptor array (<c>piVar1[0x7d]</c> in <c>_BinkOpen@8</c>, <c>+500</c> in
///         <c>_BinkOpenTrack@8</c> / <c>_BinkGetTrackType@8</c>) in
///         <c>tools/GhidraProject/ClassicRE/binkw32.dll.decompiled.txt</c>, lines 2677-2706 and
///         4430-4492. The track-open path uses EXACTLY four fields and nothing else: bits 0..15,
///         bit 29, bit 30 and bit 31. Bit 28 is never read by this build of the DLL.
///     </para>
///     <para>
///         ⚠⚠ Bits 28 and 31 split the corpus BY GAME and are perfectly anti-correlated: all 21
///         Brotherhood of Steel Xbox (2004) tracks are <c>0x7000xxxx</c> — bit 28 SET, bit 31
///         CLEAR — and all 17 Fallout Tactics (2001) tracks are <c>0xC000/0xE000xxxx</c> — bit 31
///         set, bit 28 clear. This DLL is Bink <b>1.0w</b> (its version resource; copyright
///         1994-2000) and opens an audio decoder ONLY when bit 31 is set (lines 2678, 4478), so
///         under this build the BOS tracks get NO decoder at all: they were encoded by a newer
///         Bink than the one reverse-engineered here. Bit 28's meaning therefore cannot be
///         established from this binary, which never reads it. The one hint is a sealed-oracle
///         observation and is recorded as nothing more: <c>ffprobe</c> labels the audio stream of
///         every bit-28 track with one codec name and of every bit-31 track with another, 21/21
///         and 17/17 — so the bit most likely marks a newer audio CODING, which a later audio
///         decoder would have to learn from its own RE of a newer DLL, not from here.
///     </para>
/// </summary>
/// <param name="MaxDecodedBufferSize">Largest decompressed buffer the track needs, in bytes.</param>
/// <param name="Descriptor">Packed sample rate and format bits.</param>
/// <param name="Id">Track id. Every track in our corpus has id 0.</param>
internal readonly record struct BinkAudioTrack(uint MaxDecodedBufferSize, uint Descriptor, uint Id)
{
    /// <summary>
    ///     Descriptor bits 0..15: sample rate in Hz. The DLL masks <c>&amp; 0xffff</c> before handing
    ///     it to its decoder-open callback and to <c>FUN_3001ba10</c> (decompile lines 2679, 2707,
    ///     4479, 4490). Corroborated by ffprobe's <c>sample_rate</c> on all 38 corpus tracks
    ///     (22050 x1, 44100 x20, 48000 x17).
    /// </summary>
    internal int SampleRate => (int)(Descriptor & 0xFFFF);

    /// <summary>
    ///     Descriptor bit 29: channel count minus one. ⚑ PROVEN as the DLL's reading: it computes
    ///     <c>(x &gt;&gt; 0x1d &amp; 1) + 1</c> and passes that as the CHANNEL COUNT argument of its
    ///     decoder-open callback (line 2687), of <c>FUN_3001ba10</c> (lines 2707, 4479) and into
    ///     the track record's channel slot (line 4492), and multiplies the per-second buffer size by
    ///     it (line 2699). Corroborated by the sealed oracle: the three <c>0xC000AC44</c> tracks
    ///     (bit 29 clear — tut2a/b/c) are the ONLY ones ffprobe reports as <c>channels=1</c>; the
    ///     other 35 report 2. What is NOT proven is that the DLL's audio decoder honours it —
    ///     nothing here decodes audio.
    /// </summary>
    internal int Channels => (int)((Descriptor >> 29) & 1) + 1;

    /// <summary>
    ///     Descriptor bit 30: 1 = 16-bit samples, 0 = 8-bit. ⚑ PROVEN as the DLL's reading, spelled
    ///     two ways in the same binary: <c>(x &gt;&gt; 0x1e &amp; 1) * 8 + 8</c> is the BITS PER
    ///     SAMPLE argument of the decoder-open callback (line 2686), and <c>(x &gt;&gt; 0x1b &amp; 8)
    ///     + 8</c> — the same bit 30, shifted so it reads as 8 — fills the track record's depth slot
    ///     (line 4491), whose value of 8 halves the track's buffer (line 4494); the flag at line
    ///     2690 is that depth compared with 8. Every corpus track sets it (all 38 are 16-bit), so
    ///     the 8-bit branch is never exercised by retail data.
    /// </summary>
    internal int BitsPerSample => (int)((Descriptor >> 30) & 1) * 8 + 8;

    /// <summary>
    ///     Descriptor bit 31: when clear the DLL never opens a decoder for this track (lines 2678,
    ///     4478). Set on all 38 corpus tracks.
    /// </summary>
    internal bool HasDecoder => (Descriptor & 0x8000_0000) != 0;
}
