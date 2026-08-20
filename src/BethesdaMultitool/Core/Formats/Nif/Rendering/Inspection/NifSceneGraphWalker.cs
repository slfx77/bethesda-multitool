using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Particles;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Inspection;

/// <summary>
///     Walks the NIF scene graph to classify blocks and compute world transforms.
///     Extracted from NifGeometryExtractor for modularity.
/// </summary>
internal static class NifSceneGraphWalker
{
    internal static readonly HashSet<string> NodeTypes =
    [
        "NiNode", "NiBillboardNode", "NiSwitchNode", "BSFadeNode", "BSMultiBoundNode", "BSOrderedNode",
        "BSLeafAnimNode", "BSTreeNode",
        // Morrowind-era Bethesda NiNode subclasses (nif.xml module BSLegacy). Without these the
        // subtree is never walked as scene nodes — e.g. i\in_lava_1024.nif roots its renderable
        // magma shapes under NiBSAnimationNode, which left the lava untextured/misplaced.
        "NiBSAnimationNode", "NiBSParticleNode"
    ];

    internal static readonly HashSet<string> ShapeTypes =
    [
        "NiTriShape", "NiTriStrips", "BSLODTriShape",
        // Skyrim SE / Fallout 4 / Fallout 76 self-contained geometry (BSVertexDesc-packed buffers).
        "BSTriShape", "BSSubIndexTriShape", "BSMeshLODTriShape", "BSDynamicTriShape",
        // Starfield. Self-contained in the same structural sense (the block IS its own data block and
        // carries the shader/alpha refs inline), but its vertex data is NOT in the NIF at all — the
        // block names an external geometries\<hash>.mesh blob. See NifSubmeshExtractor.
        "BSGeometry"
    ];

    /// <summary>
    ///     The subset of <see cref="ShapeTypes" /> whose geometry is embedded in the shape block
    ///     itself (no separate NiTriShapeData/NiTriStripsData block). These use the NiAVObject layout,
    ///     not NiGeometry, so the NiTriShape skin/data/property ref parsers do not apply.
    /// </summary>
    internal static readonly HashSet<string> SelfContainedShapeTypes =
        ["BSTriShape", "BSSubIndexTriShape", "BSMeshLODTriShape", "BSDynamicTriShape", "BSGeometry"];

    /// <summary>
    ///     Classify all blocks: identify nodes (with children), shapes (with data refs),
    ///     and build the scene graph structure.
    /// </summary>
    /// <summary>
    ///     Non-rendered container nodes whose descendant shapes are engine metadata, not renderable
    ///     geometry. Morrowind/older NIFs put a simplified collision mesh under a <c>RootCollisionNode</c>
    ///     and AI-avoidance volumes under an <c>AvoidNode</c> (both NiNode layout); the engine never
    ///     draws either. We must skip those shapes — otherwise the untextured hull renders as white
    ///     panels / a dark blob over the real mesh (in_lava_1024's AvoidNode shape sat right on the
    ///     lava surface). NOTE: exclusion-only today — if TES3 walk-mode collision ever extracts
    ///     RootCollisionNode hulls, AvoidNode must NOT feed it (avoid ≠ collide).
    /// </summary>
    internal static readonly HashSet<string> CollisionNodeTypes = ["RootCollisionNode", "AvoidNode"];

    internal static void ClassifyBlocks(byte[] data, NifInfo nif,
        Dictionary<int, List<int>> nodeChildren, Dictionary<int, int> shapeDataMap,
        Dictionary<int, List<int>>? shapePropertyMap = null,
        Dictionary<int, int>? shapeSkinInstanceMap = null)
    {
        var be = nif.IsBigEndian;

        // Pre-pass: collect every shape that lives under a RootCollisionNode so it is excluded from
        // rendering (it is a physics hull, untextured, that would otherwise draw over the real mesh).
        var collisionShapes = CollectCollisionShapes(data, nif);

        // Pre-pass: collect emitter-VOLUME meshes referenced by NiPSysMeshEmitter. The engine emits
        // particles from these but never renders them; left in, they extract as untextured white blobs
        // (e.g. FXDustWhirlWind01's emitter NiTriStrips). The particle system itself renders as a baked
        // quad cloud via the particle extractor, not these meshes.
        var emitterMeshShapes = NifParticleSystemParser.CollectEmitterMeshShapes(data, nif);

        for (var i = 0; i < nif.Blocks.Count; i++)
        {
            var block = nif.Blocks[i];

            if (NodeTypes.Contains(block.TypeName))
            {
                var children = NifBlockParsers.ParseNodeChildren(data, block, nif.BsVersion, nif.BinaryVersion, be,
                    nif.HasInlineStrings);
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

                // Particle emitter-volume meshes are never rendered (the engine emits from them).
                if (emitterMeshShapes.Contains(i))
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
                        // BSGeometry (Starfield) carries the same three refs but at different offsets
                        // — its NiAVObject base is followed by a bounding sphere AND box, and its
                        // "data" is an external blob path rather than inline buffers.
                        var (shaderRef, alphaRef) = (-1, -1);
                        if (block.TypeName == "BSGeometry")
                        {
                            if (NifSceneGraphBlockReader.ParseBsGeometry(
                                    data, block, nif.BinaryVersion, be, nif.HasInlineStrings) is { } geo)
                            {
                                (shaderRef, alphaRef) = (geo.ShaderRef, geo.AlphaRef);
                            }
                        }
                        else if (NifSceneGraphBlockReader.ParseBsTriShape(
                                     data, block, nif.BsVersion, nif.BinaryVersion, be) is { } info)
                        {
                            (shaderRef, alphaRef) = (info.ShaderRef, info.AlphaRef);
                        }

                        var props = new List<int>(2);
                        if (shaderRef >= 0 && shaderRef < nif.Blocks.Count)
                        {
                            props.Add(shaderRef);
                        }

                        if (alphaRef >= 0 && alphaRef < nif.Blocks.Count)
                        {
                            props.Add(alphaRef);
                        }

                        if (props.Count > 0)
                        {
                            shapePropertyMap[i] = props;
                        }
                    }

                    continue;
                }

                // Skip gore shapes identified via BSDismemberSkinInstance partition data.
                // Body part IDs 100-299 are gore caps (section caps + torso caps).
                var skinRef = NifBlockParsers.ParseShapeSkinInstanceRef(data, block, nif.BsVersion, nif.BinaryVersion,
                    be, nif.HasInlineStrings);
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

                var dataRef = NifBlockParsers.ParseShapeDataRef(data, block, nif.BsVersion, nif.BinaryVersion, be,
                    nif.HasInlineStrings);
                if (dataRef < 0 || dataRef >= nif.Blocks.Count)
                {
                    continue; // no geometry data
                }

                if (shapePropertyMap != null)
                {
                    var propRefs = NifBlockParsers.ParseShapePropertyRefs(data, block, nif.BsVersion, nif.BinaryVersion,
                        be, nif.HasInlineStrings);

                    // BSShaderProperty-era (FO3/FNV+) shapes with no texture-source property are non-visual
                    // helpers — furniture-marker / boundary / collision-viz placeholders the game never
                    // draws (e.g. LoungeChair_Tops' MarkerSource/ChairBoundary strips, NV_McCarran-
                    // WallRubble's shader-less :2 strip). Drop them so they never bake as untextured white
                    // blobs. Legacy NIFs (property inheritance) are excluded by the BSVersion gate inside
                    // the helper, and run through PropagateInheritedProperties below instead.
                    if (NifBlockParsers.IsNonRenderableHelperShape(nif, propRefs))
                    {
                        continue;
                    }

                    if (propRefs is { Count: > 0 })
                    {
                        shapePropertyMap[i] = propRefs;
                    }
                }

                shapeDataMap[i] = dataRef;
            }
        }

        // NiSwitchNode renders exactly one child subtree. Classic Skyrim trees put their skinned,
        // wind-deformed full-detail geometry in active child 0 and a static low-detail fallback in
        // child 1. Treating every shape block as independently renderable selected both; because the
        // full shapes keep faces only in NiSkinPartition, only the inactive static fallback survived
        // extraction and TreePineForest03 looked severely sparse. Remove inactive descendants before
        // transforms/skinning are built. Malformed switch metadata is deliberately non-destructive.
        var inactiveSwitchShapes = CollectInactiveSwitchShapes(data, nif, nodeChildren);
        foreach (var shapeIndex in inactiveSwitchShapes)
        {
            shapeDataMap.Remove(shapeIndex);
            shapePropertyMap?.Remove(shapeIndex);
            shapeSkinInstanceMap?.Remove(shapeIndex);
        }

        // Legacy NetImmerse (Morrowind-era, NIF ≤ 4.2.2.0) render-property inheritance: a NiAlphaProperty /
        // NiTexturingProperty / NiMaterialProperty / NiStencilProperty attached to a NiNode applies to ALL
        // descendant geometry unless a nearer node/shape carries one of the same type. Newer Bethesda NIFs
        // moved properties onto the shape (BSLightingShaderProperty), so this pass is gated to legacy NIFs.
        // Covers Morrowind meshes whose render properties live on a parent NiNode rather than the shape.
        if (shapePropertyMap != null &&
            NifVersions.IsLegacyNetImmerse(nif.BinaryVersion))
        {
            PropagateInheritedProperties(data, nif, nodeChildren, shapeDataMap, shapePropertyMap, be);
        }
    }

    /// <summary>
    ///     Returns shape descendants of every inactive <c>NiSwitchNode</c> child. Internal for a
    ///     graph-level regression test; callers should normally use <see cref="ClassifyBlocks" />.
    /// </summary>
    internal static HashSet<int> CollectInactiveSwitchShapes(
        byte[] data,
        NifInfo nif,
        IReadOnlyDictionary<int, List<int>> nodeChildren)
    {
        var result = new HashSet<int>();
        for (var switchIndex = 0; switchIndex < nif.Blocks.Count; switchIndex++)
        {
            var block = nif.Blocks[switchIndex];
            if (block.TypeName != "NiSwitchNode" ||
                !nodeChildren.TryGetValue(switchIndex, out var children))
            {
                continue;
            }

            var activeOrdinal = NifBlockParsers.ParseSwitchNodeActiveChildOrdinal(
                data,
                block,
                nif.BsVersion,
                nif.BinaryVersion,
                nif.IsBigEndian,
                nif.HasInlineStrings);
            if (!activeOrdinal.HasValue)
            {
                continue;
            }

            for (var ordinal = 0; ordinal < children.Count; ordinal++)
            {
                if (ordinal == activeOrdinal.Value)
                {
                    continue;
                }

                CollectDescendantShapes(children[ordinal], nif, nodeChildren, result, []);
            }
        }

        return result;
    }

    private static void CollectDescendantShapes(
        int blockIndex,
        NifInfo nif,
        IReadOnlyDictionary<int, List<int>> nodeChildren,
        HashSet<int> shapes,
        HashSet<int> visited)
    {
        if (blockIndex < 0 || blockIndex >= nif.Blocks.Count || !visited.Add(blockIndex))
        {
            return;
        }

        if (ShapeTypes.Contains(nif.Blocks[blockIndex].TypeName))
        {
            shapes.Add(blockIndex);
            return;
        }

        if (!nodeChildren.TryGetValue(blockIndex, out var children))
        {
            return;
        }

        foreach (var child in children)
        {
            CollectDescendantShapes(child, nif, nodeChildren, shapes, visited);
        }
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

    /// <summary>
    ///     Appends each rendered shape's ancestor-NiNode property refs to its own (NetImmerse property
    ///     inheritance). The shape's own refs stay first so they win per type — the property readers take
    ///     the first block of each type — then the nearest ancestor's, then farther ancestors'.
    /// </summary>
    private static void PropagateInheritedProperties(
        byte[] data, NifInfo nif, Dictionary<int, List<int>> nodeChildren,
        Dictionary<int, int> shapeDataMap, Dictionary<int, List<int>> shapePropertyMap, bool be)
    {
        // child block -> parent node
        var parentOf = new Dictionary<int, int>();
        foreach (var (parent, children) in nodeChildren)
        {
            foreach (var child in children)
            {
                parentOf.TryAdd(child, parent);
            }
        }

        // Parse each NiNode's own Properties array once (NiNode shares the NiAVObject layout the shape
        // property parser reads up to).
        var nodeProps = new Dictionary<int, List<int>>();
        foreach (var nodeIndex in nodeChildren.Keys)
        {
            var refs = NifBlockParsers.ParseShapePropertyRefs(
                data, nif.Blocks[nodeIndex], nif.BsVersion, nif.BinaryVersion, be, nif.HasInlineStrings);
            if (refs is { Count: > 0 })
            {
                nodeProps[nodeIndex] = refs;
            }
        }

        if (nodeProps.Count == 0)
        {
            return; // no inheritable node properties
        }

        foreach (var shapeIndex in shapeDataMap.Keys)
        {
            shapePropertyMap.TryGetValue(shapeIndex, out var own);
            var merged = own != null ? new List<int>(own) : [];
            var seen = new HashSet<int>(merged);

            // Walk ancestors nearest-first, appending their property refs after the shape's own.
            var current = shapeIndex;
            var guard = 0;
            while (parentOf.TryGetValue(current, out var parent) && guard++ < 64)
            {
                if (nodeProps.TryGetValue(parent, out var parentRefs))
                {
                    foreach (var r in parentRefs)
                    {
                        if (seen.Add(r))
                        {
                            merged.Add(r);
                        }
                    }
                }

                current = parent;
            }

            if (merged.Count > 0)
            {
                shapePropertyMap[shapeIndex] = merged;
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
            nif.BinaryVersion, nif.IsBigEndian, nif.HasInlineStrings);
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

            var skin = NifSkinningExtractor.ParseNiSkinInstance(data, nif.Blocks[skinRef], nif.IsBigEndian,
                nif.BinaryVersion);
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
    ///     camera per frame, and their block indices are mapped to the authored billboard mode. Null (default) keeps
    ///     the legacy bake — the billboard node's full transform is baked in like any other node — so
    ///     the single-NIF / NPC / export paths are unaffected.
    /// </param>
    internal static void ComputeWorldTransforms(byte[] data, NifInfo nif,
        Dictionary<int, List<int>> nodeChildren, Dictionary<int, Matrix4x4> worldTransforms,
        Dictionary<string, NifAnimationParser.AnimPoseOverride>? animOverrides = null,
        bool treatRootsAsIdentity = false,
        Dictionary<int, NifBillboardMode>? billboardShapes = null)
    {
        // TES4-era NIFs (Oblivion, 10.x–20.0.0.5) compose the scene root's authored transform UNDER
        // the REFR placement instead of replacing it — ChorrolLODHouse01's root bakes a −90°X
        // Y-up→Z-up correction and the RFN dungeon halls bake 90/180° Z yaws; discarding those
        // renders the meshes sideways/rotated both standalone and placed. The identity-root rule
        // (see the treatRootsAsIdentity doc note) is decompile-anchored to FO3+/FNV, so it stays
        // for 20.2.0.7+ (McMarranWalls wallReg / monorail curves must not regress).
        if (NifVersions.IsTes4Era(nif.BinaryVersion))
        {
            treatRootsAsIdentity = false;
        }

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
                    treatRootsAsIdentity, billboardShapes);
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
                    : NifBlockParsers.ParseNiAVObjectTransform(data, nif.Blocks[i], nif.BsVersion, nif.BinaryVersion,
                        nif.IsBigEndian, nif.HasInlineStrings);
            }
        }
    }

    internal static void WalkNode(byte[] data, NifInfo nif, int blockIndex, Matrix4x4 parentTransform,
        Dictionary<int, List<int>> nodeChildren, Dictionary<int, Matrix4x4> worldTransforms,
        Dictionary<string, NifAnimationParser.AnimPoseOverride>? animOverrides = null,
        bool ignoreOwnTransform = false,
        Dictionary<int, NifBillboardMode>? billboardShapes = null,
        NifBillboardMode? activeBillboardMode = null)
    {
        var block = nif.Blocks[blockIndex];
        // A placed-reference's scene root has its own authored transform replaced by the REFR
        // placement (applied separately downstream), so discard it here. Children are still walked
        // relative to identity. See ComputeWorldTransforms' treatRootsAsIdentity note.
        var localTransform = ignoreOwnTransform
            ? Matrix4x4.Identity
            : NifBlockParsers.ParseNiAVObjectTransform(data, block, nif.BsVersion, nif.BinaryVersion, nif.IsBigEndian,
                nif.HasInlineStrings);

        // A billboard can itself be a scene root. The child-special-case below never sees that
        // topology, so establish the mode here and discard the root billboard's authored rotation
        // just as we do for a nested billboard node.
        if (billboardShapes != null && block.TypeName == "NiBillboardNode" &&
            activeBillboardMode is null)
        {
            activeBillboardMode = NifObjectBlockReader.ReadBillboardMode(
                data, block, nif.BinaryVersion, nif.IsBigEndian, nif.HasInlineStrings);
            localTransform = StripRotationPreserveUniformScale(localTransform);
        }

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
                    // node's world translation + uniform scale into the subtree parent (drop only its
                    // rotation) so the quad lands at the right spot and size in its authored local
                    // orientation, then flag every shape underneath for per-frame re-aiming.
                    var bbLocal = NifBlockParsers.ParseNiAVObjectTransform(data, nif.Blocks[childIdx],
                        nif.BsVersion, nif.BinaryVersion, nif.IsBigEndian, nif.HasInlineStrings);
                    var bbWorld = bbLocal * worldTransform;
                    var bbParent = StripRotationPreserveUniformScale(bbWorld);
                    var mode = NifObjectBlockReader.ReadBillboardMode(
                        data, nif.Blocks[childIdx], nif.BinaryVersion, nif.IsBigEndian,
                        nif.HasInlineStrings);
                    WalkNode(data, nif, childIdx, bbParent, nodeChildren, worldTransforms, animOverrides,
                        true, billboardShapes,
                        mode);
                }
                else
                {
                    WalkNode(data, nif, childIdx, worldTransform, nodeChildren, worldTransforms, animOverrides,
                        billboardShapes: billboardShapes, activeBillboardMode: activeBillboardMode);
                }
            }
            else if (ShapeTypes.Contains(childType))
            {
                // Shape inherits parent's world transform + its own local transform
                var shapeLocal =
                    NifBlockParsers.ParseNiAVObjectTransform(data, nif.Blocks[childIdx], nif.BsVersion,
                        nif.BinaryVersion, nif.IsBigEndian, nif.HasInlineStrings);
                worldTransforms[childIdx] = shapeLocal * worldTransform;
                if (activeBillboardMode is { } mode)
                {
                    billboardShapes?[childIdx] = mode;
                }
            }
        }
    }

    private static Matrix4x4 StripRotationPreserveUniformScale(Matrix4x4 transform)
    {
        var scale = new Vector3(transform.M11, transform.M12, transform.M13).Length();
        if (!float.IsFinite(scale) || scale <= float.Epsilon)
        {
            scale = 1f;
        }

        return Matrix4x4.CreateScale(scale) * Matrix4x4.CreateTranslation(transform.Translation);
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
                var children = NifBlockParsers.ParseNodeChildren(data, block, nif.BsVersion, nif.BinaryVersion, be,
                    nif.HasInlineStrings);
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
                data, block.DataOffset, block.DataOffset + block.Size, be, nif.HasInlineStrings, nif.BinaryVersion);

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

    /// <summary>Result of NiVisController analysis for a weapon NIF.</summary>
    internal sealed record VisControllerAnalysis(
        HashSet<int> VisControlledShapeIndices,
        List<ParentBoneShapeGroup> ParentBoneGroups);

    /// <summary>A group of shapes that should be attached to a specific skeleton bone.</summary>
    internal sealed record ParentBoneShapeGroup(string BoneName, string SourceNodeName, HashSet<int> ShapeIndices);
}
