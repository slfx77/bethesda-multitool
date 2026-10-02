// Ported from AweMultitool (slfx77)
// (src/AweMultitool/Core/Formats/Granny/Gr2Oodle0Decoder.cs), adapted to this repository's house style.
// The upstream notice below is reproduced verbatim from that file.

using System.Buffers.Binary;

namespace BethesdaMultitool.Core.Formats.Granny;

/// <summary>Expands the original, 2002-era Oodle0 streams embedded in Granny 2 files.</summary>
/// <remarks>
///     This is a managed port of the clean-room MIT implementations in
///     <c>Rasetsuu/blendergranny</c> and <c>Stitchuuuu/granny-ro-js</c>. It is not modern Oodle and does
///     not use or redistribute RAD code. The source implementations were retrieved at commits
///     <c>aec91bbd8e244d82277d1f5435dc20feff3086f9</c> and
///     <c>955189e060bf38270cbc3ea8cba610e98c7f62dc</c>, respectively.
///     MIT License
///     Copyright (c) 2026 ciupix and contributors
///     Copyright (c) 2026 Stitchuuuu &lt;stitchuuuu@icloud.com&gt;
///     Permission is hereby granted, free of charge, to any person obtaining a copy of this software
///     and associated documentation files (the "Software"), to deal in the Software without
///     restriction, including without limitation the rights to use, copy, modify, merge, publish,
///     distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the
///     Software is furnished to do so, subject to the following conditions:
///     The above copyright notice and this permission notice shall be included in all copies or
///     substantial portions of the Software.
///     THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING
///     BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
///     NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM,
///     DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
///     OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
///     <para>
///         ⚑ This is the codec the Van Buren prototype actually uses: 2,735 of its 3,282 Granny
///         sections (sections 0-4 of every one of the 547 files) are Oodle0; the sixth section of
///         each is stored raw, and Oodle1 never occurs (measured 2026-09-08).
///     </para>
/// </remarks>
internal static class Gr2Oodle0Decoder
{
    private const int HeaderSize = 36;
    private const int BlockCount = 3;
    private const int OffsetSplitShift = 2;
    private const int LowOffsetMask = (1 << OffsetSplitShift) - 1;
    private const int MaximumLengthSymbol = 64;
    private const uint Mask31 = 0x7FFF_FFFF;
    private const uint OffsetByteMask = 0x1FF;
    private const int MaximumExpandedSize = 256 * 1024 * 1024;
    private const int MaximumExpansionRatio = 1024;
    private const int MaximumAlphabetSize = 1 << 21;

    private static ReadOnlySpan<int> LongLengths => [128, 192, 256, 512];

    /// <summary>Expands one section whose compression value is <see cref="Gr2Compression.Oodle0" />.</summary>
    public static byte[] Decode(Gr2Section section)
    {
        ArgumentNullException.ThrowIfNull(section);
        if (section.Compression != Gr2Compression.Oodle0)
        {
            throw new ArgumentException($"Section {section.Index} uses {section.Compression}, not Oodle0.",
                nameof(section));
        }

        return Decompress(section.Data, section.ExpandedDataSize, section.First16Bit, section.First8Bit, section.Index);
    }

    /// <summary>The codec on bare bytes: the three block headers plus the arithmetic stream.</summary>
    internal static byte[] Decompress(ReadOnlyMemory<byte> compressed, int expandedSize, int first16Bit, int first8Bit,
        int sectionIndex = 0)
    {
        if (expandedSize == 0)
        {
            return [];
        }

        if (compressed.Length < HeaderSize)
        {
            throw Invalid(sectionIndex, "stored data is too short for the three 12-byte block headers");
        }

        if (expandedSize < 0 || expandedSize > MaximumExpandedSize ||
            expandedSize > (long)compressed.Length * MaximumExpansionRatio)
        {
            throw Invalid(sectionIndex, $"expanded size {expandedSize} exceeds the decoder safety cap");
        }

        var headers = ReadHeaders(sectionIndex, compressed.Span);
        var stops = BlockStops(expandedSize, first16Bit, first8Bit);
        var bits = new ArithmeticBits(compressed, HeaderSize);
        var output = new byte[expandedSize];
        var cursor = 0;

        for (var blockIndex = 0; blockIndex < BlockCount; blockIndex++)
        {
            var stop = stops[blockIndex + 1];
            if (stop <= cursor)
            {
                continue;
            }

            var state = new LzState(sectionIndex, headers[blockIndex]);
            DecodeBlock(sectionIndex, state, bits, output, ref cursor, stop);
        }

        if (cursor != expandedSize)
        {
            throw Invalid(sectionIndex, $"expanded {cursor} bytes, expected {expandedSize}");
        }

        return output;
    }

    private static LzHeader[] ReadHeaders(int sectionIndex, ReadOnlySpan<byte> compressed)
    {
        var headers = new LzHeader[BlockCount];
        for (var index = 0; index < BlockCount; index++)
        {
            var offset = index * 12;
            headers[index] = new LzHeader(
                BinaryPrimitives.ReadUInt32LittleEndian(compressed[offset..]),
                BinaryPrimitives.ReadUInt32LittleEndian(compressed[(offset + 4)..]),
                BinaryPrimitives.ReadUInt32LittleEndian(compressed[(offset + 8)..]));
            if (headers[index].UniqueByteValues > MaximumAlphabetSize ||
                headers[index].UniqueOffsets > MaximumAlphabetSize)
            {
                throw Invalid(sectionIndex,
                    $"block {index} declares an arithmetic alphabet larger than {MaximumAlphabetSize}");
            }
        }

        return headers;
    }

    private static int[] BlockStops(int expandedSize, int first16Bit, int first8Bit)
    {
        var first16 = Math.Clamp(first16Bit, 0, expandedSize);
        var first8 = Math.Clamp(first8Bit, first16, expandedSize);
        return [0, first16, first8, expandedSize];
    }

    private static void DecodeBlock(int sectionIndex, LzState state, ArithmeticBits bits, byte[] output, ref int cursor,
        int stop)
    {
        while (cursor < stop)
        {
            var lengthSymbol = ReadModelSymbol(sectionIndex, state.Lengths[state.LastLength], bits,
                MaximumLengthSymbol + 1);
            state.LastLength = lengthSymbol;

            if (lengthSymbol == 0)
            {
                var literal = ReadModelSymbol(sectionIndex, state.Bytes, bits, state.MaximumByteValue);
                if ((uint)literal > byte.MaxValue)
                {
                    throw Invalid(sectionIndex, $"decoded invalid literal {literal}");
                }

                output[cursor++] = (byte)literal;
                state.BytesExpanded++;
                continue;
            }

            if ((uint)lengthSymbol > MaximumLengthSymbol)
            {
                throw Invalid(sectionIndex, $"decoded invalid length symbol {lengthSymbol}");
            }

            var length = lengthSymbol >= MaximumLengthSymbol - 3
                ? LongLengths[lengthSymbol - (MaximumLengthSymbol - 3)]
                : lengthSymbol + 1;
            if (length > stop - cursor)
            {
                throw Invalid(sectionIndex, $"copy of {length} bytes crosses block stop {stop}");
            }

            var low = ReadModelSymbol(sectionIndex, state.OffsetLow, bits, state.MaximumOffsetLow);
            var highScale = (Math.Min(state.MaximumOffset, state.BytesExpanded) >> OffsetSplitShift) + 1;
            var high = ReadModelSymbol(sectionIndex, state.OffsetHigh, bits, highScale);
            var distance = low + 1 + (high << OffsetSplitShift);
            if (distance <= 0 || distance > cursor)
            {
                throw Invalid(sectionIndex, $"decoded invalid copy distance {distance}");
            }

            for (var count = 0; count < length; count++)
            {
                output[cursor] = output[cursor - distance];
                cursor++;
            }

            state.BytesExpanded += length;
        }
    }

    private static int ReadModelSymbol(int sectionIndex, ArithmeticModel model, ArithmeticBits bits, int escapeScale)
    {
        var symbol = model.DecodeSymbol(sectionIndex, bits);
        if (!symbol.IsEscape)
        {
            return symbol.Value;
        }

        if (escapeScale <= 0)
        {
            throw Invalid(sectionIndex, "encountered an escape with an empty alphabet");
        }

        var escaped = bits.GetValue(escapeScale);
        model.SetEscaped(symbol.Value, escaped);
        return escaped;
    }

    private static InvalidDataException Invalid(int sectionIndex, string reason)
    {
        return new InvalidDataException($"Granny 2 section {sectionIndex}: invalid Oodle0 stream: {reason}.");
    }

    private readonly record struct LzHeader(uint MaximumOffsetAndByte, uint UniqueOffsetAndByte, uint UniqueLengths)
    {
        public int MaximumByteValue => (int)(MaximumOffsetAndByte & OffsetByteMask);

        public int MaximumOffset => (int)(MaximumOffsetAndByte >> 9);

        public int UniqueByteValues => (int)(UniqueOffsetAndByte & OffsetByteMask);

        public int UniqueOffsets => (int)(UniqueOffsetAndByte >> 9);

        public int UniqueLengthValues(int lengthSymbol)
        {
            var group = Math.Min(lengthSymbol / (MaximumLengthSymbol / 4), 3);
            return (int)((UniqueLengths >> ((3 - group) * 8)) & byte.MaxValue);
        }
    }

    private sealed class LzState
    {
        public LzState(int sectionIndex, LzHeader header)
        {
            MaximumByteValue = header.MaximumByteValue;
            MaximumOffset = header.MaximumOffset;
            MaximumOffsetLow = Math.Min(MaximumOffset, LowOffsetMask + 1);
            Bytes = new ArithmeticModel(sectionIndex, header.UniqueByteValues);
            Lengths = new ArithmeticModel[MaximumLengthSymbol + 1];
            for (var index = 0; index < Lengths.Length; index++)
            {
                Lengths[index] = new ArithmeticModel(sectionIndex, header.UniqueLengthValues(index));
            }

            OffsetLow = new ArithmeticModel(sectionIndex, MaximumOffsetLow);
            OffsetHigh = new ArithmeticModel(sectionIndex, header.UniqueOffsets);
        }

        public int MaximumByteValue { get; }

        public int MaximumOffset { get; }

        public int MaximumOffsetLow { get; }

        public ArithmeticModel Bytes { get; }

        public ArithmeticModel[] Lengths { get; }

        public ArithmeticModel OffsetLow { get; }

        public ArithmeticModel OffsetHigh { get; }

        public int BytesExpanded { get; set; }

        public int LastLength { get; set; }
    }

    private sealed class VariableBits(ReadOnlyMemory<byte> data, int offset)
    {
        private readonly ReadOnlyMemory<byte> _data = data;
        private int _bitLength;
        private uint _bits;
        private int _cursor = offset;

        public uint Get(int count)
        {
            if (count == 0)
            {
                return 0;
            }

            var mask = (1u << count) - 1;
            if (_bitLength >= count)
            {
                var value = _bits & mask;
                _bits >>= count;
                _bitLength -= count;
                return value;
            }

            var word = ReadPaddedUInt32(_data.Span, _cursor);
            _cursor += 4;
            var needed = count - _bitLength;
            var lowMask = (1u << needed) - 1;
            var result = (_bits | ((word & lowMask) << _bitLength)) & mask;
            _bits = word >> needed;
            _bitLength += 32 - count;
            return result;
        }

        public uint GetOne()
        {
            if (_bitLength != 0)
            {
                var value = _bits & 1;
                _bits >>= 1;
                _bitLength--;
                return value;
            }

            var word = ReadPaddedUInt32(_data.Span, _cursor);
            _cursor += 4;
            _bits = word >> 1;
            _bitLength = 31;
            return word & 1;
        }

        private static uint ReadPaddedUInt32(ReadOnlySpan<byte> bytes, int offset)
        {
            if ((uint)offset >= (uint)bytes.Length)
            {
                return 0;
            }

            if (offset <= bytes.Length - sizeof(uint))
            {
                return BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
            }

            uint value = 0;
            for (var index = 0; index < sizeof(uint) && offset + index < bytes.Length; index++)
            {
                value |= (uint)bytes[offset + index] << (index * 8);
            }

            return value;
        }
    }

    private sealed class ArithmeticBits
    {
        private readonly VariableBits _variableBits;
        private uint _code;
        private uint _high = Mask31;
        private uint _low;

        public ArithmeticBits(ReadOnlyMemory<byte> data, int offset)
        {
            _variableBits = new VariableBits(data, offset);
            _code = ReverseBits(_variableBits.Get(31), 31);
        }

        public int GetCount(int scale)
        {
            if (scale <= 0)
            {
                return 0;
            }

            var numerator = ((ulong)(_code - _low) + 1) * (uint)scale - 1;
            var denominator = (ulong)(_high - _low) + 1;
            return (int)(numerator / denominator);
        }

        public int GetValue(int scale)
        {
            var value = Math.Min(GetCount(scale), scale - 1);
            Remove(value, 1, scale);
            return value;
        }

        public void Remove(int start, int count, int scale)
        {
            if (scale <= 0)
            {
                return;
            }

            var width = (ulong)(_high - _low) + 1;
            _high = unchecked((uint)(_low + width * (uint)(start + count) / (uint)scale - 1));
            _low = unchecked((uint)(_low + width * (uint)start / (uint)scale));

            if (((_high ^ _low) & 0x4000_0000) == 0)
            {
                while (((_high ^ _low) & 0x7F80_0000) == 0)
                {
                    _low <<= 8;
                    _high = (_high << 8) | 0xFF;
                    var next = _variableBits.Get(8);
                    _code = (_code << 8) | (ReverseBits(next & 0xF, 4) << 4) | ReverseBits(next >> 4, 4);
                }

                if (((_high ^ _low) & 0x7800_0000) == 0)
                {
                    _low <<= 4;
                    _high = (_high << 4) | 0xF;
                    _code = (_code << 4) | ReverseBits(_variableBits.Get(4), 4);
                }

                while (((_high ^ _low) & 0x4000_0000) == 0)
                {
                    _low <<= 1;
                    _high = (_high << 1) | 1;
                    _code = (_code << 1) | _variableBits.GetOne();
                }
            }

            while ((_low & 0x2000_0000) != 0 && (_high & 0x2000_0000) == 0)
            {
                _code ^= 0x2000_0000;
                _low = (_low & 0x1FFF_FFFF) << 1;
                _high = (_high << 1) | 0x4000_0001;
                _code = (_code << 1) | _variableBits.GetOne();
            }

            _high &= Mask31;
            _low &= Mask31;
            _code &= Mask31;
        }

        private static uint ReverseBits(uint value, int count)
        {
            uint result = 0;
            for (var index = 0; index < count; index++)
            {
                result = (result << 1) | ((value >> index) & 1);
            }

            return result;
        }
    }

    private sealed class ArithmeticModel
    {
        private const int RescaleThreshold = 16_384;
        private readonly ushort[] _counts;
        private readonly ushort[] _totals = new ushort[16];
        private readonly int _uniqueValues;
        private readonly ushort[] _values;
        private int _binShift;
        private int _lastBinStart;
        private int _number;

        public ArithmeticModel(int sectionIndex, int uniqueValues)
        {
            if ((uint)uniqueValues > MaximumAlphabetSize)
            {
                throw Invalid(sectionIndex, $"arithmetic alphabet {uniqueValues} exceeds the safety cap");
            }

            _uniqueValues = uniqueValues;
            var arrayLength = (uniqueValues + 5) & ~3;
            _counts = new ushort[arrayLength];
            _values = new ushort[arrayLength];
            (_, _binShift, _lastBinStart) = BestShift(uniqueValues + 1);
            QuickIncrement(0, 3);
        }

        public DecodedSymbol DecodeSymbol(int sectionIndex, ArithmeticBits bits)
        {
            if (_totals[15] >= RescaleThreshold)
            {
                Rescale();
            }

            var scale = _totals[15];
            var count = bits.GetCount(scale);
            var (position, start) = FindPosition(sectionIndex, count);
            var oldCount = _counts[position];
            IncrementTotals(position, 1);
            bits.Remove(start, oldCount, _totals[15] - 1);
            _counts[position] = unchecked((ushort)(_counts[position] + 1));

            if (position != 0)
            {
                return new DecodedSymbol(false, _values[position]);
            }

            _number++;
            if (_number >= _counts.Length)
            {
                throw Invalid(sectionIndex, "escape exceeded its arithmetic model capacity");
            }

            QuickIncrement(_number, 2);
            if (_number == _uniqueValues)
            {
                Decrement(0, _counts[0]);
            }

            return new DecodedSymbol(true, _number);
        }

        public void SetEscaped(int position, int value)
        {
            _values[position] = unchecked((ushort)value);
        }

        private (int Position, int Start) FindPosition(int sectionIndex, int count)
        {
            var lowBin = 0;
            var highBin = _totals.Length;
            while (lowBin < highBin)
            {
                var middle = (lowBin + highBin) >> 1;
                if (count < _totals[middle])
                {
                    highBin = middle;
                }
                else
                {
                    lowBin = middle + 1;
                }
            }

            var bin = lowBin;
            var position = bin < 15 ? bin << _binShift : _lastBinStart;
            var end = bin < 15 ? Math.Min(position + (1 << _binShift), _counts.Length) : _counts.Length;
            var start = bin > 0 ? _totals[bin - 1] : 0;
            while (position < end)
            {
                var entry = _counts[position];
                if (count < start + entry)
                {
                    return (position, start);
                }

                start += entry;
                position++;
            }

            throw Invalid(sectionIndex, $"arithmetic count {count} lies outside total {start}");
        }

        private void QuickIncrement(int value, int amount)
        {
            IncrementTotals(value, amount);
            _counts[value] = unchecked((ushort)(_counts[value] + amount));
        }

        private void IncrementTotals(int value, int amount)
        {
            if (value >= _lastBinStart)
            {
                _totals[15] = unchecked((ushort)(_totals[15] + amount));
                return;
            }

            for (var index = value >> _binShift; index < _totals.Length; index++)
            {
                _totals[index] = unchecked((ushort)(_totals[index] + amount));
            }
        }

        private void Decrement(int value, int amount)
        {
            _counts[value] = unchecked((ushort)(_counts[value] - amount));
            if (value >= _lastBinStart)
            {
                _totals[15] = unchecked((ushort)(_totals[15] - amount));
                return;
            }

            for (var index = value >> _binShift; index < _totals.Length; index++)
            {
                _totals[index] = unchecked((ushort)(_totals[index] - amount));
            }
        }

        private void Rescale()
        {
            (_, _binShift, _lastBinStart) = BestShift(_number + 1);
            Span<int> bins = stackalloc int[16];
            _counts[0] >>= 1;
            bins[0 < _lastBinStart ? 0 : 15] += _counts[0];

            var maximumCount = 0;
            var maximumPosition = 0;
            var position = 1;
            var done = false;
            while (position <= _number && !done)
            {
                while (_counts[position] <= 1)
                {
                    if (position < _number)
                    {
                        _counts[position] = _counts[_number];
                        _values[position] = _values[_number];
                        _counts[_number] = 0;
                        _number--;
                    }
                    else
                    {
                        _counts[position] = 0;
                        _number--;
                        done = true;
                        break;
                    }
                }

                if (done)
                {
                    break;
                }

                _counts[position] >>= 1;
                if (_counts[position] > maximumCount)
                {
                    maximumCount = _counts[position];
                    maximumPosition = position;
                }

                bins[position < _lastBinStart ? position >> _binShift : 15] += _counts[position];
                position++;
            }

            if (maximumCount != 0)
            {
                var swapPosition = _number < _lastBinStart ? (_number >> _binShift) << _binShift : _lastBinStart;
                if (swapPosition == 0)
                {
                    swapPosition = 1;
                }

                if (maximumPosition != swapPosition)
                {
                    var oldCount = _counts[swapPosition];
                    _counts[swapPosition] = _counts[maximumPosition];
                    bins[swapPosition < _lastBinStart ? swapPosition >> _binShift : 15] +=
                        -oldCount + _counts[swapPosition];
                    bins[maximumPosition < _lastBinStart ? maximumPosition >> _binShift : 15] +=
                        oldCount - _counts[swapPosition];
                    _counts[maximumPosition] = oldCount;
                    (_values[swapPosition], _values[maximumPosition]) =
                        (_values[maximumPosition], _values[swapPosition]);
                }
            }

            if (_number != _uniqueValues && _counts[0] == 0)
            {
                _counts[0] += 2;
                bins[0 < _lastBinStart ? 0 : 15] += 2;
            }

            var running = 0;
            for (var index = 0; index < bins.Length; index++)
            {
                running += bins[index];
                _totals[index] = unchecked((ushort)running);
            }
        }

        private static (int BinSize, int BinShift, int LastBinStart) BestShift(int value)
        {
            if (value < 6)
            {
                return (0, 15, 0);
            }

            var bestMaximum = uint.MaxValue;
            var bestBin = 0;
            for (var index = 0; index < 16; index++)
            {
                var size = 1 << index;
                var binCount = Math.Min((value + size - 1) / size, 16);
                var last = value - size * (binCount - 1);
                if (last < size)
                {
                    last = size;
                }

                if (last < bestMaximum)
                {
                    bestBin = index;
                    bestMaximum = (uint)last;
                }

                if (size > value)
                {
                    break;
                }
            }

            var binSize = 1 << bestBin;
            return (binSize, bestBin, 15 * binSize);
        }
    }

    private readonly record struct DecodedSymbol(bool IsEscape, int Value);
}
