namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Skinning;

/// <summary>Skinning diagnostics for a whole NIF: the per-shape reports for every skinned shape it contains.</summary>
internal sealed class NifSkinningDiagnosticReport
{
    public required IReadOnlyList<NifSkinnedShapeDiagnostic> Shapes { get; init; }

    public int SkinnedShapeCount => Shapes.Count;
}
