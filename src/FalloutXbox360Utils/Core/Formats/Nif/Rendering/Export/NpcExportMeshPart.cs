namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Export;

internal sealed class NpcExportMeshPart
{
    public required string Name { get; init; }

    public int? NodeIndex { get; init; }

    public required RenderableSubmesh Submesh { get; init; }

    public NpcExportSkinBinding? Skin { get; init; }
}