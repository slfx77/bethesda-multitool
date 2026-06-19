using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Composition;

namespace BethesdaMultitool.CLI.Rendering.Npc;

internal sealed class NpcRenderCaches
{
    public NpcCompositionCaches Composition { get; } = new();

    public NpcRenderModelCache RenderModels { get; } = new();
}
