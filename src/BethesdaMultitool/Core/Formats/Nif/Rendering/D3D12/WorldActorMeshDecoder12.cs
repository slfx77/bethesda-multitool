using BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12.Viewer;
using BethesdaMultitool.Core.WorldData;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;

/// <summary>CPU-only static actor assembly in model space; placement transforms remain on the draw item.</summary>
internal static class WorldActorMeshDecoder12
{
    internal static DecodedNifMesh12 Decode(WorldActorScene actor, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var decoded = BethesdaViewerSceneDecoder12.Decode(actor.Scene);
        cancellationToken.ThrowIfCancellationRequested();
        var posed = BethesdaViewerScenePoseMaterializer12.Materialize(decoded);
        cancellationToken.ThrowIfCancellationRequested();
        return posed.Mesh with
        {
            GeneratedTextures = decoded.GeneratedTextures,
            ActorProvenance = new WorldActorMeshProvenance(actor.Actor, decoded.SourceLabel, actor.MeshSources,
                decoded.TextureSourcePaths, decoded.MeshParts.Select(part => part.NativeSemantics.SourceNifPath)
                    .OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                posed.Warnings.Concat(posed.UnsupportedMeshParts.Select(part => $"{part.Name}: {part.Reason}")).ToArray(),
                actor.Scene.AssetReadReceipts, actor.Scene.AssetUses)
        };
    }
}

internal sealed record WorldActorMeshProvenance(WorldActorDefinition Actor, string Appearance,
    IReadOnlyList<string> MeshSources, IReadOnlyList<string> TextureSources,
    IReadOnlyList<string> MeshPaths, IReadOnlyList<string> Warnings,
    IReadOnlyList<BethesdaMultitool.Core.Assets.AssetSelectionReceipt>? AssetReadReceipts = null,
    BethesdaMultitool.Core.Assets.AssetUseGraph? AssetUses = null);
