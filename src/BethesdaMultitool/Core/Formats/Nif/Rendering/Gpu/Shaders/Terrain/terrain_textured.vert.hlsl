// v3 Phase 2c+ engine-accurate terrain vertex shader. Replaces the prior per-quadrant
// origin/scale math with per-vertex blend weights: each vertex carries a Vector4 of weights
// into up to 4 cell-wide diffuse texture slots, sourced from `CellTerrainTextureSet`. The
// per-pixel weighted sum in the fragment shader reproduces the engine's per-vertex blend
// table behaviour, so cross-quadrant boundaries and (with neighbor-fed weight tables)
// cross-cell boundaries fade smoothly instead of the prior hard seam.
//
// Slot 0 vertex stream: shared GpuMeshUploader.GpuVertex (position/normal/texcoord/color/
// tangent/bitangent), unchanged so the slot-0 buffer is the same shared format other 3D
// passes use.
// Slot 1 vertex stream: per-vertex aLayerWeights (float4), built per-cell from the engine-
// accurate per-vertex weight table and uploaded as an independent GPU buffer per cell.

cbuffer PerFrame : register(b0)
{
    float4x4 uViewProj;
};

// b1 = PerCell texture indices, consumed by the fragment shader. b2 = PerMode
// (debug + UV scale). Per-quadrant constants from the prior layout are gone; everything
// per-cell varies via bindless texture indices + the per-vertex weight stream.
cbuffer PerMode : register(b2)
{
    // x = 1.0 → show diffuse terrain textures (consumed by the fragment shader)
    // y = diffuse UV scale (world units → texture repeats; 1/512 = 8 repeats per cell)
    // z = apply VCLR; w = FNV terrain-normal gate (both consumed by the fragment shader)
    float4 uDebugMode_UvScale_Pad;
};

// 1G — camera-relative render origin (xyz) from the shared atmosphere CB (b3). Subtracted from each
// world vertex before projection (kills the worldspace-edge wobble). Zero when camera-relative is off;
// the leading 9 float4 are the sun/sky/fog/camera fields this VS does not use.
#include "atmosphere.hlsli"

struct VSInput
{
    // Slot 0
    float3 aPosition     : TEXCOORD0;
    float3 aNormal       : TEXCOORD1;
    float2 aTexCoord     : TEXCOORD2;  // unused on this path; kept for layout-stability
    float4 aVertexColor  : TEXCOORD3;
    float3 aTangent      : TEXCOORD4;  // unused
    float3 aBitangent    : TEXCOORD5;  // unused
    // Slot 1 — 16 per-vertex layer weights as four float4s (slots 0..3, 4..7, 8..11, 12..15).
    float4 aLayerWeights0 : TEXCOORD6;
    float4 aLayerWeights1 : TEXCOORD7;
    float4 aLayerWeights2 : TEXCOORD8;
    float4 aLayerWeights3 : TEXCOORD9;
};

struct VSOutput
{
    float4 Position      : SV_Position;
    float3 vWorldNormal  : TEXCOORD0;
    float4 vVertexColor  : TEXCOORD1;
    float2 vWorldUv      : TEXCOORD2;
    float4 vLayerWeights0 : TEXCOORD3;
    float4 vLayerWeights1 : TEXCOORD4;
    float4 vLayerWeights2 : TEXCOORD5;
    float4 vLayerWeights3 : TEXCOORD6;
    float3 vWorldPos     : TEXCOORD7;  // world-space position for per-pixel distance fog
};

VSOutput main(VSInput input)
{
    VSOutput o;
    // Camera-relative: shift the vertex by -origin before projection. UVs keep ABSOLUTE world XY so the
    // terrain texture tiling is unchanged. vWorldPos becomes camera-relative to match the shader's
    // camera (= 0 in this mode) for distance fog.
    float3 worldPosRel = input.aPosition - uCameraOrigin.xyz;
    o.Position = mul(uViewProj, float4(worldPosRel, 1.0));
    o.vWorldNormal = input.aNormal;
    o.vVertexColor = input.aVertexColor;
    o.vWorldUv = input.aPosition.xy * uDebugMode_UvScale_Pad.y;
    o.vLayerWeights0 = input.aLayerWeights0;
    o.vLayerWeights1 = input.aLayerWeights1;
    o.vLayerWeights2 = input.aLayerWeights2;
    o.vLayerWeights3 = input.aLayerWeights3;
    o.vWorldPos = worldPosRel; // camera-relative world pos (matches the shader camera = 0 for fog)
    return o;
}
