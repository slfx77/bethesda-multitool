using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Inspection;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;

/// <summary>
///     Collects a <see cref="NifMeshAnimation" /> from the TES3-era per-node controller graph:
///     scene nodes carry <c>NiKeyframeController</c>/<c>BSKeyframeController</c> chains whose
///     <c>NiKeyframeData</c> holds the tracks (Morrowind banners: a 3-bone chain under "Root Bone").
///     The bone set is every named node that is a tracked node, a skin bone, or an ancestor of one —
///     flattened parents-before-children so one forward pass composes world transforms. The modern
///     NiControllerManager/NiControllerSequence graph compiles onto the same model via a separate
///     collector (the 5-family seam); nothing downstream distinguishes the eras.
/// </summary>
internal static class NifNodeKeyframeTrackCollector
{
    internal static NifMeshAnimation? Collect(byte[] data, NifInfo nif)
    {
        var be = nif.IsBigEndian;

        // Scene graph: children lists give parentage; the walker's node set gives us node blocks.
        var nodeChildren = new Dictionary<int, List<int>>();
        var shapeDataMap = new Dictionary<int, int>();
        var shapePropertyMap = new Dictionary<int, List<int>>();
        var shapeSkinInstanceMap = new Dictionary<int, int>();
        NifSceneGraphWalker.ClassifyBlocks(data, nif, nodeChildren, shapeDataMap, shapePropertyMap,
            shapeSkinInstanceMap);

        var parentOf = BuildParentMap(nodeChildren);

        // Tracks per node block index, from the controller chains.
        var tracksByNode = new Dictionary<int, NifNodeTrack>();
        foreach (var nodeIndex in nodeChildren.Keys)
        {
            var nodeName = NifBlockParsers.ReadBlockName(data, nif.Blocks[nodeIndex], nif);
            if (string.IsNullOrWhiteSpace(nodeName))
            {
                continue;
            }

            var controllerRef = NifBinaryCursor.ReadNiObjectNETControllerRef(
                data,
                nif.Blocks[nodeIndex].DataOffset,
                nif.Blocks[nodeIndex].DataOffset + nif.Blocks[nodeIndex].Size,
                be,
                nif.HasInlineStrings,
                nif.BinaryVersion);

            for (var hop = 0; hop < 8 && controllerRef >= 0 && controllerRef < nif.Blocks.Count; hop++)
            {
                var controllerBlock = nif.Blocks[controllerRef];
                if (!NifTimeControllerReader.TryRead(data, controllerBlock, be, out var header))
                {
                    break;
                }

                if (controllerBlock.TypeName is "NiKeyframeController" or "BSKeyframeController")
                {
                    var dataRef = NifKeyframeDataTrackReader.ReadControllerDataRef(data, controllerBlock, be);
                    var track = NifKeyframeDataTrackReader.TryReadTrack(
                        data, nif, dataRef, nodeName!, header.Frequency, header.Phase);
                    if (track is { HasMotion: true })
                    {
                        tracksByNode[nodeIndex] = track;
                        break;
                    }
                }

                controllerRef = header.NextControllerRef;
            }
        }

        if (tracksByNode.Count == 0)
        {
            return null;
        }

        // Bone set: tracked nodes ∪ skin bones ∪ all their ancestors (so composition is closed).
        var boneIndices = new HashSet<int>(tracksByNode.Keys);
        foreach (var boneIdx in NifSceneGraphWalker.CollectSkinBoneNodeIndices(data, nif, shapeSkinInstanceMap))
        {
            if (nodeChildren.ContainsKey(boneIdx))
            {
                boneIndices.Add(boneIdx);
            }
        }

        foreach (var idx in boneIndices.ToArray())
        {
            var walk = idx;
            while (parentOf.TryGetValue(walk, out var parent))
            {
                boneIndices.Add(parent);
                walk = parent;
            }
        }

        // Flatten parents-before-children: depth from root is a valid topological order.
        var ordered = boneIndices
            .OrderBy(i => Depth(i, parentOf))
            .ThenBy(i => i)
            .ToArray();
        var boneSlotByBlock = new Dictionary<int, int>(ordered.Length);
        for (var slot = 0; slot < ordered.Length; slot++)
        {
            boneSlotByBlock[ordered[slot]] = slot;
        }

        var bones = new NifAnimBone[ordered.Length];
        var tracks = new NifNodeTrack?[ordered.Length];
        for (var slot = 0; slot < ordered.Length; slot++)
        {
            var blockIndex = ordered[slot];
            var name = NifBlockParsers.ReadBlockName(data, nif.Blocks[blockIndex], nif) ?? $"#{blockIndex}";
            var parentSlot = parentOf.TryGetValue(blockIndex, out var parentBlock) &&
                             boneSlotByBlock.TryGetValue(parentBlock, out var ps)
                ? ps
                : -1;

            var local = NifBlockParsers.ParseNiAVObjectTransform(
                data, nif.Blocks[blockIndex], nif.BsVersion, nif.BinaryVersion, be, nif.HasInlineStrings);
            var (restTranslation, restRotation, restScale) = Decompose(local);

            bones[slot] = new NifAnimBone(name, parentSlot, restTranslation, restRotation, restScale);
            tracks[slot] = tracksByNode.TryGetValue(blockIndex, out var track) ? track : null;
        }

        var textKeys = NifTextKeyReader.ReadFirst(data, nif);
        var clip = NifAnimationClipSelector.SelectClip(textKeys, tracks);
        if (clip is not { } window)
        {
            return null; // degenerate/absent play window → treat as static
        }

        return new NifMeshAnimation(bones, tracks, textKeys, window.Start, window.Stop, window.Loops);
    }

    private static Dictionary<int, int> BuildParentMap(Dictionary<int, List<int>> nodeChildren)
    {
        var parentOf = new Dictionary<int, int>();
        foreach (var (parent, children) in nodeChildren)
        {
            foreach (var child in children)
            {
                // First writer wins — a NIF child referenced twice keeps its first parent.
                parentOf.TryAdd(child, parent);
            }
        }

        return parentOf;
    }

    private static int Depth(int index, Dictionary<int, int> parentOf)
    {
        var depth = 0;
        var walk = index;
        while (parentOf.TryGetValue(walk, out var parent) && depth < 256)
        {
            depth++;
            walk = parent;
        }

        return depth;
    }

    /// <summary>Decomposes a row-vector local transform into translation + rotation + uniform scale
    /// (NIF node transforms are rotation×scale + translation; scale is uniform by format).</summary>
    private static (Vector3 Translation, Quaternion Rotation, float Scale) Decompose(Matrix4x4 local)
    {
        var translation = local.Translation;
        var scale = new Vector3(local.M11, local.M12, local.M13).Length();
        if (scale <= 1e-6f)
        {
            return (translation, Quaternion.Identity, 1f);
        }

        var inv = 1f / scale;
        var rotationMatrix = new Matrix4x4(
            local.M11 * inv, local.M12 * inv, local.M13 * inv, 0,
            local.M21 * inv, local.M22 * inv, local.M23 * inv, 0,
            local.M31 * inv, local.M32 * inv, local.M33 * inv, 0,
            0, 0, 0, 1);
        return (translation, Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(rotationMatrix)), scale);
    }
}
