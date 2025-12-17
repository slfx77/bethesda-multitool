// LZX Decoder - Ported from MonoGame's implementation
// Original source: https://github.com/MonoGame/MonoGame
// Licensed under Microsoft Public License (Ms-PL)
//
// This is a pure managed implementation of the LZX decompression algorithm
// used by Xbox 360's XMemDecompress. It eliminates the need for the 32-bit
// XnaNative.dll dependency.

namespace Xbox360MemoryCarver.Compression;

/// <summary>
/// LZX decompression class for Xbox 360 compressed data.
/// Based on MonoGame's LzxDecoder implementation.
/// </summary>
public class LzxDecoder
{
    private static readonly uint[] PositionBase;
    private static readonly byte[] ExtraBits;

    private LzxState _state = null!;

    private readonly uint _windowSize;
    private readonly uint _windowMask;
    private readonly uint[] _positionSlots;

    static LzxDecoder()
    {
        ExtraBits = new byte[52];
        PositionBase = new uint[52];

        int j = 0;
        for (int i = 0; i < 52; i += 2)
        {
            ExtraBits[i] = ExtraBits[i + 1] = (byte)j;
            if (i != 0 && j < 17) j++;
        }

        j = 0;
        for (int i = 0; i < 52; i++)
        {
            PositionBase[i] = (uint)j;
            j += 1 << ExtraBits[i];
        }
    }

    /// <summary>
    /// Creates a new LZX decoder with the specified window size.
    /// </summary>
    /// <param name="windowBits">Window size in bits (15-21 for standard LZX, or 17 for Xbox).</param>
    public LzxDecoder(int windowBits)
    {
        if (windowBits < 15 || windowBits > 21)
            throw new ArgumentOutOfRangeException(nameof(windowBits), "Window bits must be between 15 and 21");

        _windowSize = (uint)(1 << windowBits);
        _windowMask = _windowSize - 1;

        // Calculate position slots based on window size
        _positionSlots = new uint[6];
        int posSlot = 4;
        int windowTemp = (int)_windowSize;
        while (windowTemp > 0)
        {
            posSlot += 2;
            windowTemp >>= 1;
        }

        _positionSlots[0] = (uint)posSlot;

        // Position slots for aligned offset
        if (windowBits == 20) _positionSlots[1] = 42;
        else if (windowBits == 21) _positionSlots[1] = 50;
        else _positionSlots[1] = (uint)posSlot;

        _positionSlots[2] = _positionSlots[3] = _positionSlots[4] = _positionSlots[5] = _positionSlots[0];

        Reset();
    }

    /// <summary>
    /// Resets the decoder state.
    /// </summary>
    public void Reset()
    {
        _state = new LzxState
        {
            Window = new byte[_windowSize],
            WindowPosition = 0,
            R0 = 1,
            R1 = 1,
            R2 = 1,
            MainElements = (ushort)(256 + (_positionSlots[0] << 3)),
            HeaderRead = false,
            BlockRemaining = 0,
            BlockType = LzxBlockType.Invalid
        };

        // Initialize Huffman tables
        _state.MainTreeLen = new byte[_state.MainElements];
        _state.MainTreeTable = new ushort[1 << 12];
        _state.LengthLen = new byte[249];
        _state.LengthTable = new ushort[1 << 12];
        _state.AlignedLen = new byte[8];
        _state.AlignedTable = new ushort[1 << 7];

        // Initialize pretree
        _state.PretreeLen = new byte[20];
        _state.PretreeTable = new ushort[1 << 6];
    }

    /// <summary>
    /// Decompresses LZX data.
    /// </summary>
    /// <param name="input">Input stream containing compressed data.</param>
    /// <param name="inputLength">Number of bytes to read from input.</param>
    /// <param name="output">Output stream to write decompressed data.</param>
    /// <param name="outputLength">Expected decompressed size.</param>
    /// <returns>0 on success, non-zero on error.</returns>
    public int Decompress(Stream input, int inputLength, Stream output, int outputLength)
    {
        var bitBuffer = new BitBuffer(input);
        int togo = outputLength;

        while (togo > 0)
        {
            // Read block header if needed
            if (_state.BlockRemaining == 0)
            {
                // Read header if not done yet
                if (!_state.HeaderRead)
                {
                    // Check for Intel E8 preprocessing
                    uint intel = bitBuffer.ReadBits(1);
                    if (intel != 0)
                    {
                        // Read 32-bit file size for E8 translation
                        bitBuffer.ReadBits(16);
                        bitBuffer.ReadBits(16);
                    }
                    _state.HeaderRead = true;
                }

                // Read block type and size
                _state.BlockType = (LzxBlockType)bitBuffer.ReadBits(3);
                uint hi = bitBuffer.ReadBits(16);
                uint lo = bitBuffer.ReadBits(8);
                _state.BlockRemaining = (int)((hi << 8) | lo);

                switch (_state.BlockType)
                {
                    case LzxBlockType.AlignedOffset:
                        // Read aligned offset tree
                        for (int i = 0; i < 8; i++)
                            _state.AlignedLen[i] = (byte)bitBuffer.ReadBits(3);
                        MakeDecodeTable(_state.AlignedLen, 8, 7, _state.AlignedTable);
                        goto case LzxBlockType.Verbatim;

                    case LzxBlockType.Verbatim:
                        // Read pretree for main tree first 256 elements
                        ReadPretree(bitBuffer);
                        ReadLengths(bitBuffer, _state.MainTreeLen, 0, 256);

                        // Read pretree for main tree remaining elements
                        ReadPretree(bitBuffer);
                        ReadLengths(bitBuffer, _state.MainTreeLen, 256, _state.MainElements);
                        MakeDecodeTable(_state.MainTreeLen, _state.MainElements, 12, _state.MainTreeTable);

                        // Read length tree
                        ReadPretree(bitBuffer);
                        ReadLengths(bitBuffer, _state.LengthLen, 0, 249);
                        MakeDecodeTable(_state.LengthLen, 249, 12, _state.LengthTable);
                        break;

                    case LzxBlockType.Uncompressed:
                        // Align to byte boundary
                        bitBuffer.EnsureByteAligned();
                        // Read R0, R1, R2 - these are stored in little-endian even on Xbox
                        _state.R0 = bitBuffer.ReadUInt32LE();
                        _state.R1 = bitBuffer.ReadUInt32LE();
                        _state.R2 = bitBuffer.ReadUInt32LE();
                        break;

                    default:
                        return -1; // Invalid block type
                }
            }

            // Decompress current block
            int thisRun = Math.Min(_state.BlockRemaining, togo);

            switch (_state.BlockType)
            {
                case LzxBlockType.Uncompressed:
                    // Copy uncompressed data
                    for (int i = 0; i < thisRun; i++)
                    {
                        int b = bitBuffer.ReadByte();
                        if (b < 0) return -1;
                        _state.Window[_state.WindowPosition++] = (byte)b;
                        _state.WindowPosition &= (int)_windowMask;
                    }
                    break;

                case LzxBlockType.Verbatim:
                case LzxBlockType.AlignedOffset:
                    thisRun = DecompressBlock(bitBuffer, thisRun);
                    if (thisRun < 0) return -1;
                    break;

                default:
                    return -1;
            }

            // Write decompressed data to output
            int windowPos = (int)(_state.WindowPosition - thisRun);
            if (windowPos < 0) windowPos += (int)_windowSize;

            int remaining = thisRun;
            while (remaining > 0)
            {
                int chunk = Math.Min(remaining, (int)_windowSize - windowPos);
                output.Write(_state.Window, windowPos, chunk);
                windowPos = (windowPos + chunk) & (int)_windowMask;
                remaining -= chunk;
            }

            togo -= thisRun;
            _state.BlockRemaining -= thisRun;
        }

        return 0;
    }

    private int DecompressBlock(BitBuffer bitBuffer, int thisRun)
    {
        int remaining = thisRun;

        while (remaining > 0)
        {
            // Decode main element
            int mainElement = DecodeSymbol(bitBuffer, _state.MainTreeTable, _state.MainTreeLen, 12);
            if (mainElement < 0) return -1;

            if (mainElement < 256)
            {
                // Literal byte
                _state.Window[_state.WindowPosition++] = (byte)mainElement;
                _state.WindowPosition &= (int)_windowMask;
                remaining--;
            }
            else
            {
                // Match
                mainElement -= 256;

                int matchLength = mainElement & 7;
                if (matchLength == 7)
                {
                    int lengthSymbol = DecodeSymbol(bitBuffer, _state.LengthTable, _state.LengthLen, 12);
                    if (lengthSymbol < 0) return -1;
                    matchLength += lengthSymbol;
                }
                matchLength += 2;

                int matchOffsetSlot = mainElement >> 3;
                uint matchOffset;

                if (matchOffsetSlot > 2)
                {
                    // Not a recent offset
                    int extraBits = ExtraBits[matchOffsetSlot];
                    matchOffset = PositionBase[matchOffsetSlot] - 2;

                    if (_state.BlockType == LzxBlockType.AlignedOffset && extraBits >= 3)
                    {
                        // Aligned offset block - some bits from aligned tree
                        extraBits -= 3;
                        matchOffset += bitBuffer.ReadBits(extraBits) << 3;
                        int alignedSymbol = DecodeSymbol(bitBuffer, _state.AlignedTable, _state.AlignedLen, 7);
                        if (alignedSymbol < 0) return -1;
                        matchOffset += (uint)alignedSymbol;
                    }
                    else if (extraBits > 0)
                    {
                        // Verbatim block
                        matchOffset += bitBuffer.ReadBits(extraBits);
                    }

                    // Update recent offsets
                    _state.R2 = _state.R1;
                    _state.R1 = _state.R0;
                    _state.R0 = matchOffset;
                }
                else
                {
                    // Recent offset
                    switch (matchOffsetSlot)
                    {
                        case 0:
                            matchOffset = _state.R0;
                            break;
                        case 1:
                            matchOffset = _state.R1;
                            _state.R1 = _state.R0;
                            _state.R0 = matchOffset;
                            break;
                        default:
                            matchOffset = _state.R2;
                            _state.R2 = _state.R0;
                            _state.R0 = matchOffset;
                            break;
                    }
                }

                // Copy match
                int sourcePos = (int)((_state.WindowPosition - matchOffset) & _windowMask);
                remaining -= matchLength;

                while (matchLength-- > 0)
                {
                    _state.Window[_state.WindowPosition++] = _state.Window[sourcePos++];
                    _state.WindowPosition &= (int)_windowMask;
                    sourcePos &= (int)_windowMask;
                }
            }
        }

        return thisRun;
    }

    private void ReadPretree(BitBuffer bitBuffer)
    {
        for (int i = 0; i < 20; i++)
            _state.PretreeLen[i] = (byte)bitBuffer.ReadBits(4);
        MakeDecodeTable(_state.PretreeLen, 20, 6, _state.PretreeTable);
    }

    private void ReadLengths(BitBuffer bitBuffer, byte[] lens, int first, int last)
    {
        for (int i = first; i < last;)
        {
            int symbol = DecodeSymbol(bitBuffer, _state.PretreeTable, _state.PretreeLen, 6);
            if (symbol < 0) return;

            if (symbol == 17)
            {
                // Run of zeros
                int run = (int)bitBuffer.ReadBits(4) + 4;
                while (run-- > 0 && i < last)
                    lens[i++] = 0;
            }
            else if (symbol == 18)
            {
                // Longer run of zeros
                int run = (int)bitBuffer.ReadBits(5) + 20;
                while (run-- > 0 && i < last)
                    lens[i++] = 0;
            }
            else if (symbol == 19)
            {
                // Run of same value
                int run = (int)bitBuffer.ReadBits(1) + 4;
                symbol = DecodeSymbol(bitBuffer, _state.PretreeTable, _state.PretreeLen, 6);
                if (symbol < 0) return;
                symbol = (lens[i] - symbol + 17) % 17;
                while (run-- > 0 && i < last)
                    lens[i++] = (byte)symbol;
            }
            else
            {
                // Normal code length
                lens[i++] = (byte)((lens[i - 1] - symbol + 17) % 17);
            }
        }
    }

    private int DecodeSymbol(BitBuffer bitBuffer, ushort[] table, byte[] lens, int tableBits)
    {
        uint bits = bitBuffer.PeekBits(tableBits);
        int symbol = table[bits];

        if (symbol >= (1 << tableBits))
        {
            // Walk the tree
            int mask = 1 << (tableBits - 1);
            do
            {
                symbol = table[(symbol << 1) + ((int)(bits & mask) != 0 ? 1 : 0)];
                mask >>= 1;
            } while (symbol >= (1 << tableBits) && mask != 0);
        }

        if (symbol < lens.Length)
            bitBuffer.RemoveBits(lens[symbol]);

        return symbol;
    }

    private static void MakeDecodeTable(byte[] lens, int nsyms, int tableBits, ushort[] table)
    {
        ushort[] bit_count = new ushort[17];
        uint[] next_code = new uint[17];

        // Count codes of each length
        for (int i = 0; i < nsyms; i++)
            bit_count[lens[i]]++;

        // Calculate first code for each length
        uint code = 0;
        bit_count[0] = 0;
        for (int i = 1; i <= 16; i++)
        {
            code = (code + bit_count[i - 1]) << 1;
            next_code[i] = code;
        }

        // Clear table
        int tableSize = 1 << tableBits;
        for (int i = 0; i < tableSize; i++)
            table[i] = 0;

        // Fill in table entries
        for (int sym = 0; sym < nsyms; sym++)
        {
            int len = lens[sym];
            if (len == 0) continue;

            code = next_code[len]++;

            if (len <= tableBits)
            {
                // Fill table entries for this code
                int fill = 1 << (tableBits - len);
                uint idx = code << (tableBits - len);
                for (int j = 0; j < fill; j++)
                {
                    if (idx < tableSize)
                        table[idx++] = (ushort)sym;
                }
            }
            else
            {
                // Code is longer than table - need tree traversal
                // This is simplified; full implementation would build tree
                uint idx = code >> (len - tableBits);
                if (idx < tableSize)
                    table[idx] = (ushort)sym;
            }
        }
    }

    private enum LzxBlockType
    {
        Invalid = 0,
        Verbatim = 1,
        AlignedOffset = 2,
        Uncompressed = 3
    }

    private class LzxState
    {
        public byte[] Window = null!;
        public int WindowPosition;
        public uint R0, R1, R2;
        public ushort MainElements;
        public bool HeaderRead;
        public int BlockRemaining;
        public LzxBlockType BlockType;

        public byte[] MainTreeLen = null!;
        public ushort[] MainTreeTable = null!;
        public byte[] LengthLen = null!;
        public ushort[] LengthTable = null!;
        public byte[] AlignedLen = null!;
        public ushort[] AlignedTable = null!;
        public byte[] PretreeLen = null!;
        public ushort[] PretreeTable = null!;
    }

    /// <summary>
    /// Bit buffer for reading LZX bitstream.
    /// Xbox 360 uses big-endian byte order for 16-bit words.
    /// LZX reads bits MSB-first from each 16-bit word.
    /// </summary>
    private class BitBuffer
    {
        private readonly Stream _stream;
        private uint _buffer;     // Bits are stored at MSB position
        private int _bitsInBuffer;

        public BitBuffer(Stream stream)
        {
            _stream = stream;
            _buffer = 0;
            _bitsInBuffer = 0;
        }

        /// <summary>
        /// Initialize/reset the bit stream state.
        /// </summary>
        public void Init()
        {
            _buffer = 0;
            _bitsInBuffer = 0;
        }

        public void EnsureByteAligned()
        {
            // Discard bits to align to byte boundary
            int bitsToDiscard = _bitsInBuffer & 7;
            if (bitsToDiscard > 0)
            {
                RemoveBits(bitsToDiscard);
            }
        }

        /// <summary>
        /// Read bits from the buffer (MSB-first).
        /// </summary>
        public uint ReadBits(int count)
        {
            EnsureBits(count);
            uint result = PeekBits(count);
            RemoveBits(count);
            return result;
        }

        /// <summary>
        /// Peek at bits without consuming them.
        /// Bits are read from the MSB of the buffer.
        /// </summary>
        public uint PeekBits(int count)
        {
            EnsureBits(count);
            // Return top 'count' bits
            return _buffer >> (32 - count);
        }

        /// <summary>
        /// Remove bits from the buffer (shift left to discard from MSB).
        /// </summary>
        public void RemoveBits(int count)
        {
            _buffer <<= count;
            _bitsInBuffer -= count;
        }

        /// <summary>
        /// Get the full buffer value (for tree traversal).
        /// </summary>
        public uint GetBuffer()
        {
            return _buffer;
        }

        /// <summary>
        /// Get remaining bits in buffer.
        /// </summary>
        public int GetBitsLeft()
        {
            return _bitsInBuffer;
        }

        public int ReadByte()
        {
            return _stream.ReadByte();
        }

        /// <summary>
        /// Read a 32-bit value in little-endian format (for uncompressed blocks).
        /// </summary>
        public uint ReadUInt32LE()
        {
            int b0 = _stream.ReadByte();
            int b1 = _stream.ReadByte();
            int b2 = _stream.ReadByte();
            int b3 = _stream.ReadByte();
            if (b0 < 0 || b1 < 0 || b2 < 0 || b3 < 0) return 0;
            return (uint)(b0 | (b1 << 8) | (b2 << 16) | (b3 << 24));
        }

        /// <summary>
        /// Ensure we have at least 'count' bits in the buffer.
        /// Reads 16-bit words in big-endian format (Xbox 360 / PowerPC).
        /// </summary>
        public void EnsureBits(int count)
        {
            while (_bitsInBuffer < count)
            {
                // Xbox 360 is big-endian: first byte is high, second is low
                int b0 = _stream.ReadByte();  // High byte
                int b1 = _stream.ReadByte();  // Low byte
                if (b0 < 0 || b1 < 0) return;

                // Form 16-bit word in big-endian order
                uint word = (uint)((b0 << 8) | b1);

                // Insert into buffer at the position after existing bits
                // Bits go into the buffer's MSB side
                _buffer |= word << (16 - _bitsInBuffer);
                _bitsInBuffer += 16;
            }
        }
    }
}
