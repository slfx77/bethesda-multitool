// Oblivion (TES4) grass vertex shader — a transcription of retail GRASS2020.vso.
//
// Source: Oblivion Data\Shaders\shaderpackage019.sdp, disassembled clean-room by
// tools/scripts/sdp_disasm.py; the full listing (all 31 GRASS shaders, with the CTAB constant
// names quoted below) is tools/GhidraProject/oblivion_grass_shaderpackage019_disassembled.txt.
//
// WHY THIS FILE EXISTS. The shared reference shaders light every game through one path,
// `BaseMap * (Ambient + NdotL * Sunlight)`, using the mesh's own normal. A grass blade card is
// near-vertical, so with a high sun `dot(N, L)` collapses to ~0 and the whole carpet falls to
// ambient while the terrain beneath it takes full sun — the "grass is too dark" the user reported
// against retail in-game oracles. Retail does something structurally different that no uniform can
// express: it computes the lighting IN THE VERTEX SHADER, from the TERRAIN normal rather than any
// surface normal, and the pixel shader ADDS ambient to it rather than multiplying.
//
// RETAIL (GRASS2020.vso), with CTAB names — c0 DiffuseDir, c1 DiffuseColor, c6 AmbientColor,
// c7 AddlParams, c5 AlphaParam, c20..c21 InstanceData:
//     frc r0, c20[a0.w]           ; fractional part of the instance record
//     add r0.xyz, r0, -0.5
//     mul r2.xyz, r0.w, v1        ; per-instance dimmer * vertex colour
//     add r0.xyz, r0, r0          ; 2*(frac - 0.5)  -> the decoded normal (see below)
//     dp3 r0.x, c0, r0            ; dot(DiffuseDir, N)
//     max/min -> saturate
//     mul r2.xyz, r2, r2.w        ; * saturate(N.L)
//     mul r2.xyz, r2, c1          ; * DiffuseColor
//     mul oT5.xyz, r2, c7.x       ; * AddlParams.x
//     mov oT4, c6                 ; ambient travels separately, UNattenuated
//
// THAT `frc` IS A NORMAL DECODE, NOT NOISE. It reads as pseudo-random until you notice the engine
// packs it deliberately: CreateGrass stores each normal component as min((N + 1) / 2, 0.97) in the
// fractional XYZ of the instance position, so `2 * (frac(p) - 0.5)` recovers min(N, 0.94) exactly.
// Our own CPU mirror documents the same packing — see GrassPlacementBuilder.ComposeFnvWorldMatrix.
// The integer part of that same register is still used as the placement translation
// (`add r1.xyz, r0, c20[a0.w]`), which is what makes the packing free.
//
// HOW WE GET THE SAME NORMAL. We do not pack anything into a position: GrassPlacementBuilder already
// composes the terrain normal into the world matrix's up axis (`up = fitToSlope ? terrainNormal :
// UnitZ`), so we read it straight back out. Note the axis convention — CPU System.Numerics ROWS
// become HLSL COLUMNS here (the same convention ClassicSpecularLodFade relies on in
// reference_instanced.vert.hlsl), so the up axis is column 2: (world[0].z, world[1].z, world[2].z).
//
// DELIBERATE DEVIATIONS, none of them invented values:
//   * AddlParams.x -> 1.0. Its provenance is not recovered from the CTAB and no [Grass] INI key is
//     known to feed it. Shipping 1.0 and saying so beats inventing a number; if it turns out to be
//     an INI value it becomes a GrassShaderProfile field, following AmbientLightScale's precedent.
//   * The per-instance dimmer frac(InstanceData).w is NOT applied. It rides the same packed register
//     we do not reproduce, and GrassPlacementBuilder explicitly declines to fabricate that payload.
//     Do NOT substitute FNV's frac(W)*0.75+0.25 remap: that is a different variant, and it was
//     diagnosed for FNV being too BRIGHT, so its sign is wrong here.
//   * Fog is left to the shared per-pixel ApplyFog in the paired pixel shader. Retail computes a
//     per-VERTEX linear fog into COLOR0 (oD0), but our fog stage keeps grass coherent with the
//     terrain it sits on.
//   * oT5.w — retail's smooth distance envelope, a fade-IN as well as a fade-out — is not applied.
//     The viewer keeps its authoritative CPU hard end (2000 -> 3000) unchanged from today, so this
//     shader changes LIGHTING ONLY and the soft blade silhouettes the user validated are preserved.
//
// FALSIFIABLE PREDICTION for the oracle pass: because the defect is `dot(cardNormal, sunDir)`
// collapsing at high sun, the correction must be LARGEST at noon and near-nil at low sun. If a low
// sun changes as much as noon does, this diagnosis is wrong.

cbuffer PerFrame : register(b0)
{
    float4x4 uViewProj;
};

// Prefix of the shared PerDraw CB (b1). Declaring only the leading fields is the established
// practice in this shader family and is layout-safe: HLSL reads the same byte offsets.
cbuffer PerDraw : register(b1)
{
    float4x4 uWorld;
    float4 uAlphaState;   // x = test threshold, y = comparison function, z = material alpha
    float4 uRenderState;
    float4 uTextureState; // z bit1 = clamp U, bit2 = clamp V (sampler selection)
    uint4  uTexIndices;   // x = diffuse
};

// Shared Atmosphere CB (b3). Retail CTAB mapping for the three fields this VS consumes:
// uSunDirIntensity.xyz = DiffuseDir, uSunColorLighting = DiffuseColor (w = lightingEnabled),
// uAmbientColor.rgb = AmbientColor.
#include "atmosphere.hlsli"

struct VSInput
{
    float3 aPosition    : TEXCOORD0;
    float3 aNormal      : TEXCOORD1;
    float2 aTexCoord    : TEXCOORD2;
    float4 aVertexColor : TEXCOORD3;
    float3 aTangent     : TEXCOORD4;
    float3 aBitangent   : TEXCOORD5;
};

struct VSOutput
{
    float4 Position : SV_Position;
    float2 vTexCoord : TEXCOORD0;
    // Retail oT4 / oT5.rgb. Kept as SEPARATE interpolants rather than pre-summed because the pixel
    // shader must add them (lit = oT5 + oT4) — folding them here would turn retail's additive
    // composition into whatever the consumer does with a single value.
    nointerpolation float3 vGrassAmbient : TEXCOORD1;
    float3 vGrassDiffuse : TEXCOORD2;
    nointerpolation float4 vAlphaState   : TEXCOORD3;
    nointerpolation float4 vTextureState : TEXCOORD4;
    nointerpolation uint4  vTexIndices   : TEXCOORD5;
    float3 vWorldPos : TEXCOORD6;
};

/// Retail's decoded per-instance normal, recovered from the world matrix instead of a packed
/// position. Falls back to world up for a degenerate (zero-scale) matrix.
float3 ResolveTes4GrassPlacementNormal(float4x4 world)
{
    float3 up = float3(world[0].z, world[1].z, world[2].z);
    float lengthSquared = dot(up, up);
    if (lengthSquared < 1e-8)
    {
        return float3(0.0, 0.0, 1.0);
    }

    float3 normal = up * rsqrt(lengthSquared);
    // Reproduce the engine's packing ceiling: storing min((N + 1) / 2, 0.97) means the decode can
    // never return more than 0.94 in a component, so retail's flat-ground tuft lights at 0.94 rather
    // than 1.0. Clamping here matches that exactly; omitting it would make our grass ~6% brighter
    // than retail on level ground.
    return min(normal, 0.94);
}

/// Retail oT5.rgb, minus the two packed terms we decline to fabricate (see the header).
float3 EvaluateTes4GrassDiffuse(float3 vertexRgb, float3 placementNormal)
{
    float ndotl = saturate(dot(uSunDirIntensity.xyz, placementNormal));
    return vertexRgb * ndotl * uSunColorLighting.rgb;
}

VSOutput main(VSInput input)
{
    VSOutput o;

    float4 worldPos = mul(uWorld, float4(input.aPosition, 1.0));
    o.Position = mul(uViewProj, worldPos);
    o.vWorldPos = worldPos.xyz;
    o.vTexCoord = input.aTexCoord;

    float3 placementNormal = ResolveTes4GrassPlacementNormal(uWorld);

    if (uSunColorLighting.w < 0.5)
    {
        // Lighting-off toolbar toggle. Without this arm the toggle would silently stop applying to
        // grass the moment it moved onto its own shader. Mirrors AtmosphereLight's legacy branch,
        // evaluated against the placement normal so grass stays consistent with the terrain.
        float legacyLambert = saturate(dot(placementNormal, normalize(float3(0.5, 0.5, 1.0))));
        o.vGrassAmbient = 0.4.xxx;
        o.vGrassDiffuse = (0.6 * legacyLambert).xxx * input.aVertexColor.rgb;
    }
    else
    {
        o.vGrassAmbient = uAmbientColor.rgb;
        o.vGrassDiffuse = EvaluateTes4GrassDiffuse(input.aVertexColor.rgb, placementNormal);
    }

    o.vAlphaState = uAlphaState;
    o.vTextureState = uTextureState;
    o.vTexIndices = uTexIndices;
    return o;
}
