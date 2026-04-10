using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Composition;

namespace FalloutXbox360Utils.CLI.Rendering.Npc;

internal sealed class NpcRenderCaches
{
    public NpcCompositionCaches Composition { get; } = new();

    public NpcRenderModelCache RenderModels { get; } = new();
}
