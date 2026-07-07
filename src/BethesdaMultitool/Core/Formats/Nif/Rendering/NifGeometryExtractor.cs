using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Inspection;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering;

/// <summary>
///     Extracts all renderable geometry from a NIF file with scene graph transforms applied.
///     Handles NiTriShapeData (explicit triangles) and NiTriStripsData (triangle strips).
///     Delegates to <see cref="NifSceneGraphWalker" /> for scene graph traversal,
///     <see cref="NifSkinningExtractor" /> for skeletal skinning, and
///     <see cref="NifBlockParsers" /> for NIF block parsing.
/// </summary>
internal static class NifGeometryExtractor
{
    /// <summary>
    ///     Reserved key for the root node's world transform in bone transform dictionaries.
    /// </summary>
    public const string RootTransformKey = "__root__";

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

    /// <summary>
    ///     Walks the NIF scene graph and returns each named node's world transform, optionally applying
    ///     animation pose overrides.
    /// </summary>
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
        float[]? preSkinMorphDeltas = null,
        HashSet<int>? excludeBlockIndices = null,
        Dictionary<string, NifAnimationParser.AnimPoseOverride>? animOverrides = null,
        bool treatRootsAsIdentity = false,
        bool collectBillboards = false,
        bool dropBoneAttachedShapes = false,
        IReadOnlyDictionary<string, string>? materialSwaps = null)
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
        NifSceneGraphWalker.ClassifyBlocks(data, nif, nodeChildren, shapeDataMap, shapePropertyMap,
            shapeSkinInstanceMap);

        // Remove specific block indices (e.g., shapes under NiVisController-targeted nodes)
        if (excludeBlockIndices is { Count: > 0 })
        {
            foreach (var idx in excludeBlockIndices)
            {
                shapeDataMap.Remove(idx);
                shapePropertyMap.Remove(idx);
                shapeSkinInstanceMap.Remove(idx);
            }
        }

        // Drop shapes the engine culls via the NiAVObject Hidden flag (Flags bit 0 = APP_CULLED). Bethesda
        // ships hidden proxy/template geometry inside otherwise-visible NIFs — e.g. NV_FencePickCleanGate's
        // untextured "C_gatepost" posts (Flags 0x0F vs the visible shapes' 0x0E) — which the game never
        // renders but the extractor otherwise bakes as stray white "duplicate" geometry.
        var hiddenShapes = shapeDataMap.Keys
            .Where(idx => NifBlockParsers.IsHiddenShape(data, nif.Blocks[idx], nif))
            .ToList();
        foreach (var idx in hiddenShapes)
        {
            shapeDataMap.Remove(idx);
            shapePropertyMap.Remove(idx);
            shapeSkinInstanceMap.Remove(idx);
        }

        // Filter shapes by name if requested (e.g., "NoHat" for hair NIFs to exclude "Hat" variant).
        // Hair NIFs may contain both "NoHat" (full hair) and "Hat" (trimmed for headgear) shapes,
        // and the engine only attaches one based on equipment state.
        // Some hair NIFs have only one shape with no hat/nohat naming, so keep all shapes in that case.
        if (filterShapeName != null)
        {
            var shapeNames = shapeDataMap.Keys
                .Select(idx => (idx, name: NifBlockParsers.ReadBlockName(data, nif.Blocks[idx], nif) ?? ""))
                .ToList();

            // Shape matching: "NoHat" matches shapes containing "NoHat"; "Hat" matches shapes
            // containing "Hat" but NOT "NoHat" (since "NoHat" contains "Hat" as a substring).
            bool MatchesFilter(string name)
            {
                return filterShapeName.Equals("Hat", StringComparison.OrdinalIgnoreCase)
                    ? name.Contains("Hat", StringComparison.OrdinalIgnoreCase) &&
                      !name.Contains("NoHat", StringComparison.OrdinalIgnoreCase)
                    : name.Contains(filterShapeName, StringComparison.OrdinalIgnoreCase);
            }

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

        // Drop embedded LOD geometry. Full NIFs (e.g. clutter\farm\windmill.nif) bundle a low-detail
        // "*_lod" subtree alongside the full-detail geometry for the engine's distance-LOD fade; the
        // viewer always wants full detail, so when BOTH a "_lod" shape and a non-"_lod" shape are
        // present we drop the "_lod" ones (otherwise the low- and high-detail meshes render on top of
        // each other). Standalone "*_lod.nif" files have only "_lod" shapes and are kept intact.
        var lodShapeNames = shapeDataMap.Keys
            .Select(idx => (idx, name: NifBlockParsers.ReadBlockName(data, nif.Blocks[idx], nif) ?? ""))
            .ToList();
        static bool IsLodShape(string name) => name.Contains("_lod", StringComparison.OrdinalIgnoreCase);
        if (lodShapeNames.Any(s => IsLodShape(s.name)) && lodShapeNames.Any(s => !IsLodShape(s.name)))
        {
            foreach (var (idx, _) in lodShapeNames.Where(s => IsLodShape(s.name)))
            {
                shapeDataMap.Remove(idx);
                shapePropertyMap.Remove(idx);
                shapeSkinInstanceMap.Remove(idx);
            }
        }

        // Drop rig-helper / physics-proxy geometry hung directly off skinning bones (e.g. the per-bone
        // boxes on FNV animated flags — clutter\flags\NV_NCR_Flag.NIF's TTail*/MTail*/Root boxes). The
        // engine deforms the skinned mesh by these bones and never renders geometry parented to them; the
        // worldspace reference path bakes bind pose, so without this the untextured bone boxes render as
        // stray "physics" blocks beside the flag. Only the worldspace path opts in — the NPC/weapon paths
        // legitimately attach geometry to bones. An UNSKINNED shape whose direct parent node is a skin
        // bone is the proxy; the skinned mesh itself carries its own skin instance and is never dropped.
        if (dropBoneAttachedShapes && shapeSkinInstanceMap.Count > 0)
        {
            var boneNodes = NifSceneGraphWalker.CollectSkinBoneNodeIndices(data, nif, shapeSkinInstanceMap);
            if (boneNodes.Count > 0)
            {
                var parentOf = new Dictionary<int, int>();
                foreach (var (parentIdx, kids) in nodeChildren)
                {
                    foreach (var kid in kids)
                    {
                        parentOf[kid] = parentIdx;
                    }
                }

                // Expand to the skeleton-ancestor nodes between each weighted bone and the Scene Root: a
                // cloth rig hangs a proxy box off the NonAccum "Root" too, which is an ancestor of the tail
                // bones rather than a weighted bone itself. Walk up to — but never including — block 0 (the
                // Scene Root), where the real static geometry lives.
                foreach (var bone in boneNodes.ToList())
                {
                    var cur = bone;
                    while (parentOf.TryGetValue(cur, out var parent) && parent != 0 && boneNodes.Add(parent))
                    {
                        cur = parent;
                    }
                }

                var boneAttached = shapeDataMap.Keys
                    .Where(shapeIdx => !shapeSkinInstanceMap.ContainsKey(shapeIdx) &&
                                       parentOf.TryGetValue(shapeIdx, out var parent) && boneNodes.Contains(parent))
                    .ToList();
                foreach (var idx in boneAttached)
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
        // When collectBillboards is set, shapes under a NiBillboardNode bake with the node's rotation
        // dropped (translation kept) and their indices land in billboardShapes so the caller can flag
        // them for per-frame camera facing.
        var billboardShapes = collectBillboards ? new HashSet<int>() : null;
        NifSceneGraphWalker.ComputeWorldTransforms(data, nif, nodeChildren, nodeTransforms, animOverrides,
            treatRootsAsIdentity, billboardShapes);

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
            // Default true: in practice, both hair and environment NIFs ship with BSShaderFlags2
            // bit 5 (Vertex_Colors) cleared, yet the engine applies vertex colors when present
            // (e.g., rust shading on nv_prospectorsaloon's metal sheets). Treat the flag as
            // advisory, not gating. (Skyrim+ BSLightingShaderProperty overrides this below — its
            // SLSF flags ARE authoritative.)
            var useVertexColors = true;
            var useVertexAlpha = true;
            string? specularMapPath = null;
            string? gradientMapPath = null;
            var gradientMapV = 0.5f;
            var isDoubleSided = false;
            var hasAlphaBlend = false;
            var hasAlphaTest = false;
            byte alphaTestThreshold = 128;
            byte alphaTestFunction = 4; // GREATER
            byte srcBlendMode = 6; // SRC_ALPHA
            byte dstBlendMode = 7; // INV_SRC_ALPHA
            var materialAlpha = 1f;
            var materialGlossiness = 10f;
            var specularColor = (R: 0f, G: 0f, B: 0f);
            var isEyeEnvmap = false;
            var envMapScale = 0f;
            var isDecal = false;
            var effectTint = (R: 1f, G: 1f, B: 1f);
            (float, float, float, float)? effectFalloff = null;
            SkyObjectType? skyType = null;
            List<int>? propRefs = null;
            if (shapePropertyMap.TryGetValue(shapeIndex, out propRefs))
            {
                // Shader metadata drives vertex color modulation, emissive, and eye envmap flags
                // independently of whether textures can be resolved — read it unconditionally so
                // rendering without a texture BSA still respects these flags.
                shaderMetadata = NifTextureResolver.ReadShaderMetadata(data, nif, propRefs);
                // Self-illuminated (unlit) shaders: FO3/FNV BSShaderNoLightingProperty AND the Skyrim/
                // SE/FO4 BSEffectShaderProperty (fire, magic, glow, light shafts). Without the effect
                // case, every Skyrim effect shape was N·L-lit and rendered as a faceted, shaded "gem"
                // instead of a flat self-lit billboard.
                var isEffectShader = shaderMetadata?.PropertyType == "BSEffectShaderProperty";
                isEmissive = shaderMetadata?.PropertyType is "BSShaderNoLightingProperty"
                    or "BSEffectShaderProperty";

                // SLSF1_Refraction (0x8000) / SLSF1_Fire_Refraction (0x10000) on a BSLightingShaderProperty
                // mark a screen-space heat-haze / distortion plane (e.g. the campfire's VaporTileNormal_n
                // billboard). We don't do screen-space refraction, and such a plane ships a NORMAL map in its
                // diffuse slot with no NiAlphaProperty — drawing it opaque produces a blue/purple "gem" that
                // also occludes the real fire behind it. Skip it so the distorted effect shows through.
                if (shaderMetadata is { PropertyType: "BSLightingShaderProperty", ShaderFlags: { } lsRefract }
                    && (lsRefract & 0x18000u) != 0)
                {
                    continue;
                }

                // BSShaderFlags bit 17 = Eye_Environment_Mapping + EnvMapScale for eye specular
                if (shaderMetadata?.ShaderFlags is uint shaderFlags &&
                    shaderMetadata.EnvMapScale is float resolvedEnvMapScale)
                {
                    isEyeEnvmap = (shaderFlags & 0x20000u) != 0;
                    envMapScale = resolvedEnvMapScale;
                }

                // Decal / Dynamic_Decal — bits 26/27 in both the FO3/FNV BSShaderFlags
                // (Decal_Single_Pass / Dynamic_Decal_Single_Pass) and the Skyrim+/FO4 SLSF1 enum.
                // The engine draws these coplanar overlays (grime, cracks, posters) with a depth
                // bias; the renderer keys a biased PSO off this flag.
                if (shaderMetadata?.ShaderFlags is uint decalFlags && (decalFlags & 0x0C000000u) != 0)
                {
                    isDecal = true;
                }

                // Skyrim+/FO4/FO76 BSLightingShaderProperty declares vertex-channel usage explicitly:
                // SLSF2_Vertex_Colors (flags2 bit 5) gates the RGB tint and SLSF1_Vertex_Alpha
                // (flags1 bit 3) gates the alpha channel. FO4 foliage stores WIND WEIGHT in vertex
                // alpha (trunk / thick branches = 0) with Vertex_Alpha CLEAR — multiplying that
                // alpha into the cutout test discards the entire trunk (TreeElmFree01) — and ships
                // non-visual data in RGB with Vertex_Colors clear (VineHanging05 tinting black).
                if (shaderMetadata is
                    { PropertyType: "BSLightingShaderProperty", ShaderFlags: { } lsf1, ShaderFlags2: { } lsf2 })
                {
                    useVertexColors = (lsf2 & 0x20u) != 0;
                    useVertexAlpha = (lsf1 & 0x8u) != 0;
                }

                if (textureResolver != null)
                {
                    // Fall back to a legacy NiTexturingProperty base map (e.g. effects\ambient\
                    // fxvulturesNV.nif) when no BSShader* property supplied a diffuse path — without
                    // this the mesh resolves no texture and renders as the 1x1 white pixel.
                    diffusePath = shaderMetadata?.DiffusePath
                                  ?? NifTexturingPropertyReader.ResolveBaseTexturePath(data, nif, propRefs);
                    normalMapPath = shaderMetadata?.NormalMapPath;
                }

                // NiStencilProperty DrawMode: DRAW_BOTH (3) = double-sided (no backface culling)
                isDoubleSided = NifBlockParsers.ReadIsDoubleSided(data, nif, propRefs);

                // NiAlphaProperty: alpha blend/test flags, threshold, comparison function, and blend modes
                NifBlockParsers.ReadAlphaProperty(data, nif, propRefs, out hasAlphaBlend, out hasAlphaTest,
                    out alphaTestThreshold, out alphaTestFunction, out srcBlendMode, out dstBlendMode);

                // Effect shaders are blended by the engine. When the shape carries no explicit
                // NiAlphaProperty, default to alpha blending so the effect reads as a translucent glow
                // rather than an opaque "gem" (keeps the SRC_ALPHA / INV_SRC_ALPHA defaults — additive
                // glows that DO ship a NiAlphaProperty keep their authored ONE dst-blend above).
                if (isEffectShader && !hasAlphaBlend && !hasAlphaTest)
                {
                    hasAlphaBlend = true;
                }

                // NiMaterialProperty: alpha float (< 1.0 triggers blending even without NiAlphaProperty)
                materialAlpha = NifBlockParsers.ReadMaterialAlpha(data, nif, propRefs);
                materialGlossiness = NifBlockParsers.ReadMaterialGlossiness(data, nif, propRefs);
                specularColor = NifBlockParsers.ReadMaterialSpecularColor(data, nif, propRefs);

                // FO4/FO76 external material (.bgsm/.bgem): the engine gives the material's render state
                // priority over the NIF's inline NiAlphaProperty/NiStencilProperty — the NIF commonly
                // authors threshold 128 while the material lowers it (Vine.BGSM = 92) and enables
                // two-sided, so trusting the inline state alpha-erodes foliage and backface-culls half
                // its leaf cards. Also expand the material's texture slots to their real .dds paths:
                // the normal map (slot 1) otherwise never reaches the submesh (the material path rode in
                // the diffuse slot only) — this is what enables FO4 bump mapping downstream.
                var materialPath = shaderMetadata?.MaterialPath
                                   ?? (diffusePath is not null &&
                                       (diffusePath.EndsWith(".bgsm", StringComparison.OrdinalIgnoreCase) ||
                                        diffusePath.EndsWith(".bgem", StringComparison.OrdinalIgnoreCase))
                                       ? diffusePath
                                       : null);

                // FO4/FO76 MSWP material swap (REFR XMSP): substitute the placement's replacement
                // material BEFORE reading render state, so alpha/two-sided/specular/gradient and the
                // texture-slot expansion below all come from the swap target. Normalizing here makes
                // the lookup match the swap table's pre-normalized keys regardless of the NIF's baked
                // casing/absolute-build-path form. Covers both the shader-declared MaterialPath and
                // the raw-.bgsm-in-diffuse fallback, since both merge into this one local.
                if (materialPath is not null && materialSwaps is not null &&
                    materialSwaps.TryGetValue(NifTexturePathUtility.Normalize(materialPath), out var swapped))
                {
                    materialPath = swapped;
                }

                if (materialPath is not null && textureResolver?.TryGetMaterial(materialPath) is { } bgsm)
                {
                    if (bgsm.IsEffect)
                    {
                        // Effect materials: only apply alpha state the material actually enables — the
                        // engine blends effect shaders regardless, and the forced-blend default below
                        // must survive a .bgem that authors neither blend nor test.
                        if (bgsm.AlphaBlendEnabled || bgsm.AlphaTestEnabled)
                        {
                            hasAlphaBlend = bgsm.AlphaBlendEnabled;
                            hasAlphaTest = bgsm.AlphaTestEnabled;
                            alphaTestThreshold = bgsm.AlphaTestThreshold;
                            alphaTestFunction = 4; // GREATER — material thresholding implies greater mode
                            srcBlendMode = bgsm.SourceBlendMode;
                            dstBlendMode = bgsm.DestinationBlendMode;
                        }

                        // Scene-light two classes of effect material the blanket effect-shader
                        // emissive otherwise renders full-bright at night:
                        //  • "Effect Lighting" — the material explicitly asks to be lit;
                        //  • decals (HitTechStain.BGEM on HitExtAStainsWall2x2 authors effect-
                        //    lighting OFF + decal ON) — in-engine these read dark at night only
                        //    because the global night imagespace/tonemap dims unlit surfaces too,
                        //    which this viewer doesn't model. A decal is a surface overlay, so
                        //    shading it with the scene is the faithful approximation; genuinely
                        //    glowing effects (fires, beams) are not decals and stay emissive.
                        if (bgsm.EffectLightingEnabled || bgsm.Decal)
                        {
                            isEmissive = false;
                        }

                        // The effect-fragment terms the engine multiplies into every pixel
                        // (fo76utils getDiffuseColor_Effect): rgb ×= baseColor × scale, and — when
                        // falloff is enabled — alpha ramps by |N·V| between the start/stop angles.
                        // Without these, crossed-plane mist blobs (MistLargeRoundDusty01) rendered
                        // every plane at full white opacity and additively clipped whole scenes.
                        var tintScale = Math.Min(bgsm.BaseColorScale, 1f);
                        effectTint = (
                            bgsm.BaseColor.X * tintScale,
                            bgsm.BaseColor.Y * tintScale,
                            bgsm.BaseColor.Z * tintScale);
                        if (bgsm.FalloffEnabled)
                        {
                            effectFalloff = (
                                bgsm.FalloffStartAngle, bgsm.FalloffStopAngle,
                                bgsm.FalloffStartOpacity, bgsm.FalloffStopOpacity);
                        }
                    }
                    else
                    {
                        hasAlphaBlend = bgsm.AlphaBlendEnabled;
                        hasAlphaTest = bgsm.AlphaTestEnabled;
                        alphaTestThreshold = bgsm.AlphaTestThreshold;
                        alphaTestFunction = 4;
                        srcBlendMode = bgsm.SourceBlendMode;
                        dstBlendMode = bgsm.DestinationBlendMode;
                        if (bgsm.SpecularEnabled && bgsm.SpecularStrength > 0f)
                        {
                            var s = Math.Min(bgsm.SpecularStrength, 1f);
                            specularColor = (bgsm.SpecularColor.X * s, bgsm.SpecularColor.Y * s,
                                bgsm.SpecularColor.Z * s);
                            // Smoothness is 0–1 (not an exponent); map to the Blinn-Phong exponent the
                            // shader expects — rough ≈ 4, mirror-smooth ≈ 128.
                            var smooth = Math.Clamp(bgsm.SpecularSmoothness, 0f, 1f);
                            materialGlossiness = 4f + (smooth * smooth * 124f);
                            // Per-texel mask: FO4 SmoothSpec (slot 6) / FO76 reflectance (slot 8).
                            // Without it the shader keeps specular OFF for BC5 normal maps — a
                            // uniform mask blows out whole scenes.
                            specularMapPath = bgsm.GetTexturePath(6) ?? bgsm.GetTexturePath(8);
                        }
                    }

                    isDoubleSided |= bgsm.TwoSided;
                    isDecal |= bgsm.Decal;
                    materialAlpha = Math.Min(materialAlpha, Math.Min(bgsm.Alpha, 1f));
                    if (!string.IsNullOrEmpty(bgsm.Diffuse))
                    {
                        diffusePath = bgsm.Diffuse;
                    }

                    if (!string.IsNullOrEmpty(bgsm.Normal))
                    {
                        normalMapPath = bgsm.Normal;
                    }

                    if (!string.IsNullOrEmpty(bgsm.GradientMap))
                    {
                        gradientMapPath = bgsm.GradientMap;
                        gradientMapV = bgsm.GradientMapV;
                    }
                }

                // WaterShaderProperty: placeable water (waterp*, cave/pool/reflecting-pool water) ships
                // NO diffuse texture — the engine renders it through its dedicated water system. Tag it
                // with the water-surface sentinel + force alpha-blend so it reads as a see-through water
                // surface. In the D3D12 worldspace viewer, ReferenceMeshCache12 recognizes this sentinel
                // and DIVERTS the submesh to the dedicated WaterRenderer12 (real Fresnel/ripple/depth-
                // fade shader, same as cell water) — it never draws as a reference slab. Other paths
                // (standalone NIF render/export) have no water renderer, so the sentinel maps to the
                // built-in translucent water-blue tile; without it the submesh would fall back to the
                // 1×1 white pixel and render as an opaque white slab.
                foreach (var propRef in propRefs)
                {
                    if (propRef < 0 || propRef >= nif.Blocks.Count)
                    {
                        continue;
                    }

                    // "WaterShaderProperty" is the Oblivion/legacy type; Skyrim/FO3/FNV placeable water ships
                    // the BS-prefixed "BSWaterShaderProperty" (nif.xml: "different from WaterShaderProperty").
                    // Match both, or Skyrim water (Water\Tundra*…WaterA.nif) misses the water path and renders
                    // as an opaque white slab.
                    var waterType = nif.Blocks[propRef].TypeName;
                    if (string.Equals(waterType, "WaterShaderProperty", StringComparison.Ordinal) ||
                        string.Equals(waterType, "BSWaterShaderProperty", StringComparison.Ordinal))
                    {
                        diffusePath = RenderableSubmesh.WaterSurfaceTexturePath;
                        hasAlphaBlend = true;
                        if (materialAlpha >= 0.999f) materialAlpha = 0.5f;
                        break;
                    }
                }

                // SkyShaderProperty (FO3/FNV) / BSSkyShaderProperty (Skyrim+): a layer of a climate sky
                // NIF (atmosphere / stars / clouds). Classify it by its Sky Object Type and take the baked
                // texture from the sky-shader's own FileName (the gradient atmosphere layer has none). The
                // sky renderer keys blend + texture-source off SkyType — version-driven, not a heuristic.
                foreach (var propRef in propRefs)
                {
                    if (propRef < 0 || propRef >= nif.Blocks.Count)
                    {
                        continue;
                    }

                    var typeName = nif.Blocks[propRef].TypeName;
                    if (typeName is not ("SkyShaderProperty" or "BSSkyShaderProperty"))
                    {
                        continue;
                    }

                    var block = nif.Blocks[propRef];
                    skyType = SkyNifTextureHarvester.ReadSkyObjectType(data, block.DataOffset, block.Size, nif.IsBigEndian);
                    if (SkyNifTextureHarvester.TryReadSkyShaderProperty(
                            data, block.DataOffset, block.Size, nif.IsBigEndian, out _, out var skyFile) &&
                        !string.IsNullOrWhiteSpace(skyFile))
                    {
                        diffusePath = skyFile;
                    }

                    break;
                }
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
                shapeName, shaderMetadata, diffusePath, normalMapPath, isEmissive, skinning, useVertexColors,
                isDoubleSided,
                hasAlphaBlend, hasAlphaTest, alphaTestThreshold, alphaTestFunction,
                isEyeEnvmap, envMapScale, srcBlendMode, dstBlendMode, materialAlpha, materialGlossiness,
                specularColor, useDualQuaternionSkinning, shapeMorphDeltas);
            if (submesh != null)
            {
                submesh.SourceBlockIndex = shapeIndex;
                submesh.SkyType = skyType;
                submesh.IsBillboard = billboardShapes?.Contains(shapeIndex) == true;
                submesh.SpecularMapTexturePath = specularMapPath;
                submesh.GradientMapTexturePath = gradientMapPath;
                submesh.GradientMapV = gradientMapV;
                submesh.IsDecal = isDecal;
                submesh.EffectTint = effectTint;
                submesh.EffectFalloff = effectFalloff;

                // SLSF1_Vertex_Alpha clear — the vertex alpha channel is engine data (wind weight),
                // not opacity. Neutralize it in place so every consumer (GPU alpha test, CPU
                // rasterizer, GLB export) stops treating it as a fade.
                if (!useVertexAlpha && submesh.VertexColors is { } vertexColors)
                {
                    for (var ci = 3; ci < vertexColors.Length; ci += 4)
                    {
                        vertexColors[ci] = 255;
                    }
                }

                if (propRefs != null &&
                    submesh.UVs != null &&
                    NifTextureAnimationEvaluator.TryResolveBaseUvTransform(data, nif, propRefs, out var uvTransform))
                {
                    NifTextureAnimationEvaluator.ApplyInPlace(submesh.UVs, uvTransform);
                }

                // Read animated emissive color from NiMaterialColorController chain
                if (propRefs != null)
                {
                    var animEmissive = NifBlockParsers.ReadAnimatedEmissiveColor(data, nif, propRefs);
                    if (animEmissive.HasValue)
                    {
                        submesh.AnimatedEmissiveColor = animEmissive;
                        submesh.IsEmissive = true;
                    }
                }

                if (skinning != null) model.WasSkinned = true;
                model.Submeshes.Add(submesh);
                model.ExpandBounds(submesh.Positions);
            }
        }

        // BSMeshLODTriShape far-slice fallbacks (LOD0 empty → LOD1/LOD2 extracted) are distant
        // imposters the engine never draws up close. Alongside real near-content siblings they
        // z-fight it — workshop_Res01ModernRubble02B's LOD2-only floor slab sits exactly coplanar
        // with its separate _Foundation ref's slab — so drop them. When the WHOLE model is far
        // slices (VineHanging05 authors everything in LOD2), keep them or the reference vanishes.
        if (model.Submeshes.Exists(static s => s.IsFarLodFallback) &&
            model.Submeshes.Exists(static s => !s.IsFarLodFallback))
        {
            model.Submeshes.RemoveAll(static s => s.IsFarLodFallback);
        }

        // Bake NiParticleSystem effects (NV whirlwinds, Oblivion Gates, FXDust) into camera-facing quad
        // clouds. Only on the worldspace/reference path (collectBillboards) — the same path that drives the
        // leaf-billboard VS these reuse; NPC/export paths don't render ambient particle systems. Skipped in
        // bind-pose alignment mode (no scene-graph transforms there).
        if (collectBillboards && !bindPoseOnly)
        {
            Particles.NifParticleSystemExtractor.Append(
                data, nif, model, nodeTransforms, nodeChildren, treatRootsAsIdentity);
        }

        return model.HasGeometry ? model : null;
    }

    /// <summary>
    ///     Skeleton hierarchy: named bone transforms plus parent→child links.
    /// </summary>
    internal sealed record SkeletonHierarchy(
        Dictionary<string, Matrix4x4> BoneTransforms,
        List<(string Parent, string Child)> BoneLinks);
}

