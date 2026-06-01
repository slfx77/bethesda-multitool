// v3 Phase 2a water vertex shader — emits a flat XY quad per cell at the cell's water
// height. No vertex buffer is bound; the 6 quad corners are expanded from SV_VertexID,
// scaled to cell size, and offset by the per-draw uCellOrigin uniform. Two triangles wind
// CCW when viewed from above.
//
// Per-draw constants:
//   uViewProj             — camera view·projection
//   uCellOriginAndWater   — xy = cell origin (world XY), z = water height (world Z), w = cell size
//   uWaterColor           — rgba premultiplied colour passed straight to the PS

cbuffer Uniforms : register(b0)
{
    float4x4 uViewProj;
    float4   uCellOriginAndWater;
    float4   uWaterColor;
};

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

VSOutput main(uint vid : SV_VertexID)
{
    float2 corner = kQuadCorners[vid];
    float3 worldPos = float3(
        uCellOriginAndWater.x + corner.x * uCellOriginAndWater.w,
        uCellOriginAndWater.y + corner.y * uCellOriginAndWater.w,
        uCellOriginAndWater.z);

    VSOutput o;
    o.Position = mul(uViewProj, float4(worldPos, 1.0));
    o.vColor = uWaterColor;
    return o;
}
