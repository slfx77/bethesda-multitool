#if WINDOWS_GUI
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using BethesdaMultitool.Core.Formats.Bsa.Extraction;
using BethesdaMultitool.Core.Formats.Esm.Plugin.AssetPacking;
using BethesdaMultitool.Core.Formats.Nif.Collision;
using BethesdaMultitool.Core.Formats.Nif.Conversion;
using BethesdaMultitool.Core.Formats.Nif.Materials;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Materials;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Inspection;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Particles;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Profiling;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Scene;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;
using BethesdaMultitool.Core.Formats.SpeedTree;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;

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
    /// <summary>Env-gated per-submesh decode diagnostic (shares FALLOUT_VIEWER_NIF_DROP_DIAG with
    /// the extractor's drop/origin logs): prints the emissive-tint decision, vertex-color state, and
    /// blend modes each decoded submesh ships to the GPU.</summary>
    private static readonly bool DecodeDiagnosticsEnabled =
        Environment.GetEnvironmentVariable("FALLOUT_VIEWER_NIF_DROP_DIAG") == "1";

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

    /// <summary>First bsVersion whose NIFs keep geometry in external <c>geometries\*.mesh</c> blobs.</summary>
    private const uint StarfieldBsVersion = 170;

    private int _totalMissingModelPaths;
    private int _totalSkinnedModelPaths;
    private int _totalParseFailedModelPaths;
    private int _totalMissingGeometryBlobs;
    private int _totalDecodeFailedGeometryBlobs;

    // Kill-switch: FALLOUT_VIEWER_NIF_ANIMATION=0 decodes animated/skinned placed NIFs as inert
    // static geometry (pre-v32 behavior). Diagnostic knob — cached entries reflect whichever mode
    // decoded them.
    private static readonly bool NifAnimationEnabled =
        EnvironmentVariables.Get(EnvironmentVariables.Viewer.NifAnimation) != "0";

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

    /// <summary>
    ///     Model paths whose bytes WERE found but whose NIF failed to parse. Deliberately separate
    ///     from <see cref="TotalMissingModelPaths" /> (an archive miss): the two have opposite causes
    ///     and opposite fixes, and conflating them hid the fact that 100% of Starfield's meshes were
    ///     failing at the header rather than being absent. A null parse is otherwise invisible — it is
    ///     not logged and IS persisted as a negative to the on-disk decoded-mesh cache.
    /// </summary>
    public int TotalParseFailedModelPaths => Volatile.Read(ref _totalParseFailedModelPaths);

    /// <summary>
    ///     Starfield BSGeometry blocks that named an external <c>geometries\*.mesh</c> blob which the
    ///     mounted archives did not contain — the signature of a missing/misclassified meshes archive
    ///     rather than a bad decode.
    /// </summary>
    public int TotalMissingGeometryBlobs => Volatile.Read(ref _totalMissingGeometryBlobs);

    /// <summary>
    ///     Starfield geometry blobs that WERE extracted but failed <c>StarfieldMeshFile</c> decode (or
    ///     decoded to zero triangles). The third failure class beside
    ///     <see cref="TotalMissingGeometryBlobs" /> (archive miss) and
    ///     <see cref="TotalParseFailedModelPaths" /> (NIF header failure) — without it a corrupt blob
    ///     drops silently and the empty model persists to the decoded-mesh disk cache as a negative.
    /// </summary>
    public int TotalDecodeFailedGeometryBlobs => Volatile.Read(ref _totalDecodeFailedGeometryBlobs);

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
        float? gradientMapVOverride = null,
        Vector3? externalEmittanceColor = null)
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
            var collisionProvenance = HavokCollisionProvenance.AbsentOrUnsupported;

            // Keyframe playback rig + per-shape skin inputs (set only for internally-skinned
            // animated NIFs — banners, cloth flags with tracks).
            NifMeshAnimation? animation = null;
            Dictionary<int, Skinning.NifSubmeshSkin>? skinsByShapeIndex = null;
            IReadOnlyDictionary<int, PhysicsLiteSwayDescriptor> physicsLiteRoutes =
                new Dictionary<int, PhysicsLiteSwayDescriptor>();
            // Baked delta tracks for RIGID node-animated statics (no skin): shape block → track.
            IReadOnlyDictionary<int, NifRigidNodeAnimation>? rigidTracksByShapeBlock = null;

            NifRenderableModel? model;
            NifInfo? nif = null;
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
                var rockSpeed = 1f;
                var rustleSpeed = 1f;
                if (_speedTreeDimming is not null && _speedTreeDimming.TryGetValue(lookupPath, out var dim))
                {
                    leafDimming = dim.Leaf;
                    branchDimming = dim.Branch;
                    rockSpeed = dim.RockSpeed;
                    rustleSpeed = dim.RustleSpeed;
                }

                var seed = spt.General.Token2005 != 0 ? spt.General.Token2005 : StableSeed(lookupPath);
                var sptOptions = SptGeometryOptions.FromEnvironment() with
                {
                    TargetHeight = targetHeight,
                    LeafTextureOverride = leafTexture,
                    LeafDimming = leafDimming,
                    BranchDimming = branchDimming,
                    RockSpeed = rockSpeed,
                    RustleSpeed = rustleSpeed,
                    BillboardTexturePath = SpeedTreeRuntimeLod.BillboardTexturePath(lookupPath),
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
                        Interlocked.Increment(ref _totalParseFailedModelPaths);
                        return null;
                    }

                    isBigEndian = sourceInfo.IsBigEndian;
                }

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
                        Interlocked.Increment(ref _totalParseFailedModelPaths);
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
                        Interlocked.Increment(ref _totalParseFailedModelPaths);
                        return null;
                    }
                }

                // Internally-skinned NIFs (Morrowind banners, FNV cloth flags — skeleton nodes live
                // inside the file) are skinned against their OWN authored node transforms, baking the
                // correct REST pose. Everything else keeps skipping skinning: without an external
                // skeleton there is nothing to pose those against, and bind-pose is today's behavior.
                var animationSignature = NifAnimationEnabled
                    ? NifAnimationDetector.Detect(nifData, nif)
                    : default;

                model = NifGeometryExtractor.Extract(
                    nifData, nif,
                    textureResolver: _textureResolver,
                    bindPoseOnly: false,
                    skipSkinning: !animationSignature.HasInternalSkin,
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
                    dropBoneAttachedShapes: true,
                    // REFR XEMI is placement state. Bake it into this variant-keyed decode so
                    // opaque instancing never shares one external-emittance color across refs.
                    externalEmittanceColor: externalEmittanceColor,
                    // The reference cache also owns placed-object collision. Preserve the decoded
                    // model shell when a NIF has no render geometry so authored collision provenance
                    // can still reach the collision LRU instead of becoming a total negative entry.
                    preserveEmptyModel: true,
                    // Starfield keeps no vertex data in the NIF — BSGeometry blocks name a blob under
                    // geometries\. Supplied only for bsVersion >= 170 so no other game pays an extra
                    // archive probe per shape.
                    externalMeshLoader: nif.BsVersion >= StarfieldBsVersion ? LoadExternalGeometryBlob : null,
                    onExternalMeshDecodeFailure: nif.BsVersion >= StarfieldBsVersion
                        ? _ => Interlocked.Increment(ref _totalDecodeFailedGeometryBlobs)
                        : null);

                // FNV physics-lite is source-graph data, resolved while the full source graph is
                // available. The parser is deliberately profile-gated to retail
                // 20.2.0.7 / BS34 and returns no routes when ordinary transform animation exists.
                if (FnvHavokConstraintParser.IsPhysicsLiteCandidate(nif))
                {
                    physicsLiteRoutes = PhysicsLiteSway.BuildSourceBlockRoutes(
                        FnvHavokConstraintParser.Parse(nifData, nif));
                }

                // Decode Havok from the same converted buffer while preserving an explicit layer-15
                // authored-none verdict. Absent and unsupported Havok deliberately remain one state:
                // both retain the established visual-mesh fallback.
                var havok = HavokCollisionExtractor.Extract(nifData, nif);
                collisionProvenance = havok.Provenance;
                if (havok.Soup is { } soup)
                {
                    collisionPositions = soup.Positions;
                    collisionTriangles = soup.Triangles;
                }

                // Keyframe playback rig: internally-skinned NIFs with animation tracks carry the
                // bone tree + tracks + per-shape skin inputs so the viewer can re-pose them per
                // frame. The baked vertices above stay REST-POSE — the rig is additive data. Two
                // era collectors feed the SAME model: TES3 per-node NiKeyframeController graphs
                // (Morrowind banners) and modern NiControllerManager idle sequences (FNV cloth
                // flags); a NIF carrying both (none sighted) prefers the per-node graph.
                if (animationSignature.HasInternalSkin &&
                    (animationSignature.HasNodeKeyframeTracks ||
                     animationSignature.HasControllerSequenceTracks))
                {
                    animation = animationSignature.HasNodeKeyframeTracks
                        ? NifNodeKeyframeTrackCollector.Collect(nifData, nif)
                        : null;
                    if (animation is null && animationSignature.HasControllerSequenceTracks)
                    {
                        animation = NifControllerSequenceTrackCollector.Collect(nifData, nif);
                    }

                    if (animation is not null)
                    {
                        skinsByShapeIndex = Skinning.NifSubmeshSkinExporter.Export(nifData, nif, animation);
                        if (skinsByShapeIndex is null)
                        {
                            animation = null; // rig without skinnable shapes — nothing to pose
                        }
                    }
                }
                else if (!animationSignature.HasInternalSkin &&
                         (animationSignature.HasNodeKeyframeTracks ||
                          animationSignature.HasControllerSequenceTracks) &&
                         physicsLiteRoutes.Count == 0)
                {
                    // RIGID node animation: controller-driven nodes with UNSKINNED geometry (the
                    // Goodsprings saloon sign's chains/board, mill wheels). The same collectors
                    // supply the rig; instead of skins, each shape in an animated node's subtree
                    // gets a baked model-space delta track applied at draw time like physics-lite
                    // sway. The rig stays local — DecodedNifMesh12.Animation remains the CPU
                    // skinner's contract and must not carry skinless rigs.
                    var rigidRig = animationSignature.HasNodeKeyframeTracks
                        ? NifNodeKeyframeTrackCollector.Collect(nifData, nif)
                        : null;
                    if (rigidRig is null && animationSignature.HasControllerSequenceTracks)
                    {
                        rigidRig = NifControllerSequenceTrackCollector.Collect(nifData, nif);
                    }

                    if (rigidRig is not null)
                    {
                        rigidTracksByShapeBlock = NifRigidNodeAnimationBaker.Bake(nifData, nif, rigidRig);
                    }
                }
            }

            if (model is null)
            {
                result = "extract-failed";
                return null;
            }
            // WasSkinned is EXPECTED for internally-skinned NIFs now that they rest-pose skin
            // (banners, cloth flags). The old "skinned → reject" guard here was dead code anyway:
            // with skipSkinning:true the flag could never be set on this path. Keep the tally for
            // the diagnostic counter only.
            if (model.WasSkinned)
            {
                Interlocked.Increment(ref _totalSkinnedModelPaths);
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

                var skin = skinsByShapeIndex is not null &&
                           skinsByShapeIndex.TryGetValue(sub.SourceBlockIndex, out var resolvedSkin)
                    ? resolvedSkin
                    : null;
                var sway = !sub.IsBillboard && !sub.IsParticleCloud &&
                           physicsLiteRoutes.TryGetValue(sub.SourceBlockIndex, out var resolvedSway)
                    ? resolvedSway
                    : (PhysicsLiteSwayDescriptor?)null;
                var rigidAnimation = !sub.IsBillboard && !sub.IsParticleCloud &&
                                     rigidTracksByShapeBlock is not null &&
                                     rigidTracksByShapeBlock.TryGetValue(
                                         sub.SourceBlockIndex,
                                         out var resolvedRigidAnimation)
                    ? resolvedRigidAnimation
                    : null;
                var decodedSubmesh = ReferenceSubmeshDecoder12.Decode(
                    sub,
                    new ReferenceSubmeshDecodeOptions12(
                        diffusePath,
                        normalPath,
                        gradientMapVOverride,
                        nif,
                        skin,
                        sway,
                        rigidAnimation,
                        ParticleLiveSettings.Enabled));

                if (DecodeDiagnosticsEnabled)
                {
                    var tint = decodedSubmesh.SpecularColor;
                    Console.WriteLine(
                        $"[decode-diag] '{sub.ShapeName}' emissive={sub.IsEmissive} " +
                        $"tint=({tint.X:0.##},{tint.Y:0.##},{tint.Z:0.##}) " +
                        $"matEmissive={sub.EmissiveColor} anim={sub.AnimatedEmissiveColor} " +
                        $"useVtxColors={sub.UseVertexColors} " +
                        $"vtxColors={(sub.VertexColors is null ? "null" : sub.VertexColors.Length.ToString())} " +
                        $"blend={sub.SrcBlendMode}/{sub.DstBlendMode} diffuse={diffusePath ?? "(none)"}");
                }

                submeshes.Add(decodedSubmesh);
            }

            if (submeshes.Count == 0)
            {
                // A render-empty NIF can still carry authoritative Havok. Keep only those positive
                // collision verdicts; an absent/unsupported empty model remains the established
                // negative decode and may use the cold-path bounds fallback.
                if (collisionProvenance == HavokCollisionProvenance.AbsentOrUnsupported)
                {
                    result = "empty";
                    return null;
                }

                result = "collision-only";
            }

            submeshCount = submeshes.Count;
            if (submeshes.Count > 0)
            {
                result = "success";
            }
            return new DecodedNifMesh12(
                submeshes, collisionPositions, collisionTriangles, collisionProvenance, animation,
                model.ContainsParticleSource);
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
                sub.LocalBoundsRadius,
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
                sub.UvScrollVelocity,
                sub.IsSpeedTreeBranch,
                sub.SpeedTreeWindSpeeds,
                sub.Skin,
                sub.ClampTextureU,
                sub.ClampTextureV,
                sub.IsParticleCloud,
                sub.SoftParticleFalloffDepth,
                sub.MaterialAlphaController,
                sub.PhysicsLiteSway,
                sub.IsLighting30,
                sub.Lighting30GlowMapTexturePath,
                sub.Lighting30EmissionColor,
                sub.Lighting30EmissionMultiplier,
                sub.IsTallGrass,
                sub.ClassicEnvironmentMapTexturePath,
                sub.ClassicEnvironmentMaskTexturePath,
                sub.ClassicEnvironmentMapScale,
                sub.ClassicEnvironmentMapUsesWindowReflection,
                sub.ClassicParallaxHeightMapTexturePath,
                sub.ClassicBasicShaderMode,
                sub.SourceBlockIndex,
                sub.BillboardMode,
                sub.EngineZWriteOff,
                sub.DepthTestOff,
                sub.MaterialDiffuse,
                RigidNodeAnimation: sub.RigidNodeAnimation,
                ClassicEnvironmentMapIsSphereMap: sub.ClassicEnvironmentMapIsSphereMap,
                StarfieldMaterialColor: sub.StarfieldMaterialColor,
                StarfieldMaterialAlpha: sub.StarfieldMaterialAlpha,
                BgsmGlowMapTexturePath: sub.BgsmGlowMapTexturePath,
                BgsmEmissionColor: sub.BgsmEmissionColor));
        }

        return new ReferenceDecodedMeshPayload12(
            submeshes, decoded.CollisionPositions, decoded.CollisionTriangles,
            decoded.CollisionProvenance, decoded.Animation, decoded.ContainsParticleSource);
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
                sub.LocalBoundsRadius,
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
                sub.UvScrollVelocity,
                sub.Skin,
                sub.IsSpeedTreeBranch,
                sub.SpeedTreeWindSpeeds,
                sub.ClampTextureU,
                sub.ClampTextureV,
                sub.IsParticleCloud,
                sub.SoftParticleFalloffDepth,
                sub.MaterialAlphaController,
                sub.PhysicsLiteSway,
                IsLighting30: sub.IsLighting30,
                Lighting30GlowMapTexturePath: sub.Lighting30GlowMapTexturePath,
                Lighting30EmissionColor: sub.Lighting30EmissionColor,
                Lighting30EmissionMultiplier: sub.Lighting30EmissionMultiplier,
                IsTallGrass: sub.IsTallGrass,
                ClassicEnvironmentMapTexturePath: sub.ClassicEnvironmentMapTexturePath,
                ClassicEnvironmentMaskTexturePath: sub.ClassicEnvironmentMaskTexturePath,
                ClassicEnvironmentMapScale: sub.ClassicEnvironmentMapScale,
                ClassicEnvironmentMapUsesWindowReflection:
                    sub.ClassicEnvironmentMapUsesWindowReflection,
                ClassicEnvironmentMapIsSphereMap: sub.ClassicEnvironmentMapIsSphereMap,
                ClassicParallaxHeightMapTexturePath:
                    sub.ClassicParallaxHeightMapTexturePath,
                ClassicBasicShaderMode:
                    sub.ClassicBasicShaderMode,
                SourceBlockIndex:
                    sub.SourceBlockIndex,
                BillboardMode:
                    sub.BillboardMode,
                EngineZWriteOff: sub.EngineZWriteOff,
                DepthTestOff: sub.DepthTestOff,
                MaterialDiffuse: sub.MaterialDiffuse,
                RigidNodeAnimation: sub.RigidNodeAnimation,
                StarfieldMaterialColor: sub.StarfieldMaterialColor,
                StarfieldMaterialAlpha: sub.StarfieldMaterialAlpha,
                BgsmGlowMapTexturePath: sub.BgsmGlowMapTexturePath,
                BgsmEmissionColor: sub.BgsmEmissionColor));
        }

        return new DecodedNifMesh12(
            submeshes, payload.CollisionPositions, payload.CollisionTriangles,
            payload.CollisionProvenance, payload.Animation, payload.ContainsParticleSource);
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
            if (submesh.ParticleRuntime is not null)
            {
                // Modifier/key lists and retained emitter geometry are CPU-side live state. This is a
                // conservative accounting surcharge; the large indexed mesh arrays dominate and already
                // live on the definition itself rather than the transient frame snapshot.
                total += 4096;
            }
            if (submesh.Skin is { } skin)
            {
                // base positions/normals + packed influences + inverse binds (LRU byte honesty).
                total += (long)skin.BasePositions.Length * sizeof(float);
                total += (long)(skin.BaseNormals?.Length ?? 0) * sizeof(float);
                total += (long)skin.BoneIndices.Length;
                total += (long)skin.BoneWeights.Length * sizeof(float);
                total += (long)skin.InverseBindPoses.Length * 64;
            }
        }

        if (decoded.CollisionPositions is { } cp) total += (long)cp.Length * 12;
        if (decoded.CollisionTriangles is { } ct) total += (long)ct.Length * sizeof(int);
        if (decoded.Animation is { } anim)
        {
            total += anim.Bones.Length * 96L;
            foreach (var track in anim.Tracks)
            {
                if (track is null) continue;
                total += track.RotationKeys.Length * 20L;
                total += track.TranslationKeys.Length * 16L;
                total += track.ScaleKeys.Length * 8L;
            }
        }

        return total;
    }

    /// <summary>
    ///     Loads a Starfield <c>BSGeometry</c> block's external geometry blob. The NIF stores only the
    ///     two-component hash (e.g. <c>00000042477f47c194db\3cd02fb771e05b4ac1be</c>); the
    ///     <c>geometries\</c> root and <c>.mesh</c> extension are implied and added here.
    ///     <para>
    ///         Deliberately NOT routed through <see cref="NormalizeModelPath" />, which force-prefixes
    ///         <c>meshes\</c> — these blobs live in a sibling tree, and in retail Meshes01 every one of
    ///         the 288,231 of them is under <c>geometries\</c> while no <c>.nif</c> is.
    ///     </para>
    /// </summary>
    private byte[]? LoadExternalGeometryBlob(string meshPath)
    {
        var normalized = meshPath.Replace('/', '\\').Trim().TrimStart('\\');
        if (normalized.Length == 0)
        {
            return null;
        }

        if (!normalized.StartsWith("geometries\\", StringComparison.OrdinalIgnoreCase))
        {
            normalized = "geometries\\" + normalized;
        }

        if (!normalized.EndsWith(".mesh", StringComparison.OrdinalIgnoreCase))
        {
            normalized += ".mesh";
        }

        if (_meshArchives.TryExtractFile(normalized, out var bytes, out _) && bytes.Length > 0)
        {
            return bytes;
        }

        Interlocked.Increment(ref _totalMissingGeometryBlobs);
        return null;
    }

    public static string NormalizeModelPath(string modelPath) =>
        ReferenceModelPath.Normalize(modelPath);

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

}
#endif
