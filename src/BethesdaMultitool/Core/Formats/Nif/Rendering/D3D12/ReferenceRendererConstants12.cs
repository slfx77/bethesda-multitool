using System.Numerics;
using System.Runtime.InteropServices;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;

/// <summary>
///     GPU constant-buffer ABI types for <c>ReferenceRenderer12</c>. Kept outside the WINDOWS_GUI
///     implementation file so platform-neutral tests can reflect the exact production ABI types;
///     the renderer imports them via <c>using static</c>.
/// </summary>
internal static class ReferenceRendererConstants12
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct PerDrawConstants
    {
        public Matrix4x4 World;
        public Vector4 AlphaState;
        public Vector4 RenderState;
        public Vector4 TextureState;
        // Bindless TexIndices for the (non-instanced) blended draw path. Kept here so the
        // PS interface stays uniform (it expects vTexIndices regardless of which VS produced it).
        public TexIndexQuad TexIndices;
        // Sun specular term: xyz = tint, w = Phong exponent (0 = no specular). Matches the uSpecular field
        // in reference.vert.hlsl.
        public Vector4 Specular;
        // Camera world-space right/up for per-card leaf billboards (baked particle clouds on the blended
        // path). Only read when TextureState.y marks a leaf submesh.
        public Vector4 CameraRight;
        public Vector4 CameraUp;
        // BGEM effect terms (uEffectTint / uEffectFalloff): rgb tint + falloff-enabled flag in .w,
        // then the |N·V| opacity ramp params. For classic PP Lighting30 (TextureState bit 4), the
        // mutually-exclusive EffectFalloff slot carries raw emission rgb + material multiplier.
        public Vector4 EffectTint;
        public Vector4 EffectFalloff;
        // FO4 cubemap environment mapping (uEnvMap): x = cube bindless slot (−1 until the cube
        // texture is resident), y = envMapScale, z = material smoothness, w = blend operation.
        public Vector4 EnvMap;
        // uUvScroll: xy = fractional UV phase for scrolled materials (NiUVController — waterfalls
        // draw on this blended path); z = CPU-computed classic sun-specular LOD fade; w unused.
        public Vector4 UvScroll;
        // uSoftParticle: encoded depth SRV slot/view kind, near/far, signed falloff. Total = 256 bytes.
        public Vector4 SoftParticle;
    }

    /// <summary>
    ///     Per-batch (per <c>DrawIndexedInstanced</c>) constants for the instanced opaque path,
    ///     matching the <c>InstanceDraw</c> cbuffer in <c>reference_instanced.vert.hlsl</c>.
    ///     Material/texture state is identical across a batch (it comes from the submesh, the
    ///     batch key), so it is uploaded once here instead of per instance. <see cref="InstanceBase" />
    ///     is the start offset of this batch's world matrices inside the shared instance buffer.
    ///     `.x` of <see cref="TexIndices" /> is the diffuse bindless slot, `.y` the normal slot,
    ///     and `.z` the state-discriminated specular/environment-mask/simple-height union.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct InstanceDrawConstants(
        Vector4 AlphaState,
        Vector4 RenderState,
        Vector4 TextureState,
        TexIndexQuad TexIndices,
        uint InstanceBase,
        // uUvScroll: fractional UV phase for scrolled materials (NiUVController — waterfalls, lava).
        // Occupies what were two padding uints; writers that never fill them leave 0 = no scroll.
        // Later append-only fields bring the current layout to 256 bytes (see SpecularLodParams below).
        float UvOffsetU = 0,
        float UvOffsetV = 0,
        // uWindMatrixValid: nonzero when this frame's PerFrame CB carries live wind-v2 sway matrices.
        // Writers that never upload them leave it 0 and the shader's sway path stays inert.
        uint WindMatrixValid = 0,
        // 1A — sun specular term (xyz = tint, w = Phong exponent; 0 = none). Matches the uSpecular field
        // in reference_instanced.vert.hlsl.
        Vector4 Specular = default,
        // Camera world-space right/up for per-card leaf billboards (uCameraRight/uCameraUp).
        // Identical across every batch in a frame; only read by leaf submeshes.
        Vector4 CameraRight = default,
        Vector4 CameraUp = default,
        // SpeedTree wind (uWind = rock amount/phase, rustle amount/phase).
        Vector4 Wind = default,
        // BGEM effect terms (uEffectTint / uEffectFalloff), or the mutually-exclusive classic
        // Lighting30 emission tuple selected by TextureState bit 4.
        Vector4 EffectTint = default,
        Vector4 EffectFalloff = default,
        // FO4 cubemap environment mapping (uEnvMap: x = cube slot or −1, y = scale,
        // z = smoothness, w = blend operation).
        Vector4 EnvMap = default,
        // Opaque path sentinel (x = 0).
        Vector4 SoftParticle = default,
        // FNV GRASS2000: xy fixed world +Y, z recovered setting-interpolated magnitude,
        // w wrapped time phase (radians).
        Vector4 TallGrassWind = default,
        // Classic FNV direct-sun specular LOD. Bounds = transformed root-local center/radius;
        // params = start/end/LOD-adjust/enabled. Appending preserves every prior field offset while
        // growing the logical cbuffer from 224 to its already-allocated 256-byte CBV footprint.
        Vector4 SpecularLodBounds = default,
        // Total InstanceDraw layout = 256 bytes (one CBV-aligned allocation, same as before).
        Vector4 SpecularLodParams = default);

    /// <summary>
    ///     CPU mirror of <c>reference_instanced.vert.hlsl</c>'s <c>PerFrame</c> cbuffer in its
    ///     SHADOW variant (<c>SHADOW_CARD_LIGHT_FACING</c>): the light viewProj, the SpeedTree wind
    ///     matrices, and the light-perpendicular leaf-card billboard basis.
    ///     <para>
    ///         <b>Field order is load-bearing.</b> The optional card basis comes LAST so
    ///         <see cref="WindMatrices" /> sits at the same offset 64 in both shader variants — the
    ///         main pass allocates <c>64 + 256 = 320</c> bytes and must stay byte-identical. The
    ///         previous layout put the card basis at 64, which pushed <c>uWindMatrices</c> to 96 in
    ///         the shadow variant against a 96-BYTE allocation: the wind matrices fell entirely
    ///         outside the buffer. That is why every wind term used to be <c>#ifdef</c>'d out of the
    ///         shadow pass, which left the shadow map holding a REST-POSE canopy while the visible
    ///         one swayed — the cast shadow then crawled against the leaves casting it.
    ///     </para>
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct InstancedShadowPerFrameConstants
    {
        public Matrix4x4 ViewProj;          // offset 0   — shared with the main variant
        public WindMatrixQuad WindMatrices; // offset 64  — MUST match the main variant
        public Vector4 CardRight;           // offset 320 — shadow-only tail
        public Vector4 CardUp;              // offset 336

        /// <summary>Total 352 bytes: 64 viewProj + 256 wind + 32 card basis.</summary>
        public const uint ByteSize = 64 + 256 + 32;

        /// <summary>Offset of <see cref="WindMatrices" />; must equal the main pass's, which is 64.</summary>
        public const uint WindMatricesOffset = 64;
    }

    /// <summary>
    ///     The four SpeedTree whole-canopy sway matrices as one blittable field, so the shadow
    ///     PerFrame struct can be written with a single assignment instead of pointer arithmetic.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct WindMatrixQuad
    {
        public Matrix4x4 M0;
        public Matrix4x4 M1;
        public Matrix4x4 M2;
        public Matrix4x4 M3;

        public Matrix4x4 this[int index]
        {
            set
            {
                switch (index)
                {
                    case 0: M0 = value; break;
                    case 1: M1 = value; break;
                    case 2: M2 = value; break;
                    default: M3 = value; break;
                }
            }
        }
    }

    /// <summary>
    ///     Packed 4-uint TexIndices field. C# has no <c>uint4</c> primitive, so we lay out
    ///     four contiguous uints with explicit <see cref="LayoutKind.Sequential" /> so the
    ///     C# struct blits cleanly into the HLSL <c>uint4</c>. <see cref="X" /> = diffuse,
    ///     <see cref="Y" /> = normal, <see cref="Z" /> = specular/environment-mask/height union,
    ///     <see cref="W" /> = FO4 gradient palette or classic Lighting30 glow map
    ///     (TextureState disambiguates).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct TexIndexQuad(uint X, uint Y, uint Z, uint W);
}
