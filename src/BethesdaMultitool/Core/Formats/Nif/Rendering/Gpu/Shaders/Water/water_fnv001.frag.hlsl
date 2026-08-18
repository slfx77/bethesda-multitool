// FNV WATER001 — the separately compiled, conservative approximation of retail's
// no-reflection-RT / refraction+noise+depth+fog permutation. It samples a host-owned
// opaque-scene snapshot and never changes the fallback permutation: water_fnv.frag.hlsl remains
// the WATER003 RT-free program, and this file's per-pixel fallback (FnvWater003LocalFallback)
// compiles the same math locally. Ships only as a WATER_HARDWARE_OCCLUSION build (the snapshot
// path requires the read-only DSV); the guard is kept so the clip reads identically to the
// shared depth block.

#include "water_common.hlsli"

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
    // The gap to the UNFILTERED nearest depth sample (see LoadSceneDepth). Purely an occlusion and
    // validity input here — every shading lane arrives already resolved in depthT/corrD — so it
    // must stay the occluder rather than the surface the water is over.
    float occluderGap,
    float depthT,
    float2 corrD,
    bool hasSceneDepth,
    float3 sunDir,
    float3 sunCol,
    float sunGate)
{
    // Same manual foreground rejection and shader math as the normal FNV WATER003 route. This
    // function is compiled only into the WATER001 program; the standalone WATER003 bytecode
    // (water_fnv.frag.hlsl) remains its own compile and is not rewritten by this feature.
    if (!isfinite(occluderGap) || !isfinite(depthT) || !all(isfinite(corrD)) ||
        !all(isfinite(perturbation)) || !all(isfinite(V)))
    {
        // A nonfinite main-depth sample cannot support either safe foreground rejection or the
        // WATER003 depth fade. Fail closed instead of relying on undefined clip(NaN) behavior.
        clip(-1.0);
        return float4(0.0, 0.0, 0.0, 0.0);
    }
#if !WATER_HARDWARE_OCCLUSION
    clip(occluderGap + asfloat(uDepthParams.w));
#endif
    float noiseFade = saturate((8192.0 - distXY) / 4096.0);
    float3 n3 = perturbation * depthT;
    n3.z += 1.0;
    n3.xy *= noiseFade;
    float3 N = normalize(n3);
    float ndotv = saturate(dot(N, V));

    // Body by the vertical fog-lane column, reflection gated by the slant edge lane — the same
    // WATER003 lanes as the standalone program (see water_fnv.frag.hlsl for the asm citations).
    float3 body = lerp(uShallow.rgb, uDeep.rgb, corrD.y);
    float3 bodyLightDirection = normalize(float3(sunDir.x, 4.0 * sunDir.y, sunDir.z));
    body *= saturate(dot(N, bodyLightDirection));
    float fresnel = saturate(uSurface0.y) +
        (1.0 - saturate(uSurface0.y)) * pow(1.0 - ndotv, 5.0);
    // WATER000 asm 109-111: refl = lerp(ReflectionColor, ReflectionMap_RT, c8.y), where
    // c8.y = VarAmounts.y = DNAM@20 ReflectivityAmount is a LERP WEIGHT, not a multiplier.
    // Multiplying by it instead made every ReflectivityAmount = 0 record (NVCleanWaterNoReflect)
    // reflect pure black. WATER003's unscaled ReflectionColor otherwise (night/sky-off preserved).
    float3 reflectedView = reflect(-V, N);
    float3 refl = uSkyTopSkyEnabled.w > 0.5
        ? lerp(uReflection.rgb,
               SampleSkyReflection(input.Position.xy, N, reflectedView),
               saturate(uSurface0.z))
        : uReflection.rgb;
    float fresneled = saturate(fresnel * corrD.x);

    float sunSpec = pow(saturate(dot(reflectedView, sunDir)), max(uSurface0.w, 1.0));
    float skyGlint = pow(saturate(dot(float2(N.x, N.z), float2(-0.57, 0.82))), 100.0);
    float3 spec = (sunSpec + skyGlint) * sunCol * sunGate;

    // Same WATER000-solved-for-destination-blend composite as the standalone program (see
    // water_fnv.frag.hlsl for the full derivation): the fog weight W and the Fresnel reflection
    // BOTH become coverage, because our destination holds the scene retail would have sampled
    // from its RefractionMap. WATER003's alpha (W alone) was the too-transparent bug.
    float fogNear = uLegacySurface1.x;
    float fogFar = max(uLegacySurface1.y, fogNear + 1.0);
    float aboveFog = 1.0 - saturate(fogFar * (1.0 - corrD.x) / max(fogFar - fogNear, 1e-3));
    float W = saturate(depthT * aboveFog * saturate(uFnvWater001Surface.z));
    if (fogFar - fogNear <= 1.0)
    {
        // Degenerate authored fog range (interior WATRs author FogNear == FogFar) — fall back to
        // the WATR ANAM opacity so the water does not vanish. See water_fnv.frag.hlsl.
        W = saturate(depthT * saturate(asfloat(uNoiseParams.w)));
    }
    float specCoverage = saturate(max(spec.r, max(spec.g, spec.b)));
    float alpha = saturate(1.0 - (1.0 - fresneled) * (1.0 - W) * (1.0 - specCoverage));
    if (!hasSceneDepth)
    {
        // No column information (ortho/export paths): keep the opaque output.
        return float4(ApplyFog(lerp(body, refl, fresneled) + spec, input.vWorldPos), 1.0);
    }
    float3 premultiplied = (1.0 - fresneled) * W * body + fresneled * refl + spec;
    return float4(ApplyFog(premultiplied / max(alpha, 1e-4), input.vWorldPos), alpha);
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
    // Eligibility probe: deliberately the FRONTMOST opaque sample, not the surface the water is
    // over. Declaring a tap underwater while an occluder covers part of the pixel would admit
    // WATER001 for geometry that is in front of the water, so this one stays conservative.
    float sceneNdc = LoadNearestSceneDepth(depthIndex, pixel, depthSampleCount);
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
    // sceneNdc is the surface the water is layered OVER, with MSAA occluder samples excluded (see
    // LoadSceneDepth), so it drives the shading lanes below; occluderNdc keeps the unfiltered
    // nearest for the fail-closed foreground rejection inside FnvWater003LocalFallback. Both stay
    // at the reversed-Z far plane (0) with no depth SRV bound, exactly as the ternary here did.
    float occluderNdc = 0.0;
    float sceneNdc = 0.0;
    if (depthIndex != 0xFFFFFFFFu)
    {
        sceneNdc = LoadSceneDepth(
            depthIndex, (int2)input.Position.xy, depthSampleCount, input.Position.z, occluderNdc);
    }
    float sceneDistance = LinearizeDepth(sceneNdc, near, far);
    float waterDistance = LinearizeDepth(input.Position.z, near, far);
    float occluderGap = LinearizeDepth(occluderNdc, near, far) - waterDistance;
    // Same engine depth-writer lanes as the standalone WATER003 program (see water_fnv.frag.hlsl):
    // slant + vertical water columns normalized by the ACTIVE (above-water) FogFar — the engine
    // only switches to the UnderWater fog trio when the camera itself is submerged, which this
    // route never renders. Normalizing by UnderwaterFogFar (5500) was the "still can't see
    // through the water" bug: it drove body/alpha from the wrong constants entirely.
    bool hasSceneDepth = depthIndex != 0xFFFFFFFFu;
    float aboveFogNear = uLegacySurface1.x;                         // DNAM@32 above-water FogNear
    float aboveFogFar = max(uLegacySurface1.y, aboveFogNear + 1.0); // DNAM@36 above-water FogFar
    float noiseFade = saturate((8192.0 - distXY) / 4096.0);
    float fallbackRayScale = sceneDistance / max(waterDistance, 1e-4);
    float3 fallbackScenePoint = uCamPosTime.xyz +
        (input.vWorldPos - uCamPosTime.xyz) * fallbackRayScale;
    float2 fallbackD = float2(
        max(length(fallbackScenePoint - input.vWorldPos), 0.0),
        max(input.vWorldPos.z - fallbackScenePoint.z, 0.0)) / aboveFogFar;
    float2 fallbackCorrD = hasSceneDepth
        ? saturate(lerp(float2(1.0, 1.0), fallbackD, noiseFade))
        : float2(1.0, 1.0);
    float fallbackDepthT = hasSceneDepth
        ? saturate((fallbackD.y - uSurface1.y) / max(uSurface1.z - uSurface1.y, 1e-6))
        : saturate(dot(float3(0.0, 0.0, 1.0), V));
    float3 perturbation = FnvWater001Perturbation(noiseIndex, input.vWorldPos.xy, t);

    uint snapshotIndex = uFnvWater001Snapshot.x;
    float planeHeight = asfloat(uFnvWater001Snapshot.w);
    float aboveWaterFogAmount = uFnvWater001Surface.z;
    float distortionAmount = uFnvWater001Surface.w;

    // Reconstruct the exact depth-writer lanes from the main perspective depth ray:
    //   P = E + (W-E)*(sceneDist/waterDist)
    //   D.x = |P-W|/FogFar, D.y = dot(W-P,+Z)/FogFar   (FogFar = the active above-water DNAM@36)
    // No saturation occurs here; WATER001 applies its recovered distance correction below.
    bool validDepth = hasSceneDepth && snapshotIndex != 0xFFFFFFFFu &&
        sceneNdc > 0.0 && sceneNdc <= 1.0 &&
        isfinite(sceneDistance) && isfinite(waterDistance) && waterDistance > 0.0 &&
        sceneDistance > waterDistance && isfinite(aboveFogFar) && aboveFogFar > 0.0;
    float rayScale = sceneDistance / waterDistance;
    float3 scenePoint = uCamPosTime.xyz + (input.vWorldPos - uCamPosTime.xyz) * rayScale;
    float2 rawDepth = float2(
        length(scenePoint - input.vWorldPos) / aboveFogFar,
        dot(input.vWorldPos - scenePoint, float3(0.0, 0.0, 1.0)) / aboveFogFar);
    validDepth = validDepth && all(isfinite(scenePoint)) && all(isfinite(rawDepth)) &&
        abs(input.vWorldPos.z - planeHeight) <= 1e-3 && scenePoint.z < planeHeight &&
        rawDepth.x >= 0.0 && rawDepth.y > 0.0;
    if (!validDepth)
    {
        return FnvWater003LocalFallback(
            input, perturbation, V, distXY, occluderGap, fallbackDepthT, fallbackCorrD,
            hasSceneDepth, sunDir, sunCol, sunGate);
    }

    float depthT = saturate((rawDepth.y - uSurface1.y) / max(uSurface1.z - uSurface1.y, 1e-6));
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
            input, perturbation, V, distXY, occluderGap, fallbackDepthT, fallbackCorrD,
            hasSceneDepth, sunDir, sunCol, sunGate);
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
            input, perturbation, V, distXY, occluderGap, fallbackDepthT, fallbackCorrD,
            hasSceneDepth, sunDir, sunCol, sunGate);
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

    float aboveFogRange = aboveFogFar - aboveFogNear;
    bool validWaterFog = isfinite(aboveFogNear) && isfinite(aboveFogRange) &&
        aboveFogRange > 0.0 && isfinite(aboveWaterFogAmount) &&
        aboveWaterFogAmount >= 0.0 && aboveWaterFogAmount <= 1.0;
    if (!validWaterFog)
    {
        return FnvWater003LocalFallback(
            input, perturbation, V, distXY, occluderGap, fallbackDepthT, fallbackCorrD,
            hasSceneDepth, sunDir, sunCol, sunGate);
    }
    float aboveWaterFog = (1.0 - saturate(
        aboveFogFar * (1.0 - correctedDepth.x) / aboveFogRange)) *
        aboveWaterFogAmount;
    float3 transmitted = lerp(refracted, litBody, depthT * aboveWaterFog);

    float ndotv = saturate(dot(N, V));
    float oneMinusNdotV = 1.0 - ndotv;
    float fresnel5 = oneMinusNdotV * oneMinusNdotV;
    fresnel5 *= fresnel5;
    fresnel5 *= oneMinusNdotV;
    float fresnel = saturate(uSurface0.y) +
        (1.0 - saturate(uSurface0.y)) * fresnel5;
    // WATER000 asm 109-111 `mad_pp r0.xyw, c8.y, r0, r6.xyzz`:
    //   refl = lerp(ReflectionColor, ReflectionMap_RT, VarAmounts.y)
    // with VarAmounts.y = DNAM@20 ReflectivityAmount as a BLEND WEIGHT toward the mirror. Retail at
    // default settings samples the planar ReflectionMap here — the sole source of retail water's
    // blue, since every NVCleanWater DNAM colour is green — so the sky gradient stands in for the
    // RT. A record authoring ReflectivityAmount = 0 therefore keeps its authored ReflectionColor
    // rather than reflecting black. Deliberate upgrade beyond the retail WATER001 program, which
    // stays on the constant (see the water_fnv.frag.hlsl header for the asm evidence).
    float3 reflectedView = reflect(-V, N);
    float3 reflTerm = uSkyTopSkyEnabled.w > 0.5
        ? lerp(uReflection.rgb,
               SampleSkyReflection(input.Position.xy, N, reflectedView),
               saturate(uSurface0.z))
        : uReflection.rgb;
    float3 bodyReflection = lerp(litBody, reflTerm, correctedDepth.x * fresnel);
    float3 color = lerp(transmitted, bodyReflection, correctedDepth.y);
    float sunSpec = pow(saturate(dot(reflectedView, sunDir)), max(uSurface0.w, 1.0));
    float skyGlint = pow(saturate(dot(float2(N.x, N.z), float2(-0.57, 0.82))), 100.0);
    color = saturate(color + (sunSpec + skyGlint) * sunCol * sunGate);

    float finalFog = FnvWater001SceneFogAmount(eyeDistance);
    color = lerp(color, uFogColorFogEnabled.rgb, finalFog);
    return float4(color, 1.0);
}
