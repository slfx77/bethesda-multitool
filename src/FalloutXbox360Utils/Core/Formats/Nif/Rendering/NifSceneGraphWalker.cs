using System.Numerics;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Parsing;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering;

/// <summary>
///     Walks the NIF scene graph to classify blocks and compute world transforms.
///     Extracted from NifGeometryExtractor for modularity.
/// </summary>
internal static class NifSceneGraphWalker
{
    internal static readonly HashSet<string> NodeTypes =
        ["NiNode", "NiBillboardNode", "BSFadeNode", "BSMultiBoundNode", "BSOrderedNode", "BSLeafAnimNode"];

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
                // Skip gore/dismembered shape variants and editor helper shapes by name
                var shapeName = NifBlockParsers.ReadBlockName(data, block, nif);
                if (NifBlockParsers.IsGoreShape(shapeName) || NifBlockParsers.IsEditorHelperShape(shapeName))
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
            if (ShapeTypes.Contains(nif.Blocks[i].TypeName) && !worldTransforms.ContainsKey(i) &&
                !allChildren.Contains(i))
            {
                // Root-level shape — parse its own transform
                var localTransform =
                    NifBlockParsers.ParseNiAVObjectTransform(data, nif.Blocks[i], nif.BsVersion, nif.IsBigEndian);
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
                var shapeLocal =
                    NifBlockParsers.ParseNiAVObjectTransform(data, nif.Blocks[childIdx], nif.BsVersion,
                        nif.IsBigEndian);
                worldTransforms[childIdx] = shapeLocal * worldTransform;
            }
        }
    }

    /// <summary>
    ///     Analyze a weapon NIF for NiVisController usage and attachment-bone metadata.
     ///     Returns vis-controlled shape indices (to exclude in holster mode) and
    ///     attachment groups for non-vis-controlled sibling nodes (backpack/tank shapes
    ///     that attach to specific character skeleton bones via Prn or UPB metadata).
    /// </summary>
    internal static VisControllerAnalysis AnalyzeVisControllers(byte[] data, NifInfo nif)
    {
        var be = nif.IsBigEndian;

        // Step 1: Build node children map
        var nodeChildren = new Dictionary<int, List<int>>();
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
        }

        // Step 2: Find nodes with NiVisController attached
        var visControlledNodes = new HashSet<int>();
        for (var i = 0; i < nif.Blocks.Count; i++)
        {
            var block = nif.Blocks[i];
            if (!NodeTypes.Contains(block.TypeName))
            {
                continue;
            }

            var controllerRef = NifBinaryCursor.ReadNiObjectNETControllerRef(
                data, block.DataOffset, block.DataOffset + block.Size, be);

            // Walk the controller chain (NiTimeController has a nextController ref at offset 4)
            while (controllerRef >= 0 && controllerRef < nif.Blocks.Count)
            {
                if (nif.Blocks[controllerRef].TypeName == "NiVisController")
                {
                    visControlledNodes.Add(i);
                    break;
                }

                var ctrlBlock = nif.Blocks[controllerRef];
                var ctrlPos = ctrlBlock.DataOffset;
                if (ctrlPos + 4 > ctrlBlock.DataOffset + ctrlBlock.Size)
                {
                    break;
                }

                controllerRef = BinaryUtils.ReadInt32(data, ctrlPos, be);
            }
        }

        var visControlledShapes = new HashSet<int>();
        if (visControlledNodes.Count == 0)
        {
            return new VisControllerAnalysis(visControlledShapes, []);
        }

        // Step 3: Collect all shape descendants of vis-controlled nodes
        foreach (var nodeIdx in visControlledNodes)
        {
            CollectDescendantShapes(nodeIdx, nodeChildren, nif, visControlledShapes);
        }

        // Step 4: For non-vis-controlled sibling nodes of the scene root, read their
        // attachment-bone metadata and collect their descendant shapes. This tells
        // us which character skeleton bone each group of shapes should be attached to.
        var parentBoneGroups = new List<ParentBoneShapeGroup>();
        if (nodeChildren.TryGetValue(0, out var rootChildren))
        {
            foreach (var childIdx in rootChildren)
            {
                if (childIdx < 0 || childIdx >= nif.Blocks.Count)
                {
                    continue;
                }

                if (!NodeTypes.Contains(nif.Blocks[childIdx].TypeName))
                {
                    continue;
                }

                if (visControlledNodes.Contains(childIdx))
                {
                    continue;
                }

                var attachmentBone = NifBlockParsers.ReadAttachmentBoneExtraData(data, nif.Blocks[childIdx], nif);
                if (attachmentBone == null)
                {
                    continue;
                }

                var shapes = new HashSet<int>();
                CollectDescendantShapes(childIdx, nodeChildren, nif, shapes);

                // Also include direct shape children
                if (ShapeTypes.Contains(nif.Blocks[childIdx].TypeName))
                {
                    shapes.Add(childIdx);
                }

                if (shapes.Count > 0)
                {
                    var sourceNodeName = NifBlockParsers.ReadBlockName(data, nif.Blocks[childIdx], nif) ??
                                         $"Node_{childIdx}";
                    parentBoneGroups.Add(new ParentBoneShapeGroup(attachmentBone, sourceNodeName, shapes));
                }
            }
        }

        return new VisControllerAnalysis(visControlledShapes, parentBoneGroups);
    }

    /// <summary>Result of NiVisController analysis for a weapon NIF.</summary>
    internal sealed record VisControllerAnalysis(
        HashSet<int> VisControlledShapeIndices,
        List<ParentBoneShapeGroup> ParentBoneGroups);

    /// <summary>A group of shapes that should be attached to a specific skeleton bone.</summary>
    internal sealed record ParentBoneShapeGroup(string BoneName, string SourceNodeName, HashSet<int> ShapeIndices);

    /// <summary>
    ///     Find all shape block indices that are descendants of NiNode blocks with a
    ///     NiVisController in their controller chain.
    /// </summary>
    internal static HashSet<int> FindVisControlledShapeIndices(byte[] data, NifInfo nif)
    {
        return AnalyzeVisControllers(data, nif).VisControlledShapeIndices;
    }

    private static void CollectDescendantShapes(int nodeIdx, Dictionary<int, List<int>> nodeChildren,
        NifInfo nif, HashSet<int> shapes)
    {
        if (!nodeChildren.TryGetValue(nodeIdx, out var children))
        {
            return;
        }

        foreach (var childIdx in children)
        {
            if (childIdx < 0 || childIdx >= nif.Blocks.Count)
            {
                continue;
            }

            if (ShapeTypes.Contains(nif.Blocks[childIdx].TypeName))
            {
                shapes.Add(childIdx);
            }
            else if (NodeTypes.Contains(nif.Blocks[childIdx].TypeName))
            {
                CollectDescendantShapes(childIdx, nodeChildren, nif, shapes);
            }
        }
    }
}
