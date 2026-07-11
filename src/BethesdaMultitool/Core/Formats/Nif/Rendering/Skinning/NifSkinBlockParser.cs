using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Skinning;

/// <summary>
///     Parses skin-instance and skin-data blocks from NIF files.
/// </summary>
internal static class NifSkinBlockParser
{
    /// <summary>
    ///     Parse a NiTransform (rotation 3x3 + translation + scale) from raw bytes.
    /// </summary>
    internal static Matrix4x4 ParseNiTransform(byte[] data, int pos, bool be)
    {
        var rotation = new float[9];
        for (var i = 0; i < 9; i++)
        {
            rotation[i] = BinaryUtils.ReadFloat(data, pos + i * 4, be);
        }

        pos += 36;
        var tx = BinaryUtils.ReadFloat(data, pos, be);
        var ty = BinaryUtils.ReadFloat(data, pos + 4, be);
        var tz = BinaryUtils.ReadFloat(data, pos + 8, be);
        pos += 12;

        var scale = BinaryUtils.ReadFloat(data, pos, be);
        return new Matrix4x4(
            rotation[0] * scale,
            rotation[3] * scale,
            rotation[6] * scale,
            0,
            rotation[1] * scale,
            rotation[4] * scale,
            rotation[7] * scale,
            0,
            rotation[2] * scale,
            rotation[5] * scale,
            rotation[8] * scale,
            0,
            tx,
            ty,
            tz,
            1);
    }

    /// <summary>
    ///     Parse NiSkinInstance or BSDismemberSkinInstance to extract skeleton linkage. The Skin
    ///     Partition ref between Data and Skeleton Root exists only since 10.1.0.101
    ///     (<see cref="NifVersions.HasSkinInstancePartitionRef" />) — Morrowind's 4.0.0.2 layout goes
    ///     Data + Skeleton Root + Num Bones directly, and reading the ref there shifted the whole
    ///     bone list by one field.
    /// </summary>
    internal static NifSkinInstanceData? ParseNiSkinInstance(
        byte[] data,
        BlockInfo block,
        bool be,
        uint binaryVersion)
    {
        var hasPartitionRef = NifVersions.HasSkinInstancePartitionRef(binaryVersion);
        var pos = block.DataOffset;
        var end = block.DataOffset + block.Size;
        if (pos + (hasPartitionRef ? 16 : 12) > end)
        {
            return null;
        }

        var dataRef = BinaryUtils.ReadInt32(data, pos, be);
        pos += 4;
        var skinPartitionRef = -1;
        if (hasPartitionRef)
        {
            skinPartitionRef = BinaryUtils.ReadInt32(data, pos, be);
            pos += 4;
        }

        var skeletonRootRef = BinaryUtils.ReadInt32(data, pos, be);
        pos += 4;
        var numBones = BinaryUtils.ReadUInt32(data, pos, be);
        pos += 4;

        if (numBones > 500 || pos + numBones * 4 > end)
        {
            return null;
        }

        var boneRefs = new int[(int)numBones];
        for (var i = 0; i < numBones; i++)
        {
            boneRefs[i] = BinaryUtils.ReadInt32(data, pos, be);
            pos += 4;
        }

        return new NifSkinInstanceData
        {
            DataRef = dataRef,
            SkinPartitionRef = skinPartitionRef,
            SkeletonRootRef = skeletonRootRef,
            BoneRefs = boneRefs
        };
    }

    /// <summary>
    ///     Parse NiSkinData block: overall transform, per-bone inverse bind pose, and vertex weights.
    ///     Two version-gated fields (misreading either shifts the whole bone list): a Skin Partition
    ///     ref after Num Bones exists 4.0.0.2–10.1.0.0 (<see cref="NifVersions.HasSkinDataPartitionRef" />
    ///     — Morrowind HAS it; later versions moved it onto NiSkinInstance), and the Has Vertex Weights
    ///     byte exists only since 4.2.1.0 (<see cref="NifVersions.HasSkinDataVertexWeightsFlag" /> —
    ///     Morrowind has no byte and always stores weights).
    /// </summary>
    internal static NifSkinData? ParseNiSkinData(byte[] data, BlockInfo block, bool be, uint binaryVersion)
    {
        var pos = block.DataOffset;
        var end = block.DataOffset + block.Size;
        if (pos + 52 > end)
        {
            return null;
        }

        var overallTransform = ParseNiTransform(data, pos, be);
        pos += 52;

        if (pos + 4 > end)
        {
            return null;
        }

        var numBones = BinaryUtils.ReadUInt32(data, pos, be);
        pos += 4;
        if (numBones > 500)
        {
            return null;
        }

        if (NifVersions.HasSkinDataPartitionRef(binaryVersion))
        {
            if (pos + 4 > end)
            {
                return null;
            }

            pos += 4; // Skin Partition ref — linkage only, unused here
        }

        var hasVertexWeights = true;
        if (NifVersions.HasSkinDataVertexWeightsFlag(binaryVersion))
        {
            if (pos + 1 > end)
            {
                return null;
            }

            hasVertexWeights = data[pos] != 0;
            pos += 1;
        }

        var bones = new NifBoneSkinInfo[(int)numBones];
        for (var boneIndex = 0; boneIndex < numBones; boneIndex++)
        {
            if (pos + 70 > end)
            {
                return null;
            }

            var inverseBindPose = ParseNiTransform(data, pos, be);
            pos += 52;
            pos += 16;

            var numVertices = BinaryUtils.ReadUInt16(data, pos, be);
            pos += 2;

            (ushort VertexIndex, float Weight)[] vertexWeights;
            if (hasVertexWeights && numVertices > 0)
            {
                if (pos + numVertices * 6 > end)
                {
                    return null;
                }

                vertexWeights = new (ushort VertexIndex, float Weight)[numVertices];
                for (var vertexIndex = 0; vertexIndex < numVertices; vertexIndex++)
                {
                    vertexWeights[vertexIndex] = (
                        BinaryUtils.ReadUInt16(data, pos, be),
                        BinaryUtils.ReadFloat(data, pos + 2, be));
                    pos += 6;
                }
            }
            else
            {
                vertexWeights = [];
            }

            bones[boneIndex] = new NifBoneSkinInfo
            {
                InverseBindPose = inverseBindPose,
                VertexWeights = vertexWeights
            };
        }

        return new NifSkinData
        {
            OverallTransform = overallTransform,
            Bones = bones,
            HasVertexWeights = hasVertexWeights
        };
    }
}
