using System.Numerics;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Skinning;

/// <summary>
///     Parsed NiSkinData: overall skin transform plus per-bone inverse bind pose data.
/// </summary>
internal sealed class NifSkinData
{
    public NifBoneSkinInfo[] Bones = [];
    public bool HasVertexWeights;
    public Matrix4x4 OverallTransform;
}
