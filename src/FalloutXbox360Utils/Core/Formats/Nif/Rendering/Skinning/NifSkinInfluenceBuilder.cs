using FalloutXbox360Utils.Core.Formats.Nif.Skinning;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Skinning;

/// <summary>
///     Builds per-vertex influence tables from NiSkinData or NiSkinPartition data.
/// </summary>
internal static class NifSkinInfluenceBuilder
{
    internal static (int BoneIdx, float Weight)[][] BuildPerVertexInfluences(
        NifSkinData skinData,
        int numVertices)
    {
        var influences = new List<(int BoneIdx, float Weight)>[numVertices];
        for (var vertexIndex = 0; vertexIndex < numVertices; vertexIndex++)
        {
            influences[vertexIndex] = new List<(int BoneIdx, float Weight)>(4);
        }

        for (var boneIndex = 0; boneIndex < skinData.Bones.Length; boneIndex++)
        {
            foreach (var (vertexIndex, weight) in skinData.Bones[boneIndex].VertexWeights)
            {
                if (vertexIndex < numVertices && weight > 0.0001f)
                {
                    influences[vertexIndex].Add((boneIndex, weight));
                }
            }
        }

        var result = new (int BoneIdx, float Weight)[numVertices][];
        for (var vertexIndex = 0; vertexIndex < numVertices; vertexIndex++)
        {
            var entries = influences[vertexIndex];
            if (entries.Count == 0)
            {
                result[vertexIndex] = [];
                continue;
            }

            entries.Sort((a, b) => b.Weight.CompareTo(a.Weight));
            var count = Math.Min(entries.Count, 4);
            var vertexInfluences = new (int BoneIdx, float Weight)[count];
            var totalWeight = 0f;

            for (var i = 0; i < count; i++)
            {
                vertexInfluences[i] = entries[i];
                totalWeight += vertexInfluences[i].Weight;
            }

            if (totalWeight > 0.001f && Math.Abs(totalWeight - 1f) > 0.01f)
            {
                for (var i = 0; i < count; i++)
                {
                    vertexInfluences[i].Weight /= totalWeight;
                }
            }

            result[vertexIndex] = vertexInfluences;
        }

        return result;
    }

    internal static (int BoneIdx, float Weight)[][]? BuildPerVertexInfluencesFromPartitions(
        byte[] data,
        NifInfo nif,
        NifSkinInstanceData skinInstance,
        int numVertices,
        out bool hasExpandedData)
    {
        hasExpandedData = false;
        if (skinInstance.SkinPartitionRef < 0 ||
            skinInstance.SkinPartitionRef >= nif.Blocks.Count)
        {
            return null;
        }

        var partitionBlock = nif.Blocks[skinInstance.SkinPartitionRef];
        if (partitionBlock.TypeName != "NiSkinPartition")
        {
            return null;
        }

        var partData = NifSkinPartitionExpander.Parse(
            data,
            partitionBlock.DataOffset,
            partitionBlock.Size,
            nif.IsBigEndian);
        if (partData == null || partData.Partitions.Count == 0)
        {
            return null;
        }

        var result = new (int BoneIdx, float Weight)[numVertices][];
        for (var vertexIndex = 0; vertexIndex < numVertices; vertexIndex++)
        {
            result[vertexIndex] = [];
        }

        foreach (var partition in partData.Partitions)
        {
            if (partition.Bones.Length == 0 ||
                !partition.HasVertexMap ||
                partition.VertexMap.Length == 0)
            {
                continue;
            }

            if (partition.HasVertexWeights &&
                partition.HasBoneIndices &&
                partition.VertexWeights != null &&
                partition.BoneIndices != null)
            {
                hasExpandedData = true;
                PopulateExpandedPartitionInfluences(partition, numVertices, result);
                continue;
            }

            PopulateFallbackPartitionInfluences(
                partition,
                numVertices,
                skinInstance,
                result);
        }

        return result;
    }

    private static void PopulateExpandedPartitionInfluences(
        NifSkinPartitionExpander.PartitionInfo partition,
        int numVertices,
        (int BoneIdx, float Weight)[][] result)
    {
        for (var partitionVertex = 0; partitionVertex < partition.NumVertices; partitionVertex++)
        {
            if (partitionVertex >= partition.VertexMap.Length)
            {
                break;
            }

            var globalVertexIndex = partition.VertexMap[partitionVertex];
            if (globalVertexIndex >= numVertices)
            {
                continue;
            }

            var influences = new List<(int BoneIdx, float Weight)>(
                partition.NumWeightsPerVertex);
            var totalWeight = 0f;

            for (var weightIndex = 0;
                 weightIndex < partition.NumWeightsPerVertex;
                 weightIndex++)
            {
                var weight = partition.VertexWeights![partitionVertex, weightIndex];
                if (weight <= 0.0001f)
                {
                    continue;
                }

                var partitionLocalBone = partition.BoneIndices![partitionVertex, weightIndex];
                if (partitionLocalBone >= partition.Bones.Length)
                {
                    continue;
                }

                var globalBoneIndex = partition.Bones[partitionLocalBone];
                influences.Add((globalBoneIndex, weight));
                totalWeight += weight;
            }

            if (totalWeight > 0f && Math.Abs(totalWeight - 1f) > 0.001f)
            {
                for (var i = 0; i < influences.Count; i++)
                {
                    influences[i] = (
                        influences[i].Item1,
                        influences[i].Item2 / totalWeight);
                }
            }

            if (influences.Count > 0)
            {
                result[globalVertexIndex] = influences.ToArray();
            }
        }
    }

    private static void PopulateFallbackPartitionInfluences(
        NifSkinPartitionExpander.PartitionInfo partition,
        int numVertices,
        NifSkinInstanceData skinInstance,
        (int BoneIdx, float Weight)[][] result)
    {
        var primaryBone = partition.Bones[0];
        if (primaryBone >= skinInstance.BoneRefs.Length)
        {
            return;
        }

        foreach (var globalVertexIndex in partition.VertexMap)
        {
            if (globalVertexIndex < numVertices)
            {
                result[globalVertexIndex] = [(primaryBone, 1.0f)];
            }
        }
    }
}
