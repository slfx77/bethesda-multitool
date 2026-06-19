using System.Numerics;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Export;

/// <summary>Skinning data for a GLB mesh part: the joint node set, inverse bind matrices, and per-vertex bone influences.</summary>
internal sealed class GlbSkinBinding
{
    public required int[] JointNodeIndices { get; init; }

    public required Matrix4x4[] InverseBindMatrices { get; init; }

    public required (int BoneIdx, float Weight)[][] PerVertexInfluences { get; init; }
}
