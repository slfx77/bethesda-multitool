namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Composition;

internal sealed class NpcRenderModelCache
{
    public NpcRenderModelCache()
        : this(new Dictionary<string, NifRenderableModel?>(StringComparer.OrdinalIgnoreCase))
    {
    }

    public NpcRenderModelCache(Dictionary<string, NifRenderableModel?> headMeshes)
    {
        HeadMeshes = headMeshes;
    }

    public Dictionary<string, NifRenderableModel?> HeadMeshes { get; }
}
