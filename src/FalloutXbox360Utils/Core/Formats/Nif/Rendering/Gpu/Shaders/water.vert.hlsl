// v3 water vertex shader — emits a flat quad per visible water cell. The 6 quad corners are
// expanded from SV_VertexID; per-cell origin/height/footprint comes from a structured instance
// buffer indexed by SV_InstanceID. Two triangles wind CCW from above. World position is passed
// through so the fragment shader can build view-dependent ripples + Fresnel.
//
// Per-frame constants (b0): view·proj, the three DNAM water colors, and (camera pos, time).

cbuffer Uniforms : register(b0)
{
    float4x4 uViewProj;
    float4 uShallow;     // rgb in 0..1; a unused
    float4 uDeep;        // rgb in 0..1
    float4 uReflection;  // rgb in 0..1
    float4 uCamPosTime;  // xyz = camera world pos, w = elapsed seconds
    uint4 uNoiseParams;  // x = NNAM bindless index, y = world units/tile (PS only; here for layout)
    float4 uSurface0;    // NormalsUvScale, FresnelAmount, ReflectivityAmount, Shininess (PS only)
    float4 uSurface1;    // SunPower, DepthFalloffStart, DepthFalloffEnd, spare (PS only)
    float4 uLayer1;      // per-noise-layer: UvScale, WindDirDeg, WindSpeed, AmpScale (PS only)
    float4 uLayer2;
    float4 uLayer3;
};

struct WaterInstance
{
    float4 CellOriginAndFootprint; // xy = origin, z = water height, w = square footprint size
};

StructuredBuffer<WaterInstance> uInstances : register(t0);

struct VSOutput
{
    float4 Position  : SV_Position;
    float3 vWorldPos : TEXCOORD0;
};

static const float2 kQuadCorners[6] =
{
    float2(0, 0), float2(1, 0), float2(0, 1),  // tri 1 (v00, v10, v01) — CCW from +Z
    float2(0, 1), float2(1, 0), float2(1, 1)   // tri 2 (v01, v10, v11) — CCW from +Z
};

VSOutput main(uint vid : SV_VertexID, uint instanceId : SV_InstanceID)
{
    WaterInstance instance = uInstances[instanceId];
    float2 corner = kQuadCorners[vid];
    float3 worldPos = float3(
        instance.CellOriginAndFootprint.x + corner.x * instance.CellOriginAndFootprint.w,
        instance.CellOriginAndFootprint.y + corner.y * instance.CellOriginAndFootprint.w,
        instance.CellOriginAndFootprint.z);

    VSOutput o;
    o.Position = mul(uViewProj, float4(worldPos, 1.0));
    o.vWorldPos = worldPos;
    return o;
}
