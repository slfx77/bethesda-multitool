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
// space2 ALIASES the same bindless heap slots as space1 (both ranges start at the table head in
// GpuRootSignature12) — a slot holding a TextureCube SRV (FO4 environment maps) is indexed here
// with the SAME bindless index the 2D array uses elsewhere. Only index a slot through this
// declaration when the C# side has confirmed the promoted descriptor IS a cube (vEnvMap.x >= 0).
TextureCube  cubemaps[]   : register(t0, space2);
// space3 is a third alias over the same descriptor heap for R32_FLOAT multisampled scene depth.
// Per-draw slot encoding selects this declaration only when the descriptor is Texture2DMS.
Texture2DMS<float> depthTexturesMsaa[] : register(t0, space3);
SamplerState sDiffuse     : register(s0); // wrap, anisotropic (set in C#)
SamplerState sNormalMap   : register(s1); // wrap, anisotropic (set in C#)
SamplerState sPalette     : register(s2); // CLAMP, linear — palette + cubemap lookups
SamplerState sShadowPoint : register(s3); // CLAMP, point — sun-shadow-map depth taps (PCF)
SamplerState sClampUWrapV : register(s4); // BGSM/BGEM TileU off, TileV on
SamplerState sWrapUClampV : register(s5); // BGSM/BGEM TileU on, TileV off
SamplerState sClampUV     : register(s6); // BGSM/BGEM TileU/TileV both off

cbuffer PerFrame : register(b0) { float4x4 uViewProj; }

// Shared scene atmosphere (b3). CPU mirror: WorldView3DControl.AtmosphereConstants,
// uploaded once per frame and bound for the whole scene (terrain/reference/water all read it).
cbuffer Atmosphere : register(b3)
{
    float4 uSunDirIntensity;    // xyz = sun world dir (toward sun), w = intensity
    float4 uSunColorLighting;   // rgb = sun color, w = lightingEnabled (0/1)
    float4 uAmbientColor;       // rgb = ambient, w = spare
    float4 uSkyTopSkyEnabled;   // rgb = sky-top color, w = skyEnabled (0/1)
    float4 uSkyHorizon;         // rgb = sky-horizon color, w = effective HDR active (0/1)
    float4 uFogColorFogEnabled; // rgb = fog color, w = fogEnabled (0/1)
    float4 uAtmosphereParams;   // x = gameHour, y = fogNear, z = fogFar, w = placed-light count
    float4 uCameraPosFogPower;  // xyz = camera world pos, w = fog power (1 = linear)
    float4 uFogFarColorMax;     // rgb = far-fog color, w = max powered fog amount
    float4 uCameraOrigin;       // xyz = camera-relative render origin (VS-consumed; layout parity),
                                // w = IMGS EmissiveMult for Lighting30; NoLighting does not consume it
    // Sun shadow CASCADES, near→far (appended — earlier shaders declare only the prefix above,
    // layout-safe). Each matrix: origin-relative world → that cascade's shadow clip (xy ±1,
    // z reversed 0..1); each params: x = enabled, y = texel UV size, z = bias, w = SRV slot.
    float4x4 uShadowMatrix0;
    float4x4 uShadowMatrix1;
    float4x4 uShadowMatrix2;
    float4x4 uShadowMatrix3;
    float4 uShadowParams0;
    float4 uShadowParams1;
    float4 uShadowParams2;
    float4 uShadowParams3;
    float4 uAmbientPositiveX;  // w = full DALC cube present
    float4 uAmbientNegativeX;
    float4 uAmbientPositiveY;
    float4 uAmbientNegativeY;
    float4 uAmbientPositiveZ;
    float4 uAmbientNegativeZ;
};

// Per-frame local-light list. The root SRV lives outside the legacy t0..t7 texture table so
// terrain and placed geometry can share the same forward loop without disturbing bindless slots.
// Position is already expressed in the same absolute/origin-relative space as PSInput.vWorldPos.
struct PointLight
{
    float4 PositionRadius;
    float4 ColorIntensity;
    float4 AuthoredMetadata;    // parsed falloff/FOV/flags; retained, not interpreted by FNV path
    float4 Reserved;
};
StructuredBuffer<PointLight> uPointLights : register(t9, space0);

// Skyrim BSLightingShader constant fog: q is the linear near→far distance, then the powered amount is
// capped by FNAM max opacity. That SAME amount blends near→far fog color and the surface→fog result.
// Legacy weather binds far=near/max=1, reducing exactly to its old single-color powered fog.
// ADDITIVE draws (dst blend ONE — glow fills, light shafts) fade toward BLACK instead: their blend
// ADDS the shader output to the framebuffer, so lerping toward the fog COLOR injects fog-colored
// light at any distance and distant glows never attenuate (the Strip's night dome washing out the
// wasteland view — backlog A8). Fading the contribution to zero is what the engine's fogged
// additive pass produces. MULTIPLICATIVE draws (dst * src; baked shadows and other modulate decals) use
// WHITE as their neutral contribution. Clamp those sources to the normalized legacy output range
// first: allowing HDR values above one turns a dimming layer into a glow.
float3 ApplyFog(float3 color, float3 worldPos, float blendOperation)
{
    bool additive = blendOperation > 0.5 && blendOperation < 1.5;
    bool multiplicative = blendOperation >= 1.5;
    if (multiplicative)
    {
        color = saturate(color);
    }

    if (uFogColorFogEnabled.w < 0.5)
    {
        return color;
    }

    float dist = length(worldPos - uCameraPosFogPower.xyz);
    float q = saturate((dist - uAtmosphereParams.y) / max(uAtmosphereParams.z - uAtmosphereParams.y, 1.0));
    float amount = min(pow(q, max(uCameraPosFogPower.w, 0.01)), saturate(uFogFarColorMax.w));
    float3 fogRgb = lerp(uFogColorFogEnabled.rgb, uFogFarColorMax.rgb, amount);
    return additive
        ? color * (1.0 - amount)
        : multiplicative
            ? lerp(color, 1.0, amount)
            : lerp(color, fogRgb, amount);
}

// One cascade attempt for ShadowFactor: returns true when worldPos lands inside this cascade's
// footprint (with a PCF-kernel border margin so the filter never straddles the map edge) —
// 'visibility' then carries the filtered shadow term. The PCF is a 3x3 tent of BILINEAR
// comparison taps: each GatherRed pulls the tap's 2x2 texel quad and the four COMPARE RESULTS are
// blended by the sub-texel fraction (filter the comparisons, never the depths). Ortho projection:
// w == 1, no perspective divide. Reversed-Z: stored depth GROWS toward the light, so "an occluder
// exists" reads as stored > pixelDepth + bias.
bool TryCascadeShadow(float4x4 shadowMatrix, float4 cascade, float3 worldPos, out float visibility)
{
    visibility = 1.0;
    if (cascade.x < 0.5)
    {
        return false;
    }

    float4 clip = mul(shadowMatrix, float4(worldPos, 1.0));
    float texel = cascade.y;
    float2 uv = float2(clip.x * 0.5 + 0.5, 0.5 - clip.y * 0.5);
    float border = 2.5 * texel;
    if (min(uv.x, uv.y) < border || max(uv.x, uv.y) > 1.0 - border || clip.z <= 0.0 || clip.z >= 1.0)
    {
        return false;
    }

    uint slot = (uint)cascade.w;
    float reference = clip.z + cascade.z;
    float2 texelPos = uv / texel - 0.5;
    float2 f = frac(texelPos);
    float2 gatherBase = (floor(texelPos) + 0.5) * texel;
    float lit = 0.0;
    [unroll]
    for (int dy = -1; dy <= 1; dy++)
    {
        [unroll]
        for (int dx = -1; dx <= 1; dx++)
        {
            float4 quad = textures[NonUniformResourceIndex(slot)]
                .GatherRed(sShadowPoint, gatherBase + float2(dx, dy) * texel);
            // Gather quad order (v grows downward): x=(0,+1) y=(+1,+1) z=(+1,0) w=(0,0).
            float4 vis = 1.0 - step(reference.xxxx, quad);
            lit += lerp(lerp(vis.w, vis.z, f.x), lerp(vis.x, vis.y, f.x), f.y);
        }
    }
    visibility = lit / 9.0;
    return true;
}

// Sun-shadow visibility for an (origin-relative) world position — 1 = fully lit, 0 = fully
// occluded. CASCADED: the smallest (sharpest) cascade containing the sample wins, so quality
// scales with closeness to the camera; the far cascade reaches the full render distance. All
// cascades disabled (params.x == 0) returns 1.0, keeping the scene pixel-identical to the
// pre-shadow renderer.
float ShadowFactor(float3 worldPos)
{
    float visibility;
    if (TryCascadeShadow(uShadowMatrix0, uShadowParams0, worldPos, visibility)) return visibility;
    if (TryCascadeShadow(uShadowMatrix1, uShadowParams1, worldPos, visibility)) return visibility;
    if (TryCascadeShadow(uShadowMatrix2, uShadowParams2, worldPos, visibility)) return visibility;
    if (TryCascadeShadow(uShadowMatrix3, uShadowParams3, worldPos, visibility)) return visibility;
    return 1.0;
}

// Per-pixel light factor (rgb) for a world-space normal. When lighting is disabled
// (uSunColorLighting.w == 0) this returns the EXACT legacy flat shade — scalar 0.4 + 0.6*lambert
// against the old fixed sun — so toggling lighting off is pixel-identical to the pre-atmosphere
// viewer. Enabled: colored ambient + sun·(N·L)·sunShadow (shadow = 1.0 whenever the shadow pass
// is off, preserving the exact pre-shadow output), energy-bounded so a fully sunlit surface lands
// near the legacy max (~1.0) instead of blowing out.
float3 PlacedLightContribution(float3 N, float3 worldPos)
{
    float3 contribution = 0.0;
    uint count = (uint)max(round(uAtmosphereParams.w), 0.0);
    [loop]
    for (uint i = 0; i < count; i++)
    {
        PointLight light = uPointLights[i];
        float radius = light.PositionRadius.w;
        float3 toLight = light.PositionRadius.xyz - worldPos;
        float distanceSquared = dot(toLight, toLight);
        float radiusSquared = radius * radius;
        if (radius <= 0.0 || distanceSquared >= radiusSquared)
        {
            continue;
        }

        float3 L = toLight * rsqrt(max(distanceSquared, 1e-8));
        float ndotl = saturate(dot(N, L));
        if (ndotl <= 0.0)
        {
            continue;
        }

        // Exact shipped FNV SLS2128 omni term: 1-dot((lightPos-worldPos)/radius, ...), i.e.
        // 1-(d/r)^2 (pc_land_shader_disassembly.txt, SLS 2128-2131). The engine draws a bounded
        // light volume; this global loop performs that same bound explicitly above.
        float attenuation = saturate(1.0 - distanceSquared / radiusSquared);
        contribution += light.ColorIntensity.rgb *
            (light.ColorIntensity.w * ndotl * attenuation);
    }
    return contribution;
}

float3 AtmosphereLight(
    float3 N, float3 worldPos, float sunShadow, float3 materialEmission, bool hdrActive)
{
    if (uSunColorLighting.w < 0.5)
    {
        float legacyLambert = saturate(dot(N, normalize(float3(0.5, 0.5, 1.0))));
        // Keep the legacy OFF diagnostic byte-identical when emission is zero while retaining the
        // engine's SDR boundary: clamp ambient+emission before adding the direct Lambert term.
        float3 legacyAmbient = 0.4.xxx + materialEmission;
        if (!hdrActive)
        {
            legacyAmbient = min(legacyAmbient, 1.0);
        }
        return max(legacyAmbient + 0.6 * legacyLambert, 0.0);
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
    float3 unitNormal = normalize(N);
    float3 ambient = uAmbientColor.rgb;
    if (uAmbientPositiveX.w > 0.5)
    {
        float3 normalSquared = unitNormal * unitNormal;
        ambient = (unitNormal.x >= 0.0 ? uAmbientPositiveX.rgb : uAmbientNegativeX.rgb) * normalSquared.x +
                  (unitNormal.y >= 0.0 ? uAmbientPositiveY.rgb : uAmbientNegativeY.rgb) * normalSquared.y +
                  (unitNormal.z >= 0.0 ? uAmbientPositiveZ.rgb : uAmbientNegativeZ.rgb) * normalSquared.z;
    }
    // Lighting30Shader::SetupGeometryConstants_Emittance folds no-glow emission into AmbientColor.
    // In SDR it clamps that componentwise sum before direct lights; HDR keeps the authored range.
    ambient = ambient * kAmbientScale + materialEmission;
    if (!hdrActive)
    {
        ambient = min(ambient, 1.0);
    }

    float ndotl = saturate(dot(N, uSunDirIntensity.xyz));
    float3 shade = ambient +
        uSunColorLighting.rgb * (ndotl * sunShadow) +
        PlacedLightContribution(N, worldPos);
    // Negative LIGH records subtract illumination but never produce physically meaningless
    // negative scene radiance.
    return max(shade, 0.0);
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
    nointerpolation float4 vEnvMap        : TEXCOORD13; // x = cube slot (<0 none), y = scale, z = smoothness, w = blend operation
    nointerpolation float4 vSoftParticle  : TEXCOORD14; // encoded depth slot/view, near, far, signed soft depth
    bool   IsFrontFace  : SV_IsFrontFace;
};

// Reversed-Z [1,0] depth to positive perspective view distance (world units), identical to
// water.frag's scene-column conversion. Eligible effects explicitly reject occluded fragments and
// feather intersections; a read-only DSV remains bound so all ordinary blends keep hardware depth.
float LinearizeSceneDepth(float ndcZ, float nearPlane, float farPlane)
{
    return (nearPlane * farPlane) /
        max(nearPlane + ndcZ * (farPlane - nearPlane), 1e-4);
}

float ResolveSceneDepthFeather(PSInput input)
{
    float encodedSlot = input.vSoftParticle.x;
    if (encodedSlot == 0.0)
    {
        return 1.0; // ordinary blend or an ortho/direct-NIF hardware-only path
    }

    bool multisampled = encodedSlot < 0.0;
    uint depthSlot = (uint)(abs(encodedSlot) - 1.0);
    float sceneNdc;
    if (multisampled)
    {
        uint depthWidth, depthHeight, sampleCount;
        depthTexturesMsaa[NonUniformResourceIndex(depthSlot)]
            .GetDimensions(depthWidth, depthHeight, sampleCount);
        sceneNdc = 0.0;
        [loop]
        for (uint sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
        {
            // Reversed-Z: the maximum sample is the nearest opaque surface. Choosing it avoids a
            // soft effect bleeding through one covered sample at an MSAA silhouette.
            sceneNdc = max(sceneNdc,
                depthTexturesMsaa[NonUniformResourceIndex(depthSlot)]
                    .Load((int2)input.Position.xy, sampleIndex));
        }
    }
    else
    {
        sceneNdc = textures[NonUniformResourceIndex(depthSlot)]
            .Load(int3((int2)input.Position.xy, 0)).r;
    }
    float sceneDistance = LinearizeSceneDepth(
        sceneNdc, input.vSoftParticle.y, input.vSoftParticle.z);
    float fragmentDistance = LinearizeSceneDepth(
        input.Position.z, input.vSoftParticle.y, input.vSoftParticle.z);
    float sceneGap = sceneDistance - fragmentDistance;

    // Reproduce the scene PSO's reversed-Z GreaterEqual test. A tiny world-space keep bias makes
    // an equal/coplanar tie deterministic without allowing genuinely hidden effects through.
    clip(sceneGap + 0.01);

    float falloffDepth = abs(input.vSoftParticle.w);
    return falloffDepth > 0.0
        ? saturate(max(sceneGap, 0.0) / falloffDepth)
        : 1.0;
}

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

// vTextureState.z is an exact integer bitfield carried through a float constant:
// bit 0 = specular map present, bit 1 = clamp U, bit 2 = clamp V,
// bit 3 = TexIndices.w is a classic Lighting30 glow map, bit 4 = Lighting30 route.
uint MaterialTextureFlags(float packedState)
{
    return (uint)round(packedState);
}

bool HasMaterialSpecularMap(float packedState)
{
    return (MaterialTextureFlags(packedState) & 1u) != 0u;
}

bool HasLighting30GlowMap(float packedState)
{
    return (MaterialTextureFlags(packedState) & 8u) != 0u;
}

bool IsLighting30Material(float packedState)
{
    return (MaterialTextureFlags(packedState) & 16u) != 0u;
}

float4 SampleMaterialTexture(uint slot, float2 uv, float packedState)
{
    uint addressing = (MaterialTextureFlags(packedState) >> 1u) & 3u;
    if (addressing == 1u)
        return textures[NonUniformResourceIndex(slot)].Sample(sClampUWrapV, uv);
    if (addressing == 2u)
        return textures[NonUniformResourceIndex(slot)].Sample(sWrapUClampV, uv);
    if (addressing == 3u)
        return textures[NonUniformResourceIndex(slot)].Sample(sClampUV, uv);
    return textures[NonUniformResourceIndex(slot)].Sample(sDiffuse, uv);
}

float4 SampleMaterialNormal(uint slot, float2 uv, float packedState)
{
    uint addressing = (MaterialTextureFlags(packedState) >> 1u) & 3u;
    if (addressing == 1u)
        return textures[NonUniformResourceIndex(slot)].Sample(sClampUWrapV, uv);
    if (addressing == 2u)
        return textures[NonUniformResourceIndex(slot)].Sample(sWrapUClampV, uv);
    if (addressing == 3u)
        return textures[NonUniformResourceIndex(slot)].Sample(sClampUV, uv);
    return textures[NonUniformResourceIndex(slot)].Sample(sNormalMap, uv);
}

float CalculateMaterialTextureLod(uint slot, float2 uv, float packedState)
{
    uint addressing = (MaterialTextureFlags(packedState) >> 1u) & 3u;
    if (addressing == 1u)
        return textures[NonUniformResourceIndex(slot)].CalculateLevelOfDetail(sClampUWrapV, uv);
    if (addressing == 2u)
        return textures[NonUniformResourceIndex(slot)].CalculateLevelOfDetail(sWrapUClampV, uv);
    if (addressing == 3u)
        return textures[NonUniformResourceIndex(slot)].CalculateLevelOfDetail(sClampUV, uv);
    return textures[NonUniformResourceIndex(slot)].CalculateLevelOfDetail(sDiffuse, uv);
}

float4 main(PSInput input) : SV_Target
{
    // Do this before texture/lighting work. Only explicitly eligible effects carry an encoded depth
    // slot; ordinary transparent draws rely exclusively on the simultaneously bound read-only DSV.
    float sceneDepthFeather = ResolveSceneDepthFeather(input);

    float4 sample = SampleMaterialTexture(
        input.vTexIndices.x, input.vTexCoord, input.vTextureState.z);

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
        float leafLod = CalculateMaterialTextureLod(
            input.vTexIndices.x, input.vTexCoord, input.vTextureState.z);
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
    // FO4 _s GREEN channel: per-texel smoothness multiplier (fo76utils drawPixel_FO4:
    // smoothness = material smoothness × _s.G). 1 when no _s map is bound.
    float specSmoothScale = 1.0;
    if (input.vRenderState.y > 0.5)
    {
        float4 normalSample = SampleMaterialNormal(
            input.vTexIndices.y, input.vTexCoord, input.vTextureState.z);
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
        // alpha that BC5 lacks; G = per-texel smoothness (feeds the env-map term's gloss/mip).
        // Bound at TexIndices.z, flagged by vTextureState.z.
        if (HasMaterialSpecularMap(input.vTextureState.z))
        {
            float2 specSample = SampleMaterialTexture(
                input.vTexIndices.z, input.vTexCoord, input.vTextureState.z).rg;
            specMask = specSample.r;
            specSmoothScale = specSample.g;
        }

        // Bethesda's DDS normals are already in the DirectX convention consumed by the authored
        // tangent/bitangent basis. The recovered FNV SLS1009 pixel shader unpacks RGB directly and
        // does not negate green. Green is flipped only when exporting these maps to glTF/OpenGL.
        mapN.xy *= input.vRenderState.z;

        // Only perturb when there's a usable tangent basis. SpeedTree bark carries a normal map but NO
        // tangents (the tube generator emits none), so vTangent is zero — normalize(0) = NaN, which
        // produced garbage per-pixel normals and a "deformed"/ribboned trunk once culling stopped hiding
        // the bark. With a degenerate tangent, fall back to the geometric normal (flat bark, no bump).
        float tLenSq = dot(input.vTangent, input.vTangent);
        float bLenSq = dot(input.vBitangent, input.vBitangent);
        if (tLenSq > 1e-6 && bLenSq > 1e-6)
        {
            float3 T = input.vTangent * rsqrt(tLenSq);
            float3 B = input.vBitangent * rsqrt(bLenSq);
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
    bool fullBright = input.vRenderState.w > 0.5;
    // NoLighting bypasses both scene lighting and its shadow lookup. This is also what keeps that
    // branch entirely independent of Lighting30's IMGS EmissiveMult input.
    float sunShadow = !fullBright && uSunColorLighting.w >= 0.5
        ? ShadowFactor(input.vWorldPos)
        : 1.0;
    float3 shade;
    if (fullBright)
    {
        // Emissive / full-bright shapes — unaffected by scene lighting. Their color is the
        // MATERIAL EMISSIVE glow tint (× mult) modulating the texture, the FO3/FNV
        // SHADER_NOLIGHTING output: BarrelPile03's goo sheets are a white radial gradient
        // (shared\shadefade01.dds) × green (0.05, 1, 0) × 2 — without the tint they clip to
        // pure white. The decoder rides the tint in vSpecular.rgb (specular is force-disabled
        // on emissive shapes, so the slot is free); shapes without a material carry (1,1,1).
        // Recovered SLS1003/SLS1004 NoLighting shaders do not bind IMGS EmissiveMult. The exact
        // FalloutNV MemDebug xref is in Lighting30Shader::SetupGeometryConstants_Emittance, where
        // HDR hdrData[3] scales NiMaterial emittance/glow-map constants instead. Applying it here
        // brightened every fullbright dust/effect surface merely because the exterior IMGS is 1.2.
        shade = input.vSpecular.rgb;
    }
    else
    {
        bool lighting30 = IsLighting30Material(input.vTextureState.z);
        bool hasLighting30Glow = lighting30 && HasLighting30GlowMap(input.vTextureState.z);
        bool hdrActive = uSkyHorizon.w >= 0.5;
        float3 emission = 0.0;
        if (lighting30)
        {
            // vEffectFalloff is a layout-preserving union for this mutually-exclusive material
            // family: raw selected RGB (NiMaterial or XEMI) + NiMaterial emission multiplier.
            emission = input.vEffectFalloff.rgb;
            if (hdrActive)
            {
                emission *= input.vEffectFalloff.w * uCameraOrigin.w;
            }
            else if (hasLighting30Glow)
            {
                // SDR glow setup clamps EmittanceColor itself. The no-glow path instead clamps the
                // ambient+raw-emission sum inside AtmosphereLight.
                emission = min(emission, 1.0);
            }
        }

        shade = AtmosphereLight(
            normal, input.vWorldPos, sunShadow,
            hasLighting30Glow ? 0.0 : emission,
            hdrActive);
        if (hasLighting30Glow)
        {
            float3 glow = SampleMaterialTexture(
                input.vTexIndices.w, input.vTexCoord, input.vTextureState.z).rgb;
            shade += glow * emission;
        }
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
    // (vSpecular.w > 0 via NifSpecularPolicy), a non-zero mask, and a non-emissive shape. The
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
        // Specular is pure sun light, so the sun shadow gates it too (×1.0 when shadows are off).
        lit += uSunColorLighting.rgb * (spec * uSunDirIntensity.w * sunShadow);
        // Firefly bound: pre-HDR the 8-bit target clamped every MSAA SAMPLE to 1 before the resolve
        // averaged it; the float target lost that, so sub-pixel spec glints on distant silhouettes
        // rode through at full magnitude (the "hot pixels at a distance" regression). Restore the
        // per-sample ceiling exactly where the aliasing lives — this block is gated non-emissive
        // (vRenderState.w <= 0.5), so authored HDR glow (neon/goo) is untouched.
        lit = min(lit, 1.0);
    }

    // FO4 cubemap environment reflections — the dominant "shiny" term for FO4 metal/gloss
    // (BGSM slot 4 + fEnvironmentMappingMaskScale). Grounded in fo76utils drawPixel_FO4 /
    // calculateLighting_FO4: e = cube(reflect(V,N)) at mip (1−smoothness)·maxMip, with
    // smoothness = material smoothness × _s.G; the term adds e · envMapScale · specLevel · g,
    // where specLevel = _s.R (the same per-texel mask specular uses) and
    // g = N·V / (N·V + kEnv − N·V·kEnv), kEnv = (1−smoothness)²/2. Gates: scene lighting on,
    // normal map + _s map bound (fo76utils draws NO env term without textureS), non-emissive,
    // and a PROMOTED TextureCube descriptor (vEnvMap.x stays −1 until the cube is GPU-resident,
    // so this never indexes a 2D descriptor through the cube alias). The world-space reflection
    // vector samples the cube directly — the engine's own shaders rely on D3D cube hardware with
    // no axis swizzle, so the shipped faces are authored against exactly this convention.
    // fo76utils multiplies by a constant envColor = 1.0 (its renders are always-day); this viewer
    // modulates by the scene shade factor instead — a labeled stand-in pending an FO4
    // BSLightingShader decompile — so night metal doesn't reflect a daylight cube at full strength.
    if (uSunColorLighting.w >= 0.5 && input.vRenderState.y > 0.5 && HasMaterialSpecularMap(input.vTextureState.z) &&
        input.vRenderState.w <= 0.5 && input.vEnvMap.x >= 0.0 && input.vEnvMap.y > 0.0 &&
        specMask > 0.0)
    {
        float3 V = normalize(uCameraPosFogPower.xyz - input.vWorldPos);
        float smoothness = saturate(input.vEnvMap.z * specSmoothScale);
        uint cubeSlot = (uint)input.vEnvMap.x;
        float cubeW, cubeH;
        uint cubeMips;
        cubemaps[NonUniformResourceIndex(cubeSlot)].GetDimensions(0, cubeW, cubeH, cubeMips);
        float3 reflectDir = reflect(-V, normal);
        float3 env = cubemaps[NonUniformResourceIndex(cubeSlot)]
            .SampleLevel(sPalette, reflectDir, (1.0 - smoothness) * max((float)cubeMips - 1.0, 0.0)).rgb;
        float nDotV = abs(dot(normal, V));
        float rEnv = 1.0 - min(smoothness, 0.999);
        float kEnv = rEnv * rEnv * 0.5;
        float gEnv = nDotV / (nDotV + kEnv - nDotV * kEnv);
        // Firefly bound (see the sun-specular block): EnvMapScale reaches 8, so the addend is
        // genuinely unbounded — cap it at the old per-sample LDR ceiling. Non-emissive-gated.
        lit += min(env * saturate(shade) * (input.vEnvMap.y * specMask * gEnv), 1.0);
    }

    float outAlpha = input.vAlphaState.w > 0.5
        ? saturate(sampleAlpha * input.vAlphaState.z)
        : 1.0;
    float3 outputRgb = ApplyFog(lit, input.vWorldPos, input.vEnvMap.w);
    if (abs(input.vSoftParticle.w) > 0.0)
    {
        if (input.vEnvMap.w >= 1.5)
        {
            // Multiplicative dst*src effects disappear toward WHITE, their blend-neutral source.
            outputRgb = lerp(1.0, outputRgb, sceneDepthFeather);
            outAlpha *= sceneDepthFeather;
        }
        else if (input.vSoftParticle.w > 0.0)
        {
            // SRC_ALPHA / SRC_ALPHA_SATURATE equations derive RGB contribution from output alpha.
            outAlpha *= sceneDepthFeather;
        }
        else
        {
            // ONE-source additive (and other color-factor) effects need RGB itself to approach zero.
            outputRgb *= sceneDepthFeather;
            outAlpha *= sceneDepthFeather;
        }
    }
    return float4(outputRgb, outAlpha);
}
