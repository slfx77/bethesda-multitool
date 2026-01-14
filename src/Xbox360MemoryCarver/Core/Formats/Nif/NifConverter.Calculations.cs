// NIF converter - Size and remap calculations

namespace Xbox360MemoryCarver.Core.Formats.Nif;

internal sealed partial class NifConverter
{
    /// <summary>
    ///     Calculate how much a geometry block needs to expand.
    /// </summary>
    private static GeometryBlockExpansion? CalculateGeometryExpansion(
        byte[] data, BlockInfo block, PackedGeometryData packedData, bool isSkinned = false)
    {
        var fields = ParseGeometryBlockFields(data, block);
        if (fields == null) return null;

        var sizeIncrease = CalculateSizeIncrease(fields.Value, packedData, isSkinned);
        if (sizeIncrease == 0) return null;

        return new GeometryBlockExpansion
        {
            OriginalSize = block.Size,
            NewSize = block.Size + sizeIncrease,
            SizeIncrease = sizeIncrease
        };
    }

    private static GeometryBlockFields? ParseGeometryBlockFields(byte[] data, BlockInfo block)
    {
        var pos = block.DataOffset;
        var end = block.DataOffset + block.Size;

        pos += 4; // GroupId

        if (pos + 2 > end) return null;
        var numVertices = ReadUInt16BE(data, pos);
        pos += 2;

        pos += 2; // KeepFlags, CompressFlags

        if (pos + 1 > end) return null;
        var hasVertices = data[pos];
        pos += 1;

        if (hasVertices != 0) pos += numVertices * 12;

        if (pos + 2 > end) return null;
        var bsDataFlags = ReadUInt16BE(data, pos);
        pos += 2;

        if (pos + 1 > end) return null;
        var hasNormals = data[pos];
        pos += 1;

        if (hasNormals != 0)
        {
            pos += numVertices * 12;
            if ((bsDataFlags & 4096) != 0) pos += numVertices * 24;
        }

        pos += 16; // center + radius

        if (pos + 1 > end) return null;
        var hasVertexColors = data[pos];

        return new GeometryBlockFields(numVertices, bsDataFlags, hasVertices, hasNormals, hasVertexColors);
    }

    private static int CalculateSizeIncrease(GeometryBlockFields fields, PackedGeometryData packedData, bool isSkinned)
    {
        var sizeIncrease = 0;
        var numVertices = fields.NumVertices;

        if (fields.HasVertices == 0 && packedData.Positions != null) sizeIncrease += numVertices * 12;

        if (fields.HasNormals == 0 && packedData.Normals != null)
        {
            sizeIncrease += numVertices * 12;
            if (packedData.Tangents != null) sizeIncrease += numVertices * 12;
            if (packedData.Bitangents != null) sizeIncrease += numVertices * 12;
        }

        // Vertex colors: skip for skinned meshes (ubyte4 is bone indices)
        if (fields.HasVertexColors == 0 && packedData.VertexColors != null && !isSkinned)
            sizeIncrease += numVertices * 16;

        var numUVSets = fields.BsDataFlags & 1;
        if (numUVSets == 0 && packedData.UVs != null) sizeIncrease += numVertices * 8;

        return sizeIncrease;
    }

    /// <summary>
    ///     Calculate block index remapping after removing packed blocks.
    /// </summary>
    private int[] CalculateBlockRemap(int blockCount)
    {
        var remap = new int[blockCount];
        var newIndex = 0;

        for (var i = 0; i < blockCount; i++)
            if (_blocksToStrip.Contains(i))
                remap[i] = -1;
            else
                remap[i] = newIndex++;

        return remap;
    }

    /// <summary>
    ///     Calculate total output size accounting for removed and expanded blocks.
    /// </summary>
    private int CalculateOutputSize(int originalSize, NifInfo info)
    {
        var size = originalSize;

        Log.Debug($"  Size calculation: starting from {originalSize}");

        // Add new strings for node names
        foreach (var str in _newStrings)
        {
            size += 4 + str.Length;
            Log.Debug($"    + New string '{str}': {4 + str.Length} bytes");
        }

        // Subtract removed blocks
        foreach (var blockIdx in _blocksToStrip)
        {
            var block = info.Blocks[blockIdx];
            size -= block.Size;
            size -= 4; // Block size entry in header
            size -= 2; // Block type index entry in header
            Log.Debug($"    - Remove block {blockIdx}: {block.Size} + 6 header bytes");
        }

        // Add geometry expansion sizes
        foreach (var kvp in _geometryExpansions)
        {
            size += kvp.Value.SizeIncrease;
            Log.Debug($"    + Expand geometry block {kvp.Key}: {kvp.Value.SizeIncrease} bytes");
        }

        // Add Havok expansion sizes
        foreach (var kvp in _havokExpansions)
        {
            size += kvp.Value.SizeIncrease;
            Log.Debug($"    + Expand Havok block {kvp.Key}: {kvp.Value.SizeIncrease} bytes");
        }

        // Add skin partition expansion sizes
        foreach (var kvp in _skinPartitionExpansions)
        {
            size += kvp.Value.SizeIncrease;
            Log.Debug($"    + Expand NiSkinPartition block {kvp.Key}: {kvp.Value.SizeIncrease} bytes");
        }

        Log.Debug($"  Final calculated size: {size}");

        return size;
    }

    private readonly record struct GeometryBlockFields(
        ushort NumVertices,
        ushort BsDataFlags,
        byte HasVertices,
        byte HasNormals,
        byte HasVertexColors);
}
