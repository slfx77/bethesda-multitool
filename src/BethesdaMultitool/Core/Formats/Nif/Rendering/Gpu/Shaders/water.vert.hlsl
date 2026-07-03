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
    uint4 uDepthParams;  // x = scene-depth SRV index, y/z = near/far bits (PS only; here for layout)
    float4 uRenderOrigin; // xyz = camera-relative render origin (0 on absolute paths); w spare
};

struct WaterInstance
{
    float4 OriginHeightFootprintX; // xy = origin, z = water height, w = footprint X extent
    float4 FootprintY;             // x = footprint Y extent; yzw spare
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
        instance.OriginHeightFootprintX.x + corner.x * instance.OriginHeightFootprintX.w,
        instance.OriginHeightFootprintX.y + corner.y * instance.FootprintY.x,
        instance.OriginHeightFootprintX.z);

    VSOutput o;
    // Project CAMERA-RELATIVE (worldPos − uRenderOrigin, against the caller's camera-relative viewProj)
    // for float32 precision far from the world origin — absolute ~50k-unit coordinates through the
    // absolute viewProj shimmered the water edge as the camera rotated (view-angle-dependent Z). Same
    // scheme as the reference pass (fe830cae). vWorldPos stays ABSOLUTE: the PS anchors noise UVs and
    // the view vector to world space (camera-relative UVs would make ripples swim with the camera).
    o.Position = mul(uViewProj, float4(worldPos - uRenderOrigin.xyz, 1.0));
    o.vWorldPos = worldPos;
    return o;
}
