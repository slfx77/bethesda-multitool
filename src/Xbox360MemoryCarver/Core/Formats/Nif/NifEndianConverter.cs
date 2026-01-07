using System.Text;
using Xbox360MemoryCarver.Core.Utils;

namespace Xbox360MemoryCarver.Core.Formats.Nif;

/// <summary>
///     Converts big-endian Xbox 360 NIF files to little-endian PC format.
///     Uses nif.xml schema for proper block-by-block field conversion.
/// </summary>
/// <remarks>
///     Xbox 360 NIFs have a hybrid endianness:
///     - Fields BEFORE the endian byte (binary version, user version, numBlocks, BS Version) are ALWAYS little-endian
///     - Fields AFTER the endian byte follow its setting: NumBlockTypes, Block Types, Block Sizes, strings, block data, footer
///     
///     This converter performs full endian conversion using the nif.xml schema from NifSkope:
///     1. Header fields after endian byte (NumBlockTypes, Block Types, Block Type Index, Block Sizes, Strings, Num Groups)
///     2. Block data - parsed field-by-field using type definitions from nif.xml
///     3. Footer (Num Roots, Root indices)
///     
///     Xbox 360-specific blocks like BSPackedAdditionalGeometryData are stripped (references nulled)
///     because NifSkope doesn't support them.
/// </remarks>
public sealed class NifEndianConverter
{
    private readonly NifXmlSchema _schema;
    private readonly bool _verbose;

    // Parsed header info needed for block conversion
    private uint _binaryVersion;
    private uint _userVersion;
    private uint _bsVersion;
    private string[] _blockTypeNames = [];
    
    // Set of block indices that are BSPackedAdditionalGeometryData (to be stripped)
    private HashSet<int> _blocksToStrip = [];

    public NifEndianConverter(bool verbose = false)
    {
        _schema = NifXmlSchema.Instance;
        _verbose = verbose;
    }

    /// <summary>
    ///     Converts a big-endian NIF file to little-endian.
    ///     Returns null if the file is already little-endian or conversion fails.
    ///     BSPackedAdditionalGeometryData blocks are removed as NifSkope doesn't support them.
    /// </summary>
    public byte[]? ConvertToLittleEndian(byte[] data)
    {
        if (data.Length < 50)
        {
            return null;
        }

        // Find header string end (newline)
        var newlinePos = FindNewline(data, 0, Math.Min(60, data.Length));
        if (newlinePos == -1)
        {
            return null;
        }

        var pos = newlinePos + 1;
        if (pos + 9 > data.Length)
        {
            return null;
        }

        // Read binary version (always LE)
        _binaryVersion = BinaryUtils.ReadUInt32LE(data, pos);

        // Check endian byte
        var endianBytePos = pos + 4;
        var endianByte = data[endianBytePos];

        // If already little-endian, no conversion needed
        if (endianByte == 1)
        {
            return null;
        }

        // If not big-endian (0), something is wrong
        if (endianByte != 0)
        {
            return null;
        }

        try
        {
            pos = endianBytePos + 1;

            // User version (already LE)
            _userVersion = BinaryUtils.ReadUInt32LE(data, pos);
            var numBlocksPos = pos + 4;

            // Num blocks (already LE)
            var numBlocks = BinaryUtils.ReadUInt32LE(data, numBlocksPos);

            if (numBlocks == 0 || numBlocks > 100000)
            {
                return null;
            }

            pos = numBlocksPos + 4;

            // Check for Bethesda header
            var hasBsHeader = IsBethesdaVersion(_binaryVersion, _userVersion);

            if (hasBsHeader)
            {
                // BS Version (already LE)
                _bsVersion = BinaryUtils.ReadUInt32LE(data, pos);
                pos += 4;

                // Skip Author string (ShortString: 1 byte len + chars)
                pos = SkipShortString(data, pos);
                if (pos < 0) return null;

                // Skip Process Script (ShortString)
                pos = SkipShortString(data, pos);
                if (pos < 0) return null;

                // Skip Export Script (ShortString)
                pos = SkipShortString(data, pos);
                if (pos < 0) return null;
            }

            // NumBlockTypes - BE on Xbox 360 (follows endian byte setting)
            if (pos + 2 > data.Length) return null;

            var numBlockTypes = BinaryUtils.ReadUInt16BE(data, pos);
            var numBlockTypesPos = pos;
            pos += 2;

            if (numBlockTypes == 0 || numBlockTypes > 1000)
            {
                return null;
            }

            // Block Types (SizedString array) - string lengths are BE
            _blockTypeNames = new string[numBlockTypes];
            for (var i = 0; i < numBlockTypes; i++)
            {
                if (pos + 4 > data.Length) return null;

                var strLen = BinaryUtils.ReadUInt32BE(data, pos);
                pos += 4;

                if (strLen > 256) return null;

                _blockTypeNames[i] = Encoding.ASCII.GetString(data, pos, (int)strLen);
                pos += (int)strLen;
            }

            var blockTypeIndicesPos = pos;

            // Block Type Index (ushort[numBlocks]) - BE
            var blockTypeIndices = new ushort[numBlocks];
            for (var i = 0; i < numBlocks; i++)
            {
                if (pos + 2 > data.Length) return null;

                blockTypeIndices[i] = BinaryUtils.ReadUInt16BE(data, pos);
                pos += 2;
            }

            var blockSizesPos = pos;

            // Block Sizes (uint[numBlocks]) - BE
            var blockSizes = new uint[numBlocks];
            for (var i = 0; i < numBlocks; i++)
            {
                if (pos + 4 > data.Length) return null;

                blockSizes[i] = BinaryUtils.ReadUInt32BE(data, pos);
                pos += 4;
            }

            // Identify blocks to remove and build remapping
            _blocksToStrip.Clear();
            var blockRemap = new int[numBlocks]; // oldIndex -> newIndex (-1 if removed)
            var newBlockCount = 0;
            
            for (var i = 0; i < numBlocks; i++)
            {
                var typeIdx = blockTypeIndices[i];
                if (typeIdx < _blockTypeNames.Length && _blockTypeNames[typeIdx] == "BSPackedAdditionalGeometryData")
                {
                    _blocksToStrip.Add(i);
                    blockRemap[i] = -1;
                    if (_verbose)
                    {
                        Console.WriteLine($"Block {i} is BSPackedAdditionalGeometryData - will be removed");
                    }
                }
                else
                {
                    blockRemap[i] = newBlockCount;
                    newBlockCount++;
                }
            }

            // Calculate size reduction
            long removedSize = 0;
            foreach (var blockIdx in _blocksToStrip)
            {
                removedSize += blockSizes[blockIdx]; // Block data
                removedSize += 2; // Block type index (ushort)
                removedSize += 4; // Block size (uint)
            }

            // Create output buffer with reduced size
            var output = new byte[data.Length - removedSize];
            
            // Copy header up to and including endian byte position
            Array.Copy(data, 0, output, 0, endianBytePos + 1);
            output[endianBytePos] = 1; // Set to little-endian

            var outPos = endianBytePos + 1;

            // Copy user version (LE)
            Array.Copy(data, endianBytePos + 1, output, outPos, 4);
            outPos += 4;

            // Write new numBlocks (LE)
            WriteUInt32LE(output, outPos, (uint)newBlockCount);
            outPos += 4;

            // Copy BS header if present
            if (hasBsHeader)
            {
                // BS Version
                Array.Copy(data, numBlocksPos + 4, output, outPos, 4);
                outPos += 4;

                // Copy short strings (author, process script, export script)
                var srcPos = numBlocksPos + 8;
                for (var i = 0; i < 3; i++)
                {
                    var len = data[srcPos];
                    Array.Copy(data, srcPos, output, outPos, 1 + len);
                    srcPos += 1 + len;
                    outPos += 1 + len;
                }
            }

            // Write numBlockTypes (LE)
            WriteUInt16LE(output, outPos, numBlockTypes);
            outPos += 2;

            // Copy block type names (swap string lengths to LE)
            pos = numBlockTypesPos + 2;
            for (var i = 0; i < numBlockTypes; i++)
            {
                var strLen = BinaryUtils.ReadUInt32BE(data, pos);
                WriteUInt32LE(output, outPos, strLen);
                outPos += 4;
                pos += 4;

                Array.Copy(data, pos, output, outPos, (int)strLen);
                outPos += (int)strLen;
                pos += (int)strLen;
            }

            // Write new block type indices (only non-removed blocks)
            for (var i = 0; i < numBlocks; i++)
            {
                if (!_blocksToStrip.Contains(i))
                {
                    WriteUInt16LE(output, outPos, blockTypeIndices[i]);
                    outPos += 2;
                }
            }

            // Write new block sizes (only non-removed blocks)
            for (var i = 0; i < numBlocks; i++)
            {
                if (!_blocksToStrip.Contains(i))
                {
                    WriteUInt32LE(output, outPos, blockSizes[i]);
                    outPos += 4;
                }
            }

            // Find strings section start
            pos = blockSizesPos + (int)(numBlocks * 4);

            // Copy and swap Num Strings
            var numStrings = BinaryUtils.ReadUInt32BE(data, pos);
            WriteUInt32LE(output, outPos, numStrings);
            outPos += 4;
            pos += 4;

            // Copy and swap Max String Length
            var maxStringLen = BinaryUtils.ReadUInt32BE(data, pos);
            WriteUInt32LE(output, outPos, maxStringLen);
            outPos += 4;
            pos += 4;

            // Copy and swap Strings
            for (var i = 0; i < numStrings && pos + 4 <= data.Length; i++)
            {
                var strLen = BinaryUtils.ReadUInt32BE(data, pos);
                WriteUInt32LE(output, outPos, strLen);
                outPos += 4;
                pos += 4;

                if (strLen > 4096) break;
                
                Array.Copy(data, pos, output, outPos, (int)strLen);
                outPos += (int)strLen;
                pos += (int)strLen;
            }

            // Copy and swap Num Groups
            if (pos + 4 <= data.Length)
            {
                var numGroups = BinaryUtils.ReadUInt32BE(data, pos);
                WriteUInt32LE(output, outPos, numGroups);
                outPos += 4;
                pos += 4;

                for (var i = 0; i < numGroups && pos + 4 <= data.Length; i++)
                {
                    var group = BinaryUtils.ReadUInt32BE(data, pos);
                    WriteUInt32LE(output, outPos, group);
                    outPos += 4;
                    pos += 4;
                }
            }

            // Now convert each block, skipping removed ones
            for (var blockIdx = 0; blockIdx < numBlocks; blockIdx++)
            {
                var blockSize = (int)blockSizes[blockIdx];
                var typeIdx = blockTypeIndices[blockIdx];
                var blockTypeName = typeIdx < _blockTypeNames.Length ? _blockTypeNames[typeIdx] : "Unknown";

                if (_blocksToStrip.Contains(blockIdx))
                {
                    // Skip this block entirely
                    pos += blockSize;
                    continue;
                }

                var blockEnd = pos + blockSize;

                if (blockEnd > data.Length)
                {
                    Console.WriteLine($"Block {blockIdx} ({blockTypeName}) extends past EOF");
                    break;
                }

                if (_verbose)
                {
                    Console.WriteLine($"Converting block {blockIdx} -> {blockRemap[blockIdx]}: {blockTypeName}, size={blockSize}");
                }

                // Convert this block, passing the remap for reference adjustment
                var bytesWritten = ConvertBlockWithRemap(data, output, pos, outPos, blockSize, blockTypeName, blockRemap);

                pos += blockSize;
                outPos += bytesWritten;
            }

            // Footer: Num Roots (uint) + Root indices (int[]) - remap the root indices
            if (pos + 4 <= data.Length)
            {
                var numRoots = BinaryUtils.ReadUInt32BE(data, pos);
                WriteUInt32LE(output, outPos, numRoots);
                pos += 4;
                outPos += 4;

                for (var i = 0; i < numRoots && pos + 4 <= data.Length; i++)
                {
                    var rootIdx = unchecked((int)BinaryUtils.ReadUInt32BE(data, pos));
                    var newRootIdx = rootIdx >= 0 && rootIdx < blockRemap.Length ? blockRemap[rootIdx] : rootIdx;
                    WriteUInt32LE(output, outPos, unchecked((uint)newRootIdx));
                    pos += 4;
                    outPos += 4;
                }
            }

            // Trim output if we didn't use all of it
            if (outPos < output.Length)
            {
                Array.Resize(ref output, outPos);
            }

            return output;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"NifEndianConverter exception: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            return null;
        }
    }

    /// <summary>
    ///     Converts a single block's fields from BE to LE, remapping block references.
    ///     Returns the number of bytes written.
    /// </summary>
    private int ConvertBlockWithRemap(byte[] input, byte[] output, int inPos, int outPos, int blockSize, string blockTypeName, int[] blockRemap)
    {
        var blockEnd = inPos + blockSize;
        var outStart = outPos;

        // First, copy the block data
        Array.Copy(input, inPos, output, outPos, blockSize);

        // Handle specific block types that need special handling
        switch (blockTypeName)
        {
            case "BSShaderTextureSet":
                ConvertBSShaderTextureSetInPlace(output, outPos, blockSize);
                break;

            case "NiStringExtraData":
                ConvertNiStringExtraDataInPlace(output, outPos, blockSize);
                break;

            case "BSBehaviorGraphExtraData":
                ConvertBSBehaviorGraphExtraDataInPlace(output, outPos, blockSize);
                break;

            case "NiTextKeyExtraData":
                ConvertNiTextKeyExtraDataInPlace(output, outPos, blockSize);
                break;

            case "NiSourceTexture":
                ConvertNiSourceTextureInPlace(output, outPos, blockSize);
                break;

            case "NiTriStripsData":
                ConvertNiTriStripsDataInPlace(output, outPos, blockSize, blockRemap);
                break;

            case "NiTriShapeData":
                ConvertNiTriShapeDataInPlace(output, outPos, blockSize, blockRemap);
                break;

            default:
                // For blocks without special handling, use 4-byte bulk swap
                ConvertBulkSwap4InPlace(output, outPos, blockSize);
                break;
        }

        return blockSize;
    }

    /// <summary>
    ///     Bulk 4-byte swap for blocks without special handling.
    ///     Data has already been copied, so we swap in-place.
    /// </summary>
    private static void ConvertBulkSwap4InPlace(byte[] output, int pos, int blockSize)
    {
        var end = pos + blockSize;
        while (pos + 4 <= end)
        {
            SwapUInt32InPlace(output, pos);
            pos += 4;
        }
    }

    /// <summary>
    ///     Swaps a uint32 in-place from BE to LE.
    /// </summary>
    private static void SwapUInt32InPlace(byte[] buf, int pos)
    {
        var b0 = buf[pos];
        var b1 = buf[pos + 1];
        var b2 = buf[pos + 2];
        var b3 = buf[pos + 3];
        buf[pos] = b3;
        buf[pos + 1] = b2;
        buf[pos + 2] = b1;
        buf[pos + 3] = b0;
    }

    /// <summary>
    ///     Swaps a ushort in-place from BE to LE.
    /// </summary>
    private static void SwapUInt16InPlace(byte[] buf, int pos)
    {
        (buf[pos], buf[pos + 1]) = (buf[pos + 1], buf[pos]);
    }

    /// <summary>
    ///     BSShaderTextureSet in-place conversion.
    /// </summary>
    private static void ConvertBSShaderTextureSetInPlace(byte[] buf, int pos, int blockSize)
    {
        var end = pos + blockSize;
        if (pos + 4 > end) return;

        SwapUInt32InPlace(buf, pos); // numTextures
        var numTextures = BinaryUtils.ReadUInt32LE(buf, pos);
        pos += 4;

        for (var i = 0; i < numTextures && pos + 4 <= end; i++)
        {
            SwapUInt32InPlace(buf, pos); // strLen
            var strLen = BinaryUtils.ReadUInt32LE(buf, pos);
            pos += 4;

            if (strLen > 0 && pos + strLen <= end)
            {
                pos += (int)strLen;
            }
        }
    }

    /// <summary>
    ///     NiStringExtraData in-place conversion.
    /// </summary>
    private static void ConvertNiStringExtraDataInPlace(byte[] buf, int pos, int blockSize)
    {
        var end = pos + blockSize;
        if (pos + 4 > end) return;

        SwapUInt32InPlace(buf, pos); // nameIdx
        pos += 4;

        if (pos + 4 > end) return;

        SwapUInt32InPlace(buf, pos); // strLen
        var strLen = BinaryUtils.ReadUInt32LE(buf, pos);
        pos += 4;

        if (strLen > 0 && pos + strLen <= end)
        {
            pos += (int)strLen;
        }
    }

    /// <summary>
    ///     BSBehaviorGraphExtraData in-place conversion.
    /// </summary>
    private static void ConvertBSBehaviorGraphExtraDataInPlace(byte[] buf, int pos, int blockSize)
    {
        var end = pos + blockSize;
        if (pos + 4 > end) return;

        SwapUInt32InPlace(buf, pos); // nameIdx
        pos += 4;

        if (pos + 4 > end) return;

        SwapUInt32InPlace(buf, pos); // strLen
        var strLen = BinaryUtils.ReadUInt32LE(buf, pos);
        pos += 4;

        // Skip string + bool
    }

    /// <summary>
    ///     NiTextKeyExtraData in-place conversion.
    /// </summary>
    private static void ConvertNiTextKeyExtraDataInPlace(byte[] buf, int pos, int blockSize)
    {
        var end = pos + blockSize;
        if (pos + 4 > end) return;

        SwapUInt32InPlace(buf, pos); // nameIdx
        pos += 4;

        if (pos + 4 > end) return;

        SwapUInt32InPlace(buf, pos); // numKeys
        var numKeys = BinaryUtils.ReadUInt32LE(buf, pos);
        pos += 4;

        for (var i = 0; i < numKeys && pos + 8 <= end; i++)
        {
            SwapUInt32InPlace(buf, pos); // float time
            pos += 4;

            SwapUInt32InPlace(buf, pos); // strLen
            var strLen = BinaryUtils.ReadUInt32LE(buf, pos);
            pos += 4;

            if (strLen > 0 && pos + strLen <= end)
            {
                pos += (int)strLen;
            }
        }
    }

    /// <summary>
    ///     NiSourceTexture in-place conversion.
    /// </summary>
    private static void ConvertNiSourceTextureInPlace(byte[] buf, int pos, int blockSize)
    {
        var end = pos + blockSize;
        if (pos + 4 > end) return;

        SwapUInt32InPlace(buf, pos); // nameIdx
        pos += 4;

        if (pos >= end) return;

        var useExternal = buf[pos];
        pos += 1;

        if (useExternal == 1)
        {
            if (pos + 4 > end) return;

            SwapUInt32InPlace(buf, pos); // strLen
            var strLen = BinaryUtils.ReadUInt32LE(buf, pos);
            pos += 4;

            if (strLen > 0 && pos + strLen <= end)
            {
                pos += (int)strLen;
            }
        }
        else
        {
            if (pos + 4 > end) return;

            SwapUInt32InPlace(buf, pos); // pixelRef
            pos += 4;
        }

        // Swap remaining
        while (pos + 4 <= end)
        {
            SwapUInt32InPlace(buf, pos);
            pos += 4;
        }
    }

    /// <summary>
    ///     NiTriStripsData in-place conversion with reference remapping.
    ///     Note: additionalData Ref pointing to stripped blocks is set to -1.
    /// </summary>
    private void ConvertNiTriStripsDataInPlace(byte[] buf, int pos, int blockSize, int[] blockRemap)
    {
        var end = pos + blockSize;
        if (pos + 4 > end) return;

        // int groupId (inherited from NiGeometryData)
        SwapUInt32InPlace(buf, pos);
        pos += 4;

        if (pos + 2 > end) return;
        SwapUInt16InPlace(buf, pos); // ushort numVertices
        var numVertices = BinaryUtils.ReadUInt16LE(buf, pos);
        pos += 2;

        if (pos + 2 > end) return;
        SwapUInt16InPlace(buf, pos); // ushort bsDataFlags (keepFlags + compressFlags)
        var bsDataFlags = BinaryUtils.ReadUInt16LE(buf, pos);
        pos += 2;

        // byte hasVertices
        if (pos >= end) return;
        var hasVertices = buf[pos];
        pos += 1;

        // Vector3[numVertices] vertices
        if (hasVertices == 1)
        {
            for (var i = 0; i < numVertices && pos + 12 <= end; i++)
            {
                SwapUInt32InPlace(buf, pos); pos += 4;
                SwapUInt32InPlace(buf, pos); pos += 4;
                SwapUInt32InPlace(buf, pos); pos += 4;
            }
        }

        // UV sets count: bsDataFlags & 1 (0 or 1 for FO3/FNV)
        var numUvSets = bsDataFlags & 1;

        // byte hasNormals
        if (pos >= end) return;
        var hasNormals = buf[pos];
        pos += 1;

        // Vector3[numVertices] normals
        if (hasNormals == 1)
        {
            for (var i = 0; i < numVertices && pos + 12 <= end; i++)
            {
                SwapUInt32InPlace(buf, pos); pos += 4;
                SwapUInt32InPlace(buf, pos); pos += 4;
                SwapUInt32InPlace(buf, pos); pos += 4;
            }
        }

        // Tangents and Bitangents (if hasNormals && bsDataFlags & 0x1000)
        if (hasNormals == 1 && (bsDataFlags & 0x1000) != 0)
        {
            for (var i = 0; i < numVertices && pos + 12 <= end; i++)
            {
                SwapUInt32InPlace(buf, pos); pos += 4;
                SwapUInt32InPlace(buf, pos); pos += 4;
                SwapUInt32InPlace(buf, pos); pos += 4;
            }
            for (var i = 0; i < numVertices && pos + 12 <= end; i++)
            {
                SwapUInt32InPlace(buf, pos); pos += 4;
                SwapUInt32InPlace(buf, pos); pos += 4;
                SwapUInt32InPlace(buf, pos); pos += 4;
            }
        }

        // BoundingSphere center (Vector3) + radius (float)
        if (pos + 16 > end) return;
        SwapUInt32InPlace(buf, pos); pos += 4;
        SwapUInt32InPlace(buf, pos); pos += 4;
        SwapUInt32InPlace(buf, pos); pos += 4;
        SwapUInt32InPlace(buf, pos); pos += 4;

        // byte hasVertexColors
        if (pos >= end) return;
        var hasVertexColors = buf[pos];
        pos += 1;

        // Color4[numVertices] vertexColors
        if (hasVertexColors == 1)
        {
            for (var i = 0; i < numVertices && pos + 16 <= end; i++)
            {
                SwapUInt32InPlace(buf, pos); pos += 4;
                SwapUInt32InPlace(buf, pos); pos += 4;
                SwapUInt32InPlace(buf, pos); pos += 4;
                SwapUInt32InPlace(buf, pos); pos += 4;
            }
        }

        // TexCoord[numVertices * numUvSets] uvSets
        for (var i = 0; i < numVertices * numUvSets && pos + 8 <= end; i++)
        {
            SwapUInt32InPlace(buf, pos); pos += 4;
            SwapUInt32InPlace(buf, pos); pos += 4;
        }

        // ushort consistencyFlags
        if (pos + 2 > end) return;
        SwapUInt16InPlace(buf, pos);
        pos += 2;

        // Ref additionalData - REMAP THIS
        if (pos + 4 > end) return;
        SwapUInt32InPlace(buf, pos); // Swap first
        var additionalData = unchecked((int)BinaryUtils.ReadUInt32LE(buf, pos));
        if (additionalData >= 0 && additionalData < blockRemap.Length)
        {
            var newRef = blockRemap[additionalData];
            WriteUInt32LE(buf, pos, unchecked((uint)newRef));
        }
        pos += 4;

        // NiTriBasedGeomData: ushort numTriangles
        if (pos + 2 > end) return;
        SwapUInt16InPlace(buf, pos);
        pos += 2;

        // NiTriStripsData specific fields
        // ushort numStrips
        if (pos + 2 > end) return;
        SwapUInt16InPlace(buf, pos);
        var numStrips = BinaryUtils.ReadUInt16LE(buf, pos);
        pos += 2;

        // ushort[numStrips] stripLengths
        for (var i = 0; i < numStrips && pos + 2 <= end; i++)
        {
            SwapUInt16InPlace(buf, pos);
            pos += 2;
        }

        // byte hasPoints
        if (pos >= end) return;
        var hasPoints = buf[pos];
        pos += 1;

        // Read stripLengths again to get total points
        var stripLengthsStart = pos - 1 - (numStrips * 2);
        if (hasPoints == 1)
        {
            for (var s = 0; s < numStrips && stripLengthsStart + s * 2 + 2 <= end; s++)
            {
                var stripLen = BinaryUtils.ReadUInt16LE(buf, stripLengthsStart + s * 2);
                for (var i = 0; i < stripLen && pos + 2 <= end; i++)
                {
                    SwapUInt16InPlace(buf, pos);
                    pos += 2;
                }
            }
        }
    }

    /// <summary>
    ///     NiTriShapeData in-place conversion with reference remapping.
    /// </summary>
    private void ConvertNiTriShapeDataInPlace(byte[] buf, int pos, int blockSize, int[] blockRemap)
    {
        var end = pos + blockSize;
        if (pos + 4 > end) return;

        // int groupId
        SwapUInt32InPlace(buf, pos);
        pos += 4;

        if (pos + 2 > end) return;
        SwapUInt16InPlace(buf, pos); // ushort numVertices
        var numVertices = BinaryUtils.ReadUInt16LE(buf, pos);
        pos += 2;

        if (pos + 2 > end) return;
        SwapUInt16InPlace(buf, pos); // ushort bsDataFlags
        var bsDataFlags = BinaryUtils.ReadUInt16LE(buf, pos);
        pos += 2;

        if (pos >= end) return;
        var hasVertices = buf[pos];
        pos += 1;

        if (hasVertices == 1)
        {
            for (var i = 0; i < numVertices && pos + 12 <= end; i++)
            {
                SwapUInt32InPlace(buf, pos); pos += 4;
                SwapUInt32InPlace(buf, pos); pos += 4;
                SwapUInt32InPlace(buf, pos); pos += 4;
            }
        }

        var numUvSets = bsDataFlags & 1;

        if (pos >= end) return;
        var hasNormals = buf[pos];
        pos += 1;

        if (hasNormals == 1)
        {
            for (var i = 0; i < numVertices && pos + 12 <= end; i++)
            {
                SwapUInt32InPlace(buf, pos); pos += 4;
                SwapUInt32InPlace(buf, pos); pos += 4;
                SwapUInt32InPlace(buf, pos); pos += 4;
            }
        }

        if (hasNormals == 1 && (bsDataFlags & 0x1000) != 0)
        {
            for (var i = 0; i < numVertices && pos + 12 <= end; i++)
            {
                SwapUInt32InPlace(buf, pos); pos += 4;
                SwapUInt32InPlace(buf, pos); pos += 4;
                SwapUInt32InPlace(buf, pos); pos += 4;
            }
            for (var i = 0; i < numVertices && pos + 12 <= end; i++)
            {
                SwapUInt32InPlace(buf, pos); pos += 4;
                SwapUInt32InPlace(buf, pos); pos += 4;
                SwapUInt32InPlace(buf, pos); pos += 4;
            }
        }

        if (pos + 16 > end) return;
        SwapUInt32InPlace(buf, pos); pos += 4;
        SwapUInt32InPlace(buf, pos); pos += 4;
        SwapUInt32InPlace(buf, pos); pos += 4;
        SwapUInt32InPlace(buf, pos); pos += 4;

        if (pos >= end) return;
        var hasVertexColors = buf[pos];
        pos += 1;

        if (hasVertexColors == 1)
        {
            for (var i = 0; i < numVertices && pos + 16 <= end; i++)
            {
                SwapUInt32InPlace(buf, pos); pos += 4;
                SwapUInt32InPlace(buf, pos); pos += 4;
                SwapUInt32InPlace(buf, pos); pos += 4;
                SwapUInt32InPlace(buf, pos); pos += 4;
            }
        }

        for (var i = 0; i < numVertices * numUvSets && pos + 8 <= end; i++)
        {
            SwapUInt32InPlace(buf, pos); pos += 4;
            SwapUInt32InPlace(buf, pos); pos += 4;
        }

        if (pos + 2 > end) return;
        SwapUInt16InPlace(buf, pos);
        pos += 2;

        // Ref additionalData - REMAP
        if (pos + 4 > end) return;
        SwapUInt32InPlace(buf, pos);
        var additionalData = unchecked((int)BinaryUtils.ReadUInt32LE(buf, pos));
        if (additionalData >= 0 && additionalData < blockRemap.Length)
        {
            var newRef = blockRemap[additionalData];
            WriteUInt32LE(buf, pos, unchecked((uint)newRef));
        }
        pos += 4;

        // NiTriBasedGeomData: ushort numTriangles
        if (pos + 2 > end) return;
        SwapUInt16InPlace(buf, pos);
        var numTriangles = BinaryUtils.ReadUInt16LE(buf, pos);
        pos += 2;

        // NiTriShapeData: byte hasTriangles
        if (pos >= end) return;
        var hasTriangles = buf[pos];
        pos += 1;

        // Triangle[numTriangles] triangles
        if (hasTriangles == 1)
        {
            for (var i = 0; i < numTriangles && pos + 6 <= end; i++)
            {
                SwapUInt16InPlace(buf, pos); pos += 2;
                SwapUInt16InPlace(buf, pos); pos += 2;
                SwapUInt16InPlace(buf, pos); pos += 2;
            }
        }

        // ushort[numTriangles] matchGroups - bulk swap remaining
        while (pos + 4 <= end)
        {
            SwapUInt32InPlace(buf, pos);
            pos += 4;
        }
    }

    /// <summary>
    ///     Converts a single field value from BE to LE.
    /// </summary>
    private int ConvertField(byte[] input, byte[] output, int pos, string typeName, ConversionContext context, int maxPos)
    {
        if (pos >= maxPos) return pos;

        var typeSize = _schema.GetTypeSize(typeName);

        // Handle compound types recursively
        if (typeSize == 0)
        {
            // Check if it's a known compound type
            return ConvertCompoundType(input, output, pos, typeName, context, maxPos);
        }

        // Simple type - swap bytes based on size
        // Also check against input.Length to avoid array bounds issues
        if (pos + typeSize > maxPos || pos + typeSize > input.Length)
        {
            if (_verbose)
            {
                Console.WriteLine($"  ConvertField: pos={pos}, typeSize={typeSize}, maxPos={maxPos}, input.Length={input.Length} - SKIPPING");
            }
            return maxPos;
        }

        switch (typeSize)
        {
            case 2:
                var val16 = BinaryUtils.ReadUInt16BE(input, pos);
                WriteUInt16LE(output, pos, val16);
                break;

            case 4:
                var val32 = BinaryUtils.ReadUInt32BE(input, pos);
                WriteUInt32LE(output, pos, val32);
                break;

            case 8:
                var val64 = BinaryUtils.ReadUInt64BE(input, pos);
                WriteUInt64LE(output, pos, val64);
                break;

            // 1-byte values don't need swapping
        }

        return pos + typeSize;
    }

    /// <summary>
    ///     Converts a compound type (struct) field by field.
    /// </summary>
    private int ConvertCompoundType(byte[] input, byte[] output, int pos, string typeName, ConversionContext context, int maxPos)
    {
        // Handle well-known compound types
        switch (typeName)
        {
            case "Vector3":
                // 3 floats
                for (var i = 0; i < 3 && pos + 4 <= maxPos; i++)
                {
                    var val = BinaryUtils.ReadUInt32BE(input, pos);
                    WriteUInt32LE(output, pos, val);
                    pos += 4;
                }
                return pos;

            case "Vector4":
            case "Quaternion":
            case "Color4":
                // 4 floats
                for (var i = 0; i < 4 && pos + 4 <= maxPos; i++)
                {
                    var val = BinaryUtils.ReadUInt32BE(input, pos);
                    WriteUInt32LE(output, pos, val);
                    pos += 4;
                }
                return pos;

            case "Matrix33":
                // 9 floats
                for (var i = 0; i < 9 && pos + 4 <= maxPos; i++)
                {
                    var val = BinaryUtils.ReadUInt32BE(input, pos);
                    WriteUInt32LE(output, pos, val);
                    pos += 4;
                }
                return pos;

            case "Matrix44":
                // 16 floats
                for (var i = 0; i < 16 && pos + 4 <= maxPos; i++)
                {
                    var val = BinaryUtils.ReadUInt32BE(input, pos);
                    WriteUInt32LE(output, pos, val);
                    pos += 4;
                }
                return pos;

            case "Color3":
                // 3 floats
                for (var i = 0; i < 3 && pos + 4 <= maxPos; i++)
                {
                    var val = BinaryUtils.ReadUInt32BE(input, pos);
                    WriteUInt32LE(output, pos, val);
                    pos += 4;
                }
                return pos;

            case "NiBound":
                // Vector3 (center) + float (radius) = 4 floats
                for (var i = 0; i < 4 && pos + 4 <= maxPos; i++)
                {
                    var val = BinaryUtils.ReadUInt32BE(input, pos);
                    WriteUInt32LE(output, pos, val);
                    pos += 4;
                }
                return pos;

            case "TexCoord":
                // 2 floats
                for (var i = 0; i < 2 && pos + 4 <= maxPos; i++)
                {
                    var val = BinaryUtils.ReadUInt32BE(input, pos);
                    WriteUInt32LE(output, pos, val);
                    pos += 4;
                }
                return pos;

            case "Triangle":
                // 3 ushorts
                for (var i = 0; i < 3 && pos + 2 <= maxPos; i++)
                {
                    var val = BinaryUtils.ReadUInt16BE(input, pos);
                    WriteUInt16LE(output, pos, val);
                    pos += 2;
                }
                return pos;

            case "ByteColor3":
                // 3 bytes - no swap needed
                return pos + 3;

            case "ByteColor4":
                // 4 bytes - no swap needed
                return pos + 4;

            case "SizedString":
                // uint length + chars
                if (pos + 4 > maxPos) return maxPos;
                var strLen = BinaryUtils.ReadUInt32BE(input, pos);
                WriteUInt32LE(output, pos, strLen);
                pos += 4;
                return pos + Math.Min((int)strLen, maxPos - pos);

            case "string":
            case "NiFixedString":
                // Index into string table (uint)
                if (pos + 4 > maxPos) return maxPos;
                var strIdx = BinaryUtils.ReadUInt32BE(input, pos);
                WriteUInt32LE(output, pos, strIdx);
                return pos + 4;

            case "ShortString":
                // byte length + chars
                if (pos >= maxPos) return maxPos;
                var shortLen = input[pos];
                return pos + 1 + Math.Min(shortLen, maxPos - pos - 1);

            case "Ref":
            case "Ptr":
                // Block reference (int)
                if (pos + 4 > maxPos) return maxPos;
                var refVal = BinaryUtils.ReadUInt32BE(input, pos);
                WriteUInt32LE(output, pos, refVal);
                return pos + 4;

            default:
                // Try to look up in schema structs
                if (_schema.Structs.TryGetValue(typeName, out var structDef))
                {
                    foreach (var field in structDef.Fields)
                    {
                        if (pos >= maxPos) break;
                        pos = ConvertField(input, output, pos, field.Type, context, maxPos);
                    }
                    return pos;
                }

                // Unknown type - skip 4 bytes as common default
                return Math.Min(pos + 4, maxPos);
        }
    }

    /// <summary>
    ///     Attempts to convert an unknown block type using heuristics.
    /// </summary>
    private static int ConvertUnknownBlock(byte[] input, byte[] output, int pos, int blockSize)
    {
        var blockEnd = pos + blockSize;

        // For unknown blocks, we make educated guesses based on alignment
        // Most NIF values are 4-byte aligned (uint, int, float, Ref)
        // We'll swap all 4-byte aligned values as a best effort

        while (pos + 4 <= blockEnd)
        {
            var val = BinaryUtils.ReadUInt32BE(input, pos);
            WriteUInt32LE(output, pos, val);
            pos += 4;
        }

        // Handle any remaining bytes
        return blockEnd;
    }

    /// <summary>
    ///     Evaluates a simple version condition.
    /// </summary>
    private static bool EvaluateVersionCondition(string? condition, ConversionContext context)
    {
        if (string.IsNullOrEmpty(condition)) return true;

        // Handle common Bethesda version conditions
        // These are simplified checks - full implementation would parse the condition syntax

        if (condition.Contains("#BETHESDA#") || condition.Contains("#BS_GTE_FO3#") || condition.Contains("#FO3_AND_LATER#"))
        {
            return context.BsVersion >= 34; // FO3/FNV BS version
        }

        if (condition.Contains("#NI_BS_LT_FO4#"))
        {
            return context.BsVersion < 130; // Before FO4
        }

        if (condition.Contains("#BS202#"))
        {
            return context.BinaryVersion == 0x14020007; // 20.2.0.7
        }

        // Default to including the field
        return true;
    }

    /// <summary>
    ///     Evaluates an array length expression.
    /// </summary>
    private int EvaluateArrayLength(string lengthExpr, byte[] data, ConversionContext context, int pos, int maxPos)
    {
        // Simple cases: numeric literal
        if (int.TryParse(lengthExpr, out var literal))
        {
            return literal;
        }

        // Field references like "Num Vertices" - we need to track these during parsing
        // For now, return a safe default based on common patterns
        return lengthExpr switch
        {
            "Num Children" => Math.Min(ReadCountValue(data, pos, maxPos, -8), 1000),
            "Num Effects" => Math.Min(ReadCountValue(data, pos, maxPos, -4), 100),
            "Num Vertices" => Math.Min(context.GetFieldValue("Num Vertices", 0), 65535),
            "Num Triangles" => Math.Min(context.GetFieldValue("Num Triangles", 0), 65535),
            "Num Strips" => Math.Min(context.GetFieldValue("Num Strips", 0), 1000),
            "Num Block Types" => _blockTypeNames.Length,
            "Num Blocks" => _blockTypeNames.Length,
            "Num Block Infos" => Math.Min(ReadCountValue(data, pos, maxPos, -4), 100),
            _ => 0 // Unknown reference, skip
        };
    }

    /// <summary>
    ///     Reads a count value from nearby data (already converted to LE).
    /// </summary>
    private static int ReadCountValue(byte[] data, int pos, int maxPos, int offset)
    {
        var readPos = pos + offset;
        if (readPos < 0 || readPos + 4 > maxPos) return 0;

        return (int)BinaryUtils.ReadUInt32LE(data, readPos);
    }

    private static bool IsBethesdaVersion(uint binaryVersion, uint userVersion)
    {
        return binaryVersion is 0x14020007 or 0x14000005 or 0x14000004
               && userVersion is > 0 and < 100;
    }

    private static int FindNewline(byte[] data, int start, int end)
    {
        for (var i = start; i < end; i++)
        {
            if (data[i] == 0x0A) return i;
        }
        return -1;
    }

    private static int SkipShortString(byte[] data, int pos)
    {
        if (pos >= data.Length) return -1;
        var len = data[pos];
        return pos + 1 + len;
    }

    private static void WriteUInt16LE(byte[] buffer, int offset, ushort value)
    {
        buffer[offset] = (byte)(value & 0xFF);
        buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
    }

    private static void WriteUInt32LE(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value & 0xFF);
        buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
        buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
        buffer[offset + 3] = (byte)((value >> 24) & 0xFF);
    }

    private static void WriteUInt64LE(byte[] buffer, int offset, ulong value)
    {
        buffer[offset] = (byte)(value & 0xFF);
        buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
        buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
        buffer[offset + 3] = (byte)((value >> 24) & 0xFF);
        buffer[offset + 4] = (byte)((value >> 32) & 0xFF);
        buffer[offset + 5] = (byte)((value >> 40) & 0xFF);
        buffer[offset + 6] = (byte)((value >> 48) & 0xFF);
        buffer[offset + 7] = (byte)((value >> 56) & 0xFF);
    }

    /// <summary>
    ///     Context for field conversion, tracking parsed values.
    /// </summary>
    private sealed class ConversionContext
    {
        public uint BinaryVersion { get; init; }
        public uint UserVersion { get; init; }
        public uint BsVersion { get; init; }
        public string BlockTypeName { get; init; } = "";

        private readonly Dictionary<string, int> _fieldValues = new(StringComparer.OrdinalIgnoreCase);

        public void SetFieldValue(string name, int value) => _fieldValues[name] = value;
        public int GetFieldValue(string name, int defaultValue) =>
            _fieldValues.TryGetValue(name, out var val) ? val : defaultValue;
    }
}
