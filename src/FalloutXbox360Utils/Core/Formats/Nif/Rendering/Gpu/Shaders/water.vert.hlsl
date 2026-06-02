// v3 water vertex shader — emits a flat XY quad per visible water cell. The 6 quad
// corners are expanded from SV_VertexID, while per-cell origin/height/color is read from
// a structured instance buffer by SV_InstanceID. Two triangles wind CCW from above.
//
// Per-draw constants:
//   uViewProj             — camera view·projection

cbuffer Uniforms : register(b0)
{
    float4x4 uViewProj;
};

struct WaterInstance
{
    float4 CellOriginAndWater;
    float4 Color;
};

StructuredBuffer<WaterInstance> uInstances : register(t0);

struct VSOutput
{
    float4 Position : SV_Position;
    float4 vColor   : COLOR0;
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
        instance.CellOriginAndWater.x + corner.x * instance.CellOriginAndWater.w,
        instance.CellOriginAndWater.y + corner.y * instance.CellOriginAndWater.w,
        instance.CellOriginAndWater.z);

    VSOutput o;
    o.Position = mul(uViewProj, float4(worldPos, 1.0));
    o.vColor = instance.Color;
    return o;
}
