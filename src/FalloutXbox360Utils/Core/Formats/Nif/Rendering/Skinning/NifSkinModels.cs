using System.Numerics;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Skinning;

/// <summary>
///     Parsed NiSkinInstance: links a skinned shape to its skeleton and NiSkinData.
/// </summary>
internal sealed class NifSkinInstanceData
{
    public int DataRef;
    public int SkinPartitionRef;
    public int SkeletonRootRef;
    public int[] BoneRefs = [];
}

/// <summary>
///     Parsed NiSkinData: overall skin transform plus per-bone inverse bind pose data.
/// </summary>
internal sealed class NifSkinData
{
    public Matrix4x4 OverallTransform;
    public NifBoneSkinInfo[] Bones = [];
    public bool HasVertexWeights;
}

/// <summary>
///     Per-bone inverse bind pose and the vertices weighted to that bone.
/// </summary>
internal sealed class NifBoneSkinInfo
{
    public Matrix4x4 InverseBindPose;
    public (ushort VertexIndex, float Weight)[] VertexWeights = [];
}
