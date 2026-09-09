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
// read, copied or paraphrased. ffmpeg was used only as a sealed black box: run as an executable,
// with its output pixels and samples compared to ours.

namespace BethesdaMultitool.Core.Formats.Smacker;

/// <summary>
///     Smacker's bitstream reader: LSB-first over the bytes of one chunk.
///     <para>
///         The game fetches whole little-endian 32-bit words and shifts them right one bit at a
///         time (RG.EXE <c>0x110a00</c>: <c>shr ebp,1</c> after a <c>dec byte [0x3ae9d0]</c>
///         refill counter), which is exactly bit 0 of byte 0, bit 1 of byte 0, ... bit 7, then bit 0
///         of byte 1. An <c>n</c>-bit field is assembled with the FIRST bit read in the LEAST
///         significant position (the 8-bit leaf reads at <c>0x110a4a</c> do <c>mov al,bl</c> after
///         shifting eight bits down).
///     </para>
///     <para>
///         ⚠ Reads past the end return zero bits rather than throwing. The game reads whole words,
///         so a stream may legitimately end mid-word; whether a chunk was consumed EXACTLY is what
///         <see cref="BitPosition" /> is for.
///     </para>
/// </summary>
internal sealed class SmackerBitReader
{
    private readonly byte[] _data;
    private readonly int _end;
    private int _bytePosition;
    private int _bitInByte;

    /// <summary>Starts a reader over <paramref name="data" />[<paramref name="offset" />..<paramref name="offset" /> + <paramref name="length" />).</summary>
    internal SmackerBitReader(byte[] data, int offset, int length)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (offset + length > data.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "The window runs past the end of the data.");
        }

        _data = data;
        _bytePosition = offset;
        _end = offset + length;
        Start = offset;
    }

    /// <summary>Byte offset the reader started at.</summary>
    internal int Start { get; }

    /// <summary>Bits consumed since <see cref="Start" />.</summary>
    internal long BitPosition => ((long)(_bytePosition - Start) << 3) + _bitInByte;

    /// <summary>Bytes consumed since <see cref="Start" />, rounded up to the byte the reader is in.</summary>
    internal int BytesConsumed => _bytePosition - Start + (_bitInByte > 0 ? 1 : 0);

    /// <summary>True once every bit of the window has been consumed.</summary>
    internal bool IsExhausted => _bytePosition >= _end;

    /// <summary>Reads one bit.</summary>
    internal int ReadBit()
    {
        if (_bytePosition >= _end)
        {
            return 0;
        }

        var bit = (_data[_bytePosition] >> _bitInByte) & 1;
        if (++_bitInByte == 8)
        {
            _bitInByte = 0;
            _bytePosition++;
        }

        return bit;
    }

    /// <summary>Reads <paramref name="count" /> bits (1..32), first bit read in bit 0.</summary>
    internal uint Read(int count)
    {
        if (count is < 1 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        uint value = 0;
        for (var i = 0; i < count; i++)
        {
            value |= (uint)ReadBit() << i;
        }

        return value;
    }
}
