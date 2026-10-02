namespace BethesdaMultitool.Core.Formats.DiscImage.Chd;

/// <summary>
///     The canonical Huffman decoder CHD v5 uses twice: a 16-symbol, 8-bit tree for the
///     compressed hunk map (whose lengths arrive run-length coded) and a 256-symbol, 16-bit tree for
///     the <c>huff</c> codec (whose lengths arrive coded by a small 24-symbol tree). Written from
///     the format's behaviour as libchdr (BSD-3) exhibits it — see THIRD_PARTY_LICENSES; nothing is
///     copied from MAME.
///     <para>
///         Canonical assignment walks the length histogram from the longest code down, halving the
///         running start each step, so codes of one length are consecutive and a shorter code is
///         never a prefix of a longer one; the Kraft check on every length but 1 is what rejects a
///         table that does not tile the code space. Decoding peeks <c>maxBits</c> and indexes a flat
///         table whose entries pack <c>symbol &lt;&lt; 5 | length</c>.
///     </para>
/// </summary>
internal sealed class ChdHuffmanDecoder
{
    private readonly int _maxBits;
    private readonly int _numCodes;
    private readonly byte[] _lengths;
    private readonly uint[] _codes;
    private readonly uint[] _lookup;

    public ChdHuffmanDecoder(int numCodes, int maxBits)
    {
        if (maxBits is < 1 or > 24)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBits));
        }

        _numCodes = numCodes;
        _maxBits = maxBits;
        _lengths = new byte[numCodes];
        _codes = new uint[numCodes];
        _lookup = new uint[1 << maxBits];
    }

    public uint DecodeOne(ref ChdBitReader reader)
    {
        var window = reader.Peek(_maxBits);
        var entry = _lookup[window];
        reader.Remove((int)(entry & 0x1F));
        return entry >> 5;
    }

    /// <summary>
    ///     Code lengths as a run-length stream of fixed-width values (5 bits when maxBits ≥ 16, 4
    ///     when ≥ 8, else 3): a value of 1 escapes — "1 1" is a single length of 1, "1 N R" is N
    ///     repeated R + 3 times — and anything else is one literal length.
    /// </summary>
    public void ImportTreeRle(ref ChdBitReader reader)
    {
        var numBits = _maxBits switch { >= 16 => 5, >= 8 => 4, _ => 3 };
        for (var code = 0; code < _numCodes;)
        {
            var length = (int)reader.Read(numBits);
            if (length != 1)
            {
                _lengths[code++] = (byte)length;
                continue;
            }

            length = (int)reader.Read(numBits);
            if (length == 1)
            {
                _lengths[code++] = 1;
                continue;
            }

            var repeat = (int)reader.Read(numBits) + 3;
            if (code + repeat > _numCodes)
            {
                throw new InvalidDataException("CHD Huffman table: a run of code lengths overruns the symbol count.");
            }

            while (repeat-- > 0)
            {
                _lengths[code++] = (byte)length;
            }
        }

        if (reader.Overflow)
        {
            throw new InvalidDataException("CHD Huffman table ran past the end of its stream.");
        }

        AssignCanonicalCodes();
        BuildLookupTable();
    }

    /// <summary>
    ///     Code lengths coded by a 24-symbol, 6-bit helper tree whose own lengths come first (3
    ///     bits for symbol 0, a 3-bit start index, then 3 bits each until a 7 ends them). Helper
    ///     symbol N &gt; 0 is a literal length N − 1; symbol 0 repeats the previous length 2 + a
    ///     3-bit count times, a count of 7 adding a further <c>log2(numCodes − 9)</c>-bit field.
    /// </summary>
    public void ImportTreeHuffman(ref ChdBitReader reader)
    {
        var small = new ChdHuffmanDecoder(24, 6);
        small._lengths[0] = (byte)reader.Read(3);
        var start = (int)reader.Read(3) + 1;
        var count = 0;
        for (var index = 1; index < 24; index++)
        {
            if (index < start || count == 7)
            {
                small._lengths[index] = 0;
            }
            else
            {
                count = (int)reader.Read(3);
                small._lengths[index] = (byte)(count == 7 ? 0 : count);
            }
        }

        small.AssignCanonicalCodes();
        small.BuildLookupTable();

        var rleFullBits = 0;
        for (var temp = _numCodes - 9; temp != 0; temp >>= 1)
        {
            rleFullBits++;
        }

        var last = 0;
        for (var code = 0; code < _numCodes;)
        {
            var value = (int)small.DecodeOne(ref reader);
            if (value != 0)
            {
                last = value - 1;
                _lengths[code++] = (byte)last;
                continue;
            }

            var repeat = (int)reader.Read(3) + 2;
            if (repeat == 7 + 2)
            {
                repeat += (int)reader.Read(rleFullBits);
            }

            for (; repeat != 0 && code < _numCodes; repeat--)
            {
                _lengths[code++] = (byte)last;
            }
        }

        if (reader.Overflow)
        {
            throw new InvalidDataException("CHD Huffman table ran past the end of its stream.");
        }

        AssignCanonicalCodes();
        BuildLookupTable();
    }

    private void AssignCanonicalCodes()
    {
        Span<uint> histogram = stackalloc uint[33];
        for (var code = 0; code < _numCodes; code++)
        {
            var length = _lengths[code];
            if (length > _maxBits)
            {
                throw new InvalidDataException($"CHD Huffman table: code length {length} exceeds the {_maxBits}-bit maximum.");
            }

            if (length != 0)
            {
                histogram[length]++;
            }
        }

        uint currentStart = 0;
        for (var length = 32; length > 0; length--)
        {
            var nextStart = (currentStart + histogram[length]) >> 1;
            if (length != 1 && nextStart * 2 != currentStart + histogram[length])
            {
                throw new InvalidDataException("CHD Huffman table: the code lengths do not tile the code space.");
            }

            histogram[length] = currentStart;
            currentStart = nextStart;
        }

        for (var code = 0; code < _numCodes; code++)
        {
            var length = _lengths[code];
            if (length > 0)
            {
                _codes[code] = histogram[length]++;
            }
        }
    }

    private void BuildLookupTable()
    {
        Array.Clear(_lookup);
        for (var code = 0; code < _numCodes; code++)
        {
            var length = _lengths[code];
            if (length == 0)
            {
                continue;
            }

            var shift = _maxBits - length;
            var first = _codes[code] << shift;
            var end = (_codes[code] + 1) << shift;
            if (end > (uint)_lookup.Length)
            {
                throw new InvalidDataException("CHD Huffman table: a canonical code falls outside the lookup table.");
            }

            var entry = ((uint)code << 5) | length;
            for (var slot = first; slot < end; slot++)
            {
                _lookup[slot] = entry;
            }
        }
    }
}
