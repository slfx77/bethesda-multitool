using System.Numerics;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Skinning;

/// <summary>
///     Per-bone inverse bind pose and the vertices weighted to that bone.
/// </summary>
internal sealed class NifBoneSkinInfo
{
    public Matrix4x4 InverseBindPose;
    public (ushort VertexIndex, float Weight)[] VertexWeights = [];
}
