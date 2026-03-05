using System.Numerics;
using FalloutXbox360Utils.Core.Formats.Nif.Conversion;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering;

/// <summary>
///     Walks the NIF scene graph to classify blocks and compute world transforms.
///     Extracted from NifGeometryExtractor for modularity.
/// </summary>
internal static class NifSceneGraphWalker
{
    internal static readonly HashSet<string> NodeTypes =
        ["NiNode", "BSFadeNode", "BSMultiBoundNode", "BSOrderedNode", "BSLeafAnimNode"];

    internal static readonly HashSet<string> ShapeTypes = ["NiTriShape", "NiTriStrips", "BSLODTriShape"];

    /// <summary>
    ///     Classify all blocks: identify nodes (with children), shapes (with data refs),
    ///     and build the scene graph structure.
    /// </summary>
    internal static void ClassifyBlocks(byte[] data, NifInfo nif,
        Dictionary<int, List<int>> nodeChildren, Dictionary<int, int> shapeDataMap,
        Dictionary<int, List<int>>? shapePropertyMap = null,
        Dictionary<int, int>? shapeSkinInstanceMap = null)
    {
        var be = nif.IsBigEndian;

        for (var i = 0; i < nif.Blocks.Count; i++)
        {
            var block = nif.Blocks[i];

            if (NodeTypes.Contains(block.TypeName))
            {
                var children = NifBlockParsers.ParseNodeChildren(data, block, nif.BsVersion, be);
                if (children != null)
                {
                    nodeChildren[i] = children;
                }
            }
            else if (ShapeTypes.Contains(block.TypeName))
            {
                // Skip gore/dismembered shape variants by name (e.g., "UpperBodyGore", "Decapitated")
                var shapeName = NifBlockParsers.ReadBlockName(data, block, nif);
                if (NifBlockParsers.IsGoreShape(shapeName))
                {
                                    continue;
                }

                // Skip gore shapes identified via BSDismemberSkinInstance partition data.
                // Body part IDs 100-299 are gore caps (section caps + torso caps).
                var skinRef = NifBlockParsers.ParseShapeSkinInstanceRef(data, block, nif.BsVersion, be);
                if (skinRef >= 0 && skinRef < nif.Blocks.Count &&
                    nif.Blocks[skinRef].TypeName == "BSDismemberSkinInstance")
                {
                    var bodyParts = NifBlockParsers.ParseDismemberPartitions(data, nif.Blocks[skinRef], be);
                    if (NifBlockParsers.IsDismemberGoreShape(bodyParts))
                    {
                                            continue;
                    }
                }


                // Collect skin instance ref for skeleton deformation
                if (shapeSkinInstanceMap != null && skinRef >= 0 && skinRef < nif.Blocks.Count)
                {
                    var skinBlockType = nif.Blocks[skinRef].TypeName;
                    if (skinBlockType is "NiSkinInstance" or "BSDismemberSkinInstance")
                    {
                        shapeSkinInstanceMap[i] = skinRef;
                    }
                }

                var dataRef = NifBlockParsers.ParseShapeDataRef(data, block, nif.BsVersion, be);
                if (dataRef >= 0 && dataRef < nif.Blocks.Count)
                {
                    shapeDataMap[i] = dataRef;
                }

                if (shapePropertyMap != null)
                {
                    var propRefs = NifBlockParsers.ParseShapePropertyRefs(data, block, nif.BsVersion, be);
                    if (propRefs != null && propRefs.Count > 0)
                    {
                        shapePropertyMap[i] = propRefs;
                    }
                }
            }
        }
    }

    /// <summary>
    ///     Walk the scene graph depth-first from root nodes, accumulating transforms.
    ///     Animation overrides (if any) replace the local transform of targeted nodes.
    /// </summary>
    internal static void ComputeWorldTransforms(byte[] data, NifInfo nif,
        Dictionary<int, List<int>> nodeChildren, Dictionary<int, Matrix4x4> worldTransforms,
        Dictionary<string, NifAnimationParser.AnimPoseOverride>? animOverrides = null)
    {
        // Find root nodes: nodes that are not children of any other node
        var allChildren = new HashSet<int>();
        foreach (var children in nodeChildren.Values)
        {
            foreach (var child in children)
            {
                allChildren.Add(child);
            }
        }

        // Walk from each root
        for (var i = 0; i < nif.Blocks.Count; i++)
        {
            if (!nodeChildren.ContainsKey(i) && !allChildren.Contains(i))
            {
                // Not a node and not a child — skip
                continue;
            }

            if (!allChildren.Contains(i))
            {
                // This is a root node
                WalkNode(data, nif, i, Matrix4x4.Identity, nodeChildren, worldTransforms, animOverrides);
            }
        }

        // Also handle shapes that are direct root children (not under any node)
        for (var i = 0; i < nif.Blocks.Count; i++)
        {
            if (ShapeTypes.Contains(nif.Blocks[i].TypeName) && !worldTransforms.ContainsKey(i) && !allChildren.Contains(i))
            {
                // Root-level shape — parse its own transform
                var localTransform = NifBlockParsers.ParseNiAVObjectTransform(data, nif.Blocks[i], nif.BsVersion, nif.IsBigEndian);
                worldTransforms[i] = localTransform;
            }
        }
    }

    internal static void WalkNode(byte[] data, NifInfo nif, int blockIndex, Matrix4x4 parentTransform,
        Dictionary<int, List<int>> nodeChildren, Dictionary<int, Matrix4x4> worldTransforms,
        Dictionary<string, NifAnimationParser.AnimPoseOverride>? animOverrides = null)
    {
        var block = nif.Blocks[blockIndex];
        var localTransform = NifBlockParsers.ParseNiAVObjectTransform(data, block, nif.BsVersion, nif.IsBigEndian);

        // If animation overrides are available, merge per-channel: animation rotation
        // replaces bind pose rotation, but bind pose translation/scale are preserved
        // unless the animation explicitly provides them.
        if (animOverrides != null)
        {
            var boneName = NifBlockParsers.ReadBlockName(data, block, nif);
            if (boneName != null && animOverrides.TryGetValue(boneName, out var anim))
            {
                // Extract bind pose translation from the current localTransform (row 4)
                var tx = anim.HasTranslation ? anim.Tx : localTransform.M41;
                var ty = anim.HasTranslation ? anim.Ty : localTransform.M42;
                var tz = anim.HasTranslation ? anim.Tz : localTransform.M43;

                // Extract bind pose scale (length of first column of 3x3 rotation block)
                var bindScale = anim.HasScale
                    ? anim.Scale
                    : MathF.Sqrt(localTransform.M11 * localTransform.M11 +
                                 localTransform.M21 * localTransform.M21 +
                                 localTransform.M31 * localTransform.M31);

                // Build new transform: animation rotation + preserved translation/scale
                var rot = Matrix4x4.CreateFromQuaternion(anim.Rotation);

                localTransform = new Matrix4x4(
                    rot.M11 * bindScale, rot.M12 * bindScale, rot.M13 * bindScale, 0,
                    rot.M21 * bindScale, rot.M22 * bindScale, rot.M23 * bindScale, 0,
                    rot.M31 * bindScale, rot.M32 * bindScale, rot.M33 * bindScale, 0,
                    tx, ty, tz, 1);
            }
        }

        var worldTransform = localTransform * parentTransform;
        worldTransforms[blockIndex] = worldTransform;

        if (!nodeChildren.TryGetValue(blockIndex, out var children))
        {
            return;
        }

        foreach (var childIdx in children)
        {
            if (childIdx < 0 || childIdx >= nif.Blocks.Count)
            {
                continue;
            }

            var childType = nif.Blocks[childIdx].TypeName;
            if (NodeTypes.Contains(childType))
            {
                WalkNode(data, nif, childIdx, worldTransform, nodeChildren, worldTransforms, animOverrides);
            }
            else if (ShapeTypes.Contains(childType))
            {
                // Shape inherits parent's world transform + its own local transform
                var shapeLocal = NifBlockParsers.ParseNiAVObjectTransform(data, nif.Blocks[childIdx], nif.BsVersion, nif.IsBigEndian);
                worldTransforms[childIdx] = shapeLocal * worldTransform;
            }
        }
    }
}
