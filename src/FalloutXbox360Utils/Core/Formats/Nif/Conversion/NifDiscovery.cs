// NIF converter - Discovery phase
// Combines parsing, triangle extraction, and geometry calculation methods
// that populate NifConversionState before the output-writing phase.

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

            if (additionalDataRef >= 0 &&
                _state.PackedGeometryByBlock.TryGetValue(additionalDataRef, out var packedData))
            {
                // Check if this is a skinned mesh - affects vertex color handling
                var isSkinned = _state.GeometryToSkinPartition.ContainsKey(block.Index);

                // Calculate size increase needed for expanded geometry
                var expansion = NifGeometryCalculator.CalculateGeometryExpansion(data, block, packedData, isSkinned);
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

        // Find all NiSkinPartition blocks that need expansion
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
    //  Delegated to NifDiscoveryValidator
    // ───────────────────────────────────────────────────────────────

    /// <summary>
    ///     Extract vertex maps from NiSkinPartition blocks for skinned meshes.
    /// </summary>
    internal void ExtractVertexMaps(byte[] data, NifInfo info)
    {
        NifDiscoveryValidator.ExtractVertexMaps(_state, data, info);
    }

    /// <summary>
    ///     Update geometry expansion sizes to account for triangle data from NiSkinPartition.
    ///     Only applies to NiTriShapeData blocks - NiTriStripsData keeps its strip format.
    /// </summary>
    internal void UpdateGeometryExpansionsWithTriangles()
    {
        NifDiscoveryValidator.UpdateGeometryExpansionsWithTriangles(_state);
    }

    /// <summary>
    ///     Extract triangle strips from NiTriStripsData blocks that have HasPoints=1.
    /// </summary>
    internal void ExtractNiTriStripsDataTriangles(byte[] data, NifInfo info)
    {
        NifDiscoveryValidator.ExtractNiTriStripsDataTriangles(_state, data, info);
    }
}
