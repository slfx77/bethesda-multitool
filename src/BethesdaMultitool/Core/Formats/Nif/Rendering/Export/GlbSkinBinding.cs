using System.Numerics;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Export;

internal sealed class GlbSkinBinding
{
    public required int[] JointNodeIndices { get; init; }

    public required Matrix4x4[] InverseBindMatrices { get; init; }

    public required (int BoneIdx, float Weight)[][] PerVertexInfluences { get; init; }
}
