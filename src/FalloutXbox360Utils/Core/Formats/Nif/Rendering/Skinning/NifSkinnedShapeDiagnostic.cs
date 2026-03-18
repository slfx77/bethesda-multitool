namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Skinning;

internal sealed class NifSkinnedShapeDiagnostic
{
    public required string ShapeName { get; init; }
    public required int ShapeIndex { get; init; }
    public required int VertexCount { get; init; }
    public required int BoneRefCount { get; init; }
    public required bool UsesNiSkinDataVertexWeights { get; init; }
    public required bool HasExpandedPartitionData { get; init; }
    public required bool HasNonIdentityOverallTransform { get; init; }
    public required int VerticesWithInfluences { get; init; }
    public required int VerticesWithMultipleInfluences { get; init; }
    public required int MaxInfluencesPerVertex { get; init; }
    public required int PartitionCount { get; init; }
    public required int PartitionsWithExpandedData { get; init; }
    public required int PartitionsMissingExpandedData { get; init; }

    public bool AllPartitionsExpanded => PartitionCount > 0 && PartitionsMissingExpandedData == 0;
}
