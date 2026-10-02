using BethesdaMultitool.Core.Assets;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc;
using BethesdaMultitool.Core.WorldData;

namespace BethesdaMultitool;

/// <summary>Shared declared BMT source order for world meshes, actors and textures.</summary>
internal static class WorldDataBsaPathResolver
{
    internal static BsaDiscoveryResult DiscoverSources(WorldViewData data) =>
        data.AssetSnapshot.Sources;

    internal static string[] DiscoverTextureBsaPaths(WorldViewData data) =>
        DiscoverSources(data).TexturePlan?.Mounts.Select(m => m.Path).ToArray() ?? [];
}
