using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Resources;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Composition;

/// <summary>
///     Decoded head-mesh reuse cache for NPC render/export pipelines. Bounded (the head meshes of
///     sustained NPC browsing previously accumulated without limit); least-recently-used heads are
///     dropped and re-decoded on demand. Null values are cached negatives (heads that failed to
///     build), so a broken NIF is not re-parsed per NPC.
/// </summary>
internal sealed class NpcRenderModelCache
{
    private const int MaxHeadMeshes = 64;

    public NpcRenderModelCache()
        : this(CreateHeadMeshCache())
    {
    }

    public NpcRenderModelCache(LruCache<string, NifRenderableModel?> headMeshes)
    {
        HeadMeshes = headMeshes;
    }

    public LruCache<string, NifRenderableModel?> HeadMeshes { get; }

    /// <summary>The standard bounded head-mesh cache (callers that thread a cache through builders).</summary>
    public static LruCache<string, NifRenderableModel?> CreateHeadMeshCache()
    {
        return new LruCache<string, NifRenderableModel?>(
            "NpcHeadMeshCache",
            ResourceCategory.CpuCache,
            MaxHeadMeshes,
            comparer: StringComparer.OrdinalIgnoreCase);
    }
}
