// v3 Phase 3 placed-object vertex shader. Projects mesh-local vertices through the per-draw
// world matrix and per-frame viewProj. Passes the world-space normal + UV + vertex color to
// the pixel shader for Lambert + texture sampling.
//
// Same matrix-byte convention as terrain: System.Numerics is row-major in memory; HLSL
// cbuffer reads column-major; CPU side does NOT transpose, so `mul(M, v)` in HLSL produces
// the same result as `M * v` on the CPU. This means a CPU-side row-vector world matrix
// composed as `S * Rx * Ry * Rz * T` is consumed in HLSL as a column-vector matrix doing
// `T * Rz * Ry * Rx * S` applied to (col-vector) position — same end transform.

cbuffer PerFrame : register(b0)
{
    float4x4 uViewProj;
};

cbuffer PerDraw : register(b1)
{
    float4x4 uWorld;
    // uAlphaState: x = alpha-test threshold, y = alpha-test function, z = material alpha,
    // w = blended-mode flag. Unused in VS; kept for PerDraw cbuffer layout parity with PS.
    float4 uAlphaState;
    // uRenderState: x = double-sided, y = has bump map, z = bump strength, w = unlit/emissive.
    // Unused in VS; cull state is handled on the CPU side.
    float4 uRenderState;
    // uTextureState: x = BC5/ATI2 normal map, yzw reserved.
    float4 uTextureState;
    // 4a — bindless TexIndices: .x diffuse slot, .y normal slot, .zw reserved. Mirrors
    // the instanced VS's per-instance struct; the PS reads vTexIndices regardless of path.
    uint4  uTexIndices;
};

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
    float4 Position     : SV_Position;
    float3 vWorldNormal : TEXCOORD0;
    float2 vTexCoord    : TEXCOORD1;
    float4 vVertexColor : TEXCOORD2;
    float3 vTangent     : TEXCOORD3;
    float3 vBitangent   : TEXCOORD4;
    nointerpolation float4 vAlphaState  : TEXCOORD5;
    nointerpolation float4 vRenderState : TEXCOORD6;
    nointerpolation float4 vTextureState : TEXCOORD7;
    nointerpolation uint4  vTexIndices  : TEXCOORD8;
    float3 vWorldPos    : TEXCOORD9;  // world-space position for per-pixel distance fog
};

VSOutput main(VSInput input)
{
    VSOutput o;
    float4 worldPos = mul(uWorld, float4(input.aPosition, 1.0));
    o.Position = mul(uViewProj, worldPos);
    o.vWorldPos = worldPos.xyz;
    // Uniform scale only — pass the normal through the world rotation (3x3 sub-matrix). For
    // non-uniform scale we'd want the inverse-transpose, but Bethesda REFR.Scale is uniform.
    o.vWorldNormal = mul((float3x3)uWorld, input.aNormal);
    o.vTexCoord = input.aTexCoord;
    o.vVertexColor = input.aVertexColor;
    o.vTangent = mul((float3x3)uWorld, input.aTangent);
    o.vBitangent = mul((float3x3)uWorld, input.aBitangent);
    o.vAlphaState = uAlphaState;
    o.vRenderState = uRenderState;
    o.vTextureState = uTextureState;
    o.vTexIndices = uTexIndices;
    return o;
}
