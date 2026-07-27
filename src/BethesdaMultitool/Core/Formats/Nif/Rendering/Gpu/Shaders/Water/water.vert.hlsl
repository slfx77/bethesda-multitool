// v3 water vertex shader — emits two triangles per structured-buffer packet. Cell water writes its
// existing flat quad as six explicit vertices; placed WaterShaderProperty NIFs write the authored
// indexed positions (up to two triangles per packet). This matches the retail FNV WATER000 vertex
// path, which transforms the supplied vertex position directly instead of replacing it with a slab.
// World position is passed through so the fragment shader can build view-dependent ripples + Fresnel.
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
    float4 uRenderOrigin; // xyz = camera-relative render origin; w = scene-depth sample count (PS only)
};

struct WaterInstance
{
    float4 Vertex0;
    float4 Vertex1;
    float4 Vertex2;
    float4 Vertex3;
    float4 Vertex4;
    float4 Vertex5;
};

StructuredBuffer<WaterInstance> uInstances : register(t0);

struct VSOutput
{
    float4 Position  : SV_Position;
    float3 vWorldPos : TEXCOORD0;
};

VSOutput main(uint vid : SV_VertexID, uint instanceId : SV_InstanceID)
{
    WaterInstance instance = uInstances[instanceId];
    float3 worldPos = vid == 0 ? instance.Vertex0.xyz
                    : vid == 1 ? instance.Vertex1.xyz
                    : vid == 2 ? instance.Vertex2.xyz
                    : vid == 3 ? instance.Vertex3.xyz
                    : vid == 4 ? instance.Vertex4.xyz
                               : instance.Vertex5.xyz;

    // Hardware-depth fallback (no scene-depth SRV: ortho modes and top-down/export paths): the
    // PS coplanar tie-break lives in its depth-sample branch and can't run here, so apply the SAME
    // world-unit bias (uDepthParams.w) by lifting the plane — near-coplanar terrain resolves in the
    // water's favour instead of z-fighting (Morrowind's Bitter Coast mud flats sit within ±1 unit
    // of sea level). The depth-sample path keeps the true height: its clip() bias already covers
    // the tie, and lifting there too would double the envelope.
    if (uDepthParams.x == 0xFFFFFFFFu)
    {
        worldPos.z += asfloat(uDepthParams.w);
    }

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
