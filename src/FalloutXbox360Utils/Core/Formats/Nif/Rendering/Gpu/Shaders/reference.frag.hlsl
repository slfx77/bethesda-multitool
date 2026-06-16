// v3 Phase 3 placed-object pixel shader. Samples the diffuse texture, applies alpha-test for
// foliage / fence / wire-mesh REFRs, modulates by vertex color and Lambert lighting using the
// same hardcoded sun direction as terrain (keeps the scene visually coherent).
//
// 4a — bindless: `textures[]` is an unbounded array indexed by the per-instance
// `TexIndices.x` (diffuse) / `.y` (normal). Slot indices come from
// GpuTextureCache12.Entry.BindlessIndex. NonUniformResourceIndex tells the compiler
// adjacent pixels in a quad may sample different textures (true when bucket walk
// interleaves multiple meshes via ExecuteIndirect in 4b).

// space1 so the unbounded array doesn't collide with the legacy terrain/water SRV table
// or the reference instance root SRV in space0.
Texture2D    textures[]   : register(t0, space1);
SamplerState sDiffuse     : register(s0); // wrap, anisotropic (set in C#)
SamplerState sNormalMap   : register(s1); // wrap, anisotropic (set in C#)

cbuffer PerFrame : register(b0) { float4x4 uViewProj; }

// Shared scene atmosphere (b3). CPU mirror: WorldView3DControl.AtmosphereConstants (7×float4),
// uploaded once per frame and bound for the whole scene (terrain/reference/water all read it).
cbuffer Atmosphere : register(b3)
{
    float4 uSunDirIntensity;    // xyz = sun world dir (toward sun), w = intensity
    float4 uSunColorLighting;   // rgb = sun color, w = lightingEnabled (0/1)
    float4 uAmbientColor;       // rgb = ambient, w = spare
    float4 uSkyTopSkyEnabled;   // rgb = sky-top color, w = skyEnabled (0/1)
    float4 uSkyHorizon;         // rgb = sky-horizon color, w = spare
    float4 uFogColorFogEnabled; // rgb = fog color, w = fogEnabled (0/1)
    float4 uAtmosphereParams;   // x = gameHour, y = fogNear, z = fogFar, w = time
};

// Per-pixel light factor (rgb) for a world-space normal. When lighting is disabled
// (uSunColorLighting.w == 0) this returns the EXACT legacy flat shade — scalar 0.4 + 0.6*lambert
// against the old fixed sun — so toggling lighting off is pixel-identical to the pre-atmosphere
// viewer. Enabled: colored ambient + sun·(N·L), energy-bounded so a fully sunlit surface lands near
// the legacy max (~1.0) instead of blowing out. (Placeholder sun curve; P2b grounds it in decompile.)
float3 AtmosphereLight(float3 N)
{
    if (uSunColorLighting.w < 0.5)
    {
        float legacyLambert = saturate(dot(N, normalize(float3(0.5, 0.5, 1.0))));
        return (0.4 + 0.6 * legacyLambert).xxx;
    }

    float ndotl = saturate(dot(N, uSunDirIntensity.xyz));
    float ambientLuma = saturate(dot(uAmbientColor.rgb, float3(0.3333, 0.3333, 0.3333)));
    return uAmbientColor.rgb + uSunColorLighting.rgb * ndotl * (1.0 - ambientLuma);
}

struct PSInput
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
    bool   IsFrontFace  : SV_IsFrontFace;
};

bool PassAlphaTest(float alpha, float threshold, float functionId)
{
    if (functionId < 0.0) return true;

    int fn = (int)round(functionId);
    if (fn == 0) return true;                  // ALWAYS
    if (fn == 1) return alpha < threshold;     // LESS
    if (fn == 2) return abs(alpha - threshold) <= (0.5 / 255.0); // EQUAL
    if (fn == 3) return alpha <= threshold;    // LEQUAL
    if (fn == 4) return alpha > threshold;     // GREATER
    if (fn == 5) return abs(alpha - threshold) > (0.5 / 255.0);  // NOTEQUAL
    if (fn == 6) return alpha >= threshold;    // GEQUAL
    return false;                              // NEVER / invalid
}

float4 main(PSInput input) : SV_Target
{
    float4 sample = textures[NonUniformResourceIndex(input.vTexIndices.x)].Sample(sDiffuse, input.vTexCoord);
    float sampleAlpha = saturate(sample.a * input.vVertexColor.a);

    // Alpha-test branch — controlled per-draw so foliage with NiAlphaProperty bit 9 set
    // discards transparent pixels rather than rendering them as opaque. Full NIF comparison
    // function is preserved; function < 0 disables testing for opaque and blended draws.
    if (!PassAlphaTest(sampleAlpha, input.vAlphaState.x, input.vAlphaState.y)) discard;

    float3 normal = normalize(input.vWorldNormal);
    if (input.vRenderState.x > 0.5 && !input.IsFrontFace)
    {
        normal = -normal;
    }

    if (input.vRenderState.y > 0.5)
    {
        float3 normalSample = textures[NonUniformResourceIndex(input.vTexIndices.y)].Sample(sNormalMap, input.vTexCoord).rgb;
        float3 mapN;
        if (input.vTextureState.x > 0.5)
        {
            float2 xy = normalSample.rg * 2.0 - 1.0;
            mapN = float3(xy, sqrt(saturate(1.0 - dot(xy, xy))));
        }
        else
        {
            mapN = normalSample * 2.0 - 1.0;
        }

        mapN.y = -mapN.y; // DirectX convention (Y-down normal maps), matching skin.frag.hlsl.
        mapN.xy *= input.vRenderState.z;

        float3 T = normalize(input.vTangent);
        float3 B = normalize(input.vBitangent);
        float3x3 TBN = float3x3(T, B, normal);
        normal = normalize(mul(mapN, TBN));
    }

    // Shared atmosphere lighting (rgb). Lighting-off path inside AtmosphereLight reproduces the
    // legacy `0.4 + 0.6*lambert` scalar exactly, so the OFF state is pixel-identical to before.
    float3 shade = AtmosphereLight(normal);
    if (input.vRenderState.w > 0.5)
    {
        shade = 1.0; // emissive / full-bright shapes (e.g. glow) — unaffected by scene lighting
    }

    // Vertex color modulates the diffuse — NIFs use it for art-direction tints (e.g. dusty
    // rocks, painted billboards). Default-white VCLR leaves the texture untouched.
    float outAlpha = input.vAlphaState.w > 0.5
        ? saturate(sampleAlpha * input.vAlphaState.z)
        : 1.0;
    return float4(sample.rgb * input.vVertexColor.rgb * shade, outAlpha);
}
