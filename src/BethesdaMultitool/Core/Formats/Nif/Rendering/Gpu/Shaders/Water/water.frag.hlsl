// v3 water fragment shader — a faithful port of FNV's PC water pixel shader (WATER000.pso),
// disassembled from Data/Shaders/shaderpackage019.sdp with fxc /dumpbin. Full analysis +
// raw disassembly: tools/GhidraProject/water_pc_pixel_shader_decompiled.txt.
//
// OBLIVION_WATER (compile-time define) selects Oblivion's WATER000.pso math where it genuinely
// diverges (tools/GhidraProject/oblivion_water_pixel_shader_decompiled.txt): the body color blends
// Deep→Shallow by the VIEW ANGLE (N·V), not the depth column, and the specular is a single
// sun glint (no sky-glint term). Everything else (Schlick fresnel with F0 = WATR Fresnel Amount,
// ReflectionColor→reflection-target interpolation, distance fog) is shared.
//
// The engine shader composites: reflection RT, refraction RT, a depth-map water-column factor,
// a single NNAM normal tap, a Schlick fresnel, a dual sun/sky specular, and distance fog. Our
// The normal viewer fallback has no reflection/refraction targets, so it reproduces the engine's
// WATER003 RT-free path exactly. FNV_WATER001 is a separately compiled, conservative approximation
// that samples a host-owned opaque-scene snapshot; it never changes the fallback permutation:
//   * reflection  == ReflectionColor          (engine WATER003 no-RT permutation; unlike WATER000's
//                                                RT path, it does not apply FresnelRI.w reflectivity)
//   * refraction  == the water body color      (engine blends refraction by depth; we use the body)
//   * depthT      == real scene-depth column when the host binds the D32 depth as an SRV (matches
//                    the engine's DepthMap fade over DepthFalloffStart/End); else a view-angle proxy
// The Fresnel form (Schlick, F0 = DNAM FresnelAmount, exponent 5), the Shallow->Deep-by-depth body,
// the ReflectionColor mix scaled by ReflectivityAmount, and the dual specular are the engine's exact
// math. NNAM is a NORMAL map (unpack xy = rgb*2-1, rebuild z); sampling it as color is the classic
// rainbow-water bug. Scene sun (SunDir/SunColor) is a runtime uniform the viewer lacks, so a fixed
// sun stands in. Absolute tile size + wind-speed unit live in the engine vertex shader (un-recovered),
// so they remain tunable constants.

cbuffer Uniforms : register(b0)
{
    float4x4 uViewProj;
    float4 uShallow;     // rgb in 0..1   (DNAM ShallowColor / FNV c2)
    float4 uDeep;        // rgb in 0..1   (DNAM DeepColor    / FNV c3)
    float4 uReflection;  // rgb in 0..1   (DNAM ReflectionColor / FNV c4)
    float4 uCamPosTime;  // xyz = camera world pos (FNV EyePos c1), w = elapsed seconds
    uint4 uNoiseParams;  // x = NNAM bindless index (0xFFFFFFFF = none), y = world units/tile,
                         // z = fNoiseScale bits, w = WATR ANAM opacity/100 bits (Oblivion VarAmounts.z)
    float4 uSurface0;    // NormalsUvScale, FresnelAmount(=F0), ReflectivityAmount, Shininess(spec exp)
    float4 uSurface1;    // SunPower, DepthFalloffStart, DepthFalloffEnd, w = lava flag (1 = emissive lava)
    float4 uLayer1;      // per noise layer: UvScale, WindDirDeg, WindSpeed, AmpScale
    float4 uLayer2;
    float4 uLayer3;
    uint4 uDepthParams;  // x = scene-depth SRV bindless index (0xFFFFFFFF = none), y/z = near/far bits,
                         // w = depth-occlusion tie-break bias (world units, asfloat) — water wins coplanar ties
    float4 uRenderOrigin; // xyz = camera-relative render origin; w = scene-depth sample count
    // ---- FO4_WATER-only constants (appended at the end so every other variant's offsets are
    // untouched; sourced from the FO4 WATR DNAM — see WaterShaderVariant.Fo4Water). ----
    float4 uFo4Spec;     // x = Sun Specular Magnitude, y = Silt Amount, z = Shallow Alpha, w = Deep Alpha
    float4 uFo4Ranges;   // x/y = Color Shallow/Deep Range, z/w = Alpha Shallow/Deep Range
                         //       (multipliers of Depth Amount; retail authors them ≈1.0)
    float4 uFo4DarkSilt; // rgb = DNAM silt Dark Color (the FO4 PS's unshadowed ambient add),
                         // w = DNAM Depth Amount (world-unit column at which the depth ramps saturate)
    uint4 uNormalIndices; // ordered normal sources; Oblivion repurposes y as WATR TNAM DetailMap
    float4 uLegacySurface0; // Oblivion: WaveAmplitude, WaveFrequency, ScrollXSpeed, ScrollYSpeed
    float4 uLegacySurface1; // Oblivion: FogNear, FogFar, TextureBlend, WindVelocity
    // WATER-07 opt-in FO4/FO76 architecture (FO4_WATER_ARCHITECTURAL only).
    uint4 uModernIndices;   // body/coverage, composited normal, gloss/flow, depth LUT
    uint4 uModernTechnique; // x = recovered technique ID, y = TextureCube index, z = point-light cap
    float4 uModernParams;   // glossScaleA/B, neutral outputAlpha, neutral alphaTestThreshold
    float4 uModernLightSilt;// retained LightSilt rgb, normal magnitude in w
    // Bounded FNV WATER001 tail. Appended after the established 416-byte prefix so every existing
    // WATER003/Oblivion/FO4/Morrowind constant retains its exact register.
    uint4 uFnvWater001Snapshot; // x = opaque SceneColor Texture2D index, y/z = dimensions,
                                // w = asuint(horizontal generated-cell plane height)
    float4 uFnvWater001Surface; // UnderwaterFogNear/Far, AboveWaterFogAmount, DistortionAmount
};

// Shared scene atmosphere (b3). CPU mirror: WorldView3DControl.AtmosphereConstants,
// bound once per frame for the whole scene. Water reads the sun dir/color from here so its specular
// and body-lighting track the time-of-day/weather sun like the rest of the scene (P3). When lighting
// is disabled (uSunColorLighting.w == 0) it falls back to the static kSunDir/kSunColor below, so the
// water looks exactly as it did pre-atmosphere.
#include "atmosphere.hlsli"

// Skyrim constant fog: the same powered, FNAM-capped amount blends near→far fog color and then
// surface→fog. Legacy weather binds far=near/max=1.
float3 ApplyFog(float3 color, float3 worldPos)
{
    if (uFogColorFogEnabled.w < 0.5)
    {
        return color;
    }

    // Water renders in absolute world space (its ripple UVs are world-anchored), so the fog distance
    // uses water's OWN absolute camera (uCamPosTime.xyz) rather than the shared atmosphere camera, which
    // is zeroed in camera-relative mode (1G). The fog power (uCameraPosFogPower.w) is position-free.
    float dist = length(worldPos - uCamPosTime.xyz);
    float q = saturate((dist - uAtmosphereParams.y) / max(uAtmosphereParams.z - uAtmosphereParams.y, 1.0));
    float amount = min(pow(q, max(uCameraPosFogPower.w, 0.01)), saturate(uFogFarColorMax.w));
    float3 fogRgb = lerp(uFogColorFogEnabled.rgb, uFogFarColorMax.rgb, amount);
    return lerp(color, fogRgb, amount);
}

// Bindless texture table (slot 4, space1) shared with terrain/references. The NNAM normal map
// lives at uNoiseParams.x (FNV NoiseMap, sampler s2). s0 is the shared anisotropic-wrap sampler.
Texture2D gWaterTextures[] : register(t0, space1);
// space3 aliases the same bindless heap slots for R32_FLOAT Texture2DMS scene-depth descriptors.
// uRenderOrigin.w selects this declaration only when the host supplied sampleCount > 1.
Texture2DMS<float> gWaterDepthTexturesMsaa[] : register(t0, space3);
SamplerState gWaterSampler : register(s0);
// WATER001's opaque-scene snapshot and FO4's generated LUT/cubemap paths both use the root
// signature's clamp sampler. Keep it outside the FO4 guard so the FNV permutation can bind it.
SamplerState gWaterClampSampler : register(s2);
#if FO4_WATER_ARCHITECTURAL
TextureCube gWaterCubemaps[] : register(t0, space2);
SamplerState gWaterShadowSampler : register(s3);

struct PointLight
{
    float4 PositionRadius;
    float4 ColorIntensity;
    float4 AuthoredMetadata;
    float4 Reserved;
};
StructuredBuffer<PointLight> uPointLights : register(t9, space0);
#endif

// FO3/FNV's DNAM-driven scroll/blend + normal reconstruction now runs in the explicit
// water_noise.comp.hlsl prepass. This helper remains for Skyrim's independently-authored normal maps
// and for a soft fallback if the prepass cannot reserve transient GPU constants in a saturated frame.
// FNV passes the live scene SunDir (c12) / SunColor (c13). P3 feeds those from the shared b3
// atmosphere CB (uSunDirIntensity / uSunColorLighting) when lighting is enabled, so water tracks the
// time-of-day/weather sun; when lighting is OFF these static constants stand in (the pre-atmosphere
// look — same direction the terrain shader used). P5 additionally tints the reflection with the
// atmosphere sky (uSkyTop/uSkyHorizon) when the skybox is on (see main()).
static const float3 kSunDir = float3(0.40824829, 0.40824829, 0.81649658); // normalize(0.5,0.5,1)
static const float3 kSunColor = float3(1.0, 0.97, 0.9);

struct PSInput
{
    float4 Position  : SV_Position;
    float3 vWorldPos : TEXCOORD0;
};

// Procedural fallback when the worldspace has no NNAM texture (proto/test worlds).
float2 RipplePerturb(float2 p, float t)
{
    const float2 dir1 = float2(0.80, 0.60);
    const float2 dir2 = float2(-0.50, 0.86);
    const float f1 = 0.0040;
    const float f2 = 0.0072;
    float2 grad =
        dir1 * f1 * cos(dot(p, dir1) * f1 + t * 1.3) +
        dir2 * f2 * cos(dot(p, dir2) * f2 - t * 0.9);
    return -grad * 45.0;
}

// One scrolling noise octave -> its raw xyz perturbation in [-1,1]. The FNV NoiseMap
// (genaratednoise01.dds) is a full-range RGB noise, NOT a blue-biased normal map — the engine adds the
// (0,0,1) z-bias in the pixel shader (see main()), so all three channels are the perturbation (engine
// WATER000.pso: `texld r3,v7,s2; mad r3.xyz,r3,2,-1`).
// `layer.x` is consumed by the classic prepass as fTexScale=max(1,ceil(fHeightUVScale*.01)); this
// direct fallback uses its caller-provided world frequency instead. `layer.w` remains the authored
// fAmplitude weight.
float3 SampleNoiseLayer(uint idx, float2 worldXy, float freq, float4 layer, float t)
{
    // layer = (UvScale[displacement fHeightUVScale, NOT used for noise], WindDirDeg, WindSpeed, fAmplitude).
    // Engine ISNOISESCROLLANDBLEND: each layer scrolls the noise in its own UV by WindSpeed·dt along WindDir°,
    // weighted by fAmplitude (UpdateWaterNoise, RE-recovered — no fudge multiplier on the scroll rate).
    float rad = radians(layer.y);
    // Compass-style direction: UpdateWaterNoise accumulates X with sin and Y with cos.
    float2 dir = float2(sin(rad), cos(rad));
    float2 uv = worldXy * freq + dir * (layer.z * t);
    return (gWaterTextures[NonUniformResourceIndex(idx)].Sample(gWaterSampler, uv).xyz * 2.0 - 1.0) * layer.w;
}

// Reversed-Z [1,0] depth -> positive view-space distance (world units). The scene uses reversed-Z
// (CameraState.ReverseZ): z=1 -> near, z=0 -> far. Both the sampled scene depth and this water
// fragment's own SV_Position.z are reversed, so linearizing both with this gives correct distances.
float LinearizeDepth(float ndcZ, float near, float far)
{
    return (near * far) / max(near + ndcZ * (far - near), 1e-4);
}

float LoadSceneDepth(uint depthIndex, int2 pixel, uint suppliedSampleCount)
{
    if (suppliedSampleCount <= 1u)
    {
        return gWaterTextures[NonUniformResourceIndex(depthIndex)].Load(int3(pixel, 0)).r;
    }

    uint depthWidth, depthHeight, descriptorSampleCount;
    gWaterDepthTexturesMsaa[NonUniformResourceIndex(depthIndex)]
        .GetDimensions(depthWidth, depthHeight, descriptorSampleCount);
    float nearestNdc = 0.0;
    [loop]
    for (uint sampleIndex = 0; sampleIndex < descriptorSampleCount; sampleIndex++)
    {
        // Reversed-Z: maximum is the nearest covered opaque sample. This preserves occlusion at an
        // MSAA silhouette instead of allowing water through one uncovered/far sample.
        nearestNdc = max(nearestNdc,
            gWaterDepthTexturesMsaa[NonUniformResourceIndex(depthIndex)].Load(pixel, sampleIndex));
    }
    return nearestNdc;
}

#if FO4_WATER_ARCHITECTURAL
bool TryModernCascadeShadow(float4x4 shadowMatrix, float4 cascade, float3 worldPos, out float visibility)
{
    visibility = 1.0;
    if (cascade.x < 0.5) return false;

    float4 shadowClip = mul(shadowMatrix, float4(worldPos, 1.0));
    float texel = cascade.y;
    float2 uv = float2(shadowClip.x * 0.5 + 0.5, 0.5 - shadowClip.y * 0.5);
    float border = 2.5 * texel;
    if (min(uv.x, uv.y) < border || max(uv.x, uv.y) > 1.0 - border ||
        shadowClip.z <= 0.0 || shadowClip.z >= 1.0)
    {
        return false;
    }

    uint slot = (uint)cascade.w;
    float reference = shadowClip.z + cascade.z;
    float2 texelPos = uv / texel - 0.5;
    float2 fraction = frac(texelPos);
    float2 gatherBase = (floor(texelPos) + 0.5) * texel;
    float litAmount = 0.0;
    [unroll]
    for (int dy = -1; dy <= 1; dy++)
    {
        [unroll]
        for (int dx = -1; dx <= 1; dx++)
        {
            float4 quad = gWaterTextures[NonUniformResourceIndex(slot)]
                .GatherRed(gWaterShadowSampler, gatherBase + float2(dx, dy) * texel);
            float4 visible = 1.0 - step(reference.xxxx, quad);
            litAmount += lerp(
                lerp(visible.w, visible.z, fraction.x),
                lerp(visible.x, visible.y, fraction.x),
                fraction.y);
        }
    }
    visibility = litAmount / 9.0;
    return true;
}

float ModernShadowFactor(float3 worldPos)
{
    float visibility;
    if (TryModernCascadeShadow(uShadowMatrix0, uShadowParams0, worldPos, visibility)) return visibility;
    if (TryModernCascadeShadow(uShadowMatrix1, uShadowParams1, worldPos, visibility)) return visibility;
    if (TryModernCascadeShadow(uShadowMatrix2, uShadowParams2, worldPos, visibility)) return visibility;
    if (TryModernCascadeShadow(uShadowMatrix3, uShadowParams3, worldPos, visibility)) return visibility;
    return 1.0;
}

float ModernOrenNayar(float3 V, float3 N, float3 L, float sigma)
{
    float sigma2 = sigma * sigma;
    float A = 1.0 - 0.5 * sigma2 / (sigma2 + 0.57);
    float B = 0.45 * sigma2 / (sigma2 + 0.09);
    float vdotn = dot(V, N);
    float ldotn = dot(L, N);
    float sinTan = sqrt(saturate((1.0 - vdotn * vdotn) * (1.0 - ldotn * ldotn))) /
        max(max(vdotn, ldotn), 1e-4);
    float cosPhi = max(dot(V - N * vdotn, L - N * ldotn), 0.0);
    return saturate(ldotn) * (A + B * cosPhi * sinTan);
}

float ModernSpecular(float3 V, float3 N, float3 L, float specPower, float specAmplitude)
{
    float ndotl = saturate(dot(N, L));
    float ndotvLocal = saturate(dot(N, V));
    float3 H = normalize(V + L);
    float ndoth = saturate(dot(N, H));
    float vdoth = saturate(dot(V, H));
    float normalizedBlinn = pow(ndoth, specPower) * (specPower + 2.0) * 0.159155;
    float visibility = min(2.0 * ndoth * min(ndotl, ndotvLocal) / max(vdoth, 1e-4), 1.0) /
        max(ndotvLocal, 1e-4);
    float fresnel5 = pow(1.0 - vdoth, 5.0);
    float fresnel = min(fresnel5 + 0.2 * (1.0 - fresnel5), 1.0);
    return min(visibility * fresnel * normalizedBlinn * 0.25, 15.0) * specAmplitude * ndotl;
}
#endif

#if FNV_WATER001
// WATER001 is the no-reflection-RT / refraction+noise+depth+fog retail permutation. The viewer's
// source is a copy/resolve of opaque scene color, so this remains explicitly an approximation of
// the engine's selectively populated RefractionMap. CPU preflight restricts it to one horizontal
// generated-cell plane; every per-pixel reconstruction/projection failure returns the established
// WATER003 color path (or its normal foreground clip) instead of sampling undefined content.

float3 FnvWater001Perturbation(uint noiseIndex, float2 worldXy, float t)
{
    float fUVScale = max(uSurface0.x, 1.0);
    float fNoiseScale = max(asfloat(uNoiseParams.z), 1.0);
    float fMacro = 1.0 / fUVScale;
    float fDetail = fNoiseScale / fUVScale;
    if (noiseIndex == 0xFFFFFFFFu)
    {
        return float3(RipplePerturb(worldXy, t), 0.0);
    }
    if (uNormalIndices.w != 0u)
    {
        return gWaterTextures[NonUniformResourceIndex(noiseIndex)]
            .Sample(gWaterSampler, worldXy * fMacro).xyz * 2.0 - 1.0;
    }

    uint normal1 = uNormalIndices.x == 0xFFFFFFFFu ? noiseIndex : uNormalIndices.x;
    uint normal2 = uNormalIndices.y == 0xFFFFFFFFu ? normal1 : uNormalIndices.y;
    uint normal3 = uNormalIndices.z == 0xFFFFFFFFu ? normal1 : uNormalIndices.z;
    float3 macro = SampleNoiseLayer(normal1, worldXy, fMacro, uLayer1, t)
                 + SampleNoiseLayer(normal2, worldXy, fMacro, uLayer2, t)
                 + SampleNoiseLayer(normal3, worldXy, fMacro, uLayer3, t);
    float3 detail = SampleNoiseLayer(normal1, worldXy, fDetail, uLayer1, t)
                  + SampleNoiseLayer(normal2, worldXy, fDetail, uLayer2, t)
                  + SampleNoiseLayer(normal3, worldXy, fDetail, uLayer3, t);
    return macro + detail;
}

float4 FnvWater003LocalFallback(
    PSInput input,
    float3 perturbation,
    float3 V,
    float distXY,
    float column,
    float depthT,
    float3 sunDir,
    float3 sunCol,
    float sunGate)
{
    // Same manual foreground rejection and shader math as the normal FNV WATER003 route. This
    // function is compiled only into FNV_WATER001; the standalone WATER003 bytecode below remains
    // behind the opposite preprocessor branch and is not rewritten by this feature.
    if (!isfinite(column) || !isfinite(depthT) || !all(isfinite(perturbation)) ||
        !all(isfinite(V)))
    {
        // A nonfinite main-depth sample cannot support either safe foreground rejection or the
        // WATER003 depth fade. Fail closed instead of relying on undefined clip(NaN) behavior.
        clip(-1.0);
        return float4(0.0, 0.0, 0.0, 0.0);
    }
#if !WATER_HARDWARE_OCCLUSION
    clip(column + asfloat(uDepthParams.w));
#endif
    float noiseFade = saturate((8192.0 - distXY) / 4096.0);
    float3 n3 = perturbation * depthT;
    n3.z += 1.0;
    n3.xy *= noiseFade;
    float3 N = normalize(n3);
    float ndotv = saturate(dot(N, V));

    float3 body = lerp(uShallow.rgb, uDeep.rgb, depthT);
    float3 bodyLightDirection = normalize(float3(sunDir.x, 4.0 * sunDir.y, sunDir.z));
    body *= saturate(dot(N, bodyLightDirection));
    float fresnel = saturate(uSurface0.y) +
        (1.0 - saturate(uSurface0.y)) * pow(1.0 - ndotv, 5.0);
    float3 color = lerp(body, uReflection.rgb, saturate(fresnel));

    float3 reflectedView = reflect(-V, N);
    float sunSpec = pow(saturate(dot(reflectedView, sunDir)), max(uSurface0.w, 1.0));
    float skyGlint = pow(saturate(dot(float2(N.x, N.z), float2(-0.57, 0.82))), 100.0);
    color += (sunSpec + skyGlint) * sunCol * sunGate;
    return float4(ApplyFog(color, input.vWorldPos), 1.0);
}

float FnvWater001SceneFogAmount(float distanceToEye)
{
    if (uFogColorFogEnabled.w < 0.5 ||
        !isfinite(uAtmosphereParams.y) || !isfinite(uAtmosphereParams.z) ||
        uAtmosphereParams.z <= uAtmosphereParams.y)
    {
        return 0.0;
    }
    float q = saturate((distanceToEye - uAtmosphereParams.y) /
        (uAtmosphereParams.z - uAtmosphereParams.y));
    return pow(q, max(uCameraPosFogPower.w, 0.01));
}

bool FnvWater001DepthTapIsUnderwater(
    uint depthIndex,
    int2 pixel,
    uint depthSampleCount,
    float nearPlane,
    float farPlane,
    float displacedWaterDistance,
    float3 displacedWorld,
    float planeHeight)
{
    float sceneNdc = LoadSceneDepth(depthIndex, pixel, depthSampleCount);
    float sceneDistance = LinearizeDepth(sceneNdc, nearPlane, farPlane);
    float rayScale = sceneDistance / max(displacedWaterDistance, 1e-4);
    float3 scenePoint = uCamPosTime.xyz +
        (displacedWorld - uCamPosTime.xyz) * rayScale;
    return sceneNdc > 0.0 && sceneNdc <= 1.0 &&
        isfinite(sceneDistance) && isfinite(displacedWaterDistance) &&
        displacedWaterDistance > 0.0 && sceneDistance > displacedWaterDistance &&
        all(isfinite(scenePoint)) && scenePoint.z < planeHeight;
}

float4 main(PSInput input) : SV_Target
{
    float t = uCamPosTime.w;
    bool lit = uSunColorLighting.w > 0.5;
    float3 sunDir = lit ? normalize(uSunDirIntensity.xyz) : kSunDir;
    float3 sunCol = lit ? uSunColorLighting.rgb : kSunColor;
    float sunGate = lit ? max(uSunDirIntensity.w, 0.0) : 1.0;
    uint noiseIndex = uNoiseParams.x;

    float3 eyeVector = uCamPosTime.xyz - input.vWorldPos;
    float eyeDistance = length(eyeVector);
    float distXY = length(eyeVector.xy);
    float3 V = normalize(eyeVector);
    uint depthIndex = uDepthParams.x;
    float near = asfloat(uDepthParams.y);
    float far = asfloat(uDepthParams.z);
    uint depthSampleCount = max((uint)uRenderOrigin.w, 1u);
    float sceneNdc = depthIndex == 0xFFFFFFFFu
        ? 0.0
        : LoadSceneDepth(depthIndex, (int2)input.Position.xy, depthSampleCount);
    float sceneDistance = LinearizeDepth(sceneNdc, near, far);
    float waterDistance = LinearizeDepth(input.Position.z, near, far);
    float column = sceneDistance - waterDistance;
    float fallbackDepthT = saturate((column - uSurface1.y) / max(uSurface1.z - uSurface1.y, 1e-3));
    float3 perturbation = FnvWater001Perturbation(noiseIndex, input.vWorldPos.xy, t);

    uint snapshotIndex = uFnvWater001Snapshot.x;
    float planeHeight = asfloat(uFnvWater001Snapshot.w);
    float underwaterFogNear = uFnvWater001Surface.x;
    float underwaterFogFar = uFnvWater001Surface.y;
    float aboveWaterFogAmount = uFnvWater001Surface.z;
    float distortionAmount = uFnvWater001Surface.w;

    // Reconstruct the exact WATERDEPTH writer lanes from the main perspective depth ray:
    //   P = E + (W-E)*(sceneDist/waterDist)
    //   D.x = |P-W|/UnderwaterFogFar, D.y = dot(W-P,+Z)/UnderwaterFogFar.
    // No saturation occurs here; WATER001 applies its recovered distance correction below.
    bool validDepth = depthIndex != 0xFFFFFFFFu && snapshotIndex != 0xFFFFFFFFu &&
        sceneNdc > 0.0 && sceneNdc <= 1.0 &&
        isfinite(sceneDistance) && isfinite(waterDistance) && waterDistance > 0.0 &&
        sceneDistance > waterDistance && isfinite(underwaterFogFar) && underwaterFogFar > 0.0;
    float rayScale = sceneDistance / waterDistance;
    float3 scenePoint = uCamPosTime.xyz + (input.vWorldPos - uCamPosTime.xyz) * rayScale;
    float2 rawDepth = float2(
        length(scenePoint - input.vWorldPos) / underwaterFogFar,
        dot(input.vWorldPos - scenePoint, float3(0.0, 0.0, 1.0)) / underwaterFogFar);
    validDepth = validDepth && all(isfinite(scenePoint)) && all(isfinite(rawDepth)) &&
        abs(input.vWorldPos.z - planeHeight) <= 1e-3 && scenePoint.z < planeHeight &&
        rawDepth.x >= 0.0 && rawDepth.y > 0.0;
    if (!validDepth)
    {
        return FnvWater003LocalFallback(
            input, perturbation, V, distXY, column, fallbackDepthT, sunDir, sunCol, sunGate);
    }

    float depthT = saturate((rawDepth.y - uSurface1.y) / (uSurface1.z - uSurface1.y));
    float noiseFade = saturate((8192.0 - distXY) / 4096.0);
    float distFade = saturate(distXY / 5000.0);
    float distortionScale = lerp(4.0, distortionAmount, distFade);
    float3 normalSource = perturbation * depthT + float3(0.0, 0.0, 1.0);
    normalSource.xy *= noiseFade;
    float3 N = normalize(normalSource);

    // WATER001 corrects both lanes toward one as the noise normal fades with distance. This is a
    // componentwise saturate after interpolation, not a clamp on the reconstructed D values above.
    float2 correctedDepth = saturate(lerp(float2(1.0, 1.0), rawDepth, noiseFade));
    float2 deltaXY = rawDepth.y * depthT * distortionScale * N.xy;
    float3 displacedWorld = input.vWorldPos + float3(deltaXY, 0.0);
    float4 refractionClip = mul(uViewProj, float4(displacedWorld - uRenderOrigin.xyz, 1.0));
    bool validProjection = all(isfinite(refractionClip)) && refractionClip.w > 1e-5 &&
        refractionClip.z >= 0.0 && refractionClip.z <= refractionClip.w;
    float inverseW = rcp(refractionClip.w);
    float2 refractionUv = float2(
        refractionClip.x * inverseW * 0.5 + 0.5,
        0.5 - refractionClip.y * inverseW * 0.5);
    float2 snapshotDimensions = float2(uFnvWater001Snapshot.yz);
    float2 halfTexel = 0.5 / max(snapshotDimensions, float2(1.0, 1.0));
    validProjection = validProjection && uFnvWater001Snapshot.y > 0u &&
        uFnvWater001Snapshot.z > 0u && all(isfinite(refractionUv)) &&
        all(refractionUv >= halfTexel) && all(refractionUv <= 1.0 - halfTexel);
    if (!validProjection)
    {
        return FnvWater003LocalFallback(
            input, perturbation, V, distXY, column, fallbackDepthT, sunDir, sunCol, sunGate);
    }

    // The approximation snapshot contains the whole opaque scene, unlike retail's selectively
    // populated RefractionMap.  Never pull an above-water/foreground silhouette across the water
    // edge: validate the displaced tap against scene depth and the horizontal water plane.  When
    // distortion crosses that content boundary, retain WATER001 transmission but use the original
    // pixel's already-proven underwater sample.  Falling all the way back to opaque WATER003 here
    // creates the same sharp bright rim this guard is intended to remove.
    // Match SampleLevel's bilinear footprint exactly: texel-space center is uv*size-0.5 and the
    // clamp sampler clamps each of the four integer taps to the texture edge. A single safe tap is
    // insufficient because even a small weight from any foreground texel creates a bright rim.
    int2 displacedPixelMax = int2(uFnvWater001Snapshot.yz) - 1;
    int2 displacedPixelBase = (int2)floor(refractionUv * snapshotDimensions - 0.5);
    int2 displacedPixel00 = clamp(displacedPixelBase, int2(0, 0), displacedPixelMax);
    int2 displacedPixel10 = clamp(displacedPixelBase + int2(1, 0), int2(0, 0), displacedPixelMax);
    int2 displacedPixel01 = clamp(displacedPixelBase + int2(0, 1), int2(0, 0), displacedPixelMax);
    int2 displacedPixel11 = clamp(displacedPixelBase + int2(1, 1), int2(0, 0), displacedPixelMax);
    float displacedWaterDistance = LinearizeDepth(refractionClip.z * inverseW, near, far);
    bool displacedFootprintIsUnderwater =
        FnvWater001DepthTapIsUnderwater(
            depthIndex, displacedPixel00, depthSampleCount, near, far,
            displacedWaterDistance, displacedWorld, planeHeight) &&
        FnvWater001DepthTapIsUnderwater(
            depthIndex, displacedPixel10, depthSampleCount, near, far,
            displacedWaterDistance, displacedWorld, planeHeight) &&
        FnvWater001DepthTapIsUnderwater(
            depthIndex, displacedPixel01, depthSampleCount, near, far,
            displacedWaterDistance, displacedWorld, planeHeight) &&
        FnvWater001DepthTapIsUnderwater(
            depthIndex, displacedPixel11, depthSampleCount, near, far,
            displacedWaterDistance, displacedWorld, planeHeight);
    if (!displacedFootprintIsUnderwater)
    {
        refractionUv = input.Position.xy / snapshotDimensions;
    }

    float3 refractionSample = gWaterTextures[NonUniformResourceIndex(snapshotIndex)]
        .SampleLevel(gWaterClampSampler, refractionUv, 0).rgb;
    if (!all(isfinite(refractionSample)))
    {
        return FnvWater003LocalFallback(
            input, perturbation, V, distXY, column, fallbackDepthT, sunDir, sunCol, sunGate);
    }

    // The opaque snapshot already contains scene distance fog. WATER001 first removes that fog at
    // the displaced depth, reconstructs the water/body/reflection composite, then reapplies the
    // ordinary fog at the surface distance (WATER001.pso instructions 53-110).
    float displacedFog = FnvWater001SceneFogAmount(eyeDistance + correctedDepth.y);
    float3 refracted = (refractionSample - displacedFog * uFogColorFogEnabled.rgb) /
        (1.0 - displacedFog + 1e-4);

    float3 body = lerp(uShallow.rgb, uDeep.rgb, correctedDepth.y);
    float3 bodyLightDirection = normalize(float3(sunDir.x, 4.0 * sunDir.y, sunDir.z));
    float bodyLight = saturate(dot(N, bodyLightDirection));
    float3 litBody = body * bodyLight;

    float underwaterFogRange = underwaterFogFar - underwaterFogNear;
    bool validWaterFog = isfinite(underwaterFogNear) && isfinite(underwaterFogRange) &&
        underwaterFogRange > 0.0 && isfinite(aboveWaterFogAmount) &&
        aboveWaterFogAmount >= 0.0 && aboveWaterFogAmount <= 1.0;
    if (!validWaterFog)
    {
        return FnvWater003LocalFallback(
            input, perturbation, V, distXY, column, fallbackDepthT, sunDir, sunCol, sunGate);
    }
    float aboveWaterFog = (1.0 - saturate(
        underwaterFogFar * (1.0 - correctedDepth.x) / underwaterFogRange)) *
        aboveWaterFogAmount;
    float3 transmitted = lerp(refracted, litBody, depthT * aboveWaterFog);

    float ndotv = saturate(dot(N, V));
    float oneMinusNdotV = 1.0 - ndotv;
    float fresnel5 = oneMinusNdotV * oneMinusNdotV;
    fresnel5 *= fresnel5;
    fresnel5 *= oneMinusNdotV;
    float fresnel = saturate(uSurface0.y) +
        (1.0 - saturate(uSurface0.y)) * fresnel5;
    float3 bodyReflection = lerp(litBody, uReflection.rgb, correctedDepth.x * fresnel);
    float3 color = lerp(transmitted, bodyReflection, correctedDepth.y);

    float3 reflectedView = reflect(-V, N);
    float sunSpec = pow(saturate(dot(reflectedView, sunDir)), max(uSurface0.w, 1.0));
    float skyGlint = pow(saturate(dot(float2(N.x, N.z), float2(-0.57, 0.82))), 100.0);
    color = saturate(color + (sunSpec + skyGlint) * sunCol * sunGate);

    float finalFog = FnvWater001SceneFogAmount(eyeDistance);
    color = lerp(color, uFogColorFogEnabled.rgb, finalFog);
    return float4(color, 1.0);
}
#else
float4 main(PSInput input) : SV_Target
{
    float t = uCamPosTime.w;
    // Scene sun: from the shared atmosphere CB when lighting is on (tracks time-of-day/weather),
    // else the static fallback so water is unchanged in the lighting-off state.
    bool lit = uSunColorLighting.w > 0.5;
    float3 sunDir = lit ? normalize(uSunDirIntensity.xyz) : kSunDir;
    float3 sunCol = lit ? uSunColorLighting.rgb : kSunColor;
    float sunGate = lit ? max(uSunDirIntensity.w, 0.0) : 1.0;
    uint noiseIndex = uNoiseParams.x;
    // RE-recovered scales (the 256² NNAM tiles at TexScale). The recovered VS does v7=(worldXY+QPos)/TexScale,
    // and TexScale = DNAM fUVScale (@136, ~1000 world units) — fNoiseScale (@96, ~13) is far too small to be a
    // world tile, so it is the ISNOISENORMALMAP normal-DETAIL scale, not the macro tile. The engine has BOTH:
    // a big macro tile (fUVScale) AND fine normal detail (fNoiseScale finer). We span that with 3 octaves —
    // macro (1/fUVScale → ~1000u features, far-apart repeat) down to detail (fNoiseScale/fUVScale → ~75u fine
    // ripple). Tiling everything at the 75u detail scale was the "too small + repetitive" bug.
    float fUVScale = max(uSurface0.x, 1.0);
    float fNoiseScale = max(asfloat(uNoiseParams.z), 1.0);
    float fMacro = 1.0 / fUVScale;               // macro world tile = TexScale (fUVScale ~1000): broad structure
    float fDetail = fNoiseScale / fUVScale;      // fine normal-detail scale (fUVScale/fNoiseScale ~75): dense ripple
    float F0 = saturate(uSurface0.y);            // FNV FresnelRI.x
    float reflectivity = saturate(uSurface0.z);  // FNV FresnelRI.w (reflection multiplier)
    float specExp = max(uSurface0.w, 1.0);       // FNV VarAmounts.x (sun-specular exponent)

    float3 eye = uCamPosTime.xyz - input.vWorldPos; // surface -> camera (FNV EyePos - worldPos)
    float distXY = length(eye.xy);
    float3 V = normalize(eye);

    // FNV depthT = water-column thickness from the scene depth map, normalized over DepthFalloff.
    // When the host hands us the scene depth SRV (uDepthParams.x != 0xFFFFFFFF) we reproduce that
    // exactly: linearize the sampled scene depth and this water fragment's own depth, take the
    // view-space gap, and normalize by DepthFalloffStart/End (uSurface1.yz). Occlusion against
    // nearer opaque geometry: WATER_HARDWARE_OCCLUSION compiles rely on the read-only DSV's
    // per-sample GreaterEqual test (antialiased silhouettes); legacy compiles discard here at
    // pixel rate. Falls back to a view-angle proxy when no depth SRV is set.
    uint depthIndex = uDepthParams.x;
    float depthT;
    float column = 0.0;    // raw view-space depth gap in world units (only meaningful when sampled)
    float waterDist = 0.0; // linearized view distance to the water surface (vertical-column conversion)
    if (depthIndex == 0xFFFFFFFFu)
    {
        depthT = saturate(dot(float3(0, 0, 1), V));
    }
    else
    {
        float near = asfloat(uDepthParams.y);
        float far = asfloat(uDepthParams.z);
        uint depthSampleCount = max((uint)uRenderOrigin.w, 1u);
        float sceneNdc = LoadSceneDepth(depthIndex, (int2)input.Position.xy, depthSampleCount);
        float sceneDist = LinearizeDepth(sceneNdc, near, far);
        waterDist = LinearizeDepth(input.Position.z, near, far);
        column = sceneDist - waterDist;           // >0: water over a floor; <0: geometry occludes
#if !WATER_HARDWARE_OCCLUSION
        // 3D-2 tie-break: bias the occlusion test toward KEEPING the water (uDepthParams.w world units) so
        // a shoreline where water and terrain are ~coplanar (column ≈ 0 ± sub-ULP depth noise) resolves to
        // water instead of flickering. The bias is tiny vs DepthFalloff, so genuinely occluded water
        // (column far negative) is still discarded. Hardware-occlusion compiles skip this: the pixel-rate
        // binary clip aliases at MSAA'd mesh silhouettes, while the read-only DSV's GreaterEqual test
        // rejects per sample (and covers the same coplanar tie-break in hardware).
        clip(column + asfloat(uDepthParams.w));    // discard water hidden behind opaque geometry
#endif
        float start = uSurface1.y;                 // DepthFalloffStart
        float end = uSurface1.z;                   // DepthFalloffEnd
        depthT = saturate((column - start) / max(end - start, 1e-3));
    }

    // OBLIV-2 lava: emissive, no Fresnel/reflection/specular. The DATA Shallow/Deep colors are the molten
    // body (bright crust -> darker by depth); output them at full brightness with a slow spatial pulse so a
    // flow reads as lava rather than reflective water. Opaque. uSurface1.w is the lava flag (1 = lava).
    if (uSurface1.w > 0.5)
    {
        float3 lava = lerp(uShallow.rgb, uDeep.rgb, depthT);
        float pulse = 0.9 + 0.1 * sin(t * 1.5 + (input.vWorldPos.x + input.vWorldPos.y) * 0.001);
        // No output saturate: the HDR scene target holds the lava glow (×1.3) so the tonemap rolls
        // it off instead of clipping it flat. The tonemap's own saturate is the final clamp.
        return float4(ApplyFog(lava * pulse * 1.3, input.vWorldPos), 1.0);
    }

#if MORROWIND_WATER
    // ==== Morrowind fixed-function water — NetImmerse 4.0.0.2 has no water pixel shader to port:
    // the engine draws an animated tiled diffuse plane (Morrowind.ini [Water]: water00-31.dds
    // cycling at SurfaceFPS; see docs/research/morrowind_atmosphere_water_model.md). The CPU side
    // selects the CURRENT frame and passes its bindless index in uNoiseParams.x; uNoiseParams.y is
    // the world-units-per-tile ([Water] SurfaceTileCount, TO-CONFIRM vs an OpenMW oracle); alpha is
    // [Water] World Alpha in uNoiseParams.w. No fresnel/reflection/specular — the optional
    // [PixelWater] terrain-reflection path is a follow-up. The shared depth block above still
    // discards occluded fragments, and ApplyFog recedes distant water into the weather haze.
    float mwTile = max((float)uNoiseParams.y, 1.0);
    float2 mwUv = input.vWorldPos.xy / mwTile;
    float3 mwTex = (noiseIndex == 0xFFFFFFFFu)
        ? uShallow.rgb
        : gWaterTextures[NonUniformResourceIndex(noiseIndex)].Sample(gWaterSampler, mwUv).rgb;
    if (lit)
    {
        // The FF pipeline vertex-lights the plane (N = +Z) with scene sun + ambient. The exact
        // vanilla light composition is a LABELED STAND-IN pending an exe pass; flat sun·N.z +
        // ambient keeps the surface tracking time-of-day like the rest of the scene.
        mwTex *= saturate(uAmbientColor.rgb + sunCol * saturate(sunDir.z));
    }
    return float4(ApplyFog(mwTex, input.vWorldPos), saturate(asfloat(uNoiseParams.w)));
#endif

#if FO4_WATER_ARCHITECTURAL
    // The modern path consumes the explicit t1 prepass output. The engine's water VS supplies a
    // single authored UV; the viewer has no water UV/vertex-color payload yet, so keep the existing
    // WATR world tile as the isolated coordinate fallback rather than inventing a second scale.
    float2 modernUv = input.vWorldPos.xy / max(uSurface0.x, 1.0);
    float2 modernNormalXy = gWaterTextures[NonUniformResourceIndex(uModernIndices.y)]
        .Sample(gWaterSampler, modernUv).xy * 2.0 - 1.0;
    float3 N = normalize(float3(
        modernNormalXy,
        sqrt(saturate(1.0 - dot(modernNormalXy, modernNormalXy)))));
#else
    // FNV distance fade of ripples: full within 4096 world units, -> 0 at 8192.
    float noiseFade = saturate((8192.0 - distXY) / 4096.0);

    // FNV WATER000 surface normal — engine PS (WATER000.pso lines 90-96), reproduced exactly:
    //   r3 = noise.xyz*2-1 ; r3 = r3*depthT + (0,0,1) ; r3.xy *= distFade ; N = normalize(r3)
    // Adding the (0,0,1) z-bias IN THE SHADER (engine const c7.xxww) — rather than rebuilding
    // z = sqrt(1 - |xy|^2) — is the fix for the harsh "blips": when the summed perturbation magnitude
    // exceeded 1, the rebuilt z collapsed to 0, tilting the normal flat/horizontal and smearing the sun
    // glint into elongated streaks. The z-bias keeps the normal near-vertical, so the surface reads as
    // gentle ripples. The engine pre-composites its 3 noise layers (ISNOISESCROLLANDBLEND) into one
    // texture and taps it once. uNormalIndices.w marks that recovered precomposited input; Skyrim and
    // allocation-failure fallbacks retain the direct authored-layer path below.
    float3 pert;
#if OBLIVION_WATER
    // Oblivion's NormalMap path is separate from FNV's precomposited-noise path. WATER000 unpacks
    // the current global water00..31 animation frame directly, attenuates XY by
    // (1-horizontalDistance*0.000122)^2, then normalizes; it does NOT multiply the normal by the
    // water-column factor or add FNV's (0,0,1) bias. WATR TNAM is the separate DetailMap below.
    if (noiseIndex == 0xFFFFFFFFu)
    {
        pert = float3(RipplePerturb(input.vWorldPos.xy, t), 1.0);
    }
    else
    {
        float2 legacyUv = input.vWorldPos.xy * fMacro + uLegacySurface0.zw * t;
        pert = gWaterTextures[NonUniformResourceIndex(noiseIndex)].Sample(gWaterSampler, legacyUv).xyz * 2.0 - 1.0;
    }
    float oblivionLinearDistanceAtten = 1.0 - distXY * 0.000122;
    float oblivionDistanceAtten = oblivionLinearDistanceAtten * oblivionLinearDistanceAtten;
    pert.xy *= oblivionDistanceAtten;
    float3 N = normalize(pert);
#else
    if (noiseIndex == 0xFFFFFFFFu)
    {
        pert = float3(RipplePerturb(input.vWorldPos.xy, t), 0.0);
    }
    else
    {
        if (uNormalIndices.w != 0u)
        {
            // WATER000.vso supplies (worldXY + QPosAdjust) / fUVScale. The compute prepass already
            // contains all three animated layers and the ISNOISENORMALMAP Sobel reconstruction.
            pert = gWaterTextures[NonUniformResourceIndex(noiseIndex)]
                .Sample(gWaterSampler, input.vWorldPos.xy * fMacro).xyz * 2.0 - 1.0;
        }
        else
        {
            // Skyrim keeps three independently-authored normal inputs; the same path is also the
            // fail-soft fallback if the transient ring cannot record this frame's classic prepass.
            uint normal1 = uNormalIndices.x == 0xFFFFFFFFu ? noiseIndex : uNormalIndices.x;
            uint normal2 = uNormalIndices.y == 0xFFFFFFFFu ? normal1 : uNormalIndices.y;
            uint normal3 = uNormalIndices.z == 0xFFFFFFFFu ? normal1 : uNormalIndices.z;
            float3 macro = SampleNoiseLayer(normal1, input.vWorldPos.xy, fMacro, uLayer1, t)
                         + SampleNoiseLayer(normal2, input.vWorldPos.xy, fMacro, uLayer2, t)
                         + SampleNoiseLayer(normal3, input.vWorldPos.xy, fMacro, uLayer3, t);
            float3 detail = SampleNoiseLayer(normal1, input.vWorldPos.xy, fDetail, uLayer1, t)
                          + SampleNoiseLayer(normal2, input.vWorldPos.xy, fDetail, uLayer2, t)
                          + SampleNoiseLayer(normal3, input.vWorldPos.xy, fDetail, uLayer3, t);
            pert = macro + detail;
        }
    }
    // Ripple amplitude scales with the REAL water-column depth (engine r0.z: shallow water reads flat,
    // deep water ripples). depthT is that column ONLY when a scene-depth SRV is bound; otherwise it's the
    // view-angle proxy (saturate(N·V)) — which is unrelated to water depth and wrongly flattened ripples
    // to nothing at oblique/grazing angles (V.z→0). So use the real column when we have it, else full
    // amplitude (1.0): with no depth info we can't know the column, and oblique water should still ripple.
    float rippleDepth = (depthIndex == 0xFFFFFFFFu) ? 1.0 : depthT;
    float3 n3 = pert * rippleDepth; // engine: perturbation *= water-column depth factor
    n3.z += 1.0;                // engine: + (0,0,1) z-bias — normal stays near-vertical (gentle ripples)
    n3.xy *= noiseFade;         // engine: xy faded out with distance
    float3 N = normalize(n3);
#endif
#endif // !FO4_WATER_ARCHITECTURAL

    float ndotv = saturate(dot(N, V));

#if FO4_WATER
#if FO4_WATER_ARCHITECTURAL
    // ==== FO4/FO76 recovered dynamic-water architecture (explicit opt-in) ====
    // t0/t1/t2/t15 are real per-frame GPU resources. Unrecovered generators/constants remain
    // neutral and are documented in water_modern.comp.hlsl; the established FO4 stand-in below
    // remains the default and the per-frame fallback whenever these resources cannot be recorded.
    uint modernTechnique = uModernTechnique.x;
    float4 baseCoverage = gWaterTextures[NonUniformResourceIndex(uModernIndices.x)].Load(int3(0, 0, 0));
    float4 bodyCoverage = baseCoverage;
    if ((modernTechnique & 0x40u) != 0u && depthIndex != 0xFFFFFFFFu)
    {
        float gammaDepth = pow(saturate(column / max(uFo4DarkSilt.w, 1e-4)), 0.45454545);
        bodyCoverage = gWaterTextures[NonUniformResourceIndex(uModernIndices.w)]
            .SampleLevel(gWaterClampSampler, float2(gammaDepth, 0.5), 0);
    }

    float4 glossFlow = gWaterTextures[NonUniformResourceIndex(uModernIndices.z)].Load(int3(0, 0, 0));
    float scaledGloss = glossFlow.y * uModernParams.x;
    float sigma = 1.0 - scaledGloss;
    float specPower = exp(10.0 * scaledGloss + 1.0);
    float specAmplitude = glossFlow.x * uModernParams.y * 3.14159265;
    float satNL = saturate(dot(N, sunDir));
    float relativeWorldDepthSpace = input.Position.z;
    float3 relativeWorldPos = input.vWorldPos - uCameraOrigin.xyz;
    float shadow = lit ? ModernShadowFactor(relativeWorldPos) : 1.0;

    // Recovered Oren-Nayar + transmission core. Diffuse receives shadow here and the complete
    // accumulator receives it again below, matching the shipped shader's double shadow multiply.
    float3 accumulated = ModernOrenNayar(V, N, sunDir, sigma) * sunCol * sunGate * shadow;
    float backscatter = 3.0 - 3.0 /
        (1.0 + exp(8.65591 * (2.0 * (1.0 - scaledGloss) - 1.0)));
    accumulated += saturate(dot(V, -sunDir)) * pow(1.0 - ndotv, 0.01) * satNL *
        backscatter * sunCol * sunGate;
    // cb1 wrap and under-light scales remain unrecovered. Their neutral value is zero, so no
    // residual term is fabricated here.
    accumulated *= shadow;

    float3 specularColor = ModernSpecular(V, N, sunDir, specPower, specAmplitude) *
        sunCol * sunGate * shadow;

    // Same recovered lighting core for local lights, with the shipped bounded attenuation and
    // hard clamp of twenty entries. Point-light positions are already camera-origin-relative.
    uint pointCount = lit
        ? min((uint)max(floor(uAtmosphereParams.w), 0.0), min(uModernTechnique.z, 20u))
        : 0u;
    [loop]
    for (uint pointIndex = 0; pointIndex < pointCount; pointIndex++)
    {
        PointLight pointLight = uPointLights[pointIndex];
        float radius = pointLight.PositionRadius.w;
        float3 toPoint = pointLight.PositionRadius.xyz - relativeWorldPos;
        float distanceSquared = dot(toPoint, toPoint);
        float radiusSquared = radius * radius;
        if (radius <= 0.0 || distanceSquared >= radiusSquared) continue;

        float3 pointDirection = toPoint * rsqrt(max(distanceSquared, 1e-8));
        float attenuation = pow(saturate(1.0 - distanceSquared / radiusSquared), 2.2);
        float3 pointRadiance = pointLight.ColorIntensity.rgb *
            (pointLight.ColorIntensity.w * attenuation);
        accumulated += ModernOrenNayar(V, N, pointDirection, sigma) * pointRadiance;
        specularColor += ModernSpecular(V, N, pointDirection, specPower, specAmplitude) * pointRadiance;
    }

    // SetupMaterial's ambient-source mapping is still open. Preserve the pre-existing DarkSilt
    // stand-in rather than silently replacing it with LightSilt or the DALC average.
    float3 lighting = accumulated + uFo4DarkSilt.rgb;
    float3 modernColor = bodyCoverage.rgb * lighting + specularColor;

    if ((modernTechnique & 0x100u) != 0u && uModernTechnique.y != 0xFFFFFFFFu)
    {
        float3 reflected = reflect(-V, N);
        float cubeMip = (1.0 - saturate(glossFlow.y)) * 6.0 + relativeWorldDepthSpace * 0.001953;
        float3 cube = gWaterCubemaps[NonUniformResourceIndex(uModernTechnique.y)]
            .SampleLevel(gWaterClampSampler, reflected, cubeMip).rgb;
        // Recovered factor 3 * t2.x * glossScaleB. Global cube intensity is neutral one until
        // SetupTechnique recovers cb1[2].x.
        modernColor += cube * (3.0 * glossFlow.x * uModernParams.y) * lighting;
    }

    // Exact structural alpha behavior: t0 coverage gates discard and output alpha is constant.
    // Their cb2 mappings are not recovered, so CPU supplies neutral threshold=0/output=1.
    clip(baseCoverage.a - uModernParams.w);
    return float4(ApplyFog(modernColor, input.vWorldPos), uModernParams.z);
#else
    // ==== FO4 BSWaterShader (ps_5_0, Shaders011.fxp group 5) — engine-exact math per
    // tools/GhidraProject/fo4_water_pixel_shader_decompiled.txt; asm line refs = g5_ps_00000001.asm.
    // Engine-generated inputs get labeled stand-ins: the composited displacement-normal texture is
    // our 3-layer scroll (N above), the depth→color LUT is evaluated analytically, the reflection
    // cubemap is the sky-gradient CB read, the shadow cascade term is 1 (no viewer shadow pass yet),
    // and the point-light loop is empty (hook for the placed-lights overhaul).
    float satNL = saturate(dot(N, sunDir));

    // Body color — the engine's t15 LUT bakes the DNAM Shallow→Deep ramp over normalized depth
    // (vertex-painted in-engine; DNAM Depth Amount scales it to world units — trough 18, lake 1087,
    // with the "range" fields as ≈1.0 multipliers). Evaluate from the real water column when scene
    // depth is bound, else a grazing-angle proxy.
    // The ramps are evaluated in GAMMA space: the engine PS decodes its LUT coordinate through
    // pow(x, 1/2.2) (asm 65-67 — the vertex-painted depth is gamma-decoded before the t15 sample),
    // so equal LUT steps are gamma-depth steps. A linear column/DepthAmount ramp under-responds in
    // shallow water (DepthAmount is the body's full authored depth — ocean 3007), reading far too
    // translucent over shelves.
    float fo4RampUnits = max(uFo4DarkSilt.w, 1.0);
    float colT = (depthIndex == 0xFFFFFFFFu)
        ? 1.0 - ndotv
        : pow(saturate(column / max(fo4RampUnits * uFo4Ranges.y, 1.0)), 0.454545);
    float3 body = lerp(uShallow.rgb, uDeep.rgb, colT);

    // Oren-Nayar sun diffuse (asm 110-132) — engine constants 0.57/0.09/0.45/0.5 exactly.
    // σ (roughness) is per-material in-engine (1 − cb2[11].x via the gloss pipeline); LABELED
    // STAND-IN 0.2 until BSWaterShader::SetupMaterial is decompiled.
    const float kFo4Sigma = 0.2;
    float a2 = kFo4Sigma * kFo4Sigma;
    float onA = 1.0 - 0.5 * a2 / (a2 + 0.57);
    float onB = 0.45 * a2 / (a2 + 0.09);
    float vdotn = dot(V, N);
    float ldotn = dot(sunDir, N);
    float sinTan = sqrt(saturate((1.0 - vdotn * vdotn) * (1.0 - ldotn * ldotn)))
        / max(max(vdotn, ldotn), 1e-3);
    float cosPhi = max(dot(V - N * vdotn, sunDir - N * ldotn), 0.0);
    float3 acc = satNL * (onA + onB * cosPhi * sinTan) * sunCol;

    // Transmission/backscatter (asm 134-143): sun shining through the surface toward the eye,
    // gated by a sigmoid roughness mask. The sigmoid input is the gloss-map term in-engine;
    // the DNAM Silt Amount stands in (labeled).
    float back = 3.0 - 3.0 / (1.0 + exp(8.65591 * (2.0 * (1.0 - saturate(uFo4Spec.y)) - 1.0)));
    acc += saturate(dot(V, -sunDir)) * pow(1.0 - ndotv, 0.01) * satNL * back * sunCol;

    // Wrap-lighting residual (asm 149-155): light beyond the direct N·L term, tinted by the body.
    // The wrap factor is a per-frame engine constant (cb1[7].x); LABELED default 0.5.
    const float kFo4Wrap = 0.5;
    acc += max(saturate((ldotn + kFo4Wrap) / (kFo4Wrap + 1.0)) - satNL, 0.0) * sunCol * body;

    // DNAM silt Dark Color = the unshadowed ambient add (asm 283: lighting = acc + cb2[3].yzw).
    float3 lighting = acc + uFo4DarkSilt.rgb;

    // Specular (asm 157-195): normalized Blinn-Phong ((e+2)/2π · (N·H)^e), Cook-Torrance-style
    // visibility min(2(N·H)min(N·L,N·V)/(V·H), 1)/(N·V), Schlick fresnel with the engine's literal
    // F0 = 0.2, ×0.25 energy factor, firefly clamp 15. Exponent = DNAM Sun Specular Power
    // (uSurface1.x), amplitude = DNAM Sun Specular Magnitude (uFo4Spec.x) — the authored analogs of
    // the engine's gloss-map-driven exponent/amplitude (labeled).
    float3 H = normalize(V + sunDir);
    float ndoth = saturate(dot(N, H));
    float vdoth = saturate(dot(V, H));
    float fo4SpecExp = max(uSurface1.x, 1.0);
    float blinn = pow(ndoth, fo4SpecExp) * (fo4SpecExp + 2.0) * 0.159155;
    float vis = min(2.0 * ndoth * min(satNL, ndotv) / max(vdoth, 1e-4), 1.0) / max(ndotv, 1e-4);
    float fres5 = pow(1.0 - vdoth, 5.0);
    float fres = min(fres5 + 0.2 * (1.0 - fres5), 1.0);
    float spec = min(vis * fres * blinn * 0.25, 15.0) * uFo4Spec.x * satNL;

    // Composite (asm 309-314): the reflection is multiplied by the SAME lighting as the body
    // (engine: cube·gloss·global × (lighting) + body·lighting + spec). RT-free reflection source =
    // sky gradient by the reflected ray (as the other variants), scaled by DNAM Reflectivity.
    float3 Rf = reflect(-V, N);
    float3 reflSky = uSkyTopSkyEnabled.w > 0.5
        ? lerp(uSkyHorizon.rgb, uSkyTopSkyEnabled.rgb, saturate(Rf.z))
        : uReflection.rgb;
    float3 fo4Color = body * lighting + reflSky * reflectivity * lighting + spec * sunCol;

    // Alpha: the engine draws the surface near-opaque and gets its "translucency" from the tinted
    // REFRACTION composite; RT-free, the DNAM Shallow→Deep alpha ramp stands in as framebuffer
    // coverage, evaluated in the same gamma-depth domain as the color ramp (shorelines still fade
    // where authored — ShallowAlpha is 0 on most exterior FO4 waters). The coverage is then FLOORED
    // by the surface's Schlick fresnel (the PS's literal F0 = 0.2): at grazing angles the surface
    // mirrors regardless of water depth — transmission scales by (1−F) — so distant water reads
    // opaque while looking straight down at a shallow shelf still shows the (tinted) bottom.
    float aT = (depthIndex == 0xFFFFFFFFu)
        ? 1.0 - ndotv
        : pow(saturate(column / max(fo4RampUnits * uFo4Ranges.w, 1.0)), 0.454545);
    float fo4Alpha = lerp(saturate(uFo4Spec.z), saturate(uFo4Spec.w), aT);
    float fo4SurfF = 0.2 + 0.8 * pow(1.0 - ndotv, 5.0);
    fo4Alpha = max(fo4Alpha, fo4SurfF);
    return float4(ApplyFog(fo4Color, input.vWorldPos), fo4Alpha);
#endif // FO4_WATER_ARCHITECTURAL
#else

#if OBLIVION_WATER
    // Oblivion body color: lerp(Deep, Shallow, N·V) — the view ANGLE picks the body tint
    // (grazing = deep, top-down = shallow); Oblivion's PS has no depth-column body term and no
    // body sun modulation (sun enters via the specular only).
    float3 body = lerp(uDeep.rgb, uShallow.rgb, ndotv);
#else
    // FNV body color: lerp(Shallow, Deep, depthT), lit by WATER003's recovered key direction.
    // The no-reflection/no-refraction hardware-depth permutation scales SunDir Y by four before
    // normalizing (WATER003.pso asm 101-105: c0.zwzw = 1,4,1,4). Using the raw direction changed
    // the body response substantially whenever the sun had a non-zero Y component.
    float3 body = lerp(uShallow.rgb, uDeep.rgb, depthT);
    float3 fnvBodyLightDir = normalize(float3(sunDir.x, 4.0 * sunDir.y, sunDir.z));
    body *= saturate(dot(N, fnvBodyLightDir));
#endif

    // Reflected view vector — used for both the sky-reflection tint and the sun specular below.
    float3 R = reflect(-V, N);

    // The viewer has no planar-reflection or refraction targets. FNV therefore follows the recovered
    // WATER003 no-RT permutation, which stays on the authored DNAM ReflectionColor. Replacing it with
    // the atmosphere gradient made daytime water flat gray and made the remaining night signal
    // disappear with a dark sky. Oblivion's distinct recovered shader still interpolates its authored
    // color toward the sky-gradient stand-in below.
    float3 reflectedSky = uSkyTopSkyEnabled.w > 0.5
        ? lerp(uSkyHorizon.rgb, uSkyTopSkyEnabled.rgb, saturate(R.z))
        : uReflection.rgb;
#if OBLIVION_WATER
    // Engine: lerp(ReflectionColor, planarReflectionRT, Reflectivity). The viewer's sky-gradient
    // reflection is the RT-free stand-in for that RT, so the authored color remains the base term.
    float3 refl = lerp(uReflection.rgb, reflectedSky, reflectivity);
#else
    // WATER003.pso asm 106-107: lerp(body * NdotL, c4 ReflectionColor, fresnel). FresnelRI.w is
    // absent from that no-RT composite; it only scales WATER000's sampled reflection-target path.
    // Applying authored Reflectivity here suppressed Potomac's sole night signal by another 40%.
    float3 refl = uReflection.rgb;
#endif

    // FNV Schlick fresnel: F0 + (1-F0)*(1-NdotV)^5, F0 = FresnelAmount. Reflection over body.
    float F = F0 + (1.0 - F0) * pow(1.0 - ndotv, 5.0);
#if OBLIVION_WATER
    // VarAmounts.z — the engine's runtime fresnel FLOOR (max(VarAmounts.z, Schlick), WATER000
    // asm 139). DECOMPILE-RESOLVED (Oblivion.exe FUN_00499570 fills the global; FUN_004ed660 is
    // the getter; the console "set water opacity" handler FUN_0050d8e0 writes the same slot):
    // VarAmounts.z = WATR ANAM Opacity / 100 — per water type. Vanilla: DefaultWater 100 (fully
    // floored/opaque), dungeon/sewer/oil waters 85. The recovered max feeds alpha only; applying
    // it to the RGB Fresnel interpolation was the washed-out TES4 mismatch fixed here.
    float alphaFresnel = max(asfloat(uNoiseParams.w), saturate(F));
    float fresneled = saturate(F);
#else
    float fresneled = saturate(F);
#endif
    float3 color = lerp(body, refl, fresneled);

#if OBLIVION_WATER
    // Oblivion single specular: pow(dot(reflect(-V,N), SunDir), Sun Power) × SunColor — no
    // sky-glint term in WATER000.pso.
    float sunSpec = pow(saturate(dot(R, sunDir)), max(uSurface1.x, 1.0));
    color += sunSpec * sunCol * sunGate;

    // WATER000.pso asm 116-121/166: WATR TNAM is the separate DetailMap (s2), sampled at the
    // scrolling normal UV plus N.xy*0.1. Its exact blend factor is the UNSQUARED distance term
    // (1-horizontalDistance*0.000122) times VarAmounts.w = DATA.TextureBlend/100. The normal itself
    // uses the squared distance term above; conflating those two attenuations changes mid-distance
    // detail substantially. A missing/empty TNAM skips this term, matching retail DefaultWater.
    if (uNormalIndices.y != 0xFFFFFFFFu && uLegacySurface1.z != 0.0)
    {
        // TES4 detail tiles finer than the 2048-unit surface tile by ini [Water]
        // fTileTextureDivisor = 4.75 (2048/4.75 ≈ 431 wu per repeat — ini-inferred).
        float2 detailUv = input.vWorldPos.xy * (fMacro * 4.75) + uLegacySurface0.zw * t + N.xy * 0.1;
        float3 detail = gWaterTextures[NonUniformResourceIndex(uNormalIndices.y)]
            .Sample(gWaterSampler, detailUv).rgb;
        color = lerp(color, detail, oblivionLinearDistanceAtten * uLegacySurface1.z);
    }
#else
    // FNV dual specular: sharp sun glint off the reflected view vector + a fixed sky-glint term.
    float sunSpec = pow(saturate(dot(R, sunDir)), specExp);
    float skyGlint = pow(saturate(dot(float2(N.x, N.z), float2(-0.57, 0.82))), 100.0);
    color += (sunSpec + skyGlint) * sunCol * sunGate;
#endif

#if OBLIVION_WATER
    // Oblivion WATER000 shore alpha (pkg019 asm 139-173; def c13=(0.25,-0.2,-0.55), c12.x=1/0.35):
    //   alphaBase = lerp(0.25, max(VarAmounts.z, F), depth)
    //   s         = saturate(1 − (depth − 0.2)/0.35)
    //   alpha     = alphaBase · (1 − s³)            — 0 below 0.2 column, cubic shore fade to 0.55,
    //                                                 then ≈floored fresnel (largely opaque + reflective)
    // The engine's DepthMap is a dedicated 0..1 water-depth target normalized over the ini
    // [Water] uDepthRange = 125 world units (Oblivion_default.ini, adjacent to bUseWaterDepth=1 —
    // INI-derived; the depth-pass generator itself is undecompiled). `column` is a VIEW-SPACE depth
    // gap along the pixel ray, which exaggerates vertical water depth ~2-3× at typical pitches (the
    // old hand-calibrated /512 was silently compensating for that, and left genuinely shallow urban
    // water — IC canals are ≤ 362 units deep — permanently inside the shore-fade band, i.e. mostly
    // transparent). Convert to a VERTICAL column first (world z varies linearly with view depth
    // along a ray), then normalize by uDepthRange so the 0.2..0.55 shore band spans a fixed
    // 25..69 world units of water at every view angle. Without a scene-depth SRV there is no column
    // (the N·V proxy is unrelated and zero at grazing angles, which would erase the surface) —
    // fall back to deep-water behavior (aDepth = 1).
    float verticalColumn = column * abs(input.vWorldPos.z - uCamPosTime.z) / max(waterDist, 1e-3);
    float aDepth = (depthIndex == 0xFFFFFFFFu) ? 1.0 : saturate(verticalColumn / 125.0);
    float alphaBase = lerp(0.25, alphaFresnel, aDepth);
    float shoreS = saturate(1.0 - (aDepth - 0.2) * 2.857143);
    float alpha = alphaBase * (1.0 - shoreS * shoreS * shoreS);
#else
    // Refraction stand-in via destination blend: retail WATER001 composites the water body over
    // the REFRACTED scene (transmitted = lerp(refracted, litBody, depth weight), recovered in the
    // FNV_WATER001 branch above). The RT-free path has no refraction snapshot, but the destination
    // already holds the rendered scene — exactly what the refraction RT samples, minus distortion —
    // so blending by the same depth weight reproduces the transmitted term: the bed shows through
    // shallow water and fades out by DepthFalloff, while grazing angles stay reflective-opaque via
    // the fresnel term. Without a scene-depth SRV there is no water column (ortho/export paths):
    // keep the previous opaque output rather than inventing a coverage ramp from the N·V proxy.
    // The 0.15 floor keeps a visible surface film at the waterline (the engine's shallow tint
    // never reaches fully invisible).
    float alpha = (depthIndex == 0xFFFFFFFFu)
        ? 1.0
        : max(max(saturate(depthT), fresneled), 0.15);
#endif
#if OBLIVION_WATER
    // Oblivion WATER000 surface fog: the engine's payload filler (FUN_007dcbd0, decompiled in
    // tools/GhidraProject/oblivion_water_fog_source_decompiled.txt) fills c9 FogParam AND the fog
    // color from ONE struct — the SCENE fog property (color @+0x20, near @+0x2c, far @+0x30):
    //     FogParam.x = far;  FogParam.y = far − near;  visibility = saturate((far − d) / (far − near))
    // The WATR DATA fog range is the UNDERWATER range (never read by the per-frame WATR filler;
    // DefaultWater authors FogNear = −8192, which fog-washed the whole surface when used here —
    // "water looks like fog"). Keep the linear engine formula on the scene distances; do NOT use
    // ApplyFog (its powered Skyrim curve + far-color lerp is not the recovered TES4 composite).
    if (uFogColorFogEnabled.w > 0.5 && uAtmosphereParams.z > uAtmosphereParams.y)
    {
        float viewDist = length(input.vWorldPos - uCamPosTime.xyz);
        float visibility = saturate((uAtmosphereParams.z - viewDist) /
                                    max(uAtmosphereParams.z - uAtmosphereParams.y, 1.0));
        color = lerp(uFogColorFogEnabled.rgb, color, visibility);
    }
    return float4(color, alpha);
#else
    return float4(ApplyFog(color, input.vWorldPos), alpha);
#endif
#endif // !FO4_WATER
}
#endif // FNV_WATER001
