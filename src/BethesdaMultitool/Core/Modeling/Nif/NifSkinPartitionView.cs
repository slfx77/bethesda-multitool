using System.Globalization;
using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Formats.Nif.Decoding;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     A typed view of one SkinPartition (nif.xml:6681-6781) of a PC-layout NiSkinPartition at 20.2.0.7 and BS 34 or
///     below (no LOD Level, Global VB or SSE vertex data): its bones (indices into the skin's joint palette), vertex map
///     (partition vertex to shape vertex), weights and bone indices per partition vertex, and its triangles in partition
///     vertex indices, triangulated from strips by <see cref="NifStripTriangulator" /> when the partition is stripped.
/// </summary>
/// <remarks>
///     <see cref="ReadAll" /> checks every cross-block index (plan section 1, self-check 6): partition bones below the
///     skin's bone count, vertex-map entries below the geometry's Num Vertices, bone indices below the partition's Num
///     Bones, triangle and strip points below the partition's Num Vertices, and array lengths that agree with the counts.
///     A violation is corrupt input and throws <see cref="InvalidDataException" /> naming the block, partition and field.
/// </remarks>
internal sealed class NifSkinPartitionView
{
    private NifSkinPartitionView(int ordinal, int vertexCount, int weightsPerVertex, ushort[] bones,
        ushort[]? vertexMap, float[][]? weights, byte[][]? boneIndices, NifTriangulation? triangles, int stripCount)
    {
        Ordinal = ordinal;
        VertexCount = vertexCount;
        WeightsPerVertex = weightsPerVertex;
        Bones = bones;
        VertexMap = vertexMap;
        Weights = weights;
        BoneIndices = boneIndices;
        Triangles = triangles;
        StripCount = stripCount;
    }

    /// <summary>The partition's position in the NiSkinPartition (and in a dismember body-part list).</summary>
    public int Ordinal { get; }

    /// <summary>The partition's Num Vertices.</summary>
    public int VertexCount { get; }

    /// <summary>The stored Num Weights Per Vertex.</summary>
    public int WeightsPerVertex { get; }

    /// <summary>The partition's Bones: joint-palette indices, in stored order.</summary>
    public IReadOnlyList<ushort> Bones { get; }

    /// <summary>The vertex map (partition vertex to shape vertex), or null when Has Vertex Map is 0.</summary>
    public IReadOnlyList<ushort>? VertexMap { get; }

    /// <summary>The weights per partition vertex (Num Weights Per Vertex each), or null when Has Vertex Weights is 0.</summary>
    public IReadOnlyList<float[]>? Weights { get; }

    /// <summary>The bone indices (into <see cref="Bones" />) per partition vertex, or null when Has Bone Indices is 0.</summary>
    public IReadOnlyList<byte[]>? BoneIndices { get; }

    /// <summary>The kept triangles in partition vertex indices, or null when the partition stores no faces.</summary>
    public NifTriangulation? Triangles { get; }

    /// <summary>The stored Num Strips (0 for a triangle list).</summary>
    public int StripCount { get; }

    /// <summary>Reads and checks every partition of one NiSkinPartition block that decoded exactly.</summary>
    /// <param name="block">The NiSkinPartition block.</param>
    /// <param name="skinBoneCount">The skin's bone count (the joint-palette size).</param>
    /// <param name="shapeVertexCount">The skinned geometry's Num Vertices.</param>
    /// <exception cref="InvalidDataException">A field is missing or an index is out of range (see the type remarks).</exception>
    public static IReadOnlyList<NifSkinPartitionView> ReadAll(NifDecodedBlock block, int skinBoneCount,
        int shapeVertexCount)
    {
        ArgumentNullException.ThrowIfNull(block);
        if (!block.Root.TryGet("Partitions", out var value) || value is not NifArrayValue { IsRows: false } array)
        {
            throw Invalid(block, -1, "Partitions", "did not decode as a list of partitions");
        }

        var partitions = new NifSkinPartitionView[array.Count];
        for (var p = 0; p < partitions.Length; p++)
        {
            if (array.Items[p] is not NifStructValue partition)
            {
                throw Invalid(block, p, "Partitions", "is not a SkinPartition struct");
            }

            partitions[p] = ReadOne(block, partition, p, skinBoneCount, shapeVertexCount);
        }

        return partitions;
    }

    /// <summary>The partition's native summary (counts and flags; the stored arrays are in the block row).</summary>
    public JsonObject ToJson()
    {
        return new JsonObject
        {
            ["ordinal"] = Ordinal,
            ["numVertices"] = VertexCount,
            ["weightsPerVertex"] = WeightsPerVertex,
            ["bones"] = NifModelNativeValues.Integers(Bones.Select(b => (int)b).ToList()),
            ["hasVertexMap"] = VertexMap is not null,
            ["hasVertexWeights"] = Weights is not null,
            ["hasBoneIndices"] = BoneIndices is not null,
            ["numStrips"] = StripCount,
            ["storedTriangles"] = Triangles?.StoredTriangles ?? 0,
            ["keptTriangles"] = Triangles?.KeptTriangles ?? 0,
            ["droppedRepeatedIndex"] = Triangles?.DroppedTriangles ?? 0
        };
    }

    private static NifSkinPartitionView ReadOne(NifDecodedBlock block, NifStructValue partition, int ordinal,
        int skinBoneCount, int shapeVertexCount)
    {
        var vertexCount = Integer(block, partition, ordinal, "Num Vertices");
        var boneCount = Integer(block, partition, ordinal, "Num Bones");
        var stripCount = Integer(block, partition, ordinal, "Num Strips");
        var weightsPerVertex = Integer(block, partition, ordinal, "Num Weights Per Vertex");

        var bones = UInt16s(block, partition, ordinal, "Bones", boneCount);
        for (var i = 0; i < bones.Length; i++)
        {
            if (bones[i] >= skinBoneCount)
            {
                throw Invalid(block, ordinal, "Bones", string.Create(CultureInfo.InvariantCulture,
                    $"entry {i} = {bones[i]} is not below the skin's {skinBoneCount} bones"));
            }
        }

        ushort[]? vertexMap = null;
        if (Flag(partition, "Has Vertex Map"))
        {
            vertexMap = UInt16s(block, partition, ordinal, "Vertex Map", vertexCount);
            for (var i = 0; i < vertexMap.Length; i++)
            {
                if (vertexMap[i] >= shapeVertexCount)
                {
                    throw Invalid(block, ordinal, "Vertex Map", string.Create(CultureInfo.InvariantCulture,
                        $"entry {i} = {vertexMap[i]} is not below the geometry's Num Vertices ({shapeVertexCount})"));
                }
            }
        }

        float[][]? weights = null;
        if (Flag(partition, "Has Vertex Weights"))
        {
            weights = FloatRows(block, partition, ordinal, "Vertex Weights", vertexCount, weightsPerVertex);
        }

        byte[][]? boneIndices = null;
        if (Flag(partition, "Has Bone Indices"))
        {
            boneIndices = ByteRows(block, partition, ordinal, "Bone Indices", vertexCount, weightsPerVertex);
            for (var v = 0; v < boneIndices.Length; v++)
            {
                for (var k = 0; k < boneIndices[v].Length; k++)
                {
                    if (boneIndices[v][k] >= boneCount)
                    {
                        throw Invalid(block, ordinal, "Bone Indices", string.Create(CultureInfo.InvariantCulture,
                            $"vertex {v} slot {k} = {boneIndices[v][k]} is not below the partition's {boneCount} bones"));
                    }
                }
            }
        }

        var triangles = ReadTriangles(block, partition, ordinal, vertexCount, stripCount);
        return new NifSkinPartitionView(ordinal, vertexCount, weightsPerVertex, bones, vertexMap, weights,
            boneIndices, triangles, stripCount);
    }

    private static NifTriangulation? ReadTriangles(NifDecodedBlock block, NifStructValue partition, int ordinal,
        int vertexCount, int stripCount)
    {
        if (stripCount > 0)
        {
            if (!partition.TryGet("Strips", out var stripsValue))
            {
                return null;
            }

            if (stripsValue is not NifArrayValue { IsRows: true } rows || rows.Count != stripCount)
            {
                throw Invalid(block, ordinal, "Strips", "did not decode as one row per strip");
            }

            var strips = new List<ushort[]>(rows.Count);
            foreach (var row in rows.Items)
            {
                if (row is not NifUInt16ArrayValue { ComponentsPerElement: 1 } points)
                {
                    throw Invalid(block, ordinal, "Strips", "a strip did not decode as ushort points");
                }

                var copy = points.Values.ToArray();
                RequireBelow(block, ordinal, "Strips", copy, vertexCount);
                strips.Add(copy);
            }

            return NifStripTriangulator.Triangulate(strips);
        }

        if (!partition.TryGet("Triangles", out var trianglesValue))
        {
            return null;
        }

        if (trianglesValue is not NifUInt16ArrayValue { ComponentsPerElement: 3 } triangles)
        {
            throw Invalid(block, ordinal, "Triangles", "did not decode as a Triangle array");
        }

        RequireBelow(block, ordinal, "Triangles", triangles.Values.ToArray(), vertexCount);
        return NifStripTriangulator.FilterList(triangles.Values);
    }

    private static void RequireBelow(NifDecodedBlock block, int ordinal, string field, ushort[] values, int limit)
    {
        for (var i = 0; i < values.Length; i++)
        {
            if (values[i] >= limit)
            {
                throw Invalid(block, ordinal, field, string.Create(CultureInfo.InvariantCulture,
                    $"point {i} = {values[i]} is not below the partition's Num Vertices ({limit})"));
            }
        }
    }

    private static bool Flag(NifStructValue partition, string field)
    {
        return partition.TryGet(field, out var value) && value is NifIntegerValue { Value: not 0 };
    }

    private static int Integer(NifDecodedBlock block, NifStructValue partition, int ordinal, string field)
    {
        return partition.TryGet(field, out var value) && value is NifIntegerValue integer
            ? checked((int)integer.Value)
            : throw Invalid(block, ordinal, field, "did not decode as an integer");
    }

    private static ushort[] UInt16s(NifDecodedBlock block, NifStructValue partition, int ordinal, string field,
        int count)
    {
        if (!partition.TryGet(field, out var value) ||
            value is not NifUInt16ArrayValue { ComponentsPerElement: 1 } array || array.Count != count)
        {
            throw Invalid(block, ordinal, field, string.Create(CultureInfo.InvariantCulture,
                $"did not decode as {count} ushort values"));
        }

        return array.Values.ToArray();
    }

    private static float[][] FloatRows(NifDecodedBlock block, NifStructValue partition, int ordinal, string field,
        int rows, int width)
    {
        if (!partition.TryGet(field, out var value) || value is not NifArrayValue array || array.Count != rows ||
            (!array.IsRows && rows != 0))
        {
            throw Invalid(block, ordinal, field, string.Create(CultureInfo.InvariantCulture,
                $"did not decode as {rows} rows of {width} floats"));
        }

        var result = new float[rows][];
        for (var r = 0; r < rows; r++)
        {
            if (array.Items[r] is not NifFloatArrayValue { ComponentsPerElement: 1 } row || row.Count != width)
            {
                throw Invalid(block, ordinal, field, string.Create(CultureInfo.InvariantCulture,
                    $"row {r} did not decode as {width} floats"));
            }

            result[r] = row.ToSingleArray();
        }

        return result;
    }

    private static byte[][] ByteRows(NifDecodedBlock block, NifStructValue partition, int ordinal, string field,
        int rows, int width)
    {
        if (!partition.TryGet(field, out var value) || value is not NifArrayValue array || array.Count != rows ||
            (!array.IsRows && rows != 0))
        {
            throw Invalid(block, ordinal, field, string.Create(CultureInfo.InvariantCulture,
                $"did not decode as {rows} rows of {width} bytes"));
        }

        var result = new byte[rows][];
        for (var r = 0; r < rows; r++)
        {
            if (array.Items[r] is not NifByteArrayValue row || row.Count != width)
            {
                throw Invalid(block, ordinal, field, string.Create(CultureInfo.InvariantCulture,
                    $"row {r} did not decode as {width} bytes"));
            }

            result[r] = row.Bytes.ToArray();
        }

        return result;
    }

    private static InvalidDataException Invalid(NifDecodedBlock block, int ordinal, string field, string detail)
    {
        var where = ordinal < 0
            ? $"'{field}'"
            : string.Create(CultureInfo.InvariantCulture, $"partition {ordinal} '{field}'");
        return new InvalidDataException($"NIF block {block.Index} ({block.Type}) {where} {detail}.");
    }
}
