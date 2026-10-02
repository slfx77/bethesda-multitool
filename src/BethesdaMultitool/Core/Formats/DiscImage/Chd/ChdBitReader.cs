namespace BethesdaMultitool.Core.Formats.DiscImage.Chd;

/// <summary>
///     MSB-first bit reader with a 64-bit window: the bit order every CHD v5 Huffman stream is
///     written in (the compressed hunk map and the <c>huff</c> codec). Reading past the end feeds
///     zero bytes and is reported by <see cref="Overflow" /> — a stream that ran short is corrupt,
///     but the decoder gets to finish its table before it is told so.
/// </summary>
internal ref struct ChdBitReader
{
    private readonly ReadOnlySpan<byte> _data;
    private ulong _buffer;
    private int _bits;
    private int _offset;

    public ChdBitReader(ReadOnlySpan<byte> data)
    {
        _data = data;
    }

    /// <summary>Bytes fetched into the window so far, before <see cref="Flush" /> gives partial ones back.</summary>
    public int BytesFetched => _offset;

    /// <summary>True once more bytes have been consumed than the source held.</summary>
    public bool Overflow => _offset - _bits / 8 > _data.Length;

    public uint Peek(int numBits)
    {
        if (numBits == 0)
        {
            return 0;
        }

        while (_bits < numBits)
        {
            var next = _offset < _data.Length ? _data[_offset] : (byte)0;
            _offset++;
            _buffer |= (ulong)next << (56 - _bits);
            _bits += 8;
        }

        return (uint)(_buffer >> (64 - numBits));
    }

    public void Remove(int numBits)
    {
        _buffer = numBits >= 64 ? 0 : _buffer << numBits;
        _bits -= numBits;
    }

    public uint Read(int numBits)
    {
        var value = Peek(numBits);
        Remove(numBits);
        return value;
    }

    /// <summary>Drops the partial byte in the window and returns how many whole bytes were consumed.</summary>
    public int Flush()
    {
        while (_bits >= 8)
        {
            _offset--;
            _bits -= 8;
        }

        _bits = 0;
        _buffer = 0;
        return _offset;
    }
}
