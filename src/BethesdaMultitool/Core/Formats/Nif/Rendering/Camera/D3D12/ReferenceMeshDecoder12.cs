#if WINDOWS_GUI
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using BethesdaMultitool.Core.Formats.Bsa.Extraction;
using BethesdaMultitool.Core.Formats.Esm.Plugin.AssetPacking;
using BethesdaMultitool.Core.Formats.Nif.Collision;
using BethesdaMultitool.Core.Formats.Nif.Conversion;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Inspection;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;
using BethesdaMultitool.Core.Formats.SpeedTree;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Camera.D3D12;

/// <summary>
///     Off-render-thread CPU decode for <see cref="ReferenceMeshCache12" />: extracts a model path
///     from the mesh archives and turns it into a <see cref="DecodedNifMesh12" /> (NIF parse/convert
///     /geometry-extract, or procedural SpeedTree generation), plus the decoded-mesh sizing and
///     persistent-payload conversion the cache layers on top. Holds no GPU state — pure data in/out —
///     so multiple decode tasks run it concurrently (the archive extract is lock-free; the
///     missing/skinned tallies are interlocked).
/// </summary>
internal sealed class ReferenceMeshDecoder12
{
    private readonly MeshArchiveSet _meshArchives;
    private readonly NifTextureResolver _textureResolver;

    // archive-path (trees\<name>.spt) → recorded tree height (TREE OBND Z-extent), so the procedural
    // SpeedTree generator can size each tree from the ESM data rather than a magic constant.
    private readonly IReadOnlyDictionary<string, float>? _speedTreeHeights;

    // archive-path (trees\<name>.spt) → the leaf atlas from the TREE record's ICON (the engine's
    // authoritative leaf texture; the .spt's own dev-era material often never shipped).
    private readonly IReadOnlyDictionary<string, string>? _speedTreeLeafTextures;

    // archive-path (trees\<name>.spt) → the TREE record's CNAM dimming pair (the engine's canopy-depth
    // darkening inputs, CSpeedTreeRT::Set{Leaf,Branch}DimmingScalar).
    private readonly IReadOnlyDictionary<string, SpeedTreeDimming>? _speedTreeDimming;

    private int _totalMissingModelPaths;
    private int _totalSkinnedModelPaths;

    public ReferenceMeshDecoder12(
        MeshArchiveSet meshArchives,
        NifTextureResolver textureResolver,
        IReadOnlyDictionary<string, float>? speedTreeHeights,
        IReadOnlyDictionary<string, string>? speedTreeLeafTextures,
        IReadOnlyDictionary<string, SpeedTreeDimming>? speedTreeDimming = null)
    {
        _meshArchives = meshArchives;
        _textureResolver = textureResolver;
        _speedTreeHeights = speedTreeHeights;
        _speedTreeLeafTextures = speedTreeLeafTextures;
        _speedTreeDimming = speedTreeDimming;
    }

    public int TotalMissingModelPaths => Volatile.Read(ref _totalMissingModelPaths);
    public int TotalSkinnedModelPaths => Volatile.Read(ref _totalSkinnedModelPaths);

    /// <summary>Extracts <paramref name="modelPath" /> from the mesh archives and decodes it into a
    /// <see cref="DecodedNifMesh12" /> (NIF parse or procedural SpeedTree); null when missing or unusable.</summary>
    /// <param name="overrides">
    ///     Optional MODS alternate-texture overrides (shape name → texture paths, case-insensitive).
    ///     When a decoded submesh's <c>ShapeName</c> matches, its diffuse/normal paths are substituted so
    ///     one shared NIF renders per-base textures (e.g. per-vendor billboard ads). The caller keys the
    ///     mesh cache on a variant of the model path so each override set is its own cached mesh.
    /// </param>
    /// <param name="materialSwaps">
    ///     Optional FO4/FO76 MSWP material swaps (normalized original → replacement <c>.bgsm</c> path).
    ///     Applied inside <see cref="NifGeometryExtractor" /> at material-resolution time — before the
    ///     material's render state is read — so alpha/two-sided/specular/gradient all come from the
    ///     REPLACEMENT material. Same variant-keyed caching contract as <paramref name="overrides" />.
    /// </param>
    public DecodedNifMesh12? DecodeMesh(
        string modelPath,
        IReadOnlyDictionary<string, ShapeTextureOverride>? overrides = null,
        IReadOnlyDictionary<string, string>? materialSwaps = null,
        float? gradientMapVOverride = null)
    {
        var started = RendererProfilerTrace.IsEnabled ? Stopwatch.GetTimestamp() : 0;
        var lookupPath = NormalizeModelPath(modelPath);
        var result = "missing";
        var nifBytes = 0;
        var submeshCount = 0;

        try
        {
            // No lock needed: MeshArchiveSet.TryExtractFile resolves under its own cache lock and
            // BsaExtractor.ExtractFile is memory-mapped/lock-free, so concurrent decode tasks extract
            // in parallel. This is what actually parallelizes mesh streaming (removing the old coarse
            // archive lock that serialized every decode despite the wider task pool).
            if (!_meshArchives.TryExtractFile(lookupPath, out var nifData, out _) || nifData.Length == 0)
            {
                Interlocked.Increment(ref _totalMissingModelPaths);
                return null;
            }

            nifBytes = nifData.Length;

            // Decoded Havok collision soup (set only on the NIF path below; SpeedTree has none).
            Vector3[]? collisionPositions = null;
            int[]? collisionTriangles = null;

            NifRenderableModel? model;
            if (lookupPath.EndsWith(".spt", StringComparison.OrdinalIgnoreCase))
            {
                // SpeedTree tree: the .spt is a procedural recipe, not a NIF. Parse it and generate
                // geometry (branch tubes + scattered leaf cards) via SptGeometryBuilder. Size is
                // data-driven from the TREE record's OBND height; the seed is the .spt's own embedded
                // value (token 2005 == TREE SNAM), falling back to a stable per-path hash.
                var spt = SptFile.TryParse(nifData);
                if (spt is null)
                {
                    result = "spt-parse-failed";
                    return null;
                }

                float? targetHeight = null;
                if (_speedTreeHeights is not null && _speedTreeHeights.TryGetValue(lookupPath, out var h))
                {
                    targetHeight = h;
                }

                // The engine sources the leaf atlas from the TREE record's ICON, not the .spt's dev-era
                // material (which often never shipped). Use it when we have it for this .spt.
                string? leafTexture = null;
                _speedTreeLeafTextures?.TryGetValue(lookupPath, out leafTexture);

                // TREE CNAM canopy-depth dimming: the engine overrides the .spt's leaf-dimming token with
                // the record's LeafDimmingValue and supplies BranchDimmingValue (which has no .spt source).
                float? leafDimming = null;
                float? branchDimming = null;
                if (_speedTreeDimming is not null && _speedTreeDimming.TryGetValue(lookupPath, out var dim))
                {
                    leafDimming = dim.Leaf;
                    branchDimming = dim.Branch;
                }

                var seed = spt.General.Token2005 != 0 ? spt.General.Token2005 : StableSeed(lookupPath);
                var sptOptions = SptGeometryOptions.FromEnvironment() with
                {
                    TargetHeight = targetHeight,
                    LeafTextureOverride = leafTexture,
                    LeafDimming = leafDimming,
                    BranchDimming = branchDimming,
                    // The live D3D12 viewer re-faces each leaf card to the camera per frame in the
                    // leaf-billboard vertex shader, so emit GPU billboard cards (center + offset).
                    LeafBillboard = true,
                };
                model = SptGeometryBuilder.Build(spt, seed, sptOptions);
            }
            else
            {
                // Cheap endianness probe avoids fully parsing the source NIF just to decide whether it
                // needs big-endian conversion. A determinate probe (true/false) is byte-for-byte the same
                // verdict a full parse would give; null means the header wasn't cheaply readable, so fall
                // back to a full parse. This drops the BE mesh path from three parses (source +
                // convert-internal + converted output) to two.
                var probe = NifParser.TryProbeEndianness(nifData);
                NifInfo? sourceInfo = null;
                bool isBigEndian;
                if (probe.HasValue)
                {
                    isBigEndian = probe.Value;
                }
                else
                {
                    sourceInfo = NifParser.Parse(nifData);
                    if (sourceInfo is null)
                    {
                        result = "parse-failed";
                        return null;
                    }

                    isBigEndian = sourceInfo.IsBigEndian;
                }

                NifInfo? nif;
                if (isBigEndian)
                {
                    var converted = NifConverter.Convert(nifData);
                    if (!converted.Success || converted.OutputData is null)
                    {
                        result = "convert-failed";
                        return null;
                    }

                    nifData = converted.OutputData;
                    nif = NifParser.Parse(nifData);
                    if (nif is null)
                    {
                        result = "converted-parse-failed";
                        return null;
                    }
                }
                else
                {
                    // LE source: reuse the fallback parse if we already made one, else parse now.
                    nif = sourceInfo ?? NifParser.Parse(nifData);
                    if (nif is null)
                    {
                        result = "parse-failed";
                        return null;
                    }
                }

                model = NifGeometryExtractor.Extract(
                    nifData, nif,
                    textureResolver: _textureResolver,
                    bindPoseOnly: false,
                    skipSkinning: true,
                    // MSWP per-placement re-skin: substituted where the extractor resolves each
                    // shape's .bgsm, so the replacement material's render state flows through.
                    materialSwaps: materialSwaps,
                    // Placed references get their scene-root world transform from the REFR placement
                    // (RenderableReference.ComposeWorldMatrix). Discard the root node's OWN authored
                    // transform so a non-identity root rotation (e.g. McMarranWalls wallReg at 90°,
                    // monorail curves at 15°) is not injected twice — which rendered those meshes
                    // rotated by the root angle (perpendicular for the 90° walls).
                    treatRootsAsIdentity: true,
                    // Flag geometry under a NiBillboardNode (e.g. NVashpile smoke glow) so the renderer
                    // re-aims it at the camera per frame instead of using the baked-in orientation.
                    collectBillboards: true,
                    // Drop the per-bone proxy boxes on animated cloth (e.g. NV_NCR_Flag.NIF) that
                    // would otherwise render as untextured "havok" blocks beside the flag.
                    dropBoneAttachedShapes: true);

                // Decode Havok (bhk*) collision geometry from the same (converted, LE) buffer/parse,
                // off the render thread. Walk mode prefers this gapless physics mesh over the visual
                // submeshes so the camera doesn't fall through plank gaps. Null when the NIF has none.
                if (HavokCollisionExtractor.TryExtract(nifData, nif) is { } havok)
                {
                    collisionPositions = havok.Positions;
                    collisionTriangles = havok.Triangles;
                }
            }

            if (model is null)
            {
                result = "extract-failed";
                return null;
            }
            if (model.WasSkinned)
            {
                result = "skinned";
                Interlocked.Increment(ref _totalSkinnedModelPaths);
                return null;
            }
            if (!model.HasGeometry)
            {
                result = "empty";
                return null;
            }

            var submeshes = new List<DecodedSubmesh12>(model.Submeshes.Count);
            foreach (var sub in model.Submeshes)
            {
                if (sub.Positions.Length == 0 || sub.Triangles.Length == 0) continue;

                // MODS alternate-texture re-skin: if the base record overrides this named shape, swap in
                // its TXST-resolved paths. Resolved before hasBump so an override normal map is honored.
                var diffusePath = sub.DiffuseTexturePath;
                var normalPath = sub.NormalMapTexturePath;
                if (overrides is not null && sub.ShapeName is { Length: > 0 } shapeName &&
                    overrides.TryGetValue(shapeName, out var textureOverride))
                {
                    if (textureOverride.Diffuse is not null) diffusePath = textureOverride.Diffuse;
                    if (textureOverride.Normal is not null) normalPath = textureOverride.Normal;
                }

                var alphaState = NifAlphaClassifier.Classify(sub, diffuseTexture: null);
                var alphaRenderMode = alphaState.RenderMode == NifAlphaRenderMode.AlphaToCoverage
                    ? NifAlphaRenderMode.Blend
                    : alphaState.RenderMode;
                var hasBump = sub.Tangents != null &&
                              sub.Bitangents != null &&
                              !string.IsNullOrEmpty(normalPath);

                var specularColor = new Vector3(sub.SpecularColor.R, sub.SpecularColor.G, sub.SpecularColor.B);
                var specularEnabled = ComputeSpecularEnabled(sub);
                // The env term needs the _s map (mask + per-texel smoothness) even when the
                // specular ENABLE flag is clear — fo76utils computes envMapScale from the raw
                // strength before zeroing disabled specular.
                var hasEnvMap = !string.IsNullOrEmpty(sub.EnvironmentMapTexturePath) &&
                                sub.EnvironmentMapScale > 0f;

                submeshes.Add(new DecodedSubmesh12(
                    GpuMeshUploader.BuildVertices(sub),
                    sub.Triangles,
                    diffusePath,
                    hasBump ? normalPath : null,
                    hasBump,
                    alphaRenderMode,
                    alphaState.HasAlphaBlend,
                    alphaState.HasAlphaTest,
                    alphaState.AlphaTestThreshold / 255f,
                    alphaState.AlphaTestFunction,
                    alphaState.SrcBlendMode,
                    alphaState.DstBlendMode,
                    alphaState.MaterialAlpha,
                    sub.IsDoubleSided,
                    sub.IsEmissive,
                    ComputeLocalBoundsCenter(sub.Positions),
                    sub.IsBillboard,
                    specularColor,
                    sub.MaterialGlossiness,
                    specularEnabled,
                    sub.IsLeafBillboard,
                    alphaState.DepthWritingBlend,
                    specularEnabled || hasEnvMap ? sub.SpecularMapTexturePath : null,
                    sub.GradientMapTexturePath,
                    // FO4-family MODC: the base record's Color Remapping Index overrides the
                    // material's gradient-palette row (fo76utils render.cpp) — the engine's
                    // shared-mesh colorway mechanism. One crate BGSM bakes V=1.0 (the red row);
                    // Gray/Yellow/Blue come from per-STAT MODC. Only meaningful when the material
                    // actually has a gradient map.
                    gradientMapVOverride is { } remapV && sub.GradientMapTexturePath is not null
                        ? remapV
                        : sub.GradientMapV,
                    sub.IsDecal,
                    new Vector3(sub.EffectTint.R, sub.EffectTint.G, sub.EffectTint.B),
                    sub.EffectFalloff is { } falloff
                        ? new Vector4(falloff.StartAngle, falloff.StopAngle, falloff.StartOpacity, falloff.StopOpacity)
                        : default,
                    HasEffectFalloff: sub.EffectFalloff is not null,
                    EnvironmentMapTexturePath: hasEnvMap ? sub.EnvironmentMapTexturePath : null,
                    EnvironmentMapScale: hasEnvMap ? sub.EnvironmentMapScale : 0f,
                    EnvironmentMapSmoothness: hasEnvMap ? sub.EnvironmentMapSmoothness : 0f,
                    UvScrollVelocity: sub.UvScrollVelocity));
            }

            if (submeshes.Count == 0)
            {
                result = "empty";
                return null;
            }

            submeshCount = submeshes.Count;
            result = "success";
            return new DecodedNifMesh12(submeshes, collisionPositions, collisionTriangles);
        }
        finally
        {
            if (started != 0)
            {
                RendererProfilerTrace.Event("resource-event", new Dictionary<string, object?>
                {
                    ["resource"] = "reference-mesh",
                    ["phase"] = "decode",
                    ["path"] = lookupPath,
                    ["result"] = result,
                    ["bytes"] = nifBytes,
                    ["submeshes"] = submeshCount,
                    ["elapsedMs"] = Stopwatch.GetElapsedTime(started).TotalMilliseconds
                });
            }
        }
    }

    public static ReferenceDecodedMeshPayload12 ToPersistentPayload(DecodedNifMesh12 decoded)
    {
        var submeshes = new List<ReferenceDecodedSubmeshPayload12>(decoded.Submeshes.Count);
        foreach (var sub in decoded.Submeshes)
        {
            submeshes.Add(new ReferenceDecodedSubmeshPayload12(
                sub.Vertices,
                sub.Indices,
                sub.DiffuseTexturePath,
                sub.NormalMapTexturePath,
                sub.HasBump,
                sub.AlphaRenderMode,
                sub.AlphaBlend,
                sub.AlphaTest,
                sub.AlphaTestThreshold,
                sub.AlphaTestFunction,
                sub.SrcBlendMode,
                sub.DstBlendMode,
                sub.MaterialAlpha,
                sub.DoubleSided,
                sub.IsEmissive,
                sub.LocalBoundsCenter,
                sub.IsBillboard,
                sub.SpecularColor,
                sub.Glossiness,
                sub.SpecularEnabled,
                sub.IsLeafBillboard,
                sub.DepthWritingBlend,
                sub.SpecularMapTexturePath,
                sub.GradientMapTexturePath,
                sub.GradientMapV,
                sub.IsDecal,
                sub.EffectTint,
                sub.EffectFalloffParams,
                sub.HasEffectFalloff,
                sub.EnvironmentMapTexturePath,
                sub.EnvironmentMapScale,
                sub.EnvironmentMapSmoothness,
                sub.UvScrollVelocity));
        }

        return new ReferenceDecodedMeshPayload12(submeshes, decoded.CollisionPositions, decoded.CollisionTriangles);
    }

    public static DecodedNifMesh12 FromPersistentPayload(ReferenceDecodedMeshPayload12 payload)
    {
        var submeshes = new List<DecodedSubmesh12>(payload.Submeshes.Count);
        foreach (var sub in payload.Submeshes)
        {
            submeshes.Add(new DecodedSubmesh12(
                sub.Vertices,
                sub.Indices,
                sub.DiffuseTexturePath,
                sub.NormalMapTexturePath,
                sub.HasBump,
                sub.AlphaRenderMode,
                sub.AlphaBlend,
                sub.AlphaTest,
                sub.AlphaTestThreshold,
                sub.AlphaTestFunction,
                sub.SrcBlendMode,
                sub.DstBlendMode,
                sub.MaterialAlpha,
                sub.DoubleSided,
                sub.IsEmissive,
                sub.LocalBoundsCenter,
                sub.IsBillboard,
                sub.SpecularColor,
                sub.Glossiness,
                sub.SpecularEnabled,
                sub.IsLeafBillboard,
                sub.DepthWritingBlend,
                sub.SpecularMapTexturePath,
                sub.GradientMapTexturePath,
                sub.GradientMapV,
                sub.IsDecal,
                sub.EffectTint,
                sub.EffectFalloffParams,
                sub.HasEffectFalloff,
                sub.EnvironmentMapTexturePath,
                sub.EnvironmentMapScale,
                sub.EnvironmentMapSmoothness,
                sub.UvScrollVelocity));
        }

        return new DecodedNifMesh12(submeshes, payload.CollisionPositions, payload.CollisionTriangles);
    }

    public static long EstimateDecodedMeshBytes(DecodedNifMesh12 decoded)
    {
        var vertexSize = System.Runtime.InteropServices.Marshal.SizeOf<GpuMeshUploader.GpuVertex>();
        long total = 0;
        foreach (var submesh in decoded.Submeshes)
        {
            total += (long)submesh.Vertices.Length * vertexSize;
            total += (long)submesh.Indices.Length * sizeof(ushort);
            total += 256;
        }

        if (decoded.CollisionPositions is { } cp) total += (long)cp.Length * 12;
        if (decoded.CollisionTriangles is { } ct) total += (long)ct.Length * sizeof(int);

        return total;
    }

    public static string NormalizeModelPath(string modelPath)
    {
        var normalized = modelPath.Replace('/', '\\').Trim();

        // SpeedTree trees ship at the BSA root under "trees\" (e.g. "trees\wastelandshrub01.spt"),
        // NOT under "meshes\" like NIFs — and the TREE MODL is typically a bare name with a leading
        // backslash ("\WastelandShrub01.spt"). Normalize to "trees\<name>.spt" so the archive hit
        // matches; prepending "meshes\" (correct for NIFs) would miss every .spt and the tree would
        // silently render nothing.
        if (SpeedTreeModelPath.IsSpt(normalized))
        {
            return SpeedTreeModelPath.ToArchivePath(normalized);
        }

        return normalized.StartsWith("meshes\\", StringComparison.OrdinalIgnoreCase)
            ? normalized
            : "meshes\\" + normalized.TrimStart('\\');
    }

    /// <summary>
    ///     Stable FNV-1a hash of the model path, used to seed the deterministic SpeedTree generator so
    ///     the same tree type produces identical geometry across decode/cache cycles and process runs.
    /// </summary>
    private static uint StableSeed(string path)
    {
        var hash = 2166136261u;
        foreach (var ch in path)
        {
            hash = (hash ^ char.ToLowerInvariant(ch)) * 16777619u;
        }

        return hash;
    }

    // Whether a submesh should get a sun specular highlight. Gated conservatively so it only lights up
    // where the source material asks for it — never on emissive shapes, never with a black/near-black
    // specular tint or zero gloss. FO3/FNV expose BSShaderFlags; honor their Specular bit (0x1). When no
    // shader flags are present (Morrowind NiMaterialProperty, or Skyrim+ BSLightingShaderProperty which
    // doesn't surface them), fall back to the tint/gloss gate — Skyrim shapes carry no NiMaterialProperty
    // specular so they stay matte (no regression), while Morrowind's matte materials are filtered by tint.
    private static bool ComputeSpecularEnabled(RenderableSubmesh sub)
    {
        if (sub.IsEmissive)
        {
            return false;
        }

        var (r, g, b) = sub.SpecularColor;
        if (MathF.Max(r, MathF.Max(g, b)) < 0.04f || sub.MaterialGlossiness <= 0f)
        {
            return false;
        }

        const uint specularFlag = 0x1u; // BSShaderFlags bit 0 = Specular (nif.xml)
        return sub.ShaderMetadata?.ShaderFlags is not uint flags || (flags & specularFlag) != 0;
    }

    private static Vector3 ComputeLocalBoundsCenter(float[] positions)
    {
        var min = new Vector3(float.PositiveInfinity);
        var max = new Vector3(float.NegativeInfinity);
        for (var i = 0; i + 2 < positions.Length; i += 3)
        {
            var p = new Vector3(positions[i], positions[i + 1], positions[i + 2]);
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }

        return Vector3.Multiply(min + max, 0.5f);
    }
}
#endif

