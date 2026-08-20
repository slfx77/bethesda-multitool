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

// Per-sample luminance ceiling for authored emissive/glow output. High enough that every ordinary
// glow product (goo ×2 tint × EmissiveMult 1.2 ≈ 2.4, neon 2-4) passes untouched with bloom-worthy
// headroom, low enough to bound extreme Lighting30 emittance spikes on sub-pixel triangles — the
// distant-mesh "isolated white dot" fireflies. Tuned per the 2026-07-14 hot-pixel audit (~4-8 band).
static const float kEmissiveLumaCap = 6.0;

#include "atmosphere.hlsli"
#include "scene_lighting.hlsli"
#include "fog.hlsli"

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

    float amount = FogAmountAtDistance(length(worldPos - uCameraPosFogPower.xyz));
    float3 fogRgb = FogColorForAmount(amount);
    return additive
        ? color * (1.0 - amount)
        : multiplicative
            ? lerp(color, 1.0, amount)
            : lerp(color, fogRgb, amount);
}

#include "shadow_sampling.hlsli"

// Per-pixel light factor (rgb) for a world-space normal. When lighting is disabled
// (uSunColorLighting.w == 0) this returns the EXACT legacy flat shade — scalar 0.4 + 0.6*lambert
// against the old fixed sun — so toggling lighting off is pixel-identical to the pre-atmosphere
// viewer. Enabled: colored ambient + sun·(N·L)·sunShadow (shadow = 1.0 whenever the shadow pass
// is off, preserving the exact pre-shadow output), energy-bounded so a fully sunlit surface lands
// near the legacy max (~1.0) instead of blowing out.
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
    nointerpolation float vSpecularLodFade : TEXCOORD15; // classic FNV direct-sun specular only
    float3 vFnvActiveAdtBaseLight : TEXCOORD16; // SLS2000 normalized tangent-space Sun vector
    nointerpolation float4 vHeatmap : TEXCOORD17; // FormID heatmap: rgb = ramp tint, w = active
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
// bit 3 = TexIndices.w is a classic Lighting30 glow map, bit 4 = Lighting30 route,
// bit 5 = TallGrassShaderProperty (consumed by the reference vertex shaders),
// bit 6 = classic FO3/FNV environment pass, bit 7 = TexIndices.z is its custom mask,
// bit 9 = classic SLS2058/bit-21 window-reflection direction, bit 10 = FNV active
// ID193/BSSM_ADT zero-local-light route, bit 11 = its Toggles.x vertex-RGB branch,
// bit 12 = FNV runtime TallGrass no-sun-shadow (retail's GRASS pixel-shader family never
// samples a sun/world shadow map; set only on the FNV branch of ResolveTextureState),
// bit 13 = FO3/FNV runtime SpeedTree LEAF-CARD no-sun-shadow (retail STLEAF*.vso declare no
// shadow input at all and STLEAF2000/2001.pso bind only DiffuseMap),
// bit 14 = the classic env texture is a TES3/TES4-era NiTextureEffect 2D SPHERE map sampled
// from the view-space reflection vector via textures[], never the cubemaps[] alias
// (bit 8 is classic parallax).
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

bool HasClassicEnvironmentMap(float packedState)
{
    return (MaterialTextureFlags(packedState) & 64u) != 0u;
}

bool HasClassicEnvironmentMask(float packedState)
{
    return (MaterialTextureFlags(packedState) & 128u) != 0u;
}

bool HasClassicParallax(float packedState)
{
    return (MaterialTextureFlags(packedState) & 256u) != 0u;
}

bool UsesClassicEnvironmentWindowReflection(float packedState)
{
    return (MaterialTextureFlags(packedState) & 512u) != 0u;
}

bool UsesFnvActiveAdtBaseLighting(float packedState)
{
    return (MaterialTextureFlags(packedState) & 1024u) != 0u;
}

bool UsesFnvActiveAdtBaseVertexColor(float packedState)
{
    return (MaterialTextureFlags(packedState) & 2048u) != 0u;
}

bool HasFnvGrassNoSunShadow(float packedState)
{
    return (MaterialTextureFlags(packedState) & 4096u) != 0u;
}

bool HasSpeedTreeLeafNoSunShadow(float packedState)
{
    return (MaterialTextureFlags(packedState) & 8192u) != 0u;
}

bool HasClassicSphereMapEnvironment(float packedState)
{
    return (MaterialTextureFlags(packedState) & 16384u) != 0u;
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

// PC-final SM3004 simple parallax. Height is sampled ONCE at the original UV, then the exact
// hardcoded `(height * 0.04 - 0.02)` offset is projected through the normalized tangent-space
// eye vector. The serialized BSShaderPPLightingProperty POM scale is not part of this permutation.
float2 ResolveClassicParallaxUv(PSInput input)
{
    if (!HasClassicParallax(input.vTextureState.z))
    {
        return input.vTexCoord;
    }

    float tangentLengthSquared = dot(input.vTangent, input.vTangent);
    float bitangentLengthSquared = dot(input.vBitangent, input.vBitangent);
    float normalLengthSquared = dot(input.vWorldNormal, input.vWorldNormal);
    float3 basisLengthSquared = float3(
        tangentLengthSquared, bitangentLengthSquared, normalLengthSquared);
    if (!all(isfinite(basisLengthSquared)) ||
        min(tangentLengthSquared, min(bitangentLengthSquared, normalLengthSquared)) <= 1e-8)
    {
        return input.vTexCoord;
    }

    float3 tangent = input.vTangent * rsqrt(tangentLengthSquared);
    float3 bitangent = input.vBitangent * rsqrt(bitangentLengthSquared);
    float3 geometricNormal = input.vWorldNormal * rsqrt(normalLengthSquared);
    float3 eyeMinusWorldPosition = uCameraPosFogPower.xyz - input.vWorldPos;
    if (!all(isfinite(eyeMinusWorldPosition)))
    {
        return input.vTexCoord;
    }

    float3 viewTs = float3(
        dot(tangent, eyeMinusWorldPosition),
        dot(bitangent, eyeMinusWorldPosition),
        dot(geometricNormal, eyeMinusWorldPosition));
    float viewTsLengthSquared = dot(viewTs, viewTs);
    if (!isfinite(viewTsLengthSquared) || viewTsLengthSquared <= 1e-8)
    {
        return input.vTexCoord;
    }

    viewTs *= rsqrt(viewTsLengthSquared);
    float height = SampleMaterialTexture(
        input.vTexIndices.z, input.vTexCoord, input.vTextureState.z).r;
    return input.vTexCoord + viewTs.xy * (height * 0.04 - 0.02);
}

float4 main(PSInput input) : SV_Target
{
    // Mirror-pass clip plane (b3 uClipPlane, origin-relative space; neutral (0,0,0,1) = no-op).
    clip(dot(input.vWorldPos.xyz, uClipPlane.xyz) + uClipPlane.w);

    // Do this before texture/lighting work. Only explicitly eligible effects carry an encoded depth
    // slot; ordinary transparent draws rely exclusively on the simultaneously bound read-only DSV.
    float sceneDepthFeather = ResolveSceneDepthFeather(input);

    float2 materialUv = ResolveClassicParallaxUv(input);

    float4 sample = SampleMaterialTexture(
        input.vTexIndices.x, materialUv, input.vTextureState.z);

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

    bool fnvActiveAdtBase = UsesFnvActiveAdtBaseLighting(input.vTextureState.z);
    bool fnvActiveAdtBaseVertexColor = UsesFnvActiveAdtBaseVertexColor(input.vTextureState.z);
    // The bounded active ADT route is opaque and Toggles.x consumes vertex RGB only. Its output
    // alpha is BaseMap.a * material alpha (the runtime gate requires material alpha == 1).
    float sampleAlpha = saturate(sample.a * (fnvActiveAdtBase ? 1.0 : input.vVertexColor.a));

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
            input.vTexIndices.x, materialUv, input.vTextureState.z);
        if (testAlpha > 0.25)
        {
            testAlpha = saturate(testAlpha * (1.0 + leafLod * 0.25));
        }
    }

#if ALPHA_TO_COVERAGE
    // Grass-cutout A2C variant (only ever paired with an AlphaToCoverageEnable + MSAA PSO):
    // GREATER/GEQUAL cutouts trade the binary discard for a screen-space-sharpened coverage
    // gradient written to SV_Target.a — the hardware dithers it across the MSAA samples, so the
    // blade silhouette antialiases. Every other comparison function keeps the plain discard so a
    // mixed batch can never mis-render. The branch condition is per-draw (nointerpolation), so
    // the fwidth below is quad-uniform-safe (no divergent helper lanes).
    // KNOWN LIMITATION (GrassWasteland02 "floating shard", characterized 2026-07-23): GW02 is a
    // dense-bush crossed-quad card whose blade-TOP triangles map to OPAQUE texels of the shared
    // atlas (GrassWastelandComp01.dds bush cell) and render solid at the FINEST mip (an in-shader
    // viz proved coverage=1 at LOD~0, high raw alpha — NOT a coarse-mip average). The solid tops
    // face the camera only when looking DOWN at the grass; eye-level view cuts out correctly. It is
    // NOT a coverage-formula bug (proportional + LOD-gated fades both tried and rejected — they wash
    // near grass and the tops report low LOD). Reproduces identically on PC-Final assets. The
    // dark tint of the tops (unexplained: lighting on up-facing normals vs a dark texel) is the open
    // question; do not re-tune coverage here without that + a retail-engine reference.
    float a2cCoverage = 1.0;
    bool a2cActive = false;
    int a2cFn = (int)round(input.vAlphaState.y);
    if (input.vAlphaState.y >= 0.0 && (a2cFn == 4 || a2cFn == 6))
    {
        // Same Castaño mip alpha-coverage compensation (and load-bearing 0.25 floor) as the SPT
        // leaf path above — grass mips decay alpha with distance identically. Grass never enters
        // the leaf branch (vTextureState.y == 0), so this cannot double-apply.
        float a2cLod = CalculateMaterialTextureLod(
            input.vTexIndices.x, materialUv, input.vTextureState.z);
        float a2cAlpha = testAlpha;
        if (a2cAlpha > 0.25)
        {
            a2cAlpha = saturate(a2cAlpha * (1.0 + a2cLod * 0.25));
        }
        // Sharpen the authored threshold into a ~1px gradient (fwidth BEFORE any discard so the
        // derivative's helper lanes are still live).
        a2cCoverage = saturate(
            (a2cAlpha - input.vAlphaState.x) / max(fwidth(a2cAlpha), 1e-4) + 0.5);
        a2cActive = true;
        if (a2cCoverage <= 0.0) discard;
    }
    else if (!PassAlphaTest(testAlpha, input.vAlphaState.x, input.vAlphaState.y))
    {
        discard;
    }
#else
    if (!PassAlphaTest(testAlpha, input.vAlphaState.x, input.vAlphaState.y)) discard;
#endif

    float3 normal = normalize(input.vWorldNormal);
    if (input.vRenderState.x > 0.5 && !input.IsFrontFace)
    {
        normal = -normal;
    }

    // FNV: the normal-map ALPHA channel is the per-texel specular mask (decompile-confirmed in
    // SLS2047.pso — the engine's specular SLS variant). Captured here from the same sample the bump
    // uses; 0 ⇒ no specular (the default when there's no normal map, or for alpha-less BC5).
    float specMask = 0.0;
    // Classic FO3/FNV SLS environment mapping uses this alpha when no custom slot-5 mask exists.
    // The engine's default flat normal is opaque, so shapes without a sampled normal retain 1.
    float normalMapAlpha = 1.0;
    // FO4 _s GREEN channel: per-texel smoothness multiplier (fo76utils drawPixel_FO4:
    // smoothness = material smoothness × _s.G). 1 when no _s map is bound.
    float specSmoothScale = 1.0;
    float fnvActiveAdtBaseNdotL = 0.0;
    if (input.vRenderState.y > 0.5)
    {
        float4 normalSample = SampleMaterialNormal(
            input.vTexIndices.y, materialUv, input.vTextureState.z);
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
            normalMapAlpha = normalSample.a;
            specMask = normalMapAlpha; // DXT5 _n.dds alpha = per-texel specular intensity mask
        }

        // FO4/FO76 specular map (_s.dds): R = per-texel specular mask, replacing the normal-map
        // alpha that BC5 lacks; G = per-texel smoothness (feeds the env-map term's gloss/mip).
        // Bound at TexIndices.z, flagged by vTextureState.z.
        if (HasMaterialSpecularMap(input.vTextureState.z))
        {
            float2 specSample = SampleMaterialTexture(
                input.vTexIndices.z, materialUv, input.vTextureState.z).rg;
            specMask = specSample.r;
            specSmoothScale = specSample.g;
        }

        if (fnvActiveAdtBase)
        {
            // PC-final package013 SLS2000.pso: normalize the unscaled RGB normal sample, consume
            // the interpolated per-vertex normalized light directly without re-normalizing it, and preserve
            // the raw signed dp3. The clamp occurs after Ambient + PSLightColor*dp3 below.
            float3 activeAdtNormal = normalize(normalSample.rgb * 2.0 - 1.0);
            fnvActiveAdtBaseNdotL = dot(activeAdtNormal, input.vFnvActiveAdtBaseLight);
        }

        // Bethesda's DDS normals are already in the DirectX convention consumed by the authored
        // tangent/bitangent basis. Green is flipped only when exporting these maps to glTF/OpenGL.
        // The active SLS2000 branch above has no bump-strength constant; this scale belongs only to
        // the viewer's remaining combined-material route.
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
    // branch entirely independent of Lighting30's IMGS EmissiveMult input. FNV TallGrass draws
    // (runtime bit 12) skip the sun-cascade lookup here: retail shadows only grass's SUN term
    // (GRASS2002.pso), which this shader's single shade term cannot express — the per-game grass
    // shader implements the real split, and this path is its fallback.
    // FO3/FNV SpeedTree LEAF CARDS (runtime bit 13) skip it because retail leaf cards receive no
    // shadow term at all (STLEAF*.vso declare no ShadowProj; STLEAF2000/2001.pso bind only
    // DiffuseMap). They still CAST — this is the receive side only. Suppressing it also removes the
    // per-card bisection caused by the shadow pass re-facing cards to a light-perpendicular basis
    // while this pass faces them at the camera (see RuntimeSpeedTreeLeafNoSunShadowFlag).
    float sunShadow = !fullBright && !fnvActiveAdtBase
            && !HasFnvGrassNoSunShadow(input.vTextureState.z)
            && !HasSpeedTreeLeafNoSunShadow(input.vTextureState.z) && uSunColorLighting.w >= 0.5
        ? ShadowFactor(input.vWorldPos)
        : 1.0;
    // Tracks whether this (non-full-bright) shape carries authored Lighting30 emittance — those
    // keep HDR headroom for bloom and are exempt from the generalized per-sample ceiling below.
    bool authoredGlow = false;
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
            authoredGlow = dot(emission, 1.0) > 0.0;
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

        if (fnvActiveAdtBase)
        {
            // Active ID193/BSSM_ADT, package013 SLS2000.pso instruction tail:
            //   dp3 rawSigned; mad Ambient + PSLightColor*dp3; max aggregate with zero.
            // It has no bump-strength constant, shadow sampler, placed-light loop, directional
            // ambient cube, emission term, or uAmbientColor.w scale.
            shade = max(
                uAmbientColor.rgb + uSunColorLighting.rgb * fnvActiveAdtBaseNdotL,
                0.0);
        }
        else
        {
            shade = AtmosphereLight(
                normal, input.vWorldPos, sunShadow,
                hasLighting30Glow ? 0.0 : emission,
                hdrActive);
        }
        if (hasLighting30Glow)
        {
            float3 glow = SampleMaterialTexture(
                input.vTexIndices.w, input.vTexCoord, input.vTextureState.z).rgb;
            shade += glow * emission;
        }
    }

    // Vertex color modulates the diffuse (vertexRgb is pre-neutralized for gradient shapes) —
    // NIFs use it for art-direction tints (e.g. dusty rocks, painted billboards).
    float3 baseLit = sample.rgb * shade;
    // SLS2000 Toggles.x selects BaseMap * vertexRgb * shade; otherwise it omits vertexRgb.
    // ApplyFog below remains the viewer's single common fog stage.
    float3 lit = fnvActiveAdtBaseVertexColor
        ? baseLit * vertexRgb
        : fnvActiveAdtBase
            ? baseLit
            : baseLit * vertexRgb;

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
        float spec = specMask * specTerm * input.vSpecularLodFade;
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

    // FO3/FNV classic PP-lighting environment pass — recovered retail SLS2057 equation:
    //   mask = custom slot-5 RED when present, otherwise normal-map ALPHA
    //   rgb += cube(reflect(-V,N)) * mask * EnvMapScale * AmbientColor.w
    // with optional vertex color modulation. AmbientColor.w is this material's alpha payload.
    // This deliberately has no FO4 _s.G smoothness, Fresnel/geometry term, or explicit cube mip.
    if (uSunColorLighting.w >= 0.5 && HasClassicEnvironmentMap(input.vTextureState.z) &&
        input.vRenderState.w <= 0.5 && input.vEnvMap.x >= 0.0 && input.vEnvMap.y > 0.0)
    {
        float classicEnvMask = normalMapAlpha;
        if (HasClassicEnvironmentMask(input.vTextureState.z))
        {
            classicEnvMask = SampleMaterialTexture(
                input.vTexIndices.z, input.vTexCoord, input.vTextureState.z).r;
        }

        if (classicEnvMask > 0.0)
        {
            float3 V = normalize(uCameraPosFogPower.xyz - input.vWorldPos);
            // SLS2057 ENVMAP uses the ordinary incident vector. BSShaderFlags bit 21 selects
            // SLS2058 ENVMAP_W, whose first normalize negates that vector before reflection.
            float3 reflectDir = UsesClassicEnvironmentWindowReflection(input.vTextureState.z)
                ? reflect(V, normal)
                : reflect(-V, normal);
            uint envSlot = (uint)input.vEnvMap.x;
            float3 env;
            if (HasClassicSphereMapEnvironment(input.vTextureState.z))
            {
                // TES3/TES4-era NiTextureEffect ENVIRONMENT_MAP + CG_SPHERE_MAP: a 2D sphere map
                // authored for the fixed-function EYE-SPACE reflection lookup (chrome-ball photo:
                // sky at the top of the image). The PS carries no pure view matrix, so the eye
                // basis is rebuilt per pixel from the view ray and world up (Z-up world): right =
                // up × forward, upv = forward × right, +Z_eye toward the camera. This matches true
                // eye space exactly at screen center and deviates only by the pixel's off-axis
                // angle — imperceptible for glass/window glints — while staying camera-roll-free.
                // UV is the classic sphere-map projection m = 2·sqrt(Rx² + Ry² + (Rz+1)²),
                // uv = R.xy/m + 0.5, with v flipped for D3D's top-down texture convention.
                // C# guarantees this slot is a plain 2D SRV (EnvMapState requires a resident
                // NON-cube for the sphere route), so textures[] is the correct alias here.
                float3 forward = -V;
                float3 rightBasis = cross(float3(0.0, 0.0, 1.0), forward);
                rightBasis = dot(rightBasis, rightBasis) > 1e-6
                    ? normalize(rightBasis)
                    : float3(1.0, 0.0, 0.0);
                float3 upBasis = cross(forward, rightBasis);
                float3 rv = float3(
                    dot(reflectDir, rightBasis),
                    dot(reflectDir, upBasis),
                    dot(reflectDir, V));
                float m = max(
                    2.0 * sqrt(rv.x * rv.x + rv.y * rv.y + (rv.z + 1.0) * (rv.z + 1.0)), 1e-4);
                float2 sphereUv = float2(0.5 + rv.x / m, 0.5 - rv.y / m);
                env = textures[NonUniformResourceIndex(envSlot)].Sample(sPalette, sphereUv).rgb;
            }
            else
            {
                env = cubemaps[NonUniformResourceIndex(envSlot)].Sample(sPalette, reflectDir).rgb;
            }
            lit += env * vertexRgb * (classicEnvMask * input.vEnvMap.y * input.vAlphaState.z);
        }
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

    // Generalized firefly bound: the sun-specular clamp above only reaches shapes that enter that
    // block (lighting + normal map + specular flag + mask). Matte shapes still alias — sub-pixel
    // triangles at distance modulate sun·(N·L) and the unbounded point-light sum per MSAA sample,
    // which the pre-HDR 8-bit target used to clamp at 1 before the resolve averaged it. Restore
    // that ceiling for every shape without authored glow, after ALL lighting terms. Full-bright
    // NoLighting shapes and Lighting30 emittance keep their HDR headroom (that's bloom's input —
    // flattening it is the regression the HDR program exists to avoid).
    if (!fullBright && !authoredGlow)
    {
        lit = min(lit, 1.0);
    }
    else
    {
        // Emissive firefly cap: authored glow keeps HDR headroom for bloom, but a sub-pixel glow
        // triangle (welkynd stones, gate fire, glow-map windows at distance) can ride an extreme
        // Lighting30 emittance product (EmissiveMult × HDR scale × glow map) into single MSAA
        // samples as isolated white dots. Bound its LUMINANCE at a high ceiling — hue-preserving
        // scale, never a flat saturate (that would flatten neon/goo/fire and pre-empt bloom).
        // Ordinary glow (goo ~2.4, neon 2-4) passes untouched.
        float glowLuma = dot(lit, float3(0.299, 0.587, 0.114));
        if (glowLuma > kEmissiveLumaCap)
        {
            lit *= kEmissiveLumaCap / glowLuma;
        }
    }

    // SLS2000 always writes BaseMap.a * AmbientColor.a. The bounded route requires the latter
    // (material alpha) to be exactly one, but BaseMap alpha remains observable in the render target.
    float outAlpha = fnvActiveAdtBase
        ? sampleAlpha
        : input.vAlphaState.w > 0.5
            ? saturate(sampleAlpha * input.vAlphaState.z)
            : 1.0;
#if ALPHA_TO_COVERAGE
    if (a2cActive)
    {
        // SV_Target.a is the coverage mask under AlphaToCoverageEnable — the ROP dithers it
        // across the MSAA samples (this PSO never alpha-blends, so alpha carries no other duty).
        outAlpha = a2cCoverage;
    }
#endif
    float3 outputRgb = ApplyFog(lit, input.vWorldPos, input.vEnvMap.w);
    if (input.vHeatmap.w > 0.5)
    {
        // FormID-heatmap debug overlay: REPLACE the lit result with the exact palette color so the
        // hue reads as pure data (authoring order). Fog is deliberately skipped for tinted pixels —
        // hazing toward the fog color would shift the perceived hue with distance and corrupt the
        // read. Alpha-test/coverage above already ran, and outAlpha is kept, so cutout silhouettes
        // and blend fades still shape the tinted surface; the soft-particle feather below remains a
        // pure visibility term on top.
        outputRgb = input.vHeatmap.rgb;
    }
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
