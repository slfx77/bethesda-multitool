using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Inspection;

/// <summary>
///     Walks the NIF scene graph to classify blocks and compute world transforms.
///     Extracted from NifGeometryExtractor for modularity.
/// </summary>
internal static class NifSceneGraphWalker
{
    internal static readonly HashSet<string> NodeTypes =
        ["NiNode", "NiBillboardNode", "BSFadeNode", "BSMultiBoundNode", "BSOrderedNode", "BSLeafAnimNode"];

    internal static readonly HashSet<string> ShapeTypes =
    [
        "NiTriShape", "NiTriStrips", "BSLODTriShape",
        // Skyrim SE / Fallout 4 / Fallout 76 self-contained geometry (BSVertexDesc-packed buffers).
        "BSTriShape", "BSSubIndexTriShape", "BSMeshLODTriShape", "BSDynamicTriShape"
    ];

    /// <summary>
    ///     The subset of <see cref="ShapeTypes" /> whose geometry is embedded in the shape block
    ///     itself (no separate NiTriShapeData/NiTriStripsData block). These use the NiAVObject layout,
    ///     not NiGeometry, so the NiTriShape skin/data/property ref parsers do not apply.
    /// </summary>
    internal static readonly HashSet<string> SelfContainedShapeTypes =
        ["BSTriShape", "BSSubIndexTriShape", "BSMeshLODTriShape", "BSDynamicTriShape"];

    /// <summary>
    ///     Classify all blocks: identify nodes (with children), shapes (with data refs),
    ///     and build the scene graph structure.
    /// </summary>
    /// <summary>
    ///     Collision-geometry container nodes whose descendant shapes are physics hulls, not renderable
    ///     geometry. Morrowind/older NIFs put a simplified collision mesh under a <c>RootCollisionNode</c>
    ///     (NiNode layout); the engine never draws it. We must skip those shapes — otherwise the
    ///     untextured collision hull renders as white panels / a dark blob over the real mesh.
    /// </summary>
    internal static readonly HashSet<string> CollisionNodeTypes = ["RootCollisionNode"];

    internal static void ClassifyBlocks(byte[] data, NifInfo nif,
        Dictionary<int, List<int>> nodeChildren, Dictionary<int, int> shapeDataMap,
        Dictionary<int, List<int>>? shapePropertyMap = null,
        Dictionary<int, int>? shapeSkinInstanceMap = null)
    {
        var be = nif.IsBigEndian;

        // Pre-pass: collect every shape that lives under a RootCollisionNode so it is excluded from
        // rendering (it is a physics hull, untextured, that would otherwise draw over the real mesh).
        var collisionShapes = CollectCollisionShapes(data, nif);

        for (var i = 0; i < nif.Blocks.Count; i++)
        {
            var block = nif.Blocks[i];

            if (NodeTypes.Contains(block.TypeName))
            {
                var children = NifBlockParsers.ParseNodeChildren(data, block, nif.BsVersion, be, nif.HasInlineStrings);
                if (children != null)
                {
                    nodeChildren[i] = children;
                }
            }
            else if (ShapeTypes.Contains(block.TypeName))
            {
                // Collision-hull geometry under a RootCollisionNode is never rendered.
                if (collisionShapes.Contains(i))
                {
                    continue;
                }

                // Skip gore/dismembered shape variants and editor helper shapes by name
                var shapeName = NifBlockParsers.ReadBlockName(data, block, nif);
                if (NifBlockParsers.IsGoreShape(shapeName) || NifBlockParsers.IsEditorHelperShape(shapeName))
                {
                    continue;
                }

                // BSTriShape variants carry their own vertex/index buffers — the shape block is its
                // own data block. The NiTriShape skin/data/property ref parsers below assume the
                // NiGeometry layout and would misread these, so map self-to-self and move on.
                // (Skinned BSTriShape — characters — and BSLightingShader textures are handled
                // separately; static worldspace meshes render in bind pose, untextured.)
                if (SelfContainedShapeTypes.Contains(block.TypeName))
                {
                    shapeDataMap[i] = i;

                    // Wire the shape's BSLightingShaderProperty + NiAlphaProperty refs into the
                    // property map so textures (NifTextureResolver) and alpha (NifRenderPropertyReader)
                    // resolve via the standard path. BSTriShape carries both refs inline.
                    if (shapePropertyMap != null)
                    {
                        var bsInfo = NifSceneGraphBlockReader.ParseBsTriShape(data, block, nif.BsVersion, be);
                        if (bsInfo is { } info)
                        {
                            var props = new List<int>(2);
                            if (info.ShaderRef >= 0 && info.ShaderRef < nif.Blocks.Count)
                            {
                                props.Add(info.ShaderRef);
                            }

                            if (info.AlphaRef >= 0 && info.AlphaRef < nif.Blocks.Count)
                            {
                                props.Add(info.AlphaRef);
                            }

                            if (props.Count > 0)
                            {
                                shapePropertyMap[i] = props;
                            }
                        }
                    }

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

                var dataRef = NifBlockParsers.ParseShapeDataRef(data, block, nif.BsVersion, be, nif.HasInlineStrings);
                if (dataRef >= 0 && dataRef < nif.Blocks.Count)
                {
                    shapeDataMap[i] = dataRef;
                }

                if (shapePropertyMap != null)
                {
                    var propRefs = NifBlockParsers.ParseShapePropertyRefs(data, block, nif.BsVersion, be, nif.HasInlineStrings);
                    if (propRefs != null && propRefs.Count > 0)
                    {
                        shapePropertyMap[i] = propRefs;
                    }
                }
            }
        }
    }

    /// <summary>
    ///     Collect all shape block indices that are descendants of a <see cref="CollisionNodeTypes" />
    ///     container (e.g. <c>RootCollisionNode</c>). These are physics collision hulls the engine never
    ///     renders; the geometry extractor must skip them. <c>RootCollisionNode</c> uses the NiNode
    ///     layout, so its children parse with <see cref="NifBlockParsers.ParseNodeChildren" />.
    /// </summary>
    private static HashSet<int> CollectCollisionShapes(byte[] data, NifInfo nif)
    {
        var collisionShapes = new HashSet<int>();
        var visited = new HashSet<int>();
        for (var i = 0; i < nif.Blocks.Count; i++)
        {
            if (CollisionNodeTypes.Contains(nif.Blocks[i].TypeName))
            {
                CollectShapesUnderCollisionNode(data, nif, i, collisionShapes, visited);
            }
        }

        return collisionShapes;
    }

    private static void CollectShapesUnderCollisionNode(byte[] data, NifInfo nif, int nodeIndex,
        HashSet<int> collisionShapes, HashSet<int> visited)
    {
        if (!visited.Add(nodeIndex))
        {
            return;
        }

        var children = NifBlockParsers.ParseNodeChildren(data, nif.Blocks[nodeIndex], nif.BsVersion,
            nif.IsBigEndian, nif.HasInlineStrings);
        if (children is null)
        {
            return;
        }

        foreach (var child in children)
        {
            if (child < 0 || child >= nif.Blocks.Count)
            {
                continue;
            }

            var childType = nif.Blocks[child].TypeName;
            if (ShapeTypes.Contains(childType))
            {
                collisionShapes.Add(child);
            }
            else if (NodeTypes.Contains(childType) || CollisionNodeTypes.Contains(childType))
            {
                CollectShapesUnderCollisionNode(data, nif, child, collisionShapes, visited);
            }
        }
    }

    /// <summary>
    ///     Collect the node indices that serve as skinning bones for any skinned shape in the NIF.
    ///     Geometry hung directly off such a node is a rig helper / physics proxy — e.g. the per-bone
    ///     boxes on FNV animated flags (<c>clutter\flags\NV_NCR_Flag.NIF</c>, the <c>TTail*/MTail*/Root</c>
    ///     boxes) — not part of the visible mesh. The engine deforms the skinned mesh by these bones and
    ///     never draws geometry parented to them; the worldspace reference path bakes bind pose, so
    ///     without filtering these the untextured bone boxes render as stray blocks beside the flag.
    /// </summary>
    internal static HashSet<int> CollectSkinBoneNodeIndices(byte[] data, NifInfo nif,
        IReadOnlyDictionary<int, int> shapeSkinInstanceMap)
    {
        var bones = new HashSet<int>();
        var parsedSkins = new HashSet<int>();
        foreach (var skinRef in shapeSkinInstanceMap.Values)
        {
            if (skinRef < 0 || skinRef >= nif.Blocks.Count || !parsedSkins.Add(skinRef))
            {
                continue;
            }

            var skin = NifSkinningExtractor.ParseNiSkinInstance(data, nif.Blocks[skinRef], nif.IsBigEndian);
            if (skin is null)
            {
                continue;
            }

            foreach (var boneIdx in skin.BoneRefs)
            {
                if (boneIdx >= 0 && boneIdx < nif.Blocks.Count)
                {
                    bones.Add(boneIdx);
                }
            }

            // The cloth's skeleton-root node (a separate field from the weighted bone list) also carries
            // a proxy box (e.g. the flag's "Root:0"). Treat it as a bone node too — but never block 0,
            // the file's Scene Root, where legitimate static worldspace geometry hangs.
            if (skin.SkeletonRootRef > 0 && skin.SkeletonRootRef < nif.Blocks.Count)
            {
                bones.Add(skin.SkeletonRootRef);
            }
        }

        return bones;
    }

    /// <summary>
    ///     Walk the scene graph depth-first from root nodes, accumulating transforms.
    ///     Animation overrides (if any) replace the local transform of targeted nodes.
    ///     <para>
    ///         <paramref name="treatRootsAsIdentity" />: when a NIF is placed into the world via a
    ///         REFR, the engine sets the scene-root node's world transform to the REFR placement —
    ///         the root node's OWN authored transform is discarded. For a placed-reference bake the
    ///         placement is applied separately (see <c>RenderableReference.ComposeWorldMatrix</c>),
    ///         so the root's own transform must be treated as identity here; otherwise it is injected
    ///         twice. This only matters for the rare meshes whose root carries a non-identity rotation
    ///         (e.g. <c>McMarranWallsDES\wallReg</c> at 90°, monorail curves at 15°) — for the common
    ///         identity-root mesh it is a no-op. Off by default to preserve the single-NIF / skinned
    ///         paths that key bone transforms off the full hierarchy.
    ///     </para>
    /// </summary>
    /// <param name="billboardShapes">
    ///     When non-null, shapes that sit under a <c>NiBillboardNode</c> are baked with the billboard
    ///     node's <em>rotation dropped</em> (translation kept) so the renderer can re-aim them at the
    ///     camera per frame, and their block indices are collected into this set. Null (default) keeps
    ///     the legacy bake — the billboard node's full transform is baked in like any other node — so
    ///     the single-NIF / NPC / export paths are unaffected.
    /// </param>
    internal static void ComputeWorldTransforms(byte[] data, NifInfo nif,
        Dictionary<int, List<int>> nodeChildren, Dictionary<int, Matrix4x4> worldTransforms,
        Dictionary<string, NifAnimationParser.AnimPoseOverride>? animOverrides = null,
        bool treatRootsAsIdentity = false,
        HashSet<int>? billboardShapes = null)
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
                WalkNode(data, nif, i, Matrix4x4.Identity, nodeChildren, worldTransforms, animOverrides,
                    ignoreOwnTransform: treatRootsAsIdentity, billboardShapes: billboardShapes);
            }
        }

        // Also handle shapes that are direct root children (not under any node)
        for (var i = 0; i < nif.Blocks.Count; i++)
        {
            if (ShapeTypes.Contains(nif.Blocks[i].TypeName) && !worldTransforms.ContainsKey(i) &&
                !allChildren.Contains(i))
            {
                // Root-level shape. When placed via a REFR the engine supplies its world transform,
                // so the shape's own authored transform is discarded for a placed-ref bake.
                worldTransforms[i] = treatRootsAsIdentity
                    ? Matrix4x4.Identity
                    : NifBlockParsers.ParseNiAVObjectTransform(data, nif.Blocks[i], nif.BsVersion, nif.IsBigEndian,
                        nif.HasInlineStrings);
            }
        }
    }

    internal static void WalkNode(byte[] data, NifInfo nif, int blockIndex, Matrix4x4 parentTransform,
        Dictionary<int, List<int>> nodeChildren, Dictionary<int, Matrix4x4> worldTransforms,
        Dictionary<string, NifAnimationParser.AnimPoseOverride>? animOverrides = null,
        bool ignoreOwnTransform = false,
        HashSet<int>? billboardShapes = null,
        bool underBillboard = false)
    {
        var block = nif.Blocks[blockIndex];
        // A placed-reference's scene root has its own authored transform replaced by the REFR
        // placement (applied separately downstream), so discard it here. Children are still walked
        // relative to identity. See ComputeWorldTransforms' treatRootsAsIdentity note.
        var localTransform = ignoreOwnTransform
            ? Matrix4x4.Identity
            : NifBlockParsers.ParseNiAVObjectTransform(data, block, nif.BsVersion, nif.IsBigEndian,
                nif.HasInlineStrings);

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
                if (billboardShapes != null && childType == "NiBillboardNode")
                {
                    // Billboard subtree: the engine re-orients a NiBillboardNode toward the camera every
                    // frame, so its authored rotation is meaningless for a static bake. Fold only the
                    // node's world TRANSLATION into the subtree parent (drop its rotation) so the quad
                    // lands at the right spot in its authored local orientation, then flag every shape
                    // underneath as a billboard for the renderer to re-aim per frame.
                    var bbLocal = NifBlockParsers.ParseNiAVObjectTransform(data, nif.Blocks[childIdx],
                        nif.BsVersion, nif.IsBigEndian, nif.HasInlineStrings);
                    var bbWorld = bbLocal * worldTransform;
                    var bbParent = Matrix4x4.CreateTranslation(bbWorld.Translation);
                    WalkNode(data, nif, childIdx, bbParent, nodeChildren, worldTransforms, animOverrides,
                        ignoreOwnTransform: true, billboardShapes: billboardShapes, underBillboard: true);
                }
                else
                {
                    WalkNode(data, nif, childIdx, worldTransform, nodeChildren, worldTransforms, animOverrides,
                        billboardShapes: billboardShapes, underBillboard: underBillboard);
                }
            }
            else if (ShapeTypes.Contains(childType))
            {
                // Shape inherits parent's world transform + its own local transform
                var shapeLocal =
                    NifBlockParsers.ParseNiAVObjectTransform(data, nif.Blocks[childIdx], nif.BsVersion,
                        nif.IsBigEndian, nif.HasInlineStrings);
                worldTransforms[childIdx] = shapeLocal * worldTransform;
                if (underBillboard)
                {
                    billboardShapes?.Add(childIdx);
                }
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
                var children = NifBlockParsers.ParseNodeChildren(data, block, nif.BsVersion, be, nif.HasInlineStrings);
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
                data, block.DataOffset, block.DataOffset + block.Size, be, nif.HasInlineStrings, nif.IsMorrowind);

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

    /// <summary>Result of NiVisController analysis for a weapon NIF.</summary>
    internal sealed record VisControllerAnalysis(
        HashSet<int> VisControlledShapeIndices,
        List<ParentBoneShapeGroup> ParentBoneGroups);

    /// <summary>A group of shapes that should be attached to a specific skeleton bone.</summary>
    internal sealed record ParentBoneShapeGroup(string BoneName, string SourceNodeName, HashSet<int> ShapeIndices);
}
