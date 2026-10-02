using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;

namespace BethesdaMultitool.Tests.Core.Modeling.Nif;

/// <summary>
///     A rig inspector for the renderer's <see cref="NifModelFamilyAnimationResolver" />, used only as the control that
///     shows the rule cut 1b rejects: the model's skin-bone names are given, and each candidate skeleton's node names are
///     looked up by the first byte of its payload.
/// </summary>
internal sealed class NifModelFamilyRigInspectorStub : INifModelFamilyRigInspector
{
    private readonly IReadOnlyList<string> _modelBoneNames;
    private readonly IReadOnlyDictionary<byte, string[]> _skeletonNodeNames;

    /// <summary>Creates the stub.</summary>
    /// <param name="modelBoneNames">The names the model (here, the <c>.kf</c>'s targets) requires.</param>
    /// <param name="skeletonNodeNames">Each skeleton's node names, keyed by its payload's first byte.</param>
    public NifModelFamilyRigInspectorStub(
        IReadOnlyList<string> modelBoneNames,
        IReadOnlyDictionary<byte, string[]> skeletonNodeNames)
    {
        _modelBoneNames = modelBoneNames;
        _skeletonNodeNames = skeletonNodeNames;
    }

    /// <inheritdoc />
    public NifModelFamilyModelRig? InspectModel(byte[] data)
    {
        return new NifModelFamilyModelRig(_modelBoneNames);
    }

    /// <inheritdoc />
    public NifModelFamilySkeletonRig? InspectSkeleton(byte[] data)
    {
        if (data.Length > 0 && _skeletonNodeNames.TryGetValue(data[0], out var names))
        {
            return new NifModelFamilySkeletonRig(names);
        }

        return null;
    }
}
