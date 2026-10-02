using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     What the sub-readers place on node occurrences after the hierarchy is walked (<see cref="NifModelNodeReader.WithPlacements" />):
///     meshes (geometry and material readers), skins and joint roles (<see cref="NifModelSkinReader" />) and billboards
///     (<see cref="NifModelLayerReader" />). Every key is a node index.
/// </summary>
internal sealed class NifModelNodePlacements
{
    /// <summary>Creates the placements.</summary>
    public NifModelNodePlacements(
        IReadOnlyDictionary<int, int> meshByNode,
        IReadOnlyDictionary<int, int> skinByNode,
        IReadOnlySet<int> jointNodes,
        IReadOnlyDictionary<int, SceneBillboard> billboardByNode)
    {
        MeshByNode = meshByNode;
        SkinByNode = skinByNode;
        JointNodes = jointNodes;
        BillboardByNode = billboardByNode;
    }

    /// <summary>The mesh each drawable geometry occurrence places.</summary>
    public IReadOnlyDictionary<int, int> MeshByNode { get; }

    /// <summary>The skin each skinned geometry occurrence binds.</summary>
    public IReadOnlyDictionary<int, int> SkinByNode { get; }

    /// <summary>The occurrences that are joints of a typed skin (role Joint).</summary>
    public IReadOnlySet<int> JointNodes { get; }

    /// <summary>The billboard of each NiBillboardNode occurrence.</summary>
    public IReadOnlyDictionary<int, SceneBillboard> BillboardByNode { get; }

    /// <summary>The node indices any placement touches, ascending.</summary>
    public IEnumerable<int> TouchedNodes =>
        MeshByNode.Keys.Concat(SkinByNode.Keys).Concat(JointNodes).Concat(BillboardByNode.Keys).Distinct().Order();
}
