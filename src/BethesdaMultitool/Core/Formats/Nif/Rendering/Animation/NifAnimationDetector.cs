using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Inspection;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Skinning;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;

/// <summary>
///     Cheap block-table classification of a NIF's animation traits, used by the world decode path
///     to decide its extract options. "Internal skin" means the skinned shapes' skeletons live INSIDE
///     this NIF (Morrowind banners, FNV cloth flags) — skinning them against their own authored node
///     transforms bakes the correct REST pose, no external skeleton needed. NPC body parts also
///     carry internal bone stubs at bind pose, so rest-pose skinning is the right static answer for
///     any placed NIF this path sees.
/// </summary>
internal readonly record struct NifAnimationSignature(
    bool HasInternalSkin,
    bool HasNodeKeyframeTracks,
    bool HasControllerSequenceTracks)
{
    public bool IsAnimatedOrSkinned =>
        HasInternalSkin || HasNodeKeyframeTracks || HasControllerSequenceTracks;
}

internal static class NifAnimationDetector
{
    internal static NifAnimationSignature Detect(byte[] data, NifInfo nif)
    {
        var hasInternalSkin = false;
        var hasKeyframeTracks = false;
        var hasSequences = false;
        var hasTransformInterpolators = false;

        for (var i = 0; i < nif.Blocks.Count; i++)
        {
            var typeName = nif.Blocks[i].TypeName;
            if (!hasKeyframeTracks &&
                typeName is "NiKeyframeController" or "BSKeyframeController")
            {
                hasKeyframeTracks = true;
            }
            else if (typeName == "NiControllerSequence")
            {
                hasSequences = true;
            }
            else if (typeName == "NiTransformInterpolator")
            {
                hasTransformInterpolators = true;
            }
            else if (!hasInternalSkin &&
                     typeName is "NiSkinInstance" or "BSDismemberSkinInstance")
            {
                hasInternalSkin = HasResolvableInternalBones(data, nif, i);
            }
        }

        // A sequence only yields tracks through NiTransformInterpolator indirection — a manager
        // whose sequences drive other controller kinds (flip/vis/alpha) has no transform rig here.
        // The collector applies the strict gates (idle-named sequence, BSX Animated bit).
        return new NifAnimationSignature(
            hasInternalSkin, hasKeyframeTracks, hasSequences && hasTransformInterpolators);
    }

    /// <summary>Every bone ref must resolve to a scene-node block in THIS file for the NIF's own
    /// node transforms to pose the skin.</summary>
    private static bool HasResolvableInternalBones(byte[] data, NifInfo nif, int skinInstanceIndex)
    {
        var skin = NifSkinBlockParser.ParseNiSkinInstance(
            data, nif.Blocks[skinInstanceIndex], nif.IsBigEndian, nif.BinaryVersion);
        if (skin is null || skin.BoneRefs.Length == 0)
        {
            return false;
        }

        foreach (var boneRef in skin.BoneRefs)
        {
            if (boneRef < 0 || boneRef >= nif.Blocks.Count ||
                !NifSceneGraphWalker.NodeTypes.Contains(nif.Blocks[boneRef].TypeName))
            {
                return false;
            }
        }

        return true;
    }
}
