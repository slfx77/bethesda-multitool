using System.Numerics;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Export;

internal sealed class NpcExportSkinBinding
{
    public required int[] JointNodeIndices { get; init; }

    public required Matrix4x4[] InverseBindMatrices { get; init; }

    public required (int BoneIdx, float Weight)[][] PerVertexInfluences { get; init; }
}
