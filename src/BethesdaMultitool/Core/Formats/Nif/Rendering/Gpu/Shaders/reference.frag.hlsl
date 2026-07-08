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
SamplerState sPalette     : register(s2); // CLAMP, linear — grayscale-to-palette lookup only

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

    // FNV basic SLS lighting: finalRGB = BaseMap * (Ambient + NdotL*Sunlight), ambient at FULL strength.
    // pc_basic_sls_shader_disassembly.txt's `mad r1, NdotL, PSLightColor, AmbientColor` has no scale
    // constant in ANY SLS variant, and Sun::Update stores the NAM0 Ambient band into the light's m_kAmb
    // unscaled (atmosphere_decompiled.txt:1911). An earlier reading applied a 0.3 "ambient scale" here
    // (fRam8323ca10) — REFUTED 2026-07-02: that constant is the LIGHTNING-FLASH ambient boost fraction.
    // Sky::SetColor ADDS 0.3·flashIntensity to the blended band (fadds at atmosphere_decompiled.txt:
    // 3385-3391, clamped to the weather's Lightning Color bytes) and the flash decays to zero within
    // seconds (Sky::Update :4365-4371) — it never multiplies the band. Steady-state engine ambient is the
    // pure time-blended NAM0 Ambient band; the night look is authored INTO the bands (FNV's bright
    // moonlit Night ambient is vanilla-faithful), so no scale is needed for nights either.
    // uAmbientColor.w carries GameProfile.AmbientLightScale (engine value 1.0; kept as a per-game knob);
    // falls back to 1.0 when the slot is unset (legacy/headless paths).
    float kAmbientScale = uAmbientColor.w > 0.0001 ? uAmbientColor.w : 1.0;
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
    nointerpolation float4 vEffectTint    : TEXCOORD11; // rgb = BGEM tint, w = falloff enabled
    nointerpolation float4 vEffectFalloff : TEXCOORD12; // startAngle/stopAngle/startOp/stopOp
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

    // FO4/FO76 grayscale-to-palette (vTextureState.w >= 0 = the material's GradientMapV row): the
    // palette lookup REPLACES the diffuse RGB — the raw base texture is authoring data (FO4's
    // lavender bricks). u = diffuse GREEN channel, v = row × vertexColor RED (fo76utils
    // getDiffuseColor_sRGB_G); the vertex RGB must then NOT re-modulate (red was consumed as the
    // row selector), so it is neutralized here. Alpha is untouched (still drives the cutout).
    float3 vertexRgb = input.vVertexColor.rgb;
    if (input.vTextureState.w >= 0.0)
    {
        // CLAMP sampler + explicit mip 0 (fo76utils getPixelBC_Inline(u, v, 0)): GradientMapV is
        // commonly exactly 1.0 (bottom palette row) — a wrap sampler wraps v=1.0 back to row 0,
        // which shipped palettes fill with a rainbow hue strip. And the dependent UV (u = diffuse
        // green) has garbage screen-space derivatives, so implicit-mip Sample would blend palette
        // rows together through the mip chain.
        float2 gradUv = float2(sample.g, input.vTextureState.w * input.vVertexColor.r);
        sample.rgb = textures[NonUniformResourceIndex(input.vTexIndices.w)].SampleLevel(sPalette, gradUv, 0).rgb;
        vertexRgb = 1.0;
    }

    // BGEM effect tint (fo76utils getDiffuseColor_Effect): rgb ×= baseColor × baseColorScale.
    // (1,1,1) for every non-effect material, so this is a no-op outside effect shapes. Without it
    // (and the falloff below) crossed-plane mist blobs rendered full-white on every plane and
    // additively clipped whole scenes.
    sample.rgb *= input.vEffectTint.rgb;

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

    // Alpha-tested leaf cards (vTextureState.y == 2, SPT leaves): boost the tested alpha by the
    // sampled mip level (Castaño alpha-coverage compensation). Pre-averaged DDS mips shrink the
    // leaf mask's alpha with distance, so a fixed threshold eats the canopy from a few cells out —
    // distant trees dissolved to "dead" skeletons. lod≈0 up close ⇒ no change; blend alpha
    // (outAlpha) is untouched. The 0.25 FLOOR is load-bearing: without it, high-mip texels whose
    // alpha is mostly averaged BACKGROUND (sparse sprays like dogwood: alpha ~0.1-0.2) also get
    // boosted past the threshold and the whole card renders solid in the atlas's background color
    // (white for the dogwood composite — the "mostly flowers" regression). Only texels that still
    // carry real leaf coverage (> 0.25) are rescued.
    if (input.vTextureState.y > 1.5)
    {
        float leafLod = textures[NonUniformResourceIndex(input.vTexIndices.x)]
            .CalculateLevelOfDetail(sDiffuse, input.vTexCoord);
        if (testAlpha > 0.25)
        {
            testAlpha = saturate(testAlpha * (1.0 + leafLod * 0.25));
        }
    }

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
            // BC5/ATI2 carries no alpha — the per-texel mask lives in the material's _s map,
            // sampled below when bound. No _s map ⇒ mask stays 0 (a uniform mask blows out scenes).
        }
        else
        {
            mapN = normalSample.rgb * 2.0 - 1.0;
            specMask = normalSample.a; // DXT5 _n.dds alpha = per-texel specular intensity mask
        }

        // FO4/FO76 specular map (_s.dds): R = per-texel specular mask, replacing the normal-map
        // alpha that BC5 lacks. Bound at TexIndices.z, flagged by vTextureState.z.
        if (input.vTextureState.z > 0.5)
        {
            specMask = textures[NonUniformResourceIndex(input.vTexIndices.z)].Sample(sDiffuse, input.vTexCoord).r;
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

    // BGEM view-angle falloff (fo76utils getDiffuseColor_Effect): opacity ramps
    // startOpacity→stopOpacity as |N·V| crosses startAngle→stopAngle (cosines; a negative span
    // works through the same lerp-t formula). This is what fades an effect plane out at grazing
    // view angles — the term that keeps a crossed-plane mist blob's side planes invisible.
    if (input.vEffectTint.w > 0.5)
    {
        float3 viewDir = normalize(uCameraPosFogPower.xyz - input.vWorldPos);
        float nv = abs(dot(normal, viewDir));
        float span = input.vEffectFalloff.y - input.vEffectFalloff.x;
        float t = (span * span < 1e-8) ? 0.5 : saturate((nv - input.vEffectFalloff.x) / span);
        sampleAlpha = saturate(sampleAlpha * lerp(input.vEffectFalloff.z, input.vEffectFalloff.w, t));
    }

    // Shared atmosphere lighting (rgb). Lighting-off path inside AtmosphereLight reproduces the
    // legacy `0.4 + 0.6*lambert` scalar exactly, so the OFF state is pixel-identical to before.
    // SpeedTree leaf cards need NO special lighting branch here: the engine's STLEAF chain is
    // o1 = dimmer × (Ambient + Diff·saturate(N·L)·SunDimmer) — the per-corner puffed normal comes
    // from the leaf-billboard VS, and the per-leaf canopy-depth dimmer (CIdvBranch::MakeLeaf's
    // LeafVertexColorHelp product) is baked into the vertex color, which multiplies into `lit`
    // below exactly like the engine's packed-attribute frc. (An earlier wrap-lighting stand-in
    // lived here while the dimmer was missing.)
    float3 shade = AtmosphereLight(normal);

    if (input.vRenderState.w > 0.5)
    {
        shade = 1.0; // emissive / full-bright shapes (e.g. glow) — unaffected by scene lighting
    }

    // Vertex color modulates the diffuse (vertexRgb is pre-neutralized for gradient shapes) —
    // NIFs use it for art-direction tints (e.g. dusty rocks, painted billboards).
    float3 lit = sample.rgb * vertexRgb * shade;

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
