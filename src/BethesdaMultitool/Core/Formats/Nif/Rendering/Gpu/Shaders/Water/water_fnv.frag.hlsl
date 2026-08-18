// v3 water fragment shader (FNV/FO3, the Skyrim path, and the binary-RE-only default fallback)
// — a faithful port of FNV's PC water pixel shader (WATER000.pso), disassembled from
// Data/Shaders/shaderpackage019.sdp with fxc /dumpbin. Full analysis + raw disassembly:
// tools/GhidraProject/water_pc_pixel_shader_decompiled.txt.
//
// Per-game divergences live in sibling files selected by WaterProfile.PixelShaderFile
// (water_oblivion / water_fo4 / water_morrowind .frag.hlsl); water_fnv001.frag.hlsl is FNV's
// separately compiled WATER001 opaque-snapshot refraction program. Preprocessor macros encode
// TECHNIQUE axes only (WATER_HARDWARE_OCCLUSION here).
//
// The engine shader composites: reflection RT, refraction RT, a 2-channel depth-map water-column
// factor, a single NNAM normal tap, a Schlick fresnel, a dual sun/sky specular, and distance fog.
// The viewer has no reflection/refraction targets, so this program follows the engine's WATER003
// RT-free alpha/body/fog lanes exactly, with ONE deliberate upgrade toward the default-settings
// WATER000 look:
//   * reflection  == the b3 sky gradient along the reflected ray x FresnelRI.w Reflectivity while
//                    the sky is live (stand-in for WATER000's planar ReflectionMap sample, which
//                    is where ALL of retail water's blue comes from); the sky-off fallback stays
//                    on WATER003's unscaled ReflectionColor constant
//   * refraction  == the destination blend (the framebuffer already holds the rendered scene the
//                    refraction RT would sample). Coverage is WATER000's composite solved for that
//                    blend: alpha = 1 - (1 - Fe)(1 - W), i.e. the fog weight AND the Fresnel
//                    reflection both hide the bed. Using WATER003's alpha (the fog weight alone)
//                    left grazing views transparent where retail is opaque.
//   * depthT      == the DepthFalloff shoreline feather from the reconstructed depth-writer
//                    channels when the host binds the D32 depth as an SRV; else a view-angle proxy
// The Fresnel form (Schlick, F0 = DNAM FresnelAmount, exponent 5), the Shallow->Deep-by-depth body,
// the ReflectionColor mix scaled by ReflectivityAmount, and the dual specular are the engine's exact
// math. NNAM is a NORMAL map (unpack xy = rgb*2-1, rebuild z); sampling it as color is the classic
// rainbow-water bug. Scene sun (SunDir/SunColor) is a runtime uniform the viewer lacks, so a fixed
// sun stands in. Absolute tile size + wind-speed unit live in the engine vertex shader (un-recovered),
// so they remain tunable constants.

#include "water_common.hlsli"

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
    // DNAM@20 ReflectivityAmount = WATER000's c8.y VarAmounts.y: the LERP WEIGHT from the authored
    // ReflectionColor toward the reflection RT. It is NOT FresnelRI.w (that is DNAM@160
    // ReflectionHDRMult, clamped >= 1.0 by the engine and therefore never a darkening term).
    float reflectivity = saturate(uSurface0.z);
    float specExp = max(uSurface0.w, 1.0);       // FNV VarAmounts.x (sun-specular exponent)

    float3 eye = uCamPosTime.xyz - input.vWorldPos; // surface -> camera (FNV EyePos - worldPos)
    float distXY = length(eye.xy);
    float3 V = normalize(eye);

    // FNV distance fade of ripples: full within 4096 world units, -> 0 at 8192. Also the engine's
    // far ramp in the corrected-depth lanes below (WATER003 `lrp_sat r3.xy, -r0.w, c7.w, r0`).
    float noiseFade = saturate((8192.0 - distXY) / 4096.0);

    // FNV water-column inputs. The engine renders underwater geometry into a 2-channel DepthMap
    // (SM3027.pso and the other SLS/NOLIGHT "WF" writers in shaderpackage019): oC0.x = slant path
    // |P - W| below the surface, oC0.y = perpendicular (vertical) column, both divided by
    // FogParams.x = the ACTIVE water FogFar (the above-water DNAM@36 whenever the camera is above
    // the surface — TESWaterSystem::UpdateWaterShaderProperties only switches to the UnderWater
    // trio when the camera itself is submerged). WATER003.pso then derives two DIFFERENT lanes:
    //   depthT = sat((D.y - DepthFalloffStart)/(DepthFalloffEnd - DepthFalloffStart))  [asm 66-70]
    //            — a SHORELINE FEATHER (~9 world units for NVCleanWater), NOT the transparency
    //            curve; it feathers alpha/ripple at the waterline only.
    //   corrD  = sat(lerp(1, D.xy, noiseFade))                                        [asm 82]
    //            — the body/reflection/fog inputs, pinned to "deep" beyond the ripple fade.
    // We reconstruct both writer channels from the scene-depth SRV via the view ray. Previous
    // bugs, in order: clamping the RAW world-unit ray gap against DepthFalloffEnd (0.0109,
    // normalized units) made all water opaque within ~1 cm; normalizing by UnderwaterFogFar
    // (5500) still completed the fade at ~60 units AND drove body/alpha from the feather instead
    // of the above-water fog lanes — still "essentially impossible to see through".
    // Occlusion against nearer opaque geometry: WATER_HARDWARE_OCCLUSION compiles rely on the
    // read-only DSV's per-sample GreaterEqual test (antialiased silhouettes); legacy compiles
    // discard here at pixel rate. Falls back to a view-angle proxy when no depth SRV is set.
    uint depthIndex = uDepthParams.x;
    float depthT;
    float2 corrD;          // engine corrected-depth (slant, vertical) lanes in FogFar units
    float column = 0.0;    // view-space gap to the surface the water is OVER (only when sampled)
    float fogNear = uLegacySurface1.x;                    // DNAM@32 above-water FogNear (NVCleanWater -80)
    float fogFar = max(uLegacySurface1.y, fogNear + 1.0); // DNAM@36 above-water FogFar (NVCleanWater 850)
    if (depthIndex == 0xFFFFFFFFu)
    {
        depthT = saturate(dot(float3(0, 0, 1), V));
        corrD = float2(1.0, depthT); // no depth info: full reflection edge + view-angle body proxy
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
        float waterDist = LinearizeDepth(input.Position.z, near, far);
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
        // Reconstruct the depth-writer channels: scene point along the view ray, then the slant
        // and vertical water columns between it and the surface point, in FogFar units.
        float rayScale = sceneDist / max(waterDist, 1e-4);
        float3 scenePoint = uCamPosTime.xyz + (input.vWorldPos - uCamPosTime.xyz) * rayScale;
        float slantColumn = max(length(scenePoint - input.vWorldPos), 0.0);
        float verticalColumn = max(input.vWorldPos.z - scenePoint.z, 0.0);
        float2 D = float2(slantColumn, verticalColumn) / fogFar;
        corrD = saturate(lerp(float2(1.0, 1.0), D, noiseFade));
        float start = uSurface1.y;                 // DepthFalloffStart (D.y-normalized units)
        float end = uSurface1.z;                   // DepthFalloffEnd
        if (end <= start)
        {
            // Engine sentinel: equal Start/End uploads DepthFalloff = (-1, 0), disabling the feather.
            start = -1.0;
            end = 0.0;
        }
        depthT = saturate((D.y - start) / (end - start));
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

    float ndotv = saturate(dot(N, V));


    // FNV body color: lerp(Shallow, Deep, corrD.y) — the VERTICAL fog-lane column, not the
    // shoreline feather (WATER003.pso asm 97-99 `mad_pp r2.xyz, r3.y, r2, c2` with r3.y = corrD.y).
    // Driving this from depthT deepened the body to full Deep within the ~9-unit feather. Lit by
    // WATER003's recovered key direction: the no-reflection/no-refraction hardware-depth
    // permutation scales SunDir Y by four before normalizing (asm 101-105: c0.zwzw = 1,4,1,4).
    float3 body = lerp(uShallow.rgb, uDeep.rgb, corrD.y);
    float3 fnvBodyLightDir = normalize(float3(sunDir.x, 4.0 * sunDir.y, sunDir.z));
    body *= saturate(dot(N, fnvBodyLightDir));

    // Reflected view vector — used for both the sky-reflection tint and the sun specular below.
    float3 R = reflect(-V, N);

    // Reflection term. Retail at DEFAULT PC settings runs WATER000, whose reflection is
    //   refl = lerp(ReflectionColor, ReflectionMap_RT, VarAmounts.y) * FresnelRI.w
    // (WATER000.asm 109-111 `add r0.xyw, r7.xyzz, -c4.xyzz` / `mov r6.xyz, c4` /
    // `mad_pp r0.xyw, c8.y, r0, r6.xyzz`, then 143 `mad r0.xyz, r0.xyww, c5.w, -r1`).
    //
    // c8.y = VarAmounts.y = DNAM@20 **ReflectivityAmount** — a LERP WEIGHT between the authored
    // constant and the mirror, NOT a brightness multiplier. c5.w = FresnelRI.w is a different field
    // entirely, DNAM@160 ReflectionHDRMult, which the engine clamps to >= 1.0
    // (`fsel f0,f3,f31,f4` at 0x82A71860 == max(HDRMult*0.1, 1.0)), so it can never darken.
    // Treating ReflectivityAmount as that multiplier turned NVCleanWaterNoReflect
    // (ReflectivityAmount = 0, the WATR over the Colorado) into a BLACK reflection lane, and with
    // FresnelAmount = 0.75 that lane owns ~79% of the pixel — the reported near-black water.
    // Retail at reflectivity 0 simply falls back to the authored ReflectionColor, whose luminance
    // is close to Deep, giving a flat view-independent green rather than a mirror.
    //
    // RT-free stand-in for the sampled sky: the b3 atmosphere gradient along the reflected ray.
    // WATER003 (no RT) carries neither c8.y nor c5.w and uses the bare constant (asm 106-107), which
    // is what the sky-off/night path keeps.
    float3 skyReflectionStandIn = SampleSkyReflection(input.Position.xy, N, R);
    float3 refl = uSkyTopSkyEnabled.w > 0.5
        ? lerp(uReflection.rgb, skyReflectionStandIn, reflectivity)
        : uReflection.rgb;

    // FNV Schlick fresnel: F0 + (1-F0)*(1-NdotV)^5, F0 = FresnelAmount — then gated by the engine's
    // shoreline edge factor corrD.x (WATER000 asm 95+151 `mul r0.w, r4.x, r3.x`; WATER003 asm 96
    // `mul r0.y, r3.x, r1.w`): reflections fade out over thin water, so looking down into shallows
    // shows the green body + bed, while grazing/deep views become the sky mirror.
    float F = F0 + (1.0 - F0) * pow(1.0 - ndotv, 5.0);
    float fresneled = saturate(F * corrD.x);

    // FNV dual specular: sharp sun glint off the reflected view vector + a fixed sky-glint term.
    float sunSpec = pow(saturate(dot(R, sunDir)), specExp);
    float skyGlint = pow(saturate(dot(float2(N.x, N.z), float2(-0.57, 0.82))), 100.0);
    float3 spec = (sunSpec + skyGlint) * sunCol * sunGate;

    // Fog weight W — WATER000.pso asm 136-141 (identical in WATER003, where it IS the alpha):
    //   W = depthT * (1 - sat(FogParam.z * (1 - corrD.x) / FogParam.w)) * FogColor.w
    // FogParam.z = active water FogFar, FogParam.w = FogFar - FogNear, FogColor.w = the WATR
    // above-water Fog Amount (TESWaterSystem::UpdateWaterShaderProperties fills the trio from
    // DNAM@36/@32/@132 while the camera is above the surface). Substituting D.x = slant/FogFar
    // collapses it to canonical linear fog of the slant water path over -80..850 world units,
    // capped at 0.75, for NVCleanWater.
    float aboveFog = 1.0 - saturate(fogFar * (1.0 - corrD.x) / max(fogFar - fogNear, 1e-3));
    float W = saturate(depthT * aboveFog * saturate(uFnvWater001Surface.z));

    // DEGENERATE FOG RANGE. Interior WATRs commonly author FogNear == FogFar (retail's
    // DefaultInteriorWater uses 1000/1000), which makes the ramp above collapse to zero for every
    // pixel. Retail tolerates that because WATER000/001 are OPAQUE draws whose transparency comes
    // from the RefractionMap — a zero fog weight there just means "no fog tint over the refraction".
    // We turn W into COVERAGE, so the same data would erase the water entirely (alpha ~= fresnel
    // ~= 0.003). Fall back to the authored WATR ANAM opacity, which is exactly the "how solid is
    // this water" value the record carries for that case. Exterior records never reach this branch
    // (NVCleanWater's -80..850 range yields W ~= 0.66), so Lake Mead is unaffected.
    if (fogFar - fogNear <= 1.0)
    {
        W = saturate(depthT * saturate(asfloat(uNoiseParams.w)));
    }

    // COMPOSITE. FNV at default PC settings runs WATER000, an OPAQUE draw (asm 165
    // `mov oC0.w, v6.w`) whose entire see-through comes from sampling the RefractionMap in RGB:
    //     final = lerp( lerp(Rf, litBody, W), Refl, Fe ) + Sp
    // with Rf the de-fogged refraction sample (asm 124-128) and Fe = corrD.x * fresnel. Expanded:
    //     final = (1-Fe)(1-W)·Rf + (1-Fe)·W·litBody + Fe·Refl + Sp
    // Our destination already holds Rf (the scene behind the surface), so matching the Rf
    // coefficient against straight SrcAlpha/InvSrcAlpha blending gives
    //     alpha = 1 - (1-Fe)(1-W)
    //     src   = ((1-Fe)·W·litBody + Fe·Refl + Sp) / alpha
    // NOTE this is deliberately NOT WATER003's alpha (= W alone, asm 124-129). WATER003 is the
    // permutation for when BOTH targets are unavailable; porting its coverage while our reflection
    // term is still present drops the reflection's share of the pixel, and the bed shows through at
    // grazing angles where retail is opaque — measured: the Colorado read as a faint tint over a
    // fully visible riverbed.
    //
    // Sp is added to the NUMERATOR only and must never enter alpha. SunPower reaches 826 on these
    // records, so `pow(dot(R,sunDir), specExp)` is a razor-sharp lobe that flips between 0 and 1 as
    // the animated ripple normal moves; as a coverage term it swung alpha per-frame on glinting
    // pixels and the premultiplied divide amplified it into a visible shimmer. Retail adds the glint
    // in RGB over an already-opaque pixel and so cannot produce that.
    float alpha = saturate(1.0 - (1.0 - fresneled) * (1.0 - W));
    float3 premultiplied = (1.0 - fresneled) * W * body + fresneled * refl + spec;
    // Without a scene-depth SRV there is no water column (ortho/export paths): keep the opaque
    // output rather than inventing coverage from the view-angle proxy.
    if (depthIndex == 0xFFFFFFFFu)
    {
        return float4(ApplyFog(lerp(body, refl, fresneled) + spec, input.vWorldPos), 1.0);
    }
    return float4(ApplyFog(premultiplied / max(alpha, 1e-4), input.vWorldPos), alpha);
}
