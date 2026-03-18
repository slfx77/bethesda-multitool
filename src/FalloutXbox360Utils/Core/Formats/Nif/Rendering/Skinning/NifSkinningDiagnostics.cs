using System.Numerics;
using FalloutXbox360Utils.Core.Formats.Nif.Skinning;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Skinning;

/// <summary>
///     Inspects converted skinned NIFs so regression tests can assert that
///     partition data and runtime influence tables are materially correct.
/// </summary>
internal static class NifSkinningDiagnostics
{
    internal static NifSkinningDiagnosticReport Inspect(byte[] data, NifInfo nif)
    {
        var nodeChildren = new Dictionary<int, List<int>>();
        var shapeDataMap = new Dictionary<int, int>();
        var shapePropertyMap = new Dictionary<int, List<int>>();
        var shapeSkinInstanceMap = new Dictionary<int, int>();

        NifSceneGraphWalker.ClassifyBlocks(
            data,
            nif,
            nodeChildren,
            shapeDataMap,
            shapePropertyMap,
            shapeSkinInstanceMap);

        var shapes = new List<NifSkinnedShapeDiagnostic>();
        foreach (var (shapeIndex, dataIndex) in shapeDataMap.OrderBy(entry => entry.Key))
        {
            if (!shapeSkinInstanceMap.TryGetValue(shapeIndex, out var skinInstanceIndex))
            {
                continue;
            }

            var diagnostic = InspectShape(data, nif, shapeIndex, dataIndex, skinInstanceIndex);
            if (diagnostic != null)
            {
                shapes.Add(diagnostic);
            }
        }

        return new NifSkinningDiagnosticReport
        {
            Shapes = shapes
        };
    }

    private static NifSkinnedShapeDiagnostic? InspectShape(
        byte[] data,
        NifInfo nif,
        int shapeIndex,
        int dataIndex,
        int skinInstanceIndex)
    {
        var skinInstance = NifSkinBlockParser.ParseNiSkinInstance(
            data,
            nif.Blocks[skinInstanceIndex],
            nif.IsBigEndian);
        if (skinInstance == null ||
            skinInstance.DataRef < 0 ||
            skinInstance.DataRef >= nif.Blocks.Count ||
            nif.Blocks[skinInstance.DataRef].TypeName != "NiSkinData")
        {
            return null;
        }

        var skinData = NifSkinBlockParser.ParseNiSkinData(
            data,
            nif.Blocks[skinInstance.DataRef],
            nif.IsBigEndian);
        if (skinData == null || skinData.Bones.Length == 0)
        {
            return null;
        }

        var vertexCount = NifBlockParsers.ReadVertexCount(data, nif.Blocks[dataIndex], nif.IsBigEndian);
        if (vertexCount <= 0)
        {
            return null;
        }

        var influences = skinData.HasVertexWeights
            ? NifSkinInfluenceBuilder.BuildPerVertexInfluences(skinData, vertexCount)
            : NifSkinInfluenceBuilder.BuildPerVertexInfluencesFromPartitions(
                data,
                nif,
                skinInstance,
                vertexCount,
                out _);
        var partitions = TryParsePartitions(data, nif, skinInstance);
        var partitionsWithExpandedData =
            partitions?.Count(partition => partition.HasVertexWeights && partition.HasBoneIndices) ?? 0;
        var partitionCount = partitions?.Count ?? 0;
        var partitionsMissingExpandedData = partitionCount - partitionsWithExpandedData;

        var verticesWithInfluences = 0;
        var verticesWithMultipleInfluences = 0;
        var maxInfluencesPerVertex = 0;
        if (influences != null)
        {
            foreach (var vertexInfluences in influences)
            {
                var influenceCount = vertexInfluences.Length;
                if (influenceCount > 0)
                {
                    verticesWithInfluences++;
                }

                if (influenceCount > 1)
                {
                    verticesWithMultipleInfluences++;
                }

                if (influenceCount > maxInfluencesPerVertex)
                {
                    maxInfluencesPerVertex = influenceCount;
                }
            }
        }

        var shapeName = NifBlockParsers.ReadBlockName(data, nif.Blocks[shapeIndex], nif) ?? $"Shape_{shapeIndex}";

        return new NifSkinnedShapeDiagnostic
        {
            ShapeName = shapeName,
            ShapeIndex = shapeIndex,
            VertexCount = vertexCount,
            BoneRefCount = skinInstance.BoneRefs.Length,
            UsesNiSkinDataVertexWeights = skinData.HasVertexWeights,
            HasExpandedPartitionData = partitionsWithExpandedData > 0,
            HasNonIdentityOverallTransform = !IsNearlyIdentity(skinData.OverallTransform),
            VerticesWithInfluences = verticesWithInfluences,
            VerticesWithMultipleInfluences = verticesWithMultipleInfluences,
            MaxInfluencesPerVertex = maxInfluencesPerVertex,
            PartitionCount = partitionCount,
            PartitionsWithExpandedData = partitionsWithExpandedData,
            PartitionsMissingExpandedData = partitionsMissingExpandedData
        };
    }

    private static List<NifSkinPartitionExpander.PartitionInfo>? TryParsePartitions(
        byte[] data,
        NifInfo nif,
        NifSkinInstanceData skinInstance)
    {
        if (skinInstance.SkinPartitionRef < 0 || skinInstance.SkinPartitionRef >= nif.Blocks.Count)
        {
            return null;
        }

        var partitionBlock = nif.Blocks[skinInstance.SkinPartitionRef];
        if (partitionBlock.TypeName != "NiSkinPartition")
        {
            return null;
        }

        return NifSkinPartitionExpander.Parse(
            data,
            partitionBlock.DataOffset,
            partitionBlock.Size,
            nif.IsBigEndian)?.Partitions;
    }

    private static bool IsNearlyIdentity(Matrix4x4 matrix)
    {
        return MathF.Abs(matrix.M11 - 1f) < 0.0001f &&
               MathF.Abs(matrix.M22 - 1f) < 0.0001f &&
               MathF.Abs(matrix.M33 - 1f) < 0.0001f &&
               MathF.Abs(matrix.M44 - 1f) < 0.0001f &&
               MathF.Abs(matrix.M12) < 0.0001f &&
               MathF.Abs(matrix.M13) < 0.0001f &&
               MathF.Abs(matrix.M14) < 0.0001f &&
               MathF.Abs(matrix.M21) < 0.0001f &&
               MathF.Abs(matrix.M23) < 0.0001f &&
               MathF.Abs(matrix.M24) < 0.0001f &&
               MathF.Abs(matrix.M31) < 0.0001f &&
               MathF.Abs(matrix.M32) < 0.0001f &&
               MathF.Abs(matrix.M34) < 0.0001f &&
               MathF.Abs(matrix.M41) < 0.0001f &&
               MathF.Abs(matrix.M42) < 0.0001f &&
               MathF.Abs(matrix.M43) < 0.0001f;
    }
}
