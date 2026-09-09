// ORIGINAL CLEAN-ROOM IMPLEMENTATION.
//
// Written from our own reverse engineering of RAD Game Tools' Smacker decoder as statically linked
// into two shipped games — Redguard's RG.EXE ("*** Smacker Version: 3.2b***") and Battlespire's
// GAME.EXE ("*** Smacker Version: 3.0k***") — via our Ghidra decompilations at
// tools/GhidraProject/ClassicRE/RG.EXE.decompiled.txt and GAME.EXE.decompiled.txt and our own
// capstone disassembly of the unpacked LE images, as written up in the specification document
//   scratchpad .../gap2/smacker/SPEC.md  ("Smacker Video (SMK2) - Format Specification"),
//   section 6 (the audio chunk decoder, RG.EXE FUN_00114930, called from FUN_000ccf60 with the
//   chunk pointer advanced past its two length words).
//
// NO FFmpeg- or libav-derived code, and no other third-party Smacker implementation, was consulted,
// read, copied or paraphrased. ffmpeg was used only as a sealed black box: run as an executable,
// with its output samples compared to ours.

using System.Buffers.Binary;

namespace BethesdaMultitool.Core.Formats.Smacker;

/// <summary>
///     Decodes Smacker's compressed audio chunks: Huffman-coded DPCM, one 8-bit tree per byte of
///     each channel's sample, with the first sample stored raw.
///     <para>
///         Chunk layout (after the u32 chunk length): u32 unpacked byte count, then a bitstream —
///         bit "data present" (0 = the chunk decodes to nothing), bit "stereo", bit "16-bit", then
///         1 / 2 / 4 8-bit trees each written as <c>[1][tree][0]</c>, then the base sample(s) as
///         raw 8-bit fields, then one delta per tree per sample. Output is unsigned 8-bit or signed
///         16-bit little-endian, interleaved left/right.
///     </para>
///     <para>
///         ⚠⚠ The BASE bytes are written HIGH byte first and RIGHT channel first, the opposite of
///         the little-endian, left-first output: 16-bit stereo reads <c>R.hi R.lo L.hi L.lo</c>
///         (<c>0x114e2d</c> assembles them as <c>CONCAT31(CONCAT21(CONCAT11(b0,b1),b2),b3)</c> and
///         stores the dword — L in the low half). The deltas are the natural way round: tree 0 is
///         the low byte of the first channel.
///     </para>
/// </summary>
internal static class SmackerAudioDecoder
{
    /// <summary>
    ///     Decodes one audio chunk at <paramref name="range" /> of <paramref name="bytes" />
    ///     (the range starts at the chunk's own u32 length). Returns the PCM bytes, or an empty
    ///     array for a chunk whose "data present" bit is clear.
    /// </summary>
    internal static SmackerAudioChunk Decode(byte[] bytes, SmackerByteRange range)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        if (range.Length < 8)
        {
            throw new InvalidDataException("A Smacker audio chunk is shorter than its two length words.");
        }

        var unpacked = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(range.Offset + 4));
        if (unpacked < 0 || unpacked > 1 << 26)
        {
            throw new InvalidDataException($"A Smacker audio chunk declares an implausible {unpacked} unpacked bytes.");
        }

        var reader = new SmackerBitReader(bytes, range.Offset + 8, range.Length - 8);
        if (reader.ReadBit() == 0)
        {
            return new SmackerAudioChunk([], false, false, reader.BitPosition);
        }

        var stereo = reader.ReadBit() == 1;
        var is16Bit = reader.ReadBit() == 1;
        var treeCount = (stereo ? 2 : 1) * (is16Bit ? 2 : 1);
        var trees = new SmackerTree8[treeCount];
        for (var i = 0; i < treeCount; i++)
        {
            trees[i] = SmackerTree8.Read(reader);
        }

        var output = new byte[unpacked];
        if (unpacked == 0)
        {
            return new SmackerAudioChunk(output, stereo, is16Bit, reader.BitPosition);
        }

        if (!is16Bit && !stereo)
        {
            var v = (int)reader.Read(8);
            output[0] = (byte)v;
            for (var i = 1; i < unpacked; i++)
            {
                v = (v + trees[0].Decode(reader)) & 0xFF;
                output[i] = (byte)v;
            }
        }
        else if (is16Bit && !stereo)
        {
            var hi = (int)reader.Read(8);
            var lo = (int)reader.Read(8);
            var v = (hi << 8) | lo;
            BinaryPrimitives.WriteUInt16LittleEndian(output, (ushort)v);
            for (var i = 1; i < unpacked / 2; i++)
            {
                v = (v + (trees[0].Decode(reader) | (trees[1].Decode(reader) << 8))) & 0xFFFF;
                BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(2 * i), (ushort)v);
            }
        }
        else if (!is16Bit)
        {
            var right = (int)reader.Read(8);
            var left = (int)reader.Read(8);
            output[0] = (byte)left;
            output[1] = (byte)right;
            for (var i = 1; i < unpacked / 2; i++)
            {
                left = (left + trees[0].Decode(reader)) & 0xFF;
                right = (right + trees[1].Decode(reader)) & 0xFF;
                output[2 * i] = (byte)left;
                output[2 * i + 1] = (byte)right;
            }
        }
        else
        {
            var rightHi = (int)reader.Read(8);
            var rightLo = (int)reader.Read(8);
            var leftHi = (int)reader.Read(8);
            var leftLo = (int)reader.Read(8);
            var left = (leftHi << 8) | leftLo;
            var right = (rightHi << 8) | rightLo;
            BinaryPrimitives.WriteUInt16LittleEndian(output, (ushort)left);
            BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(2), (ushort)right);
            for (var i = 1; i < unpacked / 4; i++)
            {
                left = (left + (trees[0].Decode(reader) | (trees[1].Decode(reader) << 8))) & 0xFFFF;
                right = (right + (trees[2].Decode(reader) | (trees[3].Decode(reader) << 8))) & 0xFFFF;
                BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(4 * i), (ushort)left);
                BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(4 * i + 2), (ushort)right);
            }
        }

        return new SmackerAudioChunk(output, stereo, is16Bit, reader.BitPosition);
    }

    /// <summary>
    ///     Decodes every chunk of <paramref name="track" /> in <paramref name="file" /> and
    ///     concatenates the PCM. Uncompressed tracks (header bit 31 clear) are copied raw after
    ///     their length word.
    /// </summary>
    internal static SmackerAudioTrackPcm DecodeTrack(SmackerFile file, int track)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentOutOfRangeException.ThrowIfNegative(track);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(track, SmackerFile.TrackCount);

        var descriptor = file.AudioTracks[track];
        using var pcm = new MemoryStream();
        var chunks = 0;
        var stereo = descriptor.IsStereo;
        var is16Bit = descriptor.Is16Bit;
        for (var i = 0; i < file.FrameCount; i++)
        {
            if (!file.HasAudio(i, track))
            {
                continue;
            }

            var range = file.GetFrameLayout(i).Audio[track];
            chunks++;
            if (!descriptor.IsCompressed)
            {
                // Raw PCM: the game memcpy's the bytes after the length word into the ring buffer.
                pcm.Write(file.Bytes, range.Offset + 4, range.Length - 4);
                continue;
            }

            var chunk = Decode(file.Bytes, range);
            if (chunk.Pcm.Length > 0)
            {
                stereo = chunk.IsStereo;
                is16Bit = chunk.Is16Bit;
            }

            pcm.Write(chunk.Pcm);
        }

        return new SmackerAudioTrackPcm(pcm.ToArray(), descriptor.SampleRate, stereo ? 2 : 1, is16Bit ? 16 : 8, chunks);
    }
}

/// <summary>One decoded audio chunk.</summary>
internal sealed record SmackerAudioChunk(byte[] Pcm, bool IsStereo, bool Is16Bit, long BitsUsed);

/// <summary>A whole decoded audio track: interleaved PCM plus its format.</summary>
internal sealed record SmackerAudioTrackPcm(byte[] Pcm, int SampleRate, int Channels, int BitsPerSample, int ChunkCount)
{
    /// <summary>Frames (sample tuples) in the track.</summary>
    internal int SampleFrames => Pcm.Length / (Channels * BitsPerSample / 8);

    /// <summary>Seconds of audio.</summary>
    internal double Seconds => SampleRate > 0 ? SampleFrames / (double)SampleRate : 0;
}
