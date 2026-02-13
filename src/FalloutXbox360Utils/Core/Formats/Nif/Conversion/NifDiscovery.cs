// NIF converter - Discovery phase
// Combines parsing, triangle extraction, and geometry calculation methods
// that populate NifConversionState before the output-writing phase.

using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Nif.Geometry;
using FalloutXbox360Utils.Core.Formats.Nif.Skinning;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Nif.Conversion;

internal sealed class NifDiscovery(NifConversionState state)
{
    private static readonly Logger Log = Logger.Instance;

    private readonly NifConversionState _state = state;

    // ───────────────────────────────────────────────────────────────
    //  From NifConverter.Parsing.cs
    // ───────────────────────────────────────────────────────────────

    /// <summary>
    ///     Parse NiDefaultAVObjectPalette to extract node names.
    ///     Xbox 360 NIFs have NULL names on NiNode/BSFadeNode blocks, but the names
    ///     are preserved in the palette. We restore them by adding the names to the
    ///     string table and updating the Name field on node blocks.
    /// </summary>
    internal void ParseNodeNamesFromPalette(byte[] data, NifInfo info)
    {
        var nameMappings = NifPaletteParser.ParseAll(data, info);
        if (nameMappings.BlockNames.Count == 0)
        {
            return;
        }

        // Store original string count for index calculations
        _state.OriginalStringCount = info.Strings.Count;

        // Build a set of existing strings to avoid duplicates
        var existingStrings = new HashSet<string>(info.Strings);

        // For each block -> name mapping, determine if we need a new string
        foreach (var (blockIndex, name) in nameMappings.BlockNames)
        {
            // Check if this is a node block (NiNode, BSFadeNode, etc.)
            var typeName = info.GetBlockTypeName(blockIndex);
            if (!NifConverter.IsNodeType(typeName))
            {
                continue;
            }

            _state.NodeNamesByBlock[blockIndex] = name;
            _state.NodeNameStringIndices[blockIndex] = GetOrAddStringIndex(info, existingStrings, name);
        }

        // Handle Accum Root Name for the root BSFadeNode (block 0)
        ProcessAccumRootName(info, existingStrings, nameMappings);

        if (_state.NodeNamesByBlock.Count > 0)
        {
            Log.Debug(
                $"  Found {_state.NodeNamesByBlock.Count} node names from palette/sequence, adding {_state.NewStrings.Count} new strings");
        }
    }

    private int GetOrAddStringIndex(NifInfo info, HashSet<string> existingStrings, string name)
    {
        // Check if the name already exists in the string table
        var existingIndex = info.Strings.IndexOf(name);
        if (existingIndex >= 0)
        {
            return existingIndex;
        }

        // Already added as a new string - find its index
        if (existingStrings.Contains(name))
        {
            var newIdx = _state.NewStrings.IndexOf(name);
            if (newIdx >= 0)
            {
                return _state.OriginalStringCount + newIdx;
            }
        }

        // Need to add a new string
        var newIndex = _state.OriginalStringCount + _state.NewStrings.Count;
        _state.NewStrings.Add(name);
        existingStrings.Add(name);
        return newIndex;
    }

    private void ProcessAccumRootName(NifInfo info, HashSet<string> existingStrings, NifNameMappings nameMappings)
    {
        if (nameMappings.AccumRootName == null || _state.NodeNamesByBlock.ContainsKey(0))
        {
            return;
        }

        var rootTypeName = info.GetBlockTypeName(0);
        if (!NifConverter.IsNodeType(rootTypeName))
        {
            return;
        }

        var rootName = nameMappings.AccumRootName;
        _state.NodeNamesByBlock[0] = rootName;
        _state.NodeNameStringIndices[0] = GetOrAddStringIndex(info, existingStrings, rootName);

        Log.Debug($"  Root node (block 0) name from Accum Root Name: '{rootName}'");
    }

    /// <summary>
    ///     Find BSPackedAdditionalGeometryData blocks and extract their geometry data.
    /// </summary>
    internal void FindAndExtractPackedGeometry(byte[] data, NifInfo info)
    {
        foreach (var block in info.Blocks)
        {
            if (block.TypeName == "BSPackedAdditionalGeometryData")
            {
                _state.BlocksToStrip.Add(block.Index);

                var packedData = NifPackedDataExtractor.Extract(
                    data, block.DataOffset, block.Size, info.IsBigEndian);

                if (packedData != null)
                {
                    _state.PackedGeometryByBlock[block.Index] = packedData;
                    Log.Debug(
                        $"  Block {block.Index}: BSPackedAdditionalGeometryData - extracted {packedData.NumVertices} vertices");
                }
                else
                {
                    Log.Debug($"  Block {block.Index}: BSPackedAdditionalGeometryData - extraction failed");
                }
            }
        }
    }

    /// <summary>
    ///     Find geometry blocks (NiTriStripsData, NiTriShapeData) that reference packed data.
    /// </summary>
    internal void FindGeometryExpansions(byte[] data, NifInfo info)
    {
        foreach (var block in info.Blocks.Where(b => b.TypeName is "NiTriStripsData" or "NiTriShapeData"))
        {
            // Parse the geometry block to find its Additional Data reference
            var additionalDataRef = ParseAdditionalDataRef(data, block);

            if (additionalDataRef >= 0 && _state.PackedGeometryByBlock.TryGetValue(additionalDataRef, out var packedData))
            {
                // Check if this is a skinned mesh - affects vertex color handling
                var isSkinned = _state.GeometryToSkinPartition.ContainsKey(block.Index);

                // Calculate size increase needed for expanded geometry
                var expansion = CalculateGeometryExpansion(data, block, packedData, isSkinned);
                if (expansion != null)
                {
                    expansion.BlockIndex = block.Index;
                    expansion.PackedBlockIndex = additionalDataRef;
                    _state.GeometryExpansions[block.Index] = expansion;

                    Log.Debug(
                        $"  Block {block.Index}: {block.TypeName} -> expand from {expansion.OriginalSize} to {expansion.NewSize} bytes");
                }
            }
        }
    }

    /// <summary>
    ///     Find hkPackedNiTriStripsData blocks with compressed vertices that need expansion.
    /// </summary>
    internal void FindHavokExpansions(byte[] data, NifInfo info)
    {
        foreach (var block in info.Blocks)
        {
            if (block.TypeName == "hkPackedNiTriStripsData")
            {
                var expansion = NifRefFinders.ParseHavokBlock(data, block, info.IsBigEndian);
                if (expansion != null)
                {
                    _state.HavokExpansions[block.Index] = expansion;

                    Log.Debug(
                        $"  Block {block.Index}: hkPackedNiTriStripsData -> expand from {expansion.OriginalSize} to {expansion.NewSize} bytes ({expansion.NumVertices} vertices)");
                }
            }
        }
    }

    /// <summary>
    ///     Find NiSkinPartition blocks that need bone weights/indices expansion.
    /// </summary>
    internal void FindSkinPartitionExpansions(byte[] data, NifInfo info)
    {
        // Build mapping from NiSkinPartition block index -> packed geometry data
        foreach (var kvp in _state.GeometryExpansions)
        {
            var geomBlockIndex = kvp.Key;
            var packedBlockIndex = kvp.Value.PackedBlockIndex;

            if (!_state.GeometryToSkinPartition.TryGetValue(geomBlockIndex, out var skinPartitionIndex))
            {
                continue;
            }

            if (!_state.PackedGeometryByBlock.TryGetValue(packedBlockIndex, out var packedData))
            {
                continue;
            }

            if (packedData is { BoneIndices: not null, BoneWeights: not null })
            {
                _state.SkinPartitionToPackedData[skinPartitionIndex] = packedData;
            }
        }

        // Now find all NiSkinPartition blocks that need expansion
        foreach (var block in info.Blocks.Where(b => b.TypeName == "NiSkinPartition"))
        {
            if (!_state.SkinPartitionToPackedData.ContainsKey(block.Index))
            {
                continue;
            }

            var skinData = NifSkinPartitionExpander.Parse(data, block.DataOffset, block.Size, info.IsBigEndian);
            if (skinData == null)
            {
                continue;
            }

            var newSize = NifSkinPartitionExpander.CalculateExpandedSize(skinData);
            var sizeIncrease = newSize - block.Size;

            if (sizeIncrease > 0)
            {
                _state.SkinPartitionExpansions[block.Index] = new SkinPartitionExpansion
                {
                    BlockIndex = block.Index,
                    OriginalSize = block.Size,
                    NewSize = newSize,
                    ParsedData = skinData
                };

                Log.Debug(
                    $"  Block {block.Index}: NiSkinPartition -> expand from {block.Size} to {newSize} bytes (+{sizeIncrease} for bone weights/indices)");
            }
        }
    }

    /// <summary>
    ///     Parse a geometry block to find its Additional Data block reference.
    /// </summary>
    private static int ParseAdditionalDataRef(byte[] data, BlockInfo block)
    {
        var pos = block.DataOffset;
        var end = block.DataOffset + block.Size;

        // GroupId (int)
        pos += 4;

        // NumVertices (ushort)
        if (pos + 2 > end)
        {
            return -1;
        }

        var numVertices = BinaryUtils.ReadUInt16BE(data, pos);
        pos += 2;

        // KeepFlags, CompressFlags (bytes)
        pos += 2;

        // HasVertices (bool as byte)
        if (pos + 1 > end)
        {
            return -1;
        }

        var hasVertices = data[pos++];
        if (hasVertices != 0)
        {
            pos += numVertices * 12; // Vector3 * numVerts
        }

        // BSDataFlags (ushort)
        if (pos + 2 > end)
        {
            return -1;
        }

        var bsDataFlags = BinaryUtils.ReadUInt16BE(data, pos);
        pos += 2;

        // HasNormals (bool)
        if (pos + 1 > end)
        {
            return -1;
        }

        var hasNormals = data[pos++];
        if (hasNormals != 0)
        {
            pos += numVertices * 12; // Normals
            if ((bsDataFlags & 4096) != 0)
            {
                pos += numVertices * 24; // Tangents + Bitangents
            }
        }

        // BoundingSphere (16 bytes)
        pos += 16;

        // HasVertexColors (bool)
        if (pos + 1 > end)
        {
            return -1;
        }

        var hasVertexColors = data[pos++];
        if (hasVertexColors != 0)
        {
            pos += numVertices * 16; // Color4 * numVerts
        }

        // UV Sets based on BSDataFlags bit 0
        var numUVSets = bsDataFlags & 1;
        if (numUVSets != 0)
        {
            pos += numVertices * 8; // TexCoord * numVerts
        }

        // ConsistencyFlags (ushort)
        pos += 2;

        // AdditionalData (Ref = int)
        if (pos + 4 > end)
        {
            return -1;
        }

        var additionalDataRef = BinaryUtils.ReadInt32BE(data, pos);

        return additionalDataRef;
    }

    // ───────────────────────────────────────────────────────────────
    //  From NifConverter.Triangles.cs
    // ───────────────────────────────────────────────────────────────

    /// <summary>
    ///     Extract vertex maps from NiSkinPartition blocks for skinned meshes.
    /// </summary>
    internal void ExtractVertexMaps(byte[] data, NifInfo info)
    {
        Log.Debug("  Extracting vertex maps from NiSkinPartition blocks...");

        // First, extract VertexMap and Triangles from all NiSkinPartition blocks
        ExtractFromSkinPartitionBlocks(data, info);

        // Now build geometry -> skin partition mapping via BSDismemberSkinInstance
        BuildGeometryToSkinPartitionMapping(data, info);
    }

    private void ExtractFromSkinPartitionBlocks(byte[] data, NifInfo info)
    {
        foreach (var block in info.Blocks.Where(b => b.TypeName == "NiSkinPartition"))
        {
            Log.Debug(
                $"    Checking block {block.Index}: NiSkinPartition at offset 0x{block.DataOffset:X}, size {block.Size}");

            ExtractVertexMapFromBlock(data, block, info.IsBigEndian);
            ExtractTrianglesFromBlock(data, block, info.IsBigEndian);
        }
    }

    private void ExtractVertexMapFromBlock(byte[] data, BlockInfo block, bool isBigEndian)
    {
        var vertexMap = NifSkinPartitionParser.ExtractVertexMap(data, block.DataOffset, block.Size, isBigEndian);
        if (vertexMap is { Length: > 0 })
        {
            _state.VertexMaps[block.Index] = vertexMap;
            Log.Debug($"    Block {block.Index}: NiSkinPartition - extracted {vertexMap.Length} vertex mappings");
        }
        else
        {
            Log.Debug($"    Block {block.Index}: NiSkinPartition - no vertex map found");
        }
    }

    private void ExtractTrianglesFromBlock(byte[] data, BlockInfo block, bool isBigEndian)
    {
        var triangles = NifSkinPartitionParser.ExtractTriangles(data, block.DataOffset, block.Size, isBigEndian);
        if (triangles is { Length: > 0 })
        {
            _state.SkinPartitionTriangles[block.Index] = triangles;
            Log.Debug(
                $"    Block {block.Index}: NiSkinPartition - extracted {triangles.Length / 3} triangles from strips");
        }
    }

    private void BuildGeometryToSkinPartitionMapping(byte[] data, NifInfo info)
    {
        foreach (var block in info.Blocks.Where(b => b.TypeName is "NiTriShape" or "NiTriStrips"))
        {
            var skinInstanceRef = NifRefFinders.FindSkinInstanceRef(data, block, info);
            if (skinInstanceRef < 0)
            {
                continue;
            }

            var skinInstanceBlock = info.Blocks.FirstOrDefault(b => b.Index == skinInstanceRef);
            if (skinInstanceBlock?.TypeName is not ("BSDismemberSkinInstance" or "NiSkinInstance"))
            {
                continue;
            }

            TryMapGeometryToSkinPartition(data, block, skinInstanceBlock, info);
        }
    }

    private void TryMapGeometryToSkinPartition(byte[] data, BlockInfo geometryBlock, BlockInfo skinInstanceBlock,
        NifInfo info)
    {
        // Read the skin partition ref from offset 4 in the skin instance
        var skinPartitionRefPos = skinInstanceBlock.DataOffset + 4;
        if (skinPartitionRefPos + 4 > data.Length)
        {
            return;
        }

        var skinPartitionRef = info.IsBigEndian
            ? BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(skinPartitionRefPos, 4))
            : BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(skinPartitionRefPos, 4));

        if (skinPartitionRef < 0)
        {
            return;
        }

        // Find the data ref in the NiTriShape
        var dataRef = NifRefFinders.FindDataRef(data, geometryBlock, info);
        if (dataRef >= 0 && _state.VertexMaps.ContainsKey(skinPartitionRef))
        {
            _state.GeometryToSkinPartition[dataRef] = skinPartitionRef;
            Log.Debug($"  Mapped geometry block {dataRef} -> NiSkinPartition {skinPartitionRef}");
        }
    }

    /// <summary>
    ///     Update geometry expansion sizes to account for triangle data from NiSkinPartition.
    ///     Only applies to NiTriShapeData blocks - NiTriStripsData keeps its strip format.
    /// </summary>
    internal void UpdateGeometryExpansionsWithTriangles()
    {
        foreach (var kvp in _state.GeometryExpansions)
        {
            var geomBlockIndex = kvp.Key;
            var expansion = kvp.Value;

            // Only add triangle bytes for NiTriShapeData blocks
            // NiTriStripsData blocks use strip format and don't need triangle data added
            if (expansion.BlockTypeName != "NiTriShapeData")
            {
                continue;
            }

            // Check if this geometry block has triangles from its skin partition
            if (_state.GeometryToSkinPartition.TryGetValue(geomBlockIndex, out var skinPartIndex) &&
                _state.SkinPartitionTriangles.TryGetValue(skinPartIndex, out var triangles))
            {
                var triangleBytes = triangles.Length * 2;
                expansion.NewSize += triangleBytes;
                expansion.SizeIncrease += triangleBytes;

                Log.Debug(
                    $"    Block {geomBlockIndex}: Adding {triangles.Length / 3} triangles ({triangleBytes} bytes) from skin partition {skinPartIndex}");
            }

            // Also check for NiTriStripsData triangles (non-skinned meshes)
            if (_state.GeometryStripTriangles.TryGetValue(geomBlockIndex, out var stripTriangles))
            {
                Log.Debug(
                    $"    Block {geomBlockIndex}: Has {stripTriangles.Length / 3} triangles from NiTriStripsData strips");
            }
        }
    }

    /// <summary>
    ///     Extract triangle strips from NiTriStripsData blocks that have HasPoints=1.
    /// </summary>
    internal void ExtractNiTriStripsDataTriangles(byte[] data, NifInfo info)
    {
        Log.Debug("  Extracting triangles from NiTriStripsData blocks...");

        foreach (var block in info.Blocks.Where(b => b.TypeName == "NiTriStripsData"))
        {
            // Skip if this geometry block has a skin partition (triangles come from there)
            if (_state.GeometryToSkinPartition.ContainsKey(block.Index))
            {
                continue;
            }

            // Skip if not in our geometry expansions (no packed data to expand)
            if (!_state.GeometryExpansions.ContainsKey(block.Index))
            {
                continue;
            }

            var triangles = ExtractTrianglesFromTriStripsData(data, block, info.IsBigEndian);
            if (triangles is { Length: > 0 })
            {
                _state.GeometryStripTriangles[block.Index] = triangles;
                Log.Debug(
                    $"    Block {block.Index}: NiTriStripsData - extracted {triangles.Length / 3} triangles from strips");
            }
        }
    }

    /// <summary>
    ///     Extract triangles from a NiTriStripsData block.
    /// </summary>
    private static ushort[]? ExtractTrianglesFromTriStripsData(byte[] data, BlockInfo block, bool isBigEndian)
    {
        var pos = block.DataOffset;
        var end = block.DataOffset + block.Size;

        // Skip NiGeometryData common fields to get to strip data
        pos = SkipGeometryDataFields(data, pos, end, isBigEndian);
        if (pos < 0)
        {
            return null;
        }

        // Now at NiTriStripsData specific fields
        return ExtractStripsSection(data, pos, end, isBigEndian);
    }

    private static int SkipGeometryDataFields(byte[] data, int pos, int end, bool isBigEndian)
    {
        pos += 4; // GroupId

        if (pos + 2 > end)
        {
            return -1;
        }

        var numVerts = BinaryUtils.ReadUInt16(data, pos, isBigEndian);
        pos += 2;

        pos += 2; // KeepFlags, CompressFlags

        if (pos + 1 > end)
        {
            return -1;
        }

        var hasVertices = data[pos++];
        if (hasVertices != 0)
        {
            pos += numVerts * 12;
        }

        if (pos + 2 > end)
        {
            return -1;
        }

        var bsDataFlags = BinaryUtils.ReadUInt16(data, pos, isBigEndian);
        pos += 2;

        if (pos + 1 > end)
        {
            return -1;
        }

        var hasNormals = data[pos++];
        if (hasNormals != 0)
        {
            pos += numVerts * 12;
            if ((bsDataFlags & 4096) != 0)
            {
                pos += numVerts * 24;
            }
        }

        pos += 16; // BoundingSphere

        if (pos + 1 > end)
        {
            return -1;
        }

        var hasVertexColors = data[pos++];
        if (hasVertexColors != 0)
        {
            pos += numVerts * 16;
        }

        var numUVSets = bsDataFlags & 1;
        if (numUVSets != 0)
        {
            pos += numVerts * 8;
        }

        pos += 2; // ConsistencyFlags
        pos += 4; // AdditionalData ref

        return pos;
    }

    private static ushort[]? ExtractStripsSection(byte[] data, int pos, int end, bool isBigEndian)
    {
        if (pos + 2 > end)
        {
            return null;
        }

        pos += 2; // NumTriangles

        if (pos + 2 > end)
        {
            return null;
        }

        var numStrips = BinaryUtils.ReadUInt16(data, pos, isBigEndian);
        pos += 2;

        if (numStrips == 0)
        {
            return null;
        }

        // Read strip lengths
        var stripLengths = new ushort[numStrips];
        for (var i = 0; i < numStrips; i++)
        {
            if (pos + 2 > end)
            {
                return null;
            }

            stripLengths[i] = BinaryUtils.ReadUInt16(data, pos, isBigEndian);
            pos += 2;
        }

        if (pos + 1 > end)
        {
            return null;
        }

        var hasPoints = data[pos++];
        if (hasPoints == 0)
        {
            return null;
        }

        // Read all strip indices
        var allStrips = new List<ushort[]>();
        for (var i = 0; i < numStrips; i++)
        {
            var stripLen = stripLengths[i];
            if (pos + stripLen * 2 > end)
            {
                return null;
            }

            var strip = new ushort[stripLen];
            for (var j = 0; j < stripLen; j++)
            {
                strip[j] = BinaryUtils.ReadUInt16(data, pos, isBigEndian);
                pos += 2;
            }

            allStrips.Add(strip);
        }

        return ConvertStripsToTriangles(allStrips);
    }

    /// <summary>
    ///     Convert triangle strips to explicit triangles.
    /// </summary>
    internal static ushort[] ConvertStripsToTriangles(List<ushort[]> strips)
    {
        var triangles = new List<ushort>();

        foreach (var strip in strips)
        {
            if (strip.Length < 3)
            {
                continue;
            }

            for (var i = 0; i < strip.Length - 2; i++)
            {
                // Skip degenerate triangles
                if (strip[i] == strip[i + 1] || strip[i + 1] == strip[i + 2] || strip[i] == strip[i + 2])
                {
                    continue;
                }

                // Alternate winding order
                if ((i & 1) == 0)
                {
                    triangles.Add(strip[i]);
                    triangles.Add(strip[i + 1]);
                    triangles.Add(strip[i + 2]);
                }
                else
                {
                    triangles.Add(strip[i]);
                    triangles.Add(strip[i + 2]);
                    triangles.Add(strip[i + 1]);
                }
            }
        }

        return [.. triangles];
    }

    // ───────────────────────────────────────────────────────────────
    //  From NifConverter.Calculations.cs (static helpers only)
    // ───────────────────────────────────────────────────────────────

    /// <summary>
    ///     Calculate how much a geometry block needs to expand.
    /// </summary>
    private static GeometryBlockExpansion? CalculateGeometryExpansion(
        byte[] data, BlockInfo block, PackedGeometryData packedData, bool isSkinned = false)
    {
        var fields = ParseGeometryBlockFields(data, block);
        if (fields == null)
        {
            return null;
        }

        var sizeIncrease = CalculateSizeIncrease(fields.Value, packedData, isSkinned);
        if (sizeIncrease == 0)
        {
            return null;
        }

        return new GeometryBlockExpansion
        {
            OriginalSize = block.Size,
            NewSize = block.Size + sizeIncrease,
            SizeIncrease = sizeIncrease,
            BlockTypeName = block.TypeName
        };
    }

    private static GeometryBlockFields? ParseGeometryBlockFields(byte[] data, BlockInfo block)
    {
        var pos = block.DataOffset;
        var end = block.DataOffset + block.Size;

        pos += 4; // GroupId

        if (pos + 2 > end)
        {
            return null;
        }

        var numVertices = BinaryUtils.ReadUInt16BE(data, pos);
        pos += 2;

        pos += 2; // KeepFlags, CompressFlags

        if (pos + 1 > end)
        {
            return null;
        }

        var hasVertices = data[pos];
        pos += 1;

        if (hasVertices != 0)
        {
            pos += numVertices * 12;
        }

        if (pos + 2 > end)
        {
            return null;
        }

        var bsDataFlags = BinaryUtils.ReadUInt16BE(data, pos);
        pos += 2;

        if (pos + 1 > end)
        {
            return null;
        }

        var hasNormals = data[pos];
        pos += 1;

        if (hasNormals != 0)
        {
            pos += numVertices * 12;
            if ((bsDataFlags & 4096) != 0)
            {
                pos += numVertices * 24;
            }
        }

        pos += 16; // center + radius

        if (pos + 1 > end)
        {
            return null;
        }

        var hasVertexColors = data[pos];

        return new GeometryBlockFields(numVertices, bsDataFlags, hasVertices, hasNormals, hasVertexColors);
    }

    private static int CalculateSizeIncrease(GeometryBlockFields fields, PackedGeometryData packedData, bool isSkinned)
    {
        var sizeIncrease = 0;
        var numVertices = fields.NumVertices;

        if (fields.HasVertices == 0 && packedData.Positions != null)
        {
            sizeIncrease += numVertices * 12;
        }

        if (fields.HasNormals == 0 && packedData.Normals != null)
        {
            sizeIncrease += numVertices * 12;
            if (packedData.Tangents != null)
            {
                sizeIncrease += numVertices * 12;
            }

            if (packedData.Bitangents != null)
            {
                sizeIncrease += numVertices * 12;
            }
        }

        // Vertex colors: skip for skinned meshes (ubyte4 is bone indices)
        if (fields.HasVertexColors == 0 && packedData.VertexColors != null && !isSkinned)
        {
            sizeIncrease += numVertices * 16;
        }

        var numUVSets = fields.BsDataFlags & 1;
        if (numUVSets == 0 && packedData.UVs != null)
        {
            sizeIncrease += numVertices * 8;
        }

        return sizeIncrease;
    }

    internal readonly record struct GeometryBlockFields(
        ushort NumVertices,
        ushort BsDataFlags,
        byte HasVertices,
        byte HasNormals,
        byte HasVertexColors);
}
