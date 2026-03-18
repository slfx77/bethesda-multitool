namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Skinning;

/// <summary>
///     Parsed NiSkinInstance: links a skinned shape to its skeleton and NiSkinData.
/// </summary>
internal sealed class NifSkinInstanceData
{
    public int[] BoneRefs = [];
    public int DataRef;
    public int SkeletonRootRef;
    public int SkinPartitionRef;
}
