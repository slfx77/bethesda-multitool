using System.Globalization;
using System.Numerics;
using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Shadowkey;

/// <summary>The node structure of a Shadowkey mesh document (<see cref="ShadowkeyMeshModelLayers.Build" />).</summary>
/// <param name="Nodes">One node per skin; node k carries mesh k.</param>
/// <param name="Roots">The scene roots: every node (each skin is its own root).</param>
/// <param name="LayerSets">The exclusive skin group, or empty for one skin.</param>
/// <param name="ClipNodes">
///     The nodes the clips drive: only the default-on skin's node (see <see cref="ShadowkeyMeshModelLayers" /> remarks).
/// </param>
internal sealed record ShadowkeyMeshModelLayerResult(
    IReadOnlyList<SceneNode> Nodes,
    IReadOnlyList<int> Roots,
    IReadOnlyList<SceneLayerSet> LayerSets,
    IReadOnlyList<int> ClipNodes);

/// <summary>
///     The skin alternatives of a Shadowkey mesh document (cut-2 plan section 3.4, decision D4). Faces carry no texture
///     index, so a record's extra skins are whole-mesh alternatives (19 retail records carry 2 to 19, 319 skins in all;
///     the entity table or a script picks one). Node k carries mesh k, whose primitive binds skin k's material; every
///     node is a scene root with the identity transform (as the Starfield reader's LOD nodes are). With one skin, node 0
///     is named after the record and there are no layer sets. With several, node k is named
///     <c>&lt;record&gt;.skinNN</c> and the nodes form layer sets <c>skinNN</c> in the exclusive group
///     <see cref="ExclusiveGroup" />, <c>skin00</c> on by default. The per-skin primitives are rebuilt over the same
///     vertex and index arrays and share the morph target objects by reference.
/// </summary>
/// <remarks>
///     <para>
///         GLB writes the default-on member and reports the others (Shared <c>ModelGltfLayers</c>,
///         <c>ModelGltfDrawSelection</c>); Blender makes one collection per skin. A mesh's primitives are drawn together,
///         so skins inside one mesh would draw every skin at once; layer sets are the contract's alternative-selection
///         vocabulary (as the Starfield reader's LODs).
///     </para>
///     <para>
///         ⚠ Clips drive only <see cref="DefaultSkin" />'s node (<see cref="ShadowkeyMeshModelLayerResult.ClipNodes" />;
///         cut-2 review finding 1, option (a), pending the owner's ruling on D4). At Shared 2e7af70 the GLB draw selection
///         refuses any morph track whose node the layer selection leaves undrawn (<c>ModelGltfDrawSelection.Plan</c>,
///         <c>draw.morph-target-suppressed</c>, and the writer then throws), so a track on every skin node made every
///         multi-skin record (all 19 are animated) unwritable to GLB in Default mode, and a shared Transform root made
///         All mode unwritable too (<c>layers.shared-hierarchy-unsupported</c>). With the tracks on skin 0 and every skin
///         a scene root, Default and All write; an Explicit selection of another skin is still refused, and the
///         alternatives carry the shared frame targets but no clip drives them, so they hold frame 0 in both writers
///         (diagnostic <see cref="ShadowkeyModelDiagnostics.SkinSelection" />). The alternatives are option (b), skin 0
///         as the only mesh with the other skins as images, unreferenced materials and native state, and option (c), a
///         Shared change letting draw selection drop the tracks of unselected exclusive-group members with a row.
///     </para>
/// </remarks>
internal static class ShadowkeyMeshModelLayers
{
    /// <summary>The source kind of the skin layer sets.</summary>
    public const string SourceKind = "bmt.shadowkey.skin";

    /// <summary>The exclusive group every skin layer set belongs to.</summary>
    public const string ExclusiveGroup = "shadowkey.skin";

    /// <summary>The skin shown by default and the only one the clips drive.</summary>
    public const int DefaultSkin = 0;

    /// <summary>The identity transform every node carries.</summary>
    public static SceneTrs Identity { get; } = new(Vector3.Zero, Quaternion.Identity, Vector3.One);

    /// <summary>The layer-set id of skin <paramref name="skin" />.</summary>
    public static string SkinId(int skin)
    {
        return string.Create(CultureInfo.InvariantCulture, $"skin{skin:00}");
    }

    /// <summary>The node, mesh and material name of skin <paramref name="skin" /> of a multi-skin record.</summary>
    public static string SkinName(string name, int skin)
    {
        ArgumentNullException.ThrowIfNull(name);
        return string.Create(CultureInfo.InvariantCulture, $"{name}.{SkinId(skin)}");
    }

    /// <summary>Builds the nodes, roots and layer sets for <paramref name="skins" /> meshes (see the type summary).</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="skins" /> is not positive (the builder refuses such a record first).</exception>
    public static ShadowkeyMeshModelLayerResult Build(string name, int skins, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(skins);
        if (skins == 1)
        {
            return new ShadowkeyMeshModelLayerResult(
                [new SceneNode(name, Identity, meshIndex: 0) { Role = SceneNodeRole.Transform }], [0], [],
                [DefaultSkin]);
        }

        var nodes = new List<SceneNode>(skins);
        var layerSets = new List<SceneLayerSet>(skins);
        for (var skin = 0; skin < skins; skin++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            layerSets.Add(new SceneLayerSet(SkinId(skin), string.Create(CultureInfo.InvariantCulture, $"Skin {skin}"),
                [skin], skin == DefaultSkin, SourceKind, ExclusiveGroup, cancellationToken: cancellationToken));
            nodes.Add(new SceneNode(SkinName(name, skin), Identity, meshIndex: skin) { Role = SceneNodeRole.Transform });
        }

        return new ShadowkeyMeshModelLayerResult(nodes, Enumerable.Range(0, skins).ToArray(), layerSets, [DefaultSkin]);
    }
}
