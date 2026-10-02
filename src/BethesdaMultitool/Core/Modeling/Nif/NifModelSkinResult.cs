using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>What <see cref="NifModelSkinReader" /> produced for one read.</summary>
internal sealed class NifModelSkinResult
{
    /// <summary>Creates a result.</summary>
    public NifModelSkinResult(
        IReadOnlyList<SceneSkin> skins,
        IReadOnlyDictionary<int, int> skinByNode,
        IReadOnlySet<int> jointNodes,
        IReadOnlyList<SceneNativeState> nativeRows,
        IReadOnlyDictionary<int, NifModelBlockDisposition> dispositions,
        IReadOnlyDictionary<int, int> skinByFedBlock,
        IReadOnlyList<SceneDiagnostic> diagnostics)
    {
        Skins = skins;
        SkinByNode = skinByNode;
        JointNodes = jointNodes;
        NativeRows = nativeRows;
        Dispositions = dispositions;
        SkinByFedBlock = skinByFedBlock;
        Diagnostics = diagnostics;
    }

    /// <summary>The document skins: one per skin instance and resolved joint palette, in first-use order.</summary>
    public IReadOnlyList<SceneSkin> Skins { get; }

    /// <summary>The skin of each skinned geometry occurrence (node index), for its <see cref="SceneNode.SkinIndex" />.</summary>
    public IReadOnlyDictionary<int, int> SkinByNode { get; }

    /// <summary>Every node occurrence that is a joint of a typed skin (its role becomes Joint).</summary>
    public IReadOnlySet<int> JointNodes { get; }

    /// <summary>One <c>bmt.nif.skin</c> row per skin.</summary>
    public IReadOnlyList<SceneNativeState> NativeRows { get; }

    /// <summary>Coverage decisions for the skin instance, skin data and skin partition blocks the reader visited.</summary>
    public IReadOnlyDictionary<int, NifModelBlockDisposition> Dispositions { get; }

    /// <summary>For each skin instance, skin data or partition block that fed a skin, the first skin it fed.</summary>
    public IReadOnlyDictionary<int, int> SkinByFedBlock { get; }

    /// <summary>Skin diagnostics, bounded per code.</summary>
    public IReadOnlyList<SceneDiagnostic> Diagnostics { get; }
}
