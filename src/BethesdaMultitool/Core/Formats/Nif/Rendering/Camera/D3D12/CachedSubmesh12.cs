#if WINDOWS_GUI
using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
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
    ///     Per-draw env-map constants (uEnvMap): x = cube bindless slot, y = scale, z = smoothness,
    ///     w = additive-blend flag (see <see cref="IsAdditiveBlend" /> — consumed by the fog term).
    ///     x stays −1 until the entry is RESIDENT as a TextureCube — cold placeholders are 2D SRVs,
    ///     and indexing one through the shader's cube alias would read a mismatched descriptor.
    ///     Deliberately NOT cached: per-frame CB fills re-read it so promotion lands (same pattern
    ///     as the bindless indices on frozen batches).
    /// </summary>
    public Vector4 EnvMapState =>
        EnvMap is { IsResident: true, IsCubemap: true } env
            ? new Vector4(env.BindlessIndex, EnvMapScale, EnvMapSmoothness, IsAdditiveBlend ? 1f : 0f)
            : new Vector4(-1f, 0f, 0f, IsAdditiveBlend ? 1f : 0f);

    /// <summary>
    ///     Destination blend factor ONE (NIF blend byte 0; 10 = GL src-alpha-saturate approximates
    ///     One in the PSO too) — an additive draw. Rides EnvMapState.w so the fog term can fade
    ///     additive contributions toward BLACK with distance (engine behavior) instead of lerping
    ///     toward the fog color, which ADDS fog-colored light to every distant glow (backlog A8:
    ///     the distant Strip glow never attenuated).
    /// </summary>
    public bool IsAdditiveBlend => AlphaBlend && DstBlendMode is 0 or 10;

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

            var state = new Vector4(
                Normal.NormalDecodeMode == GpuNormalDecodeMode.Bc5ReconstructZ ? 1f : 0f,
                // .y > 0.5 routes the instanced VS to the leaf-billboard branch; 2 additionally marks
                // an ALPHA-TESTED leaf card (SPT leaves — the PS boosts test alpha by texture LOD to
                // undo mip alpha decay; baked particle clouds are blends and stay at 1).
                IsSpeedTreeBranch ? -1f : IsLeafBillboard ? (AlphaTest ? 2f : 1f) : 0f,
                // Exact integer flags carried in a float constant: bit 0 = sample TexIndices.z for
                // the spec mask, bit 1 = clamp U, bit 2 = clamp V. All values are <= 7 and therefore
                // exactly representable; shaders decode with integer bit tests.
                (SpecularMap is not null ? 1f : 0f) +
                (ClampTextureU ? 2f : 0f) +
                (ClampTextureV ? 4f : 0f),
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
                                 EnvMap is not { IsReady: false };
    public required bool HasBump { get; init; }
    public required NifAlphaRenderMode AlphaRenderMode { get; init; }
    public required bool AlphaBlend { get; init; }
    public required bool AlphaTest { get; init; }
    public required float AlphaTestThreshold { get; init; }
    public required byte AlphaTestFunction { get; init; }
    public required byte SrcBlendMode { get; init; }
    public required byte DstBlendMode { get; init; }
    public required float MaterialAlpha { get; init; }
    public required bool DoubleSided { get; init; }
    public required bool IsEmissive { get; init; }
    public required Vector3 LocalBoundsCenter { get; init; }

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
