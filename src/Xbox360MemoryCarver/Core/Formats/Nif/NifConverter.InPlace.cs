// NIF converter - In-place conversion methods (when no size changes needed)

using System.Buffers.Binary;
using static Xbox360MemoryCarver.Core.Formats.Nif.NifEndianUtils;

namespace Xbox360MemoryCarver.Core.Formats.Nif;

internal sealed partial class NifConverter
{
    /// <summary>
    ///     Convert the NIF in place (no size changes).
    /// </summary>
    private void ConvertInPlace(byte[] buf, NifInfo info, int[] blockRemap)
    {
        // Convert header
        ConvertHeader(buf, info);

        // Create schema converter
        var schemaConverter = new NifSchemaConverter(
            _schema,
            info.BinaryVersion,
            (int)info.UserVersion,
            (int)info.BsVersion);

        // Convert each block using schema
        foreach (var block in info.Blocks)
        {
            if (_blocksToStrip.Contains(block.Index))
            {
                Log.Debug($"  Block {block.Index}: {block.TypeName} - skipping (will be removed)");
                continue;
            }

            Log.Debug($"  Block {block.Index}: {block.TypeName} at offset {block.DataOffset:X}, size {block.Size}");

            if (!schemaConverter.TryConvert(buf, block.DataOffset, block.Size, block.TypeName, blockRemap))
            {
                // Fallback: bulk swap all 4-byte values (may break some data)
                Log.Debug("    -> Using fallback bulk swap");
                BulkSwap32(buf, block.DataOffset, block.Size);
            }
        }

        // Convert footer
        ConvertFooter(buf, info);
    }

    /// <summary>
    ///     Convert header endianness.
    /// </summary>
    private static void ConvertHeader(byte[] buf, NifInfo info)
    {
        // The header string and version are always little-endian
        // Only the endian byte needs to change from 0 (BE) to 1 (LE)

        // Find the endian byte position (after header string + binary version)
        var pos = info.HeaderString.Length + 1 + 4; // +1 for newline, +4 for binary version

        // Change endian byte from 0 to 1
        if (buf[pos] == 0)
        {
            buf[pos] = 1;
        }

        // Swap header fields
        SwapHeaderFields(buf, info);
    }

    /// <summary>
    ///     Convert footer endianness.
    /// </summary>
    private static void ConvertFooter(byte[] buf, NifInfo info)
    {
        // Footer is at the end of the file after all blocks
        // Structure: Num Roots (uint) + Root indices (int[Num Roots])

        // Calculate footer position
        var lastBlock = info.Blocks[^1];
        var footerPos = lastBlock.DataOffset + lastBlock.Size;

        if (footerPos + 4 > buf.Length)
        {
            return;
        }

        // Swap num roots
        SwapUInt32InPlace(buf, footerPos);
        var numRoots = ReadUInt32LE(buf, footerPos);
        footerPos += 4;

        // Swap root indices
        for (var i = 0; i < numRoots && footerPos + 4 <= buf.Length; i++)
        {
            SwapUInt32InPlace(buf, footerPos);
            footerPos += 4;
        }
    }

    /// <summary>
    ///     Swap all header fields from big-endian to little-endian.
    /// </summary>
    private static void SwapHeaderFields(byte[] buf, NifInfo info)
    {
        // Position after header string + newline + binary version + endian byte
        var pos = info.HeaderString.Length + 1 + 4 + 1;

        // User version (4 bytes) - already LE in Bethesda
        pos += 4;

        // Num blocks (4 bytes) - already LE in Bethesda
        pos += 4;

        // BS Header (Bethesda specific)
        // BS Version (4 bytes) - already LE
        var bsVersion = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(pos, 4));
        pos += 4;

        // Author string (1 byte length + chars)
        var authorLen = buf[pos];
        pos += 1 + authorLen;

        // Unknown int if bsVersion > 130
        if (bsVersion > 130)
        {
            pos += 4;
        }

        // Process Script if bsVersion < 131
        if (bsVersion < 131)
        {
            var psLen = buf[pos];
            pos += 1 + psLen;
        }

        // Export Script
        var esLen = buf[pos];
        pos += 1 + esLen;

        // Max Filepath if bsVersion >= 103
        if (bsVersion >= 103)
        {
            var mfLen = buf[pos];
            pos += 1 + mfLen;
        }

        // Now we're at Num Block Types (ushort) - needs swap
        SwapUInt16InPlace(buf, pos);
        var numBlockTypes = ReadUInt16LE(buf, pos);
        pos += 2;

        // Block type strings (SizedString: uint length + chars)
        for (var i = 0; i < numBlockTypes; i++)
        {
            SwapUInt32InPlace(buf, pos);
            var strLen = ReadUInt32LE(buf, pos);
            pos += 4 + (int)strLen;
        }

        // Block type indices (ushort[numBlocks])
        for (var i = 0; i < info.BlockCount; i++)
        {
            SwapUInt16InPlace(buf, pos);
            pos += 2;
        }

        // Block sizes (uint[numBlocks])
        for (var i = 0; i < info.BlockCount; i++)
        {
            SwapUInt32InPlace(buf, pos);
            pos += 4;
        }

        // Num strings (uint)
        SwapUInt32InPlace(buf, pos);
        var numStrings = ReadUInt32LE(buf, pos);
        pos += 4;

        // Max string length (uint)
        SwapUInt32InPlace(buf, pos);
        pos += 4;

        // Strings (SizedString: uint length + chars)
        for (var i = 0; i < numStrings; i++)
        {
            SwapUInt32InPlace(buf, pos);
            var strLen = ReadUInt32LE(buf, pos);
            pos += 4 + (int)strLen;
        }

        // Num groups (uint)
        SwapUInt32InPlace(buf, pos);
        var numGroups = ReadUInt32LE(buf, pos);
        pos += 4;

        // Groups (uint[numGroups])
        for (var i = 0; i < numGroups; i++)
        {
            SwapUInt32InPlace(buf, pos);
            pos += 4;
        }
    }

    /// <summary>
    ///     Convert half-precision float (IEEE 754 binary16) to single precision float.
    /// </summary>
    private static float HalfToFloat(ushort h)
    {
        var sign = (h >> 15) & 0x0001;
        var exp = (h >> 10) & 0x001F;
        var mant = h & 0x03FF;

        if (exp == 0)
        {
            // Zero or denormalized
            if (mant == 0)
            {
                return sign != 0 ? -0.0f : 0.0f;
            }

            // Denormalized: convert to normalized
            while ((mant & 0x0400) == 0)
            {
                mant <<= 1;
                exp--;
            }

            exp++;
            mant &= ~0x0400;
        }
        else if (exp == 31)
        {
            // Inf or NaN
            if (mant != 0)
            {
                return float.NaN;
            }

            return sign != 0 ? float.NegativeInfinity : float.PositiveInfinity;
        }

        exp += 127 - 15;
        mant <<= 13;

        var bits = (sign << 31) | (exp << 23) | mant;
        return BitConverter.Int32BitsToSingle(bits);
    }
}
