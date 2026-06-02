namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Export;

internal sealed class GlbMeshPart
{
    public required string Name { get; init; }

    public int? NodeIndex { get; init; }

    public required RenderableSubmesh Submesh { get; init; }

    public GlbSkinBinding? Skin { get; init; }
}
