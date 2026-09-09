// ORIGINAL CLEAN-ROOM IMPLEMENTATION.
//
// Written from our own reverse engineering of RAD Game Tools' shipped decoder binkw32.dll
// (Fallout Tactics, md5 ecbd8213e89f8afde368f8eb05ff5a9c), via our Ghidra decompilation at
// tools/GhidraProject/ClassicRE/binkw32.dll.decompiled.txt and our own capstone disassembly of the
// same file, as written up in the specification document
//   scratchpad .../cleanroom/bink/SPEC.md  ("Bink Video (BIKi) - Format Specification"), section 3.
//
// NO FFmpeg- or libav-derived code, and no other third-party Bink implementation, was consulted,
// read, copied or paraphrased. ffmpeg was used only as a sealed black box: run as an executable,
// with its output pixels compared to ours.

namespace BethesdaMultitool.Core.Formats.Bink;

/// <summary>
///     Bink's bitstream reader: a 32-bit-word, LSB-first reader over the file's bytes.
///     <para>
///         The stream is bit 0 of byte 0, bit 1 of byte 0, ... bit 7 of byte 0, bit 0 of byte 1, and
///         a <c>k</c>-bit read returns those bits with the FIRST bit read in the LEAST significant
///         position of the result.
///     </para>
///     <para>
///         ⚠ Only whole 32-bit words are ever fetched, so a plane can legitimately read up to three
///         bytes past its own logical end; that is why <see cref="WordPointer" /> - not a bit count -
///         is what a plane's declared length is checked against, and why the reader tolerates a
///         short tail (missing bytes read as zero) rather than throwing at the last word of a file.
///     </para>
///     <para>
///         ⚠⚠ Each plane RESTARTS the cache (<see cref="ResetToWordBoundary" />). Getting that wrong
///         is silent for a while: the Y plane and the first chroma plane still decode, and only the
///         SECOND chroma plane's Huffman header blows up.
///     </para>
/// </summary>
internal sealed class BinkBitReader
{
    private readonly byte[] _data;
    private uint _cache;
    private int _cachedBits;

    /// <summary>Starts a reader at <paramref name="byteOffset" /> in <paramref name="data" />.</summary>
    internal BinkBitReader(byte[] data, int byteOffset)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentOutOfRangeException.ThrowIfNegative(byteOffset);

        _data = data;
        WordPointer = byteOffset;
    }

    /// <summary>
    ///     Byte offset of the next unread 32-bit word. A plane that has consumed its bitstream
    ///     exactly lands this on <c>(&amp;lengthField) + length</c>; a single mis-read bit anywhere
    ///     in the plane moves it, which makes this a far sharper check than pixel equality.
    /// </summary>
    internal int WordPointer { get; private set; }

    /// <summary>Discards the bit cache so the next read starts at the next whole word.</summary>
    internal void ResetToWordBoundary()
    {
        _cache = 0;
        _cachedBits = 0;
    }

    /// <summary>Positions the reader at a byte offset, discarding any cached bits.</summary>
    internal void SeekToByte(int byteOffset)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(byteOffset);

        WordPointer = byteOffset;
        ResetToWordBoundary();
    }

    /// <summary>Reads <paramref name="count" /> bits (1..32), first bit read in bit 0.</summary>
    internal uint Read(int count)
    {
        if (count is < 1 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        var mask = count == 32 ? uint.MaxValue : (1u << count) - 1;
        if (_cachedBits >= count)
        {
            var cached = _cache & mask;
            _cache >>= count;
            _cachedBits -= count;
            return cached;
        }

        var word = NextWord();
        var value = ((word << _cachedBits) | _cache) & mask;
        var shift = count - _cachedBits;

        // shift is 1..32; C# masks a uint shift count to 5 bits, so 32 must be spelled out.
        _cache = shift == 32 ? 0u : word >> shift;
        _cachedBits = _cachedBits + 32 - count;
        return value;
    }

    /// <summary>Reads a single bit. Equivalent to <c>Read(1)</c>.</summary>
    internal uint ReadBit()
    {
        return Read(1);
    }

    /// <summary>Reads a single bit as a bool.</summary>
    internal bool ReadFlag()
    {
        return Read(1) != 0;
    }

    /// <summary>
    ///     Returns the next <paramref name="count" /> bits without consuming them. Used only by the
    ///     Huffman symbol decode, which peeks a code book's full width and then consumes the length
    ///     the table reports.
    /// </summary>
    internal uint Peek(int count)
    {
        if (count is < 1 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        var mask = count == 32 ? uint.MaxValue : (1u << count) - 1;
        if (_cachedBits >= count)
        {
            return _cache & mask;
        }

        var word = WordAt(WordPointer);
        return ((word << _cachedBits) | _cache) & mask;
    }

    /// <summary>Consumes <paramref name="count" /> bits previously inspected with <see cref="Peek" />.</summary>
    internal void Consume(int count)
    {
        if (count > 0)
        {
            Read(count);
        }
    }

    private uint NextWord()
    {
        var word = WordAt(WordPointer);
        WordPointer += 4;
        return word;
    }

    private uint WordAt(int offset)
    {
        // Unaligned little-endian read. Frame and plane starts are byte offsets into the file and
        // are not word-aligned in general; the DLL reads them with a plain x86 dword load.
        if (offset >= 0 && offset + 4 <= _data.Length)
        {
            return (uint)(_data[offset]
                          | (_data[offset + 1] << 8)
                          | (_data[offset + 2] << 16)
                          | (_data[offset + 3] << 24));
        }

        uint value = 0;
        for (var i = 0; i < 4; i++)
        {
            var at = offset + i;
            if (at >= 0 && at < _data.Length)
            {
                value |= (uint)_data[at] << (i * 8);
            }
        }

        return value;
    }
}
