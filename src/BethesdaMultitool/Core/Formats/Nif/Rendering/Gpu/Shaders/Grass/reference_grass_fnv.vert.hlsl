// FO3/FNV grass vertex shader — a transcription of the engine's shipped GRASS permutation
// (GRASS2002.vso in Data/Shaders/shaderpackage019.sdp; its lighting lanes are byte-identical to
// GRASS2000.vso) onto this renderer's instanced ABI.
//
// Why a per-game shader: retail grass is NOT lit like ordinary geometry. The engine bakes terrain
// data into each instance at placement time and lights the blade from THAT, so a whole grass patch
// shades as a continuation of the hillside it grows on:
//
//   L        = 0.25 + 0.75 * frac(InstanceData.w)          (asm: mad r1.w, r1.w, c8.x, c8.y)
//   N        = 2 * frac(InstanceData.xyz) - 1              (= min(terrainNormal, 0.94), UNNORMALIZED)
//   ambient  = L * AmbientColor                            (asm: mul oT4, r1.w, c6 — no vertex color)
//   sun      = AddlParams.x * L * saturate(dot(DiffuseDir, N)) * vColor.rgb * DiffuseColor.rgb
//
// The 1.5 sun multiplier is a `def` IMMEDIATE in the shipped PC program, not a driven constant:
// GRASS2002.vso declares `def c7, 0.750000, 0.250000, 0.007812, 1.500000` and ends with
// `mul o5.xyz, r0, c7.wwww`, and its CTAB contains no AddlParams entry at all (PC disassembly:
// TestOutput/fnv-completion/fnv-grass-retail-20260716/grass-pc-shader-disassembly.txt:161-234;
// the Xbox 360 build matches). `AddlParams` as a CPU-driven register belongs to OBLIVION's
// GRASS2020 family — a different program — so there is no per-frame fill to chase here.
//
// The two payloads reach us in the instance world matrix's otherwise-unused w-lanes (HLSL
// world[3]), baked by FnvGrassLighting/GrassPlacementBuilder from the authored LAND vertex colors
// and normals — see TESTerrainLODManager::CreateGrass (0x823BA528) and
// TallGrassShaderProperty::AddGrass (0x82A456B8) in grass_session_g_fnv_decompiled.txt, plus the
// land queries in land_query_decompiled.txt.
//
// Deliberate deviations from the retail program, in one place so they stay reviewable:
//   * Distance culling stays CPU-side (GrassDistanceCullPolicy's hard 8000-unit end), so the
//     engine's oT5.w distance gate is not reproduced here.
//   * GRASS2001 (mesh-normal lighting, GRAS "Vertex Lighting" flag) is not implemented: every
//     shipped FO3 and FNV GRAS record authors flags 0x06 with that bit CLEAR.
//   * Wind is this renderer's recovered FnvTallGrassWind (identical GRASS2000 model), carried over
//     verbatim from reference_instanced.vert.hlsl so grass keeps its sway on this route.

cbuffer PerFrame : register(b0)
{
    // Prefix declaration of the shared 320-byte PerFrame CB: HLSL only requires that the leading
    // layout match, and this program needs nothing past the view-projection matrix.
    float4x4 uViewProj;
};

// Per-batch constants — the FULL shared InstanceDraw layout. The offsets are load-bearing
// (uTextureState @32, uInstanceBase @64, uTallGrassWind @208), so the whole struct is declared
// verbatim rather than truncated.
cbuffer InstanceDraw : register(b1)
{
    float4 uAlphaState;
    float4 uRenderState;
    float4 uTextureState;
    uint4  uTexIndices;
    uint   uInstanceBase;
    float2 uUvScroll;
    uint   uWindMatrixValid;
    float4 uSpecular;
    float4 uCameraRight;
    float4 uCameraUp;
    float4 uWind;
    float4 uEffectTint;
    float4 uEffectFalloff;
    float4 uEnvMap;
    float4 uSoftParticle;
    float4 uTallGrassWind; // xy = fixed world +Y direction, z = magnitude, w = wrapped phase
    float4 uSpecularLodBounds;
    float4 uSpecularLodParams;
};

#include "atmosphere.hlsli"

StructuredBuffer<float4x4> uInstanceWorlds : register(t8);

struct VSInput
{
    float3 aPosition    : TEXCOORD0;
    float3 aNormal      : TEXCOORD1;
    float2 aTexCoord    : TEXCOORD2;
    float4 aVertexColor : TEXCOORD3;
    float3 aTangent     : TEXCOORD4;
    float3 aBitangent   : TEXCOORD5;
};

// Reduced interpolant set: the engine's grass shader carries a lit sun term, an ambient term, the
// texture coordinate and its fog — nothing else. vGrassSun interpolates (vertex color varies along
// the blade); vGrassAmbient is constant across the instance.
struct VSOutput
{
    float4 Position                      : SV_Position;
    float2 vTexCoord                     : TEXCOORD0;
    float3 vGrassSun                     : TEXCOORD1;
    nointerpolation float3 vGrassAmbient : TEXCOORD2;
    nointerpolation float4 vAlphaState   : TEXCOORD3;
    nointerpolation float4 vTextureState : TEXCOORD4;
    nointerpolation uint4  vTexIndices   : TEXCOORD5;
    float3 vWorldPos                     : TEXCOORD6;
};

// GRASS2000/2002 remap constants — `def c8, 0.75, 0.25, 0.0078125, 0` in the shipped program
// (CPU mirror: FnvGrassLighting).
static const float LightBaseConstant = 0.25;
static const float LightRangeConstant = 0.75;
// Fallback only, for hosts that do not publish the grass lane: the engine's sun multiplier is the
// imagespace GrassDimmer, whose shipped FNV DefaultImageSpaceExterior value is 1.3.
static const float SunBoostFallback = 1.3;

bool IsTallGrassMaterial(float packedState)
{
    return (((uint)round(packedState)) & 32u) != 0u;
}

float WrapTallGrassPhase(float phase)
{
    const float Pi = 3.14159265358979323846;
    const float TwoPi = 6.28318530717958647692;
    phase = fmod(phase + Pi, TwoPi);
    if (phase < 0.0) phase += TwoPi;
    return phase - Pi;
}

VSOutput main(VSInput input, uint instanceId : SV_InstanceID)
{
    VSOutput o;

    float4x4 world = uInstanceWorlds[uInstanceBase + instanceId];
    // The baked lighting payload: xyz = min(terrainNormal, 0.94) exactly as the engine's shader
    // decodes it (deliberately NOT renormalized), w = the baked terrain light in [0, 0.99].
    float4 payload = world[3];

    float4 worldPos = mul(world, float4(input.aPosition, 1.0));
    // The payload lives in the lanes that would otherwise contribute to w; restore the affine value
    // before anything downstream uses it.
    worldPos.w = 1.0;

    bool isTallGrass = IsTallGrassMaterial(uTextureState.z);
    float tallGrassWindWeight = isTallGrass ? saturate(input.aVertexColor.a) : 0.0;

    // GRASS2000 wind: applied in WORLD XY after the card transform, phase seeded from ABSOLUTE
    // placement XY so it is invariant under the renderer's snapped-origin rebasing.
    float3 relativePlacement = mul(world, float4(0.0, 0.0, 0.0, 1.0)).xyz;
    if (isTallGrass && uTallGrassWind.z != 0.0)
    {
        float2 absolutePlacement = relativePlacement.xy + uCameraOrigin.xy;
        float phase = WrapTallGrassPhase(
            (absolutePlacement.x + absolutePlacement.y) / 128.0 + uTallGrassWind.w);
        float weightedOffset = sin(phase) * uTallGrassWind.z *
            tallGrassWindWeight * tallGrassWindWeight;
        worldPos.xy += uTallGrassWind.xy * weightedOffset;
    }

    o.Position = mul(uViewProj, worldPos);
    o.vWorldPos = worldPos.xyz;
    o.vTexCoord = input.aTexCoord + uUvScroll;
    o.vAlphaState = uAlphaState;
    o.vTextureState = uTextureState;
    o.vTexIndices = uTexIndices;

    // frac() is retained from the engine literal even though the CPU bake already stores a
    // fraction: an instance whose matrix carries no payload (a plain affine matrix, w-lane 1.0)
    // then fails safe to the minimum L = 0.25 rather than full brightness.
    float bakedLight = LightBaseConstant + LightRangeConstant * frac(payload.w);
    float3 terrainNormal = payload.xyz;

    if (uSunColorLighting.w < 0.5)
    {
        // Lighting-off view state: match the flat presentation the rest of the viewer uses rather
        // than leaving grass black (mirrors reference_grass_oblivion.vert.hlsl's toggle arm).
        float3 unit = dot(terrainNormal, terrainNormal) > 1e-8
            ? normalize(terrainNormal)
            : float3(0.0, 0.0, 1.0);
        float flatLambert = saturate(dot(unit, normalize(float3(0.5, 0.5, 1.0))));
        o.vGrassAmbient = float3(0.4, 0.4, 0.4);
        o.vGrassSun = (0.6 * flatLambert) * input.aVertexColor.rgb;
    }
    else
    {
        // asm: mul oT4, r1.w, c6 — the ambient term carries the baked light and nothing else.
        // The SDR clamp mirrors the shared reference path (reference.frag.hlsl:124-127): outside
        // HDR the engine clamps the ambient sum componentwise before direct lights.
        o.vGrassAmbient = uSkyHorizon.w > 0.5
            ? bakedLight * uAmbientColor.rgb
            : min(bakedLight * uAmbientColor.rgb, 1.0);
        // asm: mul r1.yzw, r1.w, v1.xxyz / mul r1.xyz, r1.x, r1.yzww / mul r1.xyz, r1, c1 /
        //      mul oT3.xyz, r1, c7.x  — vertex color rides the SUN term only, and the whole term is
        // scaled by AddlParams.x (c7.x), which TallGrassShader::SetupGeometryConstants writes from
        // the imagespace GrassDimmer under HDR. DiffuseColor (c1) is the sun NiLight's
        // m_kDiffuseColor copied RAW by TallGrassShader::SetupTransformations — the SunlightDimmer
        // that statics/actors/LAND receive via BSShaderLightingProperty is never applied to grass,
        // so uSunColorLighting.rgb (which has it folded in) would double-count.
        float lambert = saturate(dot(uSunDirIntensity.xyz, terrainNormal));
        bool hasGrassLane = uGrassSunColorScale.w > 0.0001;
        float3 grassSunColor = hasGrassLane ? uGrassSunColorScale.rgb : uSunColorLighting.rgb;
        float grassDimmer = hasGrassLane ? uGrassSunColorScale.w : SunBoostFallback;
        o.vGrassSun = grassDimmer * bakedLight * lambert *
            input.aVertexColor.rgb * grassSunColor;
    }

    return o;
}
