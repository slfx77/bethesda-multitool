namespace BethesdaMultitool.Core.Formats.DiscImage.Chd;

/// <summary>
///     A raw LZMA decoder (no header, no end marker required) written from the public-domain LZMA
///     specification, for the <c>lzma</c> and <c>cdlz</c> CHD codecs. chdman encodes with the
///     encoder's level-9 defaults — <c>lc</c> 3, <c>lp</c> 0, <c>pb</c> 2 — and never lets a match
///     reach back past the hunk, so the output buffer doubles as the dictionary and the size is the
///     hunk's. The five range-coder priming bytes open the stream (the first must be zero).
/// </summary>
internal sealed class LzmaRawDecoder
{
    private const int NumStates = 12;
    private const int NumPosBitsMax = 4;
    private const int NumLenToPosStates = 4;
    private const int NumAlignBits = 4;
    private const int EndPosModelIndex = 14;
    private const int NumFullDistances = 1 << (EndPosModelIndex >> 1);
    private const int MatchMinLen = 2;
    private const ushort ProbInit = 1024;

    private readonly int _lc;
    private readonly int _lp;
    private readonly int _pb;
    private readonly ushort[] _literal;
    private readonly ushort[] _isMatch = new ushort[NumStates << NumPosBitsMax];
    private readonly ushort[] _isRep = new ushort[NumStates];
    private readonly ushort[] _isRepG0 = new ushort[NumStates];
    private readonly ushort[] _isRepG1 = new ushort[NumStates];
    private readonly ushort[] _isRepG2 = new ushort[NumStates];
    private readonly ushort[] _isRep0Long = new ushort[NumStates << NumPosBitsMax];
    private readonly ushort[] _posSlot = new ushort[NumLenToPosStates << 6];
    private readonly ushort[] _posDecoders = new ushort[1 + NumFullDistances - EndPosModelIndex];
    private readonly ushort[] _align = new ushort[1 << NumAlignBits];
    private readonly LengthDecoder _lenDecoder = new();
    private readonly LengthDecoder _repLenDecoder = new();

    public LzmaRawDecoder(int lc = 3, int lp = 0, int pb = 2)
    {
        if (lc is < 0 or > 8 || lp is < 0 or > 4 || pb is < 0 or > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(lc), "LZMA properties out of range.");
        }

        _lc = lc;
        _lp = lp;
        _pb = pb;
        _literal = new ushort[0x300 << (lc + lp)];
    }

    /// <summary>Decodes <paramref name="input" /> into exactly <paramref name="output" />.Length bytes.</summary>
    public void Decode(ReadOnlySpan<byte> input, Span<byte> output)
    {
        Reset();
        var rc = new RangeDecoder(input);
        var unpackSize = output.Length;
        var outPos = 0;
        var state = 0;
        uint rep0 = 0, rep1 = 0, rep2 = 0, rep3 = 0;
        var posMask = (1u << _pb) - 1;
        var lpMask = (1u << _lp) - 1;

        while (outPos < unpackSize)
        {
            var posState = (int)((uint)outPos & posMask);
            if (rc.DecodeBit(ref _isMatch[(state << NumPosBitsMax) + posState]) == 0)
            {
                var prevByte = outPos > 0 ? output[outPos - 1] : (byte)0;
                var literalState = (int)((((uint)outPos & lpMask) << _lc) + (uint)(prevByte >> (8 - _lc)));
                var probBase = 0x300 * literalState;
                uint symbol = 1;
                if (state >= 7)
                {
                    if (rep0 >= (uint)outPos)
                    {
                        throw new InvalidDataException("LZMA: a matched literal refers before the start of the output.");
                    }

                    uint matchByte = output[outPos - (int)rep0 - 1];
                    do
                    {
                        var matchBit = (matchByte >> 7) & 1;
                        matchByte <<= 1;
                        var bit = rc.DecodeBit(ref _literal[probBase + (int)((1 + matchBit) << 8) + (int)symbol]);
                        symbol = (symbol << 1) | bit;
                        if (matchBit != bit)
                        {
                            break;
                        }
                    } while (symbol < 0x100);
                }

                while (symbol < 0x100)
                {
                    symbol = (symbol << 1) | rc.DecodeBit(ref _literal[probBase + (int)symbol]);
                }

                output[outPos++] = (byte)symbol;
                state = state switch { < 4 => 0, < 10 => state - 3, _ => state - 6 };
                continue;
            }

            int len;
            if (rc.DecodeBit(ref _isRep[state]) != 0)
            {
                if (outPos == 0)
                {
                    throw new InvalidDataException("LZMA: a repeat match before any output.");
                }

                if (rc.DecodeBit(ref _isRepG0[state]) == 0)
                {
                    if (rc.DecodeBit(ref _isRep0Long[(state << NumPosBitsMax) + posState]) == 0)
                    {
                        // Short rep: one byte at distance rep0.
                        state = state < 7 ? 9 : 11;
                        output[outPos] = output[outPos - (int)rep0 - 1];
                        outPos++;
                        continue;
                    }
                }
                else
                {
                    uint distance;
                    if (rc.DecodeBit(ref _isRepG1[state]) == 0)
                    {
                        distance = rep1;
                    }
                    else
                    {
                        if (rc.DecodeBit(ref _isRepG2[state]) == 0)
                        {
                            distance = rep2;
                        }
                        else
                        {
                            distance = rep3;
                            rep3 = rep2;
                        }

                        rep2 = rep1;
                    }

                    rep1 = rep0;
                    rep0 = distance;
                }

                len = _repLenDecoder.Decode(ref rc, posState);
                state = state < 7 ? 8 : 11;
            }
            else
            {
                rep3 = rep2;
                rep2 = rep1;
                rep1 = rep0;
                len = _lenDecoder.Decode(ref rc, posState);
                state = state < 7 ? 7 : 10;
                rep0 = DecodeDistance(ref rc, len);
                if (rep0 == 0xFFFFFFFF)
                {
                    // End marker: only acceptable once the output is complete.
                    if (outPos != unpackSize)
                    {
                        throw new InvalidDataException("LZMA: end marker before the hunk was complete.");
                    }

                    break;
                }

                if (rep0 >= (uint)outPos)
                {
                    throw new InvalidDataException("LZMA: a match distance reaches before the start of the output.");
                }
            }

            len += MatchMinLen;
            var source = outPos - (int)rep0 - 1;
            if (len > unpackSize - outPos)
            {
                throw new InvalidDataException("LZMA: a match runs past the end of the hunk.");
            }

            for (var i = 0; i < len; i++)
            {
                output[outPos++] = output[source++];
            }
        }
    }

    private uint DecodeDistance(ref RangeDecoder rc, int len)
    {
        var lenState = Math.Min(len, NumLenToPosStates - 1);
        var posSlot = (int)BitTreeDecode(ref rc, _posSlot, lenState << 6, 6);
        if (posSlot < 4)
        {
            return (uint)posSlot;
        }

        var numDirectBits = (posSlot >> 1) - 1;
        var distance = (2u | (uint)(posSlot & 1)) << numDirectBits;
        if (posSlot < EndPosModelIndex)
        {
            distance += BitTreeReverseDecode(ref rc, _posDecoders, (int)distance - posSlot, numDirectBits);
        }
        else
        {
            distance += rc.DecodeDirectBits(numDirectBits - NumAlignBits) << NumAlignBits;
            distance += BitTreeReverseDecode(ref rc, _align, 0, NumAlignBits);
        }

        return distance;
    }

    private static uint BitTreeDecode(ref RangeDecoder rc, ushort[] probs, int baseIndex, int numBits)
    {
        uint m = 1;
        for (var i = 0; i < numBits; i++)
        {
            m = (m << 1) + rc.DecodeBit(ref probs[baseIndex + (int)m]);
        }

        return m - (1u << numBits);
    }

    private static uint BitTreeReverseDecode(ref RangeDecoder rc, ushort[] probs, int baseIndex, int numBits)
    {
        uint m = 1;
        uint symbol = 0;
        for (var i = 0; i < numBits; i++)
        {
            var bit = rc.DecodeBit(ref probs[baseIndex + (int)m]);
            m = (m << 1) + bit;
            symbol |= bit << i;
        }

        return symbol;
    }

    private void Reset()
    {
        Array.Fill(_literal, ProbInit);
        Array.Fill(_isMatch, ProbInit);
        Array.Fill(_isRep, ProbInit);
        Array.Fill(_isRepG0, ProbInit);
        Array.Fill(_isRepG1, ProbInit);
        Array.Fill(_isRepG2, ProbInit);
        Array.Fill(_isRep0Long, ProbInit);
        Array.Fill(_posSlot, ProbInit);
        Array.Fill(_posDecoders, ProbInit);
        Array.Fill(_align, ProbInit);
        _lenDecoder.Reset();
        _repLenDecoder.Reset();
    }

    private sealed class LengthDecoder
    {
        private readonly ushort[] _choice = new ushort[2];
        private readonly ushort[] _low = new ushort[(1 << NumPosBitsMax) << 3];
        private readonly ushort[] _mid = new ushort[(1 << NumPosBitsMax) << 3];
        private readonly ushort[] _high = new ushort[256];

        public void Reset()
        {
            Array.Fill(_choice, ProbInit);
            Array.Fill(_low, ProbInit);
            Array.Fill(_mid, ProbInit);
            Array.Fill(_high, ProbInit);
        }

        public int Decode(ref RangeDecoder rc, int posState)
        {
            if (rc.DecodeBit(ref _choice[0]) == 0)
            {
                return (int)BitTreeDecode(ref rc, _low, posState << 3, 3);
            }

            if (rc.DecodeBit(ref _choice[1]) == 0)
            {
                return 8 + (int)BitTreeDecode(ref rc, _mid, posState << 3, 3);
            }

            return 16 + (int)BitTreeDecode(ref rc, _high, 0, 8);
        }
    }

    private ref struct RangeDecoder
    {
        private const uint TopValue = 1u << 24;
        private readonly ReadOnlySpan<byte> _data;
        private int _pos;
        private uint _range;
        private uint _code;

        public RangeDecoder(ReadOnlySpan<byte> data)
        {
            _data = data;
            if (data.Length < 5 || data[0] != 0)
            {
                throw new InvalidDataException("LZMA: the range coder is not primed by five bytes starting with zero.");
            }

            _range = 0xFFFFFFFF;
            _code = 0;
            for (var i = 1; i < 5; i++)
            {
                _code = (_code << 8) | data[i];
            }

            _pos = 5;
        }

        public uint DecodeBit(ref ushort prob)
        {
            var bound = (_range >> 11) * prob;
            uint bit;
            if (_code < bound)
            {
                prob = (ushort)(prob + ((2048 - prob) >> 5));
                _range = bound;
                bit = 0;
            }
            else
            {
                prob = (ushort)(prob - (prob >> 5));
                _code -= bound;
                _range -= bound;
                bit = 1;
            }

            Normalize();
            return bit;
        }

        public uint DecodeDirectBits(int numBits)
        {
            uint result = 0;
            for (var i = 0; i < numBits; i++)
            {
                _range >>= 1;
                _code -= _range;
                var t = 0u - (_code >> 31);
                _code += _range & t;
                if (_code == _range)
                {
                    throw new InvalidDataException("LZMA: corrupt direct bits.");
                }

                Normalize();
                result = (result << 1) + (t + 1);
            }

            return result;
        }

        private void Normalize()
        {
            if (_range < TopValue)
            {
                _range <<= 8;
                _code = (_code << 8) | (_pos < _data.Length ? _data[_pos] : (byte)0);
                _pos++;
            }
        }
    }
}
