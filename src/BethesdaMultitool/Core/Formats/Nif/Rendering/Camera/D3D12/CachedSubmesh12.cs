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
                IsLeafBillboard ? 1f : 0f, // .y > 0.5 routes the instanced VS to the leaf-billboard branch
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
                                 SpecularMap is not { IsReady: false } && GradientMap is not { IsReady: false };
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
}
#endif
