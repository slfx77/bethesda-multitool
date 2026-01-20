// NIF converter - Parsing and discovery methods
// Methods for finding packed data, geometry expansions, and skin partitions

namespace Xbox360MemoryCarver.Core.Formats.Nif;

internal sealed partial class NifConverter
{
    /// <summary>
    ///     Parse NiDefaultAVObjectPalette to extract node names.
    ///     Xbox 360 NIFs have NULL names on NiNode/BSFadeNode blocks, but the names
    ///     are preserved in the palette. We restore them by adding the names to the
    ///     string table and updating the Name field on node blocks.
    /// </summary>
    private void ParseNodeNamesFromPalette(byte[] data, NifInfo info)
    {
        var nameMappings = NifPaletteParser.ParseAll(data, info);
        if (nameMappings.BlockNames.Count == 0)
        {
            return;
        }

        // Store original string count for index calculations
        _originalStringCount = info.Strings.Count;

        // Build a set of existing strings to avoid duplicates
        var existingStrings = new HashSet<string>(info.Strings);

        // For each block -> name mapping, determine if we need a new string
        foreach (var (blockIndex, name) in nameMappings.BlockNames)
        {
            // Check if this is a node block (NiNode, BSFadeNode, etc.)
            var typeName = info.GetBlockTypeName(blockIndex);
            if (!IsNodeType(typeName))
            {
                continue;
            }

            _nodeNamesByBlock[blockIndex] = name;
            _nodeNameStringIndices[blockIndex] = GetOrAddStringIndex(info, existingStrings, name);
        }

        // Handle Accum Root Name for the root BSFadeNode (block 0)
        ProcessAccumRootName(info, existingStrings, nameMappings);

        if (_nodeNamesByBlock.Count > 0)
        {
            Log.Debug(
                $"  Found {_nodeNamesByBlock.Count} node names from palette/sequence, adding {_newStrings.Count} new strings");
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
            var newIdx = _newStrings.IndexOf(name);
            if (newIdx >= 0)
            {
                return _originalStringCount + newIdx;
            }
        }

        // Need to add a new string
        var newIndex = _originalStringCount + _newStrings.Count;
        _newStrings.Add(name);
        existingStrings.Add(name);
        return newIndex;
    }

    private void ProcessAccumRootName(NifInfo info, HashSet<string> existingStrings, NifNameMappings nameMappings)
    {
        if (nameMappings.AccumRootName == null || _nodeNamesByBlock.ContainsKey(0))
        {
            return;
        }

        var rootTypeName = info.GetBlockTypeName(0);
        if (!IsNodeType(rootTypeName))
        {
            return;
        }

        var rootName = nameMappings.AccumRootName;
        _nodeNamesByBlock[0] = rootName;
        _nodeNameStringIndices[0] = GetOrAddStringIndex(info, existingStrings, rootName);

        Log.Debug($"  Root node (block 0) name from Accum Root Name: '{rootName}'");
    }

    /// <summary>
    ///     Find BSPackedAdditionalGeometryData blocks and extract their geometry data.
    /// </summary>
    private void FindAndExtractPackedGeometry(byte[] data, NifInfo info)
    {
        foreach (var block in info.Blocks)
        {
            if (block.TypeName == "BSPackedAdditionalGeometryData")
            {
                _blocksToStrip.Add(block.Index);

                var packedData = NifPackedDataExtractor.Extract(
                    data, block.DataOffset, block.Size, info.IsBigEndian);

                if (packedData != null)
                {
                    _packedGeometryByBlock[block.Index] = packedData;
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
    private void FindGeometryExpansions(byte[] data, NifInfo info)
    {
        foreach (var block in info.Blocks.Where(b => b.TypeName is "NiTriStripsData" or "NiTriShapeData"))
        {
            // Parse the geometry block to find its Additional Data reference
            var additionalDataRef = ParseAdditionalDataRef(data, block);

            if (additionalDataRef >= 0 && _packedGeometryByBlock.TryGetValue(additionalDataRef, out var packedData))
            {
                // Check if this is a skinned mesh - affects vertex color handling
                var isSkinned = _geometryToSkinPartition.ContainsKey(block.Index);

                // Calculate size increase needed for expanded geometry
                var expansion = CalculateGeometryExpansion(data, block, packedData, isSkinned);
                if (expansion != null)
                {
                    expansion.BlockIndex = block.Index;
                    expansion.PackedBlockIndex = additionalDataRef;
                    _geometryExpansions[block.Index] = expansion;

                    Log.Debug(
                        $"  Block {block.Index}: {block.TypeName} -> expand from {expansion.OriginalSize} to {expansion.NewSize} bytes");
                }
            }
        }
    }

    /// <summary>
    ///     Find hkPackedNiTriStripsData blocks with compressed vertices that need expansion.
    /// </summary>
    private void FindHavokExpansions(byte[] data, NifInfo info)
    {
        foreach (var block in info.Blocks)
        {
            if (block.TypeName == "hkPackedNiTriStripsData")
            {
                var expansion = ParseHavokBlock(data, block, info.IsBigEndian);
                if (expansion != null)
                {
                    _havokExpansions[block.Index] = expansion;

                    Log.Debug(
                        $"  Block {block.Index}: hkPackedNiTriStripsData -> expand from {expansion.OriginalSize} to {expansion.NewSize} bytes ({expansion.NumVertices} vertices)");
                }
            }
        }
    }

    /// <summary>
    ///     Find NiSkinPartition blocks that need bone weights/indices expansion.
    /// </summary>
    private void FindSkinPartitionExpansions(byte[] data, NifInfo info)
    {
        // Build mapping from NiSkinPartition block index -> packed geometry data
        foreach (var kvp in _geometryExpansions)
        {
            var geomBlockIndex = kvp.Key;
            var packedBlockIndex = kvp.Value.PackedBlockIndex;

            if (!_geometryToSkinPartition.TryGetValue(geomBlockIndex, out var skinPartitionIndex))
            {
                continue;
            }

            if (!_packedGeometryByBlock.TryGetValue(packedBlockIndex, out var packedData))
            {
                continue;
            }

            if (packedData is { BoneIndices: not null, BoneWeights: not null })
            {
                _skinPartitionToPackedData[skinPartitionIndex] = packedData;
            }
        }

        // Now find all NiSkinPartition blocks that need expansion
        foreach (var block in info.Blocks.Where(b => b.TypeName == "NiSkinPartition"))
        {
            if (!_skinPartitionToPackedData.ContainsKey(block.Index))
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
                _skinPartitionExpansions[block.Index] = new SkinPartitionExpansion
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

        var numVertices = ReadUInt16BE(data, pos);
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

        var bsDataFlags = ReadUInt16BE(data, pos);
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

        var additionalDataRef = ReadInt32BE(data, pos);

        return additionalDataRef;
    }
}
