using System.Numerics;
using FalloutXbox360Utils.Core.Formats.Nif.Conversion;
using FalloutXbox360Utils.Core.Formats.Nif.Geometry;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering;

/// <summary>
///     Extracts all renderable geometry from a NIF file with scene graph transforms applied.
///     Handles NiTriShapeData (explicit triangles) and NiTriStripsData (triangle strips).
///     Delegates to <see cref="NifSceneGraphWalker"/> for scene graph traversal,
///     <see cref="NifSkinningExtractor"/> for skeletal skinning, and
///     <see cref="NifBlockParsers"/> for NIF block parsing.
/// </summary>
internal static class NifGeometryExtractor
{
    /// <summary>
    ///     Reserved key for the root node's world transform in bone transform dictionaries.
    /// </summary>
    public const string RootTransformKey = "__root__";

    /// <summary>
    ///     Skeleton hierarchy: named bone transforms plus parent→child links.
    /// </summary>
    internal sealed record SkeletonHierarchy(
        Dictionary<string, Matrix4x4> BoneTransforms,
        List<(string Parent, string Child)> BoneLinks);

    /// <summary>
    ///     Extracts the full skeleton hierarchy including parent-child bone links.
    ///     Used for skeleton-only debug visualization.
    /// </summary>
    public static SkeletonHierarchy? ExtractSkeletonHierarchy(byte[] data, NifInfo nif,
        Dictionary<string, NifAnimationParser.AnimPoseOverride>? animOverrides = null)
    {
        var nodeChildren = new Dictionary<int, List<int>>();
        var nodeTransforms = new Dictionary<int, Matrix4x4>();
        var shapeDataMap = new Dictionary<int, int>();
        var shapeSkinInstanceMap = new Dictionary<int, int>();

        NifSceneGraphWalker.ClassifyBlocks(data, nif, nodeChildren, shapeDataMap, null, shapeSkinInstanceMap);
        NifSceneGraphWalker.ComputeWorldTransforms(data, nif, nodeChildren, nodeTransforms, animOverrides);

        var bones = new Dictionary<string, Matrix4x4>(StringComparer.OrdinalIgnoreCase);
        var blockToName = new Dictionary<int, string>();

        foreach (var (blockIndex, worldTransform) in nodeTransforms)
        {
            if (blockIndex < 0 || blockIndex >= nif.Blocks.Count) continue;
            var block = nif.Blocks[blockIndex];
            if (!NifSceneGraphWalker.NodeTypes.Contains(block.TypeName)) continue;
            var name = NifBlockParsers.ReadBlockName(data, block, nif);
            if (name == null) continue;
            bones[name] = worldTransform;
            blockToName[blockIndex] = name;
        }

        var links = new List<(string Parent, string Child)>();
        foreach (var (parentIdx, children) in nodeChildren)
        {
            if (!blockToName.TryGetValue(parentIdx, out var parentName)) continue;
            foreach (var childIdx in children)
            {
                if (blockToName.TryGetValue(childIdx, out var childName))
                    links.Add((parentName, childName));
            }
        }

        return bones.Count > 0 ? new SkeletonHierarchy(bones, links) : null;
    }

    public static Dictionary<string, Matrix4x4> ExtractNamedBoneTransforms(byte[] data, NifInfo nif,
        Dictionary<string, NifAnimationParser.AnimPoseOverride>? animOverrides = null)
    {
        var nodeChildren = new Dictionary<int, List<int>>();
        var nodeTransforms = new Dictionary<int, Matrix4x4>();
        var shapeDataMap = new Dictionary<int, int>();
        var shapeSkinInstanceMap = new Dictionary<int, int>();

        NifSceneGraphWalker.ClassifyBlocks(data, nif, nodeChildren, shapeDataMap, null, shapeSkinInstanceMap);
        NifSceneGraphWalker.ComputeWorldTransforms(data, nif, nodeChildren, nodeTransforms, animOverrides);

        var result = new Dictionary<string, Matrix4x4>(StringComparer.OrdinalIgnoreCase);
        foreach (var (blockIndex, worldTransform) in nodeTransforms)
        {
            if (blockIndex < 0 || blockIndex >= nif.Blocks.Count)
            {
                continue;
            }

            var block = nif.Blocks[blockIndex];
            if (!NifSceneGraphWalker.NodeTypes.Contains(block.TypeName))
            {
                continue;
            }

            var name = NifBlockParsers.ReadBlockName(data, block, nif);
            if (name != null)
            {
                result[name] = worldTransform;
            }
        }

        // Include root node transform under a reserved key for root-to-root mapping
        if (nif.Blocks.Count > 0 && nodeTransforms.TryGetValue(0, out var rootTransform))
        {
            result[RootTransformKey] = rootTransform;
        }

        return result;
    }

    /// <summary>
    ///     Extract all renderable geometry from a parsed NIF file.
    /// </summary>
    /// <param name="data">Raw NIF file bytes.</param>
    /// <param name="nif">Parsed NIF header info.</param>
    /// <param name="textureResolver">Optional texture resolver for extracting diffuse texture paths.</param>
    /// <param name="bindPoseOnly">
    ///     When true, skip skeletal skinning and node hierarchy transforms — return vertices in raw
    ///     bind-pose space. Useful for determining the mesh-local coordinate origin when compositing
    ///     multiple NIFs that need alignment.
    /// </param>
    public static NifRenderableModel? Extract(byte[] data, NifInfo nif,
        NifTextureResolver? textureResolver = null, bool bindPoseOnly = false,
        bool skipSkinning = false,
        Dictionary<string, Matrix4x4>? externalBoneTransforms = null,
        string? filterShapeName = null,
        Dictionary<string, Matrix4x4>? externalPoseDeltas = null,
        bool useDualQuaternionSkinning = false,
        float[]? preSkinMorphDeltas = null)
    {
        if (nif.Blocks.Count == 0)
        {
            return null;
        }

        // Build the scene graph: for each node, find its children and its transform
        var nodeTransforms = new Dictionary<int, Matrix4x4>();
        var nodeChildren = new Dictionary<int, List<int>>();
        var shapeDataMap = new Dictionary<int, int>(); // shape block → data block
        var shapePropertyMap = new Dictionary<int, List<int>>();

        var shapeSkinInstanceMap = new Dictionary<int, int>();
        NifSceneGraphWalker.ClassifyBlocks(data, nif, nodeChildren, shapeDataMap, shapePropertyMap, shapeSkinInstanceMap);

        // Filter shapes by name if requested (e.g., "NoHat" for hair NIFs to exclude "Hat" variant).
        // Hair NIFs may contain both "NoHat" (full hair) and "Hat" (trimmed for headgear) shapes;
        // the engine only attaches one based on equipment state.
        // Some hair NIFs have only one shape with no hat/nohat naming — keep all shapes in that case.
        if (filterShapeName != null)
        {
            var shapeNames = shapeDataMap.Keys
                .Select(idx => (idx, name: NifBlockParsers.ReadBlockName(data, nif.Blocks[idx], nif) ?? ""))
                .ToList();

            // Shape matching: "NoHat" matches shapes containing "NoHat"; "Hat" matches shapes
            // containing "Hat" but NOT "NoHat" (since "NoHat" contains "Hat" as a substring).
            bool MatchesFilter(string name) => filterShapeName.Equals("Hat", StringComparison.OrdinalIgnoreCase)
                ? name.Contains("Hat", StringComparison.OrdinalIgnoreCase) &&
                  !name.Contains("NoHat", StringComparison.OrdinalIgnoreCase)
                : name.Contains(filterShapeName, StringComparison.OrdinalIgnoreCase);

            var hasMatchingShape = shapeNames.Any(s => MatchesFilter(s.name));
            if (hasMatchingShape)
            {
                var toRemove = shapeNames
                    .Where(s => !MatchesFilter(s.name))
                    .Select(s => s.idx)
                    .ToList();
                foreach (var idx in toRemove)
                {
                    shapeDataMap.Remove(idx);
                    shapePropertyMap.Remove(idx);
                    shapeSkinInstanceMap.Remove(idx);
                }
            }
        }

        // Compute world transforms by walking the scene graph from root.
        // Static NIF transforms represent the rest pose — animation overrides are not applied
        // since NiControllerSequence keyframes define runtime motion, not the initial pose.
        NifSceneGraphWalker.ComputeWorldTransforms(data, nif, nodeChildren, nodeTransforms);

        Dictionary<int, ((int BoneIdx, float Weight)[][] PerVertexInfluences, Matrix4x4[] BoneSkinMatrices)>
            shapeSkinning;

        if (bindPoseOnly || skipSkinning)
        {
            // Skip skinning — vertices stay in bind-pose space
            shapeSkinning = [];
        }
        else
        {
            // Build skinning data for each skinned shape
            // NiSkinData has HasVertexWeights=true after NIF conversion (vertex weights are
            // written from BSPackedAdditionalGeometryData during the NiSkinData expansion)
            shapeSkinning = NifSkinningExtractor.BuildShapeSkinningData(data, nif, shapeSkinInstanceMap, shapeDataMap,
                nodeTransforms, externalBoneTransforms, externalPoseDeltas);
        }

        // bindPoseOnly: strip ALL transforms (vertices in raw mesh-local space) — used for alignment offset
        // skipSkinning: skip bone skinning but KEEP scene graph transforms — used for eye meshes
        var effectiveTransforms = bindPoseOnly ? new Dictionary<int, Matrix4x4>() : nodeTransforms;

        // Extract geometry from each shape block
        var model = new NifRenderableModel();

        foreach (var (shapeIndex, dataIndex) in shapeDataMap)
        {
            // Resolve texture paths and shader flags from shader properties
            var shapeName = NifBlockParsers.ReadBlockName(
                data,
                nif.Blocks[shapeIndex],
                nif);
            NifShaderTextureMetadata? shaderMetadata = null;
            string? diffusePath = null;
            string? normalMapPath = null;
            var isEmissive = false;
            var useVertexColors = false; // default to false; only enable if shader flags explicitly set Vertex_Colors
            var isDoubleSided = false;
            var hasAlphaBlend = false;
            var hasAlphaTest = false;
            byte alphaTestThreshold = 128;
            byte alphaTestFunction = 4; // GREATER
            byte srcBlendMode = 6; // SRC_ALPHA
            byte dstBlendMode = 7; // INV_SRC_ALPHA
            var materialAlpha = 1f;
            var isEyeEnvmap = false;
            var envMapScale = 0f;
            if (shapePropertyMap.TryGetValue(shapeIndex, out var propRefs))
            {
                if (textureResolver != null)
                {
                    shaderMetadata = NifTextureResolver.ReadShaderMetadata(
                        data,
                        nif,
                        propRefs);
                    diffusePath = shaderMetadata?.DiffusePath;
                    normalMapPath = shaderMetadata?.NormalMapPath;
                    isEmissive = shaderMetadata?.PropertyType ==
                        "BSShaderNoLightingProperty";

                    // BSShaderFlags2 bit 5 = Vertex_Colors: controls whether vertex colors
                    // should modulate the diffuse texture. Hair NIFs have vertex color data
                    // in the geometry but this flag unset, meaning the engine ignores them.
                    if (shaderMetadata?.ShaderFlags2 is uint shaderFlags2)
                    {
                        useVertexColors = (shaderFlags2 & (1u << 5)) != 0;
                    }

                    // BSShaderFlags bit 17 = Eye_Environment_Mapping + EnvMapScale for eye specular
                    if (shaderMetadata?.ShaderFlags is uint shaderFlags &&
                        shaderMetadata.EnvMapScale is float resolvedEnvMapScale)
                    {
                        isEyeEnvmap = (shaderFlags & 0x20000u) != 0;
                        envMapScale = resolvedEnvMapScale;
                    }
                }

                // NiStencilProperty DrawMode: DRAW_BOTH (3) = double-sided (no backface culling)
                isDoubleSided = NifBlockParsers.ReadIsDoubleSided(data, nif, propRefs);

                // NiAlphaProperty: alpha blend/test flags, threshold, comparison function, and blend modes
                NifBlockParsers.ReadAlphaProperty(data, nif, propRefs, out hasAlphaBlend, out hasAlphaTest,
                    out alphaTestThreshold, out alphaTestFunction, out srcBlendMode, out dstBlendMode);

                // NiMaterialProperty: alpha float (< 1.0 triggers blending even without NiAlphaProperty)
                materialAlpha = NifBlockParsers.ReadMaterialAlpha(data, nif, propRefs);
            }

            // Look up skinning data for this shape (null if not skinned or bind-pose mode)
            ((int BoneIdx, float Weight)[][] PerVertexInfluences, Matrix4x4[] BoneSkinMatrices)? skinning =
                shapeSkinning.TryGetValue(shapeIndex, out var sd) ? sd : null;

            // Apply pre-skinning morph deltas only to the first skinned shape (head mesh).
            // Once applied, clear the reference so subsequent shapes don't get them.
            float[]? shapeMorphDeltas = null;
            if (preSkinMorphDeltas != null && skinning != null)
            {
                shapeMorphDeltas = preSkinMorphDeltas;
                preSkinMorphDeltas = null;
            }

            var submesh = NifBlockParsers.ExtractSubmesh(data, nif, shapeIndex, dataIndex, effectiveTransforms,
                shapeName, shaderMetadata, diffusePath, normalMapPath, isEmissive, skinning, useVertexColors, isDoubleSided,
                hasAlphaBlend, hasAlphaTest, alphaTestThreshold, alphaTestFunction,
                isEyeEnvmap, envMapScale, srcBlendMode, dstBlendMode, materialAlpha,
                useDualQuaternionSkinning, shapeMorphDeltas);
            if (submesh != null)
            {
                model.Submeshes.Add(submesh);
                model.ExpandBounds(submesh.Positions);
            }
        }

        return model.HasGeometry ? model : null;
    }
}
