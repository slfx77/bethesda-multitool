#if WINDOWS_GUI
using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
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
    ///     Per-draw env-map constants (uEnvMap): x = cube bindless slot, y = scale, z = smoothness.
    ///     x stays −1 until the entry is RESIDENT as a TextureCube — cold placeholders are 2D SRVs,
    ///     and indexing one through the shader's cube alias would read a mismatched descriptor.
    ///     Deliberately NOT cached: per-frame CB fills re-read it so promotion lands (same pattern
    ///     as the bindless indices on frozen batches).
    /// </summary>
    public Vector4 EnvMapState =>
        EnvMap is { IsResident: true, IsCubemap: true } env
            ? new Vector4(env.BindlessIndex, EnvMapScale, EnvMapSmoothness, 0f)
            : new Vector4(-1f, 0f, 0f, 0f);

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
                IsLeafBillboard ? (AlphaTest ? 2f : 1f) : 0f,
                SpecularMap is not null ? 1f : 0f, // .z > 0.5 = sample TexIndices.z for the spec mask
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
    public bool IsLeafBillboard { get; init; }

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
    ///     Per-frame skinned-pose vertex buffer override, set (or cleared) every frame by the CPU
    ///     mesh skinner for animated skinned meshes; null = draw the static rest-pose VB. Ring-buffer
    ///     backed — valid for the frame it was written, hence the clear-or-refresh-every-frame rule.
    ///     Render-thread only.
    /// </summary>
    public VertexBufferView? AnimatedVertexBufferView { get; set; }

    /// <summary>The vertex buffer the draw should bind this frame (animated override, else static).</summary>
    public VertexBufferView EffectiveVertexBufferView => AnimatedVertexBufferView ?? VertexBufferView;
}
#endif
