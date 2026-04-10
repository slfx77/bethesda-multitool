namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Composition;

internal sealed class NpcBodyMeshPlan
{
    public required string MeshPath { get; init; }

    public string? TextureOverride { get; init; }

    public int RenderOrder { get; init; }
}