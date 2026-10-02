using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Starfield;

/// <summary>The scene structure of a Starfield <c>.mesh</c> document (<see cref="StarfieldMeshModelLayers.Build" />).</summary>
/// <param name="Nodes">Node 0 (mesh 0), then one node per LOD list.</param>
/// <param name="Meshes">Mesh 0 (the main index list), then one mesh per non-empty LOD list.</param>
/// <param name="LayerSets">The exclusive LOD group, or empty without LOD lists.</param>
/// <param name="Roots">Every node index, the scene's roots.</param>
internal sealed record StarfieldMeshModelLayerResult(
    IReadOnlyList<SceneNode> Nodes,
    IReadOnlyList<SceneMesh> Meshes,
    IReadOnlyList<SceneLayerSet> LayerSets,
    IReadOnlyList<int> Roots);
