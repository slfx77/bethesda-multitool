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
    float4 uCameraPosFogPower;  // xyz = camera world pos, w = fog power (1 = linear)
};

// Engine distance fog (grounded in Sky::UpdateFog): a linear near→far ramp toward the resolved fog
// color, raised to the weather's fog power. fogEnabled (uFogColorFogEnabled.w) gates it; OFF returns
// the color unchanged. near/far/power are the daylight-blended WTHR FNAM values from AtmosphereState.
float3 ApplyFog(float3 color, float3 worldPos)
{
    if (uFogColorFogEnabled.w < 0.5)
    {
        return color;
    }

    float dist = length(worldPos - uCameraPosFogPower.xyz);
    float f = saturate((dist - uAtmosphereParams.y) / max(uAtmosphereParams.z - uAtmosphereParams.y, 1.0));
    f = pow(f, max(uCameraPosFogPower.w, 0.01));
    return lerp(color, uFogColorFogEnabled.rgb, f);
}

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

    // Matches the FNV PC SLS pixel shader EXACTLY: finalRGB = BaseMap * (AmbientColor + NdotL*SunColor)
    // -- a STRAIGHT ambient + directional sum with NO energy-conservation scale. The removed
    // (1 - ambientLuma) factor was suppressing the sun (the "lighting too weak" symptom). Grounded in
    // pc_basic_sls_shader_disassembly.txt: `mad r1, NdotL, PSLightColor, AmbientColor`. HDR/tonemap
    // absorbs values > 1, exactly as the engine does.
    float ndotl = saturate(dot(N, uSunDirIntensity.xyz));
    return uAmbientColor.rgb + uSunColorLighting.rgb * ndotl;
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
    float3 vWorldPos    : TEXCOORD9;
    nointerpolation float4 vSpecular   : TEXCOORD10; // xyz = tint, w = Phong exponent (0 = none)
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

        // Only perturb when there's a usable tangent basis. SpeedTree bark carries a normal map but NO
        // tangents (the tube generator emits none), so vTangent is zero — normalize(0) = NaN, which
        // produced garbage per-pixel normals and a "deformed"/ribboned trunk once culling stopped hiding
        // the bark. With a degenerate tangent, fall back to the geometric normal (flat bark, no bump).
        float tLenSq = dot(input.vTangent, input.vTangent);
        if (tLenSq > 1e-6)
        {
            float3 T = input.vTangent * rsqrt(tLenSq);
            float3 B = normalize(input.vBitangent);
            float3x3 TBN = float3x3(T, B, normal);
            normal = normalize(mul(mapN, TBN));
        }
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
    float3 lit = sample.rgb * input.vVertexColor.rgb * shade;

    // Blinn-Phong sun specular (1A). Gated: only when scene lighting is on, the material enables it
    // (vSpecular.w > 0 — set CPU-side from BSShaderFlags' Specular bit + a non-black NiMaterialProperty
    // specular tint), and the shape is not emissive. Half-vector from the view dir (camera pos in the
    // atmosphere CB) and the sun dir; N·L gates out the shadowed side; sun intensity scales it with the
    // daylight fraction so it fades at dusk. Additive on top of the lit diffuse, then fogged.
    if (uSunColorLighting.w >= 0.5 && input.vSpecular.w > 0.0 && input.vRenderState.w <= 0.5)
    {
        float3 V = normalize(uCameraPosFogPower.xyz - input.vWorldPos);
        float3 H = normalize(uSunDirIntensity.xyz + V);
        float ndotl = saturate(dot(normal, uSunDirIntensity.xyz));
        float specTerm = pow(saturate(dot(normal, H)), max(input.vSpecular.w, 1.0));
        lit += uSunColorLighting.rgb * input.vSpecular.rgb * (specTerm * ndotl * uSunDirIntensity.w);
    }

    float outAlpha = input.vAlphaState.w > 0.5
        ? saturate(sampleAlpha * input.vAlphaState.z)
        : 1.0;
    return float4(ApplyFog(lit, input.vWorldPos), outAlpha);
}
