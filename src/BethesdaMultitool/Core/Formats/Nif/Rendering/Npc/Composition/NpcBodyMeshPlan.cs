namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Composition;

/// <summary>One body/equipment mesh to attach when composing an NPC: its mesh path and optional texture override.</summary>
internal sealed class NpcBodyMeshPlan
{
    public required string MeshPath { get; init; }

    public string? TextureOverride { get; init; }

    public int RenderOrder { get; init; }
}
