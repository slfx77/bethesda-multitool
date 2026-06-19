namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Export;

/// <summary>One mesh primitive in a GLB scene: a renderable submesh attached to a node, optionally skinned.</summary>
internal sealed class GlbMeshPart
{
    public required string Name { get; init; }

    public int? NodeIndex { get; init; }

    public required RenderableSubmesh Submesh { get; init; }

    public GlbSkinBinding? Skin { get; init; }
}
