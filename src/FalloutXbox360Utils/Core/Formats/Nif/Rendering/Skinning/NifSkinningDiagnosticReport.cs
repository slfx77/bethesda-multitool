namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Skinning;

internal sealed class NifSkinningDiagnosticReport
{
    public required IReadOnlyList<NifSkinnedShapeDiagnostic> Shapes { get; init; }

    public int SkinnedShapeCount => Shapes.Count;
}
