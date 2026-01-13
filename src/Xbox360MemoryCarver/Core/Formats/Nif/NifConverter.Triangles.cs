// NIF converter - Triangle extraction methods
// Extracts vertex maps and triangles from skin partitions and strips

using System.Buffers.Binary;

namespace Xbox360MemoryCarver.Core.Formats.Nif;

internal sealed partial class NifConverter
{
    /// <summary>
    ///     Extract vertex maps from NiSkinPartition blocks for skinned meshes.
    /// </summary>
    private void ExtractVertexMaps(byte[] data, NifInfo info)
    {
        Log.Debug("  Extracting vertex maps from NiSkinPartition blocks...");

        // First, extract VertexMap and Triangles from all NiSkinPartition blocks
        foreach (var block in info.Blocks)
        {
            if (block.TypeName == "NiSkinPartition")
            {
                Log.Debug($"    Checking block {block.Index}: NiSkinPartition at offset 0x{block.DataOffset:X}, size {block.Size}");

                var vertexMap = NifSkinPartitionParser.ExtractVertexMap(
                    data, block.DataOffset, block.Size, info.IsBigEndian);
                if (vertexMap != null && vertexMap.Length > 0)
                {
                    _vertexMaps[block.Index] = vertexMap;
                    Log.Debug($"    Block {block.Index}: NiSkinPartition - extracted {vertexMap.Length} vertex mappings");
                }
                else
                {
                    Log.Debug($"    Block {block.Index}: NiSkinPartition - no vertex map found");
                }

                // Also extract triangles from strips for NiTriShapeData expansion
                var triangles = NifSkinPartitionParser.ExtractTriangles(
                    data, block.DataOffset, block.Size, info.IsBigEndian);
                if (triangles != null && triangles.Length > 0)
                {
                    _skinPartitionTriangles[block.Index] = triangles;
                    Log.Debug($"    Block {block.Index}: NiSkinPartition - extracted {triangles.Length / 3} triangles from strips");
                }
            }
        }

        // Now build geometry -> skin partition mapping via BSDismemberSkinInstance
        foreach (var block in info.Blocks.Where(b => b.TypeName is "NiTriShape" or "NiTriStrips"))
        {
            var skinInstanceRef = FindSkinInstanceRef(data, block, info);
            if (skinInstanceRef < 0)
            {
                continue;
            }

            var skinInstanceBlock = info.Blocks.FirstOrDefault(b => b.Index == skinInstanceRef);
            if (skinInstanceBlock?.TypeName is not ("BSDismemberSkinInstance" or "NiSkinInstance"))
            {
                continue;
            }

            // Read the skin partition ref from offset 4 in the skin instance
            var skinPartitionRefPos = skinInstanceBlock.DataOffset + 4;
            if (skinPartitionRefPos + 4 > data.Length)
            {
                continue;
            }

            var skinPartitionRef = info.IsBigEndian
                ? BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(skinPartitionRefPos, 4))
                : BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(skinPartitionRefPos, 4));

            if (skinPartitionRef < 0)
            {
                continue;
            }

            // Find the data ref in the NiTriShape
            var dataRef = FindDataRef(data, block, info);
            if (dataRef >= 0 && _vertexMaps.ContainsKey(skinPartitionRef))
            {
                _geometryToSkinPartition[dataRef] = skinPartitionRef;
                Log.Debug($"  Mapped geometry block {dataRef} -> NiSkinPartition {skinPartitionRef}");
            }
        }
    }

    /// <summary>
    ///     Update geometry expansion sizes to account for triangle data from NiSkinPartition.
    /// </summary>
    private void UpdateGeometryExpansionsWithTriangles()
    {
        foreach (var kvp in _geometryExpansions)
        {
            var geomBlockIndex = kvp.Key;
            var expansion = kvp.Value;

            // Check if this geometry block has triangles from its skin partition
            if (_geometryToSkinPartition.TryGetValue(geomBlockIndex, out var skinPartIndex) &&
                _skinPartitionTriangles.TryGetValue(skinPartIndex, out var triangles))
            {
                var triangleBytes = triangles.Length * 2;
                expansion.NewSize += triangleBytes;
                expansion.SizeIncrease += triangleBytes;

                Log.Debug($"    Block {geomBlockIndex}: Adding {triangles.Length / 3} triangles ({triangleBytes} bytes) from skin partition {skinPartIndex}");
            }

            // Also check for NiTriStripsData triangles (non-skinned meshes)
            if (_geometryStripTriangles.TryGetValue(geomBlockIndex, out var stripTriangles))
            {
                Log.Debug($"    Block {geomBlockIndex}: Has {stripTriangles.Length / 3} triangles from NiTriStripsData strips");
            }
        }
    }

    /// <summary>
    ///     Extract triangle strips from NiTriStripsData blocks that have HasPoints=1.
    /// </summary>
    private void ExtractNiTriStripsDataTriangles(byte[] data, NifInfo info)
    {
        Log.Debug("  Extracting triangles from NiTriStripsData blocks...");

        foreach (var block in info.Blocks.Where(b => b.TypeName == "NiTriStripsData"))
        {
            // Skip if this geometry block has a skin partition (triangles come from there)
            if (_geometryToSkinPartition.ContainsKey(block.Index))
            {
                continue;
            }

            // Skip if not in our geometry expansions (no packed data to expand)
            if (!_geometryExpansions.ContainsKey(block.Index))
            {
                continue;
            }

            var triangles = ExtractTrianglesFromTriStripsData(data, block, info.IsBigEndian);
            if (triangles != null && triangles.Length > 0)
            {
                _geometryStripTriangles[block.Index] = triangles;
                Log.Debug($"    Block {block.Index}: NiTriStripsData - extracted {triangles.Length / 3} triangles from strips");
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

        // Skip NiGeometryData common fields
        pos += 4; // GroupId

        if (pos + 2 > end) return null;
        pos += 2; // NumVertices

        pos += 2; // KeepFlags, CompressFlags

        if (pos + 1 > end) return null;
        var hasVertices = data[pos++];
        if (hasVertices != 0)
        {
            var numVerts = isBigEndian
                ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(block.DataOffset + 4, 2))
                : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(block.DataOffset + 4, 2));
            pos += numVerts * 12;
        }

        if (pos + 2 > end) return null;
        var bsDataFlags = isBigEndian
            ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos, 2))
            : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos, 2));
        pos += 2;

        if (pos + 1 > end) return null;
        var hasNormals = data[pos++];
        if (hasNormals != 0)
        {
            var numVerts = isBigEndian
                ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(block.DataOffset + 4, 2))
                : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(block.DataOffset + 4, 2));
            pos += numVerts * 12;
            if ((bsDataFlags & 4096) != 0)
            {
                pos += numVerts * 24;
            }
        }

        pos += 16; // BoundingSphere

        if (pos + 1 > end) return null;
        var hasVertexColors = data[pos++];
        if (hasVertexColors != 0)
        {
            var numVerts = isBigEndian
                ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(block.DataOffset + 4, 2))
                : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(block.DataOffset + 4, 2));
            pos += numVerts * 16;
        }

        var numUVSets = bsDataFlags & 1;
        if (numUVSets != 0)
        {
            var numVerts = isBigEndian
                ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(block.DataOffset + 4, 2))
                : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(block.DataOffset + 4, 2));
            pos += numVerts * 8;
        }

        pos += 2; // ConsistencyFlags
        pos += 4; // AdditionalData ref

        // Now at NiTriStripsData specific fields
        if (pos + 2 > end) return null;
        pos += 2; // NumTriangles

        if (pos + 2 > end) return null;
        var numStrips = isBigEndian
            ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos, 2))
            : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos, 2));
        pos += 2;

        if (numStrips == 0) return null;

        // StripLengths
        var stripLengths = new ushort[numStrips];
        for (var i = 0; i < numStrips; i++)
        {
            if (pos + 2 > end) return null;
            stripLengths[i] = isBigEndian
                ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos, 2))
                : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos, 2));
            pos += 2;
        }

        if (pos + 1 > end) return null;
        var hasPoints = data[pos++];
        if (hasPoints == 0) return null;

        // Read all strip indices
        var allStrips = new List<ushort[]>();
        for (var i = 0; i < numStrips; i++)
        {
            var stripLen = stripLengths[i];
            if (pos + stripLen * 2 > end) return null;

            var strip = new ushort[stripLen];
            for (var j = 0; j < stripLen; j++)
            {
                strip[j] = isBigEndian
                    ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos, 2))
                    : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos, 2));
                pos += 2;
            }

            allStrips.Add(strip);
        }

        return ConvertStripsToTriangles(allStrips);
    }

    /// <summary>
    ///     Convert triangle strips to explicit triangles.
    /// </summary>
    private static ushort[] ConvertStripsToTriangles(List<ushort[]> strips)
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
}
