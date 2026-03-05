// NIF converter - Discovery validation and triangle extraction
// Extracts vertex maps, builds geometry-to-skin-partition mappings,
// and validates/updates geometry expansion sizes with triangle data.

using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Nif.Skinning;

namespace FalloutXbox360Utils.Core.Formats.Nif.Conversion;

/// <summary>
///     Handles skin partition vertex map extraction, geometry-to-skin-partition mapping,
///     triangle extraction from NiTriStripsData blocks, and geometry expansion updates.
/// </summary>
internal static class NifDiscoveryValidator
{
    private static readonly Logger Log = Logger.Instance;

    /// <summary>
    ///     Extract vertex maps from NiSkinPartition blocks for skinned meshes.
    /// </summary>
    internal static void ExtractVertexMaps(NifConversionState state, byte[] data, NifInfo info)
    {
        Log.Debug("  Extracting vertex maps from NiSkinPartition blocks...");

        // First, extract VertexMap and Triangles from all NiSkinPartition blocks
        ExtractFromSkinPartitionBlocks(state, data, info);

        // Build geometry -> skin partition mapping via BSDismemberSkinInstance
        BuildGeometryToSkinPartitionMapping(state, data, info);
    }

    private static void ExtractFromSkinPartitionBlocks(NifConversionState state, byte[] data, NifInfo info)
    {
        foreach (var block in info.Blocks.Where(b => b.TypeName == "NiSkinPartition"))
        {
            Log.Debug(
                $"    Checking block {block.Index}: NiSkinPartition at offset 0x{block.DataOffset:X}, size {block.Size}");

            ExtractVertexMapFromBlock(state, data, block, info.IsBigEndian);
            ExtractTrianglesFromBlock(state, data, block, info.IsBigEndian);
        }
    }

    private static void ExtractVertexMapFromBlock(NifConversionState state, byte[] data, BlockInfo block,
        bool isBigEndian)
    {
        var vertexMap = NifSkinPartitionParser.ExtractVertexMap(data, block.DataOffset, block.Size, isBigEndian);
        if (vertexMap is { Length: > 0 })
        {
            state.VertexMaps[block.Index] = vertexMap;
            Log.Debug($"    Block {block.Index}: NiSkinPartition - extracted {vertexMap.Length} vertex mappings");
        }
        else
        {
            Log.Debug($"    Block {block.Index}: NiSkinPartition - no vertex map found");
        }
    }

    private static void ExtractTrianglesFromBlock(NifConversionState state, byte[] data, BlockInfo block,
        bool isBigEndian)
    {
        var triangles = NifSkinPartitionParser.ExtractTriangles(data, block.DataOffset, block.Size, isBigEndian);
        if (triangles is { Length: > 0 })
        {
            state.SkinPartitionTriangles[block.Index] = triangles;
            Log.Debug(
                $"    Block {block.Index}: NiSkinPartition - extracted {triangles.Length / 3} triangles from strips");
        }
    }

    private static void BuildGeometryToSkinPartitionMapping(NifConversionState state, byte[] data, NifInfo info)
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

            TryMapGeometryToSkinPartition(state, data, block, skinInstanceBlock, info);
        }
    }

    private static void TryMapGeometryToSkinPartition(NifConversionState state, byte[] data,
        BlockInfo geometryBlock, BlockInfo skinInstanceBlock, NifInfo info)
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
        if (dataRef >= 0 && state.VertexMaps.ContainsKey(skinPartitionRef))
        {
            state.GeometryToSkinPartition[dataRef] = skinPartitionRef;
            Log.Debug($"  Mapped geometry block {dataRef} -> NiSkinPartition {skinPartitionRef}");
        }
    }

    /// <summary>
    ///     Update geometry expansion sizes to account for triangle data from NiSkinPartition.
    ///     Only applies to NiTriShapeData blocks - NiTriStripsData keeps its strip format.
    /// </summary>
    internal static void UpdateGeometryExpansionsWithTriangles(NifConversionState state)
    {
        foreach (var kvp in state.GeometryExpansions)
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
            if (state.GeometryToSkinPartition.TryGetValue(geomBlockIndex, out var skinPartIndex) &&
                state.SkinPartitionTriangles.TryGetValue(skinPartIndex, out var triangles))
            {
                var triangleBytes = triangles.Length * 2;
                expansion.NewSize += triangleBytes;
                expansion.SizeIncrease += triangleBytes;

                Log.Debug(
                    $"    Block {geomBlockIndex}: Adding {triangles.Length / 3} triangles ({triangleBytes} bytes) from skin partition {skinPartIndex}");
            }

            // Also check for NiTriStripsData triangles (non-skinned meshes)
            if (state.GeometryStripTriangles.TryGetValue(geomBlockIndex, out var stripTriangles))
            {
                Log.Debug(
                    $"    Block {geomBlockIndex}: Has {stripTriangles.Length / 3} triangles from NiTriStripsData strips");
            }
        }
    }

    /// <summary>
    ///     Extract triangle strips from NiTriStripsData blocks that have HasPoints=1.
    /// </summary>
    internal static void ExtractNiTriStripsDataTriangles(NifConversionState state, byte[] data, NifInfo info)
    {
        Log.Debug("  Extracting triangles from NiTriStripsData blocks...");

        foreach (var block in info.Blocks.Where(b => b.TypeName == "NiTriStripsData"))
        {
            // Skip if this geometry block has a skin partition (triangles come from there)
            if (state.GeometryToSkinPartition.ContainsKey(block.Index))
            {
                continue;
            }

            // Skip if not in our geometry expansions (no packed data to expand)
            if (!state.GeometryExpansions.ContainsKey(block.Index))
            {
                continue;
            }

            var triangles = NifTriStripExtractor.ExtractTrianglesFromTriStripsData(data, block, info.IsBigEndian);
            if (triangles is { Length: > 0 })
            {
                state.GeometryStripTriangles[block.Index] = triangles;
                Log.Debug(
                    $"    Block {block.Index}: NiTriStripsData - extracted {triangles.Length / 3} triangles from strips");
            }
        }
    }
}
