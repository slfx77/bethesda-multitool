using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Inspection;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;

/// <summary>
///     Shared bone-tree assembly for the era collectors (TES3 per-node controllers,
///     NiControllerManager sequences): given the tracks each collector found per NODE BLOCK, builds
///     the parents-before-children <see cref="NifAnimBone" /> array + aligned track array. The bone
///     set is every tracked node ∪ skin-referenced node ∪ their ancestors, so one forward pass
///     composes world transforms.
///     <para>
///         Parentless (file-root) bones get IDENTITY rest and their tracks are dropped: the decode
///         path extracts geometry with <c>treatRootsAsIdentity</c> (the REFR placement supplies the
///         world transform), so the pose evaluator must not re-introduce the root's authored
///         transform — a root-rotated NIF would otherwise render rotated only while animated.
///     </para>
/// </summary>
internal static class NifAnimationRigBuilder
{
    internal static (NifAnimBone[] Bones, NifNodeTrack?[] Tracks)? Build(
        byte[] data,
        NifInfo nif,
        Dictionary<int, List<int>> nodeChildren,
        Dictionary<int, int> shapeSkinInstanceMap,
        Dictionary<int, NifNodeTrack> tracksByNode)
    {
        if (tracksByNode.Count == 0)
        {
            return null;
        }

        var parentOf = BuildParentMap(nodeChildren);

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

            if (parentSlot < 0)
            {
                // File-root policy (see class docs): identity rest, no track.
                bones[slot] = new NifAnimBone(name, -1, Vector3.Zero, Quaternion.Identity, 1f, blockIndex);
                tracks[slot] = null;
                continue;
            }

            var local = NifBlockParsers.ParseNiAVObjectTransform(
                data, nif.Blocks[blockIndex], nif.BsVersion, nif.BinaryVersion, nif.IsBigEndian,
                nif.HasInlineStrings);
            var (restTranslation, restRotation, restScale) = Decompose(local);

            bones[slot] = new NifAnimBone(
                name, parentSlot, restTranslation, restRotation, restScale, blockIndex);
            tracks[slot] = tracksByNode.TryGetValue(blockIndex, out var track) ? track : null;
        }

        return (bones, tracks);
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

    /// <summary>
    ///     Decomposes a row-vector local transform into translation + rotation + uniform scale
    ///     (NIF node transforms are rotation×scale + translation; scale is uniform by format).
    /// </summary>
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
