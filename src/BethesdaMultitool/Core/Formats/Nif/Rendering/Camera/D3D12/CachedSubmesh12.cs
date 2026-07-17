#if WINDOWS_GUI
using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Effects;
using BethesdaMultitool.Core.Formats.SpeedTree;
using Vortice.Direct3D12;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Camera.D3D12;

/// <summary>One submesh of a cached reference mesh: its GPU vertex/index buffer views, diffuse/normal texture entries, and packed alpha/render state.</summary>
internal sealed class CachedSubmesh12
{
    private Vector4 _textureState;
    private bool _textureStateCached;

    public required VertexBufferView VertexBufferView { get; init; }
    public required IndexBufferView IndexBufferView { get; init; }
    public required int IndexCount { get; init; }
    public required GpuTextureCache12.Entry Diffuse { get; init; }
    public required GpuTextureCache12.Entry Normal { get; init; }

    /// <summary>
    ///     FO4/FO76 per-texel specular mask (<c>_s.dds</c>, R channel), or null when the material has
    ///     none — the shader then leaves specular off for alpha-less (BC5) normal maps instead of
    ///     applying a uniform mask, which blows out whole scenes.
    /// </summary>
    public GpuTextureCache12.Entry? SpecularMap { get; init; }

    /// <summary>
    ///     FO4/FO76 grayscale-to-palette texture, or null. When set, the shader replaces the diffuse
    ///     RGB with <c>palette.Sample(u: diffuse.G, v: GradientMapV × vertexColor.R)</c>.
    /// </summary>
    public GpuTextureCache12.Entry? GradientMap { get; init; }

    /// <summary>
    ///     Classic FO3/FNV Lighting30 glow mask. It shares TexIndices.w with the FO4 gradient map;
    ///     the engine families and policy routes are mutually exclusive.
    /// </summary>
    public GpuTextureCache12.Entry? Lighting30GlowMap { get; init; }

    /// <summary>Palette row (V) for the gradient lookup; only meaningful when <see cref="GradientMap" /> is set.</summary>
    public float GradientMapV { get; init; }

    /// <summary>
    ///     FO4 environment cubemap (BGSM slot 4), or null. The shader adds
    ///     <c>cube(reflect(V,N)) × EnvMapScale × _s.R × g(N·V)</c> once the entry has promoted to
    ///     a real TextureCube — see <see cref="EnvMapState" />.
    /// </summary>
    public GpuTextureCache12.Entry? EnvMap { get; init; }

    /// <summary>fo76utils envMapScale (envScale × raw specular strength, clamped to 8).</summary>
    public float EnvMapScale { get; init; }

    /// <summary>Material specular smoothness 0–1 (cube mip + geometry-term input).</summary>
    public float EnvMapSmoothness { get; init; }

    /// <summary>
    ///     FO3/FNV PP-lighting environment cubemap (texture-set slot 4). This drives the classic
    ///     additive SLS pass and is intentionally distinct from <see cref="EnvMap" />.
    /// </summary>
    public GpuTextureCache12.Entry? ClassicEnvMap { get; init; }

    /// <summary>Optional classic slot-5 custom environment mask (red channel).</summary>
    public GpuTextureCache12.Entry? ClassicEnvMask { get; init; }

    /// <summary>
    ///     FO3/FNV simple-parallax height map. It shares TexIndices.z with the FO4 specular map and
    ///     classic environment mask; explicit material-state bits make those routes exclusive.
    /// </summary>
    public GpuTextureCache12.Entry? ClassicParallaxHeightMap { get; init; }

    /// <summary>Authored classic BSShaderProperty EnvMapScale.</summary>
    public float ClassicEnvMapScale { get; init; }

    /// <summary>Classic bit-21/SLS2058 window-reflection direction instead of SLS2057.</summary>
    public bool ClassicEnvMapUsesWindowReflection { get; init; }

    /// <summary>
    ///     Per-draw env-map constants (uEnvMap): x = cube bindless slot, y = scale, z = smoothness,
    ///     w = <see cref="NifD3D12BlendOperation" /> (consumed by the fog/output-range term).
    ///     x stays −1 until the entry is RESIDENT as a TextureCube — cold placeholders are 2D SRVs,
    ///     and indexing one through the shader's cube alias would read a mismatched descriptor.
    ///     Deliberately NOT cached: per-frame CB fills re-read it so promotion lands (same pattern
    ///     as the bindless indices on frozen batches).
    /// </summary>
    public Vector4 EnvMapState
    {
        get
        {
            System.Diagnostics.Debug.Assert(
                EnvMap is null || ClassicEnvMap is null,
                "FO4 and classic FO3/FNV environment-map routes are mutually exclusive.");
            var env = ClassicEnvMap ?? EnvMap;
            var scale = ClassicEnvMap is not null ? ClassicEnvMapScale : EnvMapScale;
            var smoothness = ClassicEnvMap is not null ? 0f : EnvMapSmoothness;
            return env is { IsResident: true, IsCubemap: true }
                ? new Vector4(env.BindlessIndex, scale, smoothness, (float)BlendOperation)
                : new Vector4(-1f, 0f, 0f, (float)BlendOperation);
        }
    }

    /// <summary>
    ///     Destination blend factor ONE (NIF blend byte 0; 10 = GL src-alpha-saturate approximates
    ///     One in the PSO too) — an additive draw. Rides EnvMapState.w so the fog term can fade
    ///     additive contributions toward BLACK with distance (engine behavior) instead of lerping
    ///     toward the fog color, which ADDS fog-colored light to every distant glow (backlog A8:
    ///     the distant Strip glow never attenuated).
    /// </summary>
    public NifD3D12BlendOperation BlendOperation =>
        NifD3D12BlendMapper.ClassifyOperation(AlphaBlend, SrcBlendMode, DstBlendMode);

    public bool IsAdditiveBlend => BlendOperation == NifD3D12BlendOperation.Additive;

    /// <summary>
    ///     Multiplicative shadow/dust equation. These draws require a white fog neutral and a
    ///     normalized source range; values above one brighten the destination instead of dimming it.
    /// </summary>
    public bool IsMultiplicativeBlend => BlendOperation == NifD3D12BlendOperation.Multiplicative;

    public required Vector4 AlphaState { get; init; }
    public required Vector4 RenderState { get; init; }
    // Sun specular term (1A): xyz = tint, w = Phong exponent (0 = no specular). Mirrors the
    // uSpecular cbuffer field in reference(_instanced).vert.hlsl / reference.frag.hlsl.
    public required Vector4 Specular { get; init; }
    public Vector4 TextureState
    {
        get
        {
            if (_textureStateCached)
            {
                return _textureState;
            }

            System.Diagnostics.Debug.Assert(
                GradientMap is null || Lighting30GlowMap is null,
                "FO4 gradient and classic Lighting30 glow maps cannot share TexIndices.w.");
            System.Diagnostics.Debug.Assert(
                SpecularMap is null || ClassicEnvMask is null,
                "FO4 specular and classic environment masks cannot share TexIndices.z.");
            System.Diagnostics.Debug.Assert(
                (SpecularMap is null ? 0 : 1) +
                (ClassicEnvMask is null ? 0 : 1) +
                (ClassicParallaxHeightMap is null ? 0 : 1) <= 1,
                "Specular, classic environment-mask, and classic height maps cannot share TexIndices.z.");

            var state = new Vector4(
                Normal.NormalDecodeMode == GpuNormalDecodeMode.Bc5ReconstructZ ? 1f : 0f,
                // .y > 0.5 routes the instanced VS to the leaf-billboard branch; 2 additionally marks
                // an ALPHA-TESTED leaf card (SPT leaves — the PS boosts test alpha by texture LOD to
                // undo mip alpha decay; baked particle clouds are blends and stay at 1).
                IsSpeedTreeBranch ? -1f : IsLeafBillboard ? (AlphaTest ? 2f : 1f) : 0f,
                // Exact integer flags carried in a float constant: bit 0 = sample TexIndices.z for
                // the spec mask, bits 1/2 = clamp U/V, bit 3 = TexIndices.w is a Lighting30 glow
                // map, bit 4 = classic Lighting30 material route, bit 5 = TallGrassShaderProperty,
                // bit 6 = classic FO3/FNV environment pass, bit 7 = TexIndices.z is its custom mask,
                // bit 9 = classic bit-21/SLS2058 window-reflection direction (bit 8 is parallax).
                // All values are <= 1023 and exactly
                // representable; shaders decode with integer bit tests.
                (SpecularMap is not null ? 1f : 0f) +
                (ClampTextureU ? 2f : 0f) +
                (ClampTextureV ? 4f : 0f) +
                (Lighting30GlowMap is not null ? 8f : 0f) +
                (IsLighting30 ? 16f : 0f) +
                (IsTallGrass ? 32f : 0f) +
                (ClassicEnvMap is not null && ClassicEnvMapScale > 0f ? 64f : 0f) +
                (ClassicEnvMask is not null ? 128f : 0f) +
                (ClassicParallaxHeightMap is not null ? 256f : 0f) +
                (ClassicEnvMapUsesWindowReflection ? 512f : 0f),
                GradientMap is not null ? GradientMapV : -1f); // .w >= 0 = palette row for TexIndices.w
            if (TexturesReady)
            {
                _textureState = state;
                _textureStateCached = true;
            }

            return state;
        }
    }
    public bool TexturesReady => Diffuse.IsReady && Normal.IsReady &&
                                  SpecularMap is not { IsReady: false } && GradientMap is not { IsReady: false } &&
                                  Lighting30GlowMap is not { IsReady: false } &&
                                  EnvMap is not { IsReady: false } &&
                                  ClassicEnvMap is not { IsReady: false } &&
                                  ClassicEnvMask is not { IsReady: false } &&
                                  ClassicParallaxHeightMap is not { IsReady: false };
    public required bool HasBump { get; init; }
    public required NifAlphaRenderMode AlphaRenderMode { get; init; }
    public required bool AlphaBlend { get; init; }
    public required bool AlphaTest { get; init; }
    public required float AlphaTestThreshold { get; init; }
    public required byte AlphaTestFunction { get; init; }
    public required byte SrcBlendMode { get; init; }
    public required byte DstBlendMode { get; init; }
    public required float MaterialAlpha { get; init; }

    /// <summary>Optional live material-opacity curve sampled by the blended draw path.</summary>
    public NifMaterialAlphaController? MaterialAlphaController { get; init; }

    /// <summary>
    ///     Strict FNV BS34 ambient-sway descriptor in baked mesh-root-local coordinates. The phase
    ///     seed remains per placed reference and is evaluated while matrices enter the frame ring,
    ///     so this property stays part of the submesh batch key without splitting instances.
    /// </summary>
    public PhysicsLiteSwayDescriptor? PhysicsLiteSway { get; init; }
    public required bool DoubleSided { get; init; }
    public required bool IsEmissive { get; init; }

    /// <summary>
    ///     Exact TallGrassShaderProperty identity. VertexColor.w is raw authored wind weight for
    ///     these cached vertices; the reference VS restores outgoing coverage alpha to one.
    /// </summary>
    public bool IsTallGrass { get; init; }

    /// <summary>True for the classic scene-lit Lighting30 material path (never full-bright).</summary>
    public bool IsLighting30 { get; init; }

    /// <summary>
    ///     Audit-only PC-final PS1 basic-bump identity, reused as the strict ordinary-material and
    ///     vertex-color discriminator by the separately gated active-retail FNV ADT policy.
    /// </summary>
    public FnvClassicBasicShaderMode ClassicBasicShaderMode { get; init; }

    /// <summary>
    ///     Raw selected Lighting30 RGB in xyz and NiMaterial emission multiplier in w. The shader
    ///     applies w and IMGS EmissiveMult only while HDR is active.
    /// </summary>
    public Vector4 Lighting30Emission { get; init; } = new(0f, 0f, 0f, 1f);

    /// <summary>
    ///     Stable block index of the source shape in the decoded NIF. This remains CPU-only and is
    ///     not part of the shared mesh or GPU instance identity; -1 means unavailable.
    /// </summary>
    public int SourceBlockIndex { get; init; } = -1;

    public required Vector3 LocalBoundsCenter { get; init; }
    public required float LocalBoundsRadius { get; init; }

    /// <summary>
    ///     True if this submesh sat under a <c>NiBillboardNode</c> in the source NIF. The renderer
    ///     routes it to the per-draw blended path and replaces the placement world matrix with a
    ///     cylindrical camera-facing matrix so the quad re-aims at the camera every frame.
    /// </summary>
    public required bool IsBillboard { get; init; }

    /// <summary>
    ///     Authored horizontal front axis recovered from the submesh's indexed winding. +Y is the
    ///     historical fallback; FNV effect meshes such as FireBall09 author their visible side as -Y.
    /// </summary>
    public Vector2 BillboardFrontAxis { get; init; } = Vector2.UnitY;

    public bool IsLeafBillboard { get; init; }

    /// <summary>
    ///     One local-space center per baked particle quad. Null for ordinary geometry and
    ///     SpeedTree leaf cards. The blended renderer uses these to build a transient stable
    ///     back-to-front index order for the current camera.
    /// </summary>
    public Vector3[]? ParticleCenters { get; init; }

    /// <summary>
    ///     Optional owner of transient, time-sampled particle geometry. Null is the default/static path.
    ///     Its views are frame-ring-backed and therefore activated or cleared before every draw frame.
    /// </summary>
    public LiveParticleOwner12? LiveParticles { get; init; }

    /// <summary>BGSM/BGEM material addressing: clamp U when TileU is disabled.</summary>
    public bool ClampTextureU { get; init; }

    /// <summary>BGSM/BGEM material addressing: clamp V when TileV is disabled.</summary>
    public bool ClampTextureV { get; init; }

    /// <summary>SpeedTree bark/frond vertex route. TextureState.y = -1 tells the VS to decode the
    /// specialized TBN-magnitude wind payload while remaining distinct from leaf billboards.</summary>
    public bool IsSpeedTreeBranch { get; init; }

    /// <summary>TREE CNAM (RockSpeed, RustleSpeed); defaults to 1 for bare .spt meshes.</summary>
    public Vector2 SpeedTreeWindSpeeds { get; init; } = Vector2.One;

    /// <summary>Opt-in .spt runtime-LOD component/level, or null on the default single-LOD path.</summary>
    public SpeedTreeLodMetadata? SpeedTreeLod { get; init; }

    /// <summary>
    ///     True for an alpha-BLEND shape the engine writes depth for (effects-folder foliage like
    ///     NVSeaPlant02). The renderer draws it inline BEFORE the water pass with a depth-writing blend
    ///     PSO, so water occludes it from above instead of it painting over the surface.
    /// </summary>
    public bool DepthWritingBlend { get; init; }

    /// <summary>
    ///     True for decal overlay geometry (BGSM decal byte / shader-flags bits 26-27 — grime,
    ///     cracks, posters authored coplanar with their backing surface). The renderer selects a
    ///     depth-biased PSO variant so the decal wins the depth tie instead of z-fighting.
    /// </summary>
    public bool IsDecal { get; init; }

    /// <summary>BGEM effect tint (base color × scale) multiplied into the source texture RGB;
    /// (1,1,1) for non-effect materials so the shader term is a no-op.</summary>
    public Vector3 EffectTint { get; init; } = Vector3.One;

    /// <summary>BGEM |N·V| opacity falloff (startAngle, stopAngle, startOpacity, stopOpacity);
    /// only consumed when <see cref="HasEffectFalloff" />.</summary>
    public Vector4 EffectFalloffParams { get; init; }

    /// <summary>True when the effect material enables the view-angle opacity falloff.</summary>
    public bool HasEffectFalloff { get; init; }

    /// <summary>
    ///     Scene-depth feather eligibility resolved once from authored soft-effect data or the
    ///     explicitly-scoped particle/effects fallback. Disabled for ordinary alpha geometry.
    /// </summary>
    public NifSoftParticleSettings SoftParticle { get; init; }

    /// <summary>
    ///     Constant UV scroll velocity (UV units/second) from a TES3 NiUVController looping ramp
    ///     (waterfalls, lava). Zero = static. The renderer fills the per-draw UV offset with
    ///     <c>frac(velocity × animClock)</c> — batching is unaffected because the velocity is a
    ///     property of the submesh, which IS the batch key.
    /// </summary>
    public Vector2 UvScrollVelocity { get; init; }

    /// <summary>
    ///     CPU skinning inputs for keyframe playback (raw skin-space base geometry + influences +
    ///     inverse binds), null for unskinned/unanimated submeshes. Consumed by the per-frame mesh
    ///     skinner together with the owning mesh's animation rig.
    /// </summary>
    public BethesdaMultitool.Core.Formats.Nif.Rendering.Skinning.NifSubmeshSkin? Skin { get; init; }

    /// <summary>
    ///     CPU copy of the interleaved rest-pose vertices, retained ONLY for skinned submeshes
    ///     (<see cref="Skin" /> non-null) as the per-frame skinner's write template — it rewrites
    ///     position/normal and keeps uv/color/tangents. Null for the common static submesh.
    /// </summary>
    public Gpu.GpuMeshUploader.GpuVertex[]? RestPoseVertices { get; init; }

    /// <summary>
    ///     Per-frame skinned-pose vertex buffer override, set (or cleared) every frame by the CPU
    ///     mesh skinner for animated skinned meshes; null = draw the static rest-pose VB. Ring-buffer
    ///     backed — valid for the frame it was written, hence the clear-or-refresh-every-frame rule.
    ///     Render-thread only.
    /// </summary>
    public VertexBufferView? AnimatedVertexBufferView { get; set; }

    /// <summary>The vertex buffer the draw should bind this frame (live particle, skinned, then static).</summary>
    public VertexBufferView EffectiveVertexBufferView =>
        LiveParticles is { HasLiveFrame: true, VertexBufferView: { } live }
            ? live
            : AnimatedVertexBufferView ?? VertexBufferView;

    /// <summary>Current coherent index-buffer partner for <see cref="EffectiveVertexBufferView" />.</summary>
    public IndexBufferView EffectiveIndexBufferView =>
        LiveParticles is { HasLiveFrame: true, IndexBufferView: { } live }
            ? live
            : IndexBufferView;

    /// <summary>Zero during an authored quiet live interval; static count when live upload fell back.</summary>
    public int EffectiveIndexCount =>
        LiveParticles is { HasLiveFrame: true } live ? live.IndexCount : IndexCount;

    /// <summary>Per-quad centers matching the effective VB, for the transient camera-sort IB.</summary>
    public Vector3[]? EffectiveParticleCenters =>
        LiveParticles is { HasLiveFrame: true } live ? live.ParticleCenters : ParticleCenters;
}
#endif
