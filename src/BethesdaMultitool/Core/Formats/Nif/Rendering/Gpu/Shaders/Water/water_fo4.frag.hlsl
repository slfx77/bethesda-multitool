// FO4 water pixel shader — BSWaterShader (ps_5_0, Shaders011.fxp group 5), engine-exact math per
// tools/GhidraProject/fo4_water_pixel_shader_decompiled.txt. FO76_WATER_OPTICS is a deliberately
// separate, bounded reference approximation: its decoded WATR inputs and transmission equation are
// grounded in vendored fo76utils, but FO76's shipped BSWaterShader has not been recovered.
// FO4_WATER_ARCHITECTURAL is the remaining TECHNIQUE macro: it opts into the recovered
// dynamic-water architecture (explicit t0/t1/t2/t15 prepass resources, cascaded shadows, point
// lights, cubemap reflection) compiled lazily by ModernWaterResources12; the default compile is
// the established RT-free stand-in and the per-frame fallback.
// Selected by WaterProfile.PixelShaderFile for BethesdaGame.Fallout4 / Fallout76.

#include "water_common.hlsli"

#if FO76_WATER_OPTICS && FO4_WATER_ARCHITECTURAL
#error FO76_WATER_OPTICS is the direct three-normal path, not an architectural-prepass permutation.
#endif

#if FO76_WATER_OPTICS
struct Fo76WaterOutput
{
    // Target0 is already the water's source contribution. Target1 is not a second RTV: D3D12
    // dual-source blending consumes its RGB as the existing SceneColor destination factor.
    float4 SourceContribution : SV_Target0;
    float4 DestinationTransmission : SV_Target1;
};

float Fo76RelativeLayerScale(float4 layer)
{
    // FO76's absolute vertex-shader UV conversion is not recovered. Preserve only the authored
    // relative frequencies, normalized to layer 1, over the established world-tile fallback.
    float baseScale = max(abs(uLayer1.x), 1.0);
    return max(abs(layer.x), 1.0) / baseScale;
}

float2 Fo76LayerScroll(float4 layer, float t)
{
    float angle = radians(layer.y);
    return float2(sin(angle), cos(angle)) * (layer.z * t);
}

float3 SampleFo76AuthoredNormal(uint index, float2 uv)
{
    if (index == 0xFFFFFFFFu)
    {
        return float3(0.0, 0.0, 1.0);
    }

    // One texture sample per authored NAM2/NAM3/NAM4 input. Reconstructing Z from XY matches the
    // existing modern prepass without importing the classic six-sample macro/detail octave path.
    float2 xy = gWaterTextures[NonUniformResourceIndex(index)].Sample(gWaterSampler, uv).xy * 2.0 - 1.0;
    return float3(xy, sqrt(saturate(1.0 - dot(xy, xy))));
}

Fo76WaterOutput MakeFo76WaterOutput(float3 sourceContribution, float3 destinationTransmission)
{
    Fo76WaterOutput output;
    output.SourceContribution = float4(sourceContribution, 1.0);
    output.DestinationTransmission = float4(destinationTransmission, 0.0);
    return output;
}
#endif

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

// Architectural water consumes the exact same cascade filter as reference, terrain, and grass.
// Retarget only the resource names; shadow_sampling.hlsli owns both the legacy s3 path and the
// opt-in comparison-sampler s7 path so those implementations cannot drift again.
#define SHADOW_ATLAS gWaterTextures
#define SHADOW_SAMPLER gWaterShadowSampler
#include "shadow_sampling.hlsli"
#undef SHADOW_SAMPLER
#undef SHADOW_ATLAS

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

#if FO76_WATER_OPTICS
Fo76WaterOutput main(PSInput input)
#else
float4 main(PSInput input) : SV_Target
#endif
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
    float column = 0.0;    // view-space gap to the surface the water is OVER (only when sampled)
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
        // sceneNdc is the surface the water is layered OVER, with MSAA occluder samples excluded
        // (see LoadSceneDepth); occluderNdc is the unfiltered nearest, for the clip below only.
        float occluderNdc;
        float sceneNdc = LoadSceneDepth(
            depthIndex, (int2)input.Position.xy, depthSampleCount, input.Position.z, occluderNdc);
        float sceneDist = LinearizeDepth(sceneNdc, near, far);
        waterDist = LinearizeDepth(input.Position.z, near, far);
        column = sceneDist - waterDist;           // >0: water over a floor
#if !WATER_HARDWARE_OCCLUSION
        // 3D-2 tie-break: bias the occlusion test toward KEEPING the water (uDepthParams.w world units) so
        // a shoreline where water and terrain are ~coplanar (column ≈ 0 ± sub-ULP depth noise) resolves to
        // water instead of flickering. The bias is tiny vs DepthFalloff, so genuinely occluded water
        // (column far negative) is still discarded. Hardware-occlusion compiles skip this: the pixel-rate
        // binary clip aliases at MSAA'd mesh silhouettes, while the read-only DSV's GreaterEqual test
        // rejects per sample (and covers the same coplanar tie-break in hardware). This is the one
        // consumer that wants the UNFILTERED nearest sample — it asks "is anything in front of me?",
        // not "what am I over?" — so it tests occluderNdc rather than the shading column.
        clip(LinearizeDepth(occluderNdc, near, far) - waterDist + asfloat(uDepthParams.w));
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
#if FO76_WATER_OPTICS
        return MakeFo76WaterOutput(
            ApplyFog(lava * pulse * 1.3, input.vWorldPos),
            float3(0.0, 0.0, 0.0));
#else
        return float4(ApplyFog(lava * pulse * 1.3, input.vWorldPos), 1.0);
#endif
    }

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
#elif FO76_WATER_OPTICS
    // FO76 WATR authors three ordered normal textures plus direction, speed, amplitude and UV
    // scale for each. Sample every source exactly once. The world tile remains the explicitly
    // labeled fallback above because the absolute FO76 vertex transform is still unrecovered.
    float2 fo76BaseUv = input.vWorldPos.xy / max(uSurface0.x, 1.0);
    float3 fo76Normal1 = SampleFo76AuthoredNormal(
        uNormalIndices.x,
        fo76BaseUv * Fo76RelativeLayerScale(uLayer1) + Fo76LayerScroll(uLayer1, t));
    float3 fo76Normal2 = SampleFo76AuthoredNormal(
        uNormalIndices.y,
        fo76BaseUv * Fo76RelativeLayerScale(uLayer2) + Fo76LayerScroll(uLayer2, t));
    float3 fo76Normal3 = SampleFo76AuthoredNormal(
        uNormalIndices.z,
        fo76BaseUv * Fo76RelativeLayerScale(uLayer3) + Fo76LayerScroll(uLayer3, t));
    float3 fo76CombinedNormal =
        fo76Normal1 * uLayer1.w + fo76Normal2 * uLayer2.w + fo76Normal3 * uLayer3.w;
    fo76CombinedNormal.xy *= max(uModernLightSilt.w, 0.0);
    float3 N = normalize(float3(fo76CombinedNormal.xy, max(fo76CombinedNormal.z, 1e-4)));
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
#endif // normal-source permutation

    float ndotv = saturate(dot(N, V));

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
    float shadow = lit ? ShadowFactor(relativeWorldPos) : 1.0;

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
#if FO76_WATER_OPTICS
    // Exact float RGB from the typed 148-byte FO76 payload. This deliberately bypasses uShallow /
    // uDeep, whose values are only the old RGB8 compatibility projection of this same colour.
    float3 body = uFo4DarkSilt.rgb;
#else
    float colT = (depthIndex == 0xFFFFFFFFu)
        ? 1.0 - ndotv
        : pow(saturate(column / max(fo4RampUnits * uFo4Ranges.y, 1.0)), 0.454545);
    float3 body = lerp(uShallow.rgb, uDeep.rgb, colT);
#endif

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
#if FO76_WATER_OPTICS
    // FO76 has no DarkSilt lane: uFo4DarkSilt.rgb is BaseColor in this permutation. Use the
    // scene's authored ambient term instead of accidentally adding BaseColor as illumination.
    // The retained FO4 direct/backscatter/wrap/specular terms around it remain labeled stand-ins.
    float3 lighting = acc + (lit ? uAmbientColor.rgb : float3(1.0, 1.0, 1.0));
#else
    float3 lighting = acc + uFo4DarkSilt.rgb;
#endif

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
#if FO76_WATER_OPTICS
    // fo76utils transmission-only reference approximation (NOT Bethesda shader parity):
    // plot3d.cpp drawPixel_Water_G
    // uses exp2(-3 * depth/maxDepth - 0.75), then calculateLighting_Water transmits the existing
    // scene independently per channel by a * (1 - channelOpacity). The viewer has a linearized
    // view-space scene-depth gap rather than fo76utils' pixel-depth proxy; normalizing that gap by
    // DepthAmount is the explicitly bounded approximation made here, not an engine-derived mapping.
    // With no scene-depth SRV, choose the shallow endpoint (column = 0) rather than inventing a
    // view-angle-as-depth mapping. The fixed-function blend then evaluates exactly:
    //   final.rgb = sourceContribution + previousScene.rgb * destinationTransmission.
    // This intentionally stops at the requested transmission slice. fo76utils subsequently applies
    // a Fresnel environment lerp to that mixture; the additive reflection/specular below remain the
    // viewer's labeled FO4-family stand-ins, so this is NOT full fo76utils lighting parity either.
    float fo76DepthFraction = depthIndex == 0xFFFFFFFFu
        ? 0.0
        : saturate(max(column, 0.0) / max(uFo4DarkSilt.w, 0.125));
    float fo76Transparency = exp2(-3.0 * fo76DepthFraction - 0.75);
    float3 destinationTransmission =
        fo76Transparency * (1.0 - uModernLightSilt.rgb);
    float3 sourceContribution =
        body * lighting * (1.0 - destinationTransmission) +
        reflSky * reflectivity * lighting + spec * sunCol;
    return MakeFo76WaterOutput(
        ApplyFog(sourceContribution, input.vWorldPos),
        saturate(destinationTransmission));
#else
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
#endif // FO76_WATER_OPTICS
#endif // FO4_WATER_ARCHITECTURAL
}
