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

    // FNV basic SLS lighting, decompile-exact (Sky::UpdateColors, atmosphere_decompiled.txt): the
    // engine scales the lighting bands by per-category constants before the SLS sum — Sunlight (cat 4)
    // × fRam8323ca04 = 1.0, Ambient (cat 3) × fRam8323ca10 = 0.3 (both read straight from the MemDebug
    // binary's .data: 0x8323ca04 = 0x3f800000 = 1.0, 0x8323ca10 = 0x3e99999a = 0.3). The viewer used the
    // Ambient band at FULL strength — ~3.3× too much fill — which washed out daytime contrast and kept
    // nights too bright; apply the 0.3 attenuation. Sunlight keeps its 1.0 scale. Net:
    // finalRGB = BaseMap * (0.3*Ambient + NdotL*Sunlight). pc_basic_sls_shader_disassembly.txt shows the
    // `mad r1, NdotL, PSLightColor, AmbientColor` sum; HDR/tonemap absorbs values > 1 as the engine does.
    // Per-game ambient ("fill") scale in uAmbientColor.w: 0.3 = FNV's fRam8323ca10, but the older
    // ambient-heavier TES4 engines (Oblivion) read far softer, so the host raises it (GameProfile.
    // AmbientLightScale) to keep vertical/shadow surfaces lit instead of point-lit. Falls back to 0.3 when
    // the slot is unset (legacy/headless paths), so unchanged callers keep FNV's value.
    float kAmbientScale = uAmbientColor.w > 0.0001 ? uAmbientColor.w : 0.3;
    float ndotl = saturate(dot(N, uSunDirIntensity.xyz));
    return uAmbientColor.rgb * kAmbientScale + uSunColorLighting.rgb * ndotl;
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
    // A shape that is BOTH blended (vAlphaState.w > 0.5) AND alpha-tested is depth-writing-blend
    // foliage (e.g. NVSeaPlant02): test the TEXTURE alpha alone, not the vertex-color-modulated
    // alpha. Its (binary) leaf mask must survive the test so only the transparent card background
    // discards, while the low vertex-color alpha stays a soft underwater fade applied to outAlpha —
    // testing the modulated alpha would push the whole leaf below the threshold and blank the plant.
    float testAlpha = (input.vAlphaState.w > 0.5) ? sample.a : sampleAlpha;
    if (!PassAlphaTest(testAlpha, input.vAlphaState.x, input.vAlphaState.y)) discard;

    float3 normal = normalize(input.vWorldNormal);
    if (input.vRenderState.x > 0.5 && !input.IsFrontFace)
    {
        normal = -normal;
    }

    // FNV: the normal-map ALPHA channel is the per-texel specular mask (decompile-confirmed in
    // SLS2047.pso — the engine's specular SLS variant). Captured here from the same sample the bump
    // uses; 0 ⇒ no specular (the default when there's no normal map, or for alpha-less BC5).
    float specMask = 0.0;
    if (input.vRenderState.y > 0.5)
    {
        float4 normalSample = textures[NonUniformResourceIndex(input.vTexIndices.y)].Sample(sNormalMap, input.vTexCoord);
        float3 mapN;
        if (input.vTextureState.x > 0.5)
        {
            float2 xy = normalSample.rg * 2.0 - 1.0;
            mapN = float3(xy, sqrt(saturate(1.0 - dot(xy, xy))));
            // BC5/ATI2 carries no alpha → no spec mask (Skyrim+; FNV normal maps are DXT5/DXT1).
        }
        else
        {
            mapN = normalSample.rgb * 2.0 - 1.0;
            specMask = normalSample.a; // DXT5 _n.dds alpha = per-texel specular intensity mask
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

    // FNV sun specular — grounded in the engine's specular SLS pixel shader (SLS2047.pso):
    //   spec = NormalMap.a * pow(saturate(N·H), shininess); a soft N·L ramp fades it on grazing/
    //   back-lit faces; the specular color is the LIGHT color (no per-material specular tint).
    // The per-texel mask (normal-map alpha) is the key correctness fix vs the old always-on
    // Blinn-Phong: the engine only highlights where the material's spec mask is bright (metal/wet
    // trim), not the whole surface — which is why specular previously read as far too strong. Gated on
    // scene lighting on, a normal map present (the mask's source), the material's Specular flag
    // (vSpecular.w > 0 via ComputeSpecularEnabled), a non-zero mask, and a non-emissive shape. The
    // exponent uses the material glossiness as a per-material stand-in for the engine's global shininess
    // constant (Toggles.z / c27.z), which isn't recoverable from the static shader.
    if (uSunColorLighting.w >= 0.5 && input.vRenderState.y > 0.5 && input.vSpecular.w > 0.0 &&
        input.vRenderState.w <= 0.5 && specMask > 0.0)
    {
        float3 V = normalize(uCameraPosFogPower.xyz - input.vWorldPos);
        float3 H = normalize(uSunDirIntensity.xyz + V);
        float specTerm = pow(saturate(dot(normal, H)), max(input.vSpecular.w, 1.0));
        float spec = specMask * specTerm;
        // SLS2047 soft ramp: below N·L 0.2, scale by (N·L + 0.5) (→ 0 by N·L −0.5); full above.
        float ndotl = dot(normal, uSunDirIntensity.xyz);
        if (ndotl <= 0.2) spec *= max(ndotl + 0.5, 0.0);
        lit += uSunColorLighting.rgb * (spec * uSunDirIntensity.w);
    }

    float outAlpha = input.vAlphaState.w > 0.5
        ? saturate(sampleAlpha * input.vAlphaState.z)
        : 1.0;
    return float4(ApplyFog(lit, input.vWorldPos), outAlpha);
}
