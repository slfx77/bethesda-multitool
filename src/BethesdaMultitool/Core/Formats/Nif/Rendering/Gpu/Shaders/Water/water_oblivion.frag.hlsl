// Oblivion water pixel shader — WATER000.pso math where it genuinely diverges from the shared
// FNV WATER000 port (tools/GhidraProject/oblivion_water_pixel_shader_decompiled.txt): the body
// color blends Deep→Shallow by the VIEW ANGLE (N·V), not the depth column, and the specular is a
// single sun glint (no sky-glint term). Everything else (Schlick fresnel with F0 = WATR Fresnel
// Amount, ReflectionColor→reflection-target interpolation, distance fog) is shared with the
// recovered WATER000 family — see water_fnv.frag.hlsl for the shared-path derivation notes.
// Selected by WaterProfile.PixelShaderFile for BethesdaGame.Oblivion.

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

    // (An FNV noiseFade ramp used to be declared here and never read. TES4 has no counterpart term:
    //  its far-field flattening IS the squared 1 - distXY/8192 attenuation applied below, which
    //  reaches zero at the same 8192-unit envelope by a different expression.)

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
    // Ripple distance attenuation, ported exactly from WATER000.pso (oblivion_water_pkg019.asm:
    //   def c14, 2.0, -1.0, 0.0, -0.000122
    //   mad r2.w, dist, c14.w, -c14.y   -> 1 - dist*0.000122
    //   mul r3.w, r2.w, r2.w            -> squared
    //   mul r0.xy, r3.w, r0             -> perturbation.xy *= atten
    // 0.000122 is 1/8192 to the disassembler's 6 decimal places, so the linear term reaches ZERO at
    // exactly 8192 world units — the same two-cell envelope FNV expresses as its noiseFade ramp.
    //
    // The asm carries no _sat on any of those three instructions, and retail never needs one: its
    // water grid and fog far plane mean distXY cannot exceed the envelope. This viewer permits a
    // 34,000-unit aerial camera, where the UNSATURATED term crosses zero and then grows without
    // bound — at the frame edge of the reported top-down pose (distXY ~ 25,000) it reaches ~4.2, so
    // an encoded +-0.42 perturbation became +-1.7 and normalize() tilted the surface normal up to
    // ~72 degrees from vertical, lighting the pow(...,SunPower) lobe across the whole far field.
    // saturate() is therefore an OUT-OF-ENVELOPE GUARD, not recovered engine math: inside retail's
    // envelope it is bit-identical to the asm, and outside it clamps to the engine's own zero
    // instead of extrapolating. It also fixes the detail-map blend below, whose weight uses the
    // UNSQUARED term and so went NEGATIVE past 8192 (colour extrapolation).
    float oblivionLinearDistanceAtten = saturate(1.0 - distXY * 0.000122);
    float oblivionDistanceAtten = oblivionLinearDistanceAtten * oblivionLinearDistanceAtten;
    pert.xy *= oblivionDistanceAtten;
    float3 N = normalize(pert);

    float ndotv = saturate(dot(N, V));


    // Oblivion body color: lerp(Deep, Shallow, N·V) — the view ANGLE picks the body tint
    // (grazing = deep, top-down = shallow); Oblivion's PS has no depth-column body term and no
    // body sun modulation (sun enters via the specular only).
    float3 body = lerp(uDeep.rgb, uShallow.rgb, ndotv);

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
    // Engine: lerp(ReflectionColor, planarReflectionRT, Reflectivity). The viewer's sky-gradient
    // reflection is the RT-free stand-in for that RT, so the authored color remains the base term.
    float3 refl = lerp(uReflection.rgb, reflectedSky, reflectivity);

    // FNV Schlick fresnel: F0 + (1-F0)*(1-NdotV)^5, F0 = FresnelAmount. Reflection over body.
    float F = F0 + (1.0 - F0) * pow(1.0 - ndotv, 5.0);
    // VarAmounts.z — the engine's runtime fresnel FLOOR (max(VarAmounts.z, Schlick), WATER000
    // asm 139). DECOMPILE-RESOLVED (Oblivion.exe FUN_00499570 fills the global; FUN_004ed660 is
    // the getter; the console "set water opacity" handler FUN_0050d8e0 writes the same slot):
    // VarAmounts.z = WATR ANAM Opacity / 100 — per water type. Vanilla: DefaultWater 100 (fully
    // floored/opaque), dungeon/sewer/oil waters 85. The recovered max feeds alpha only; applying
    // it to the RGB Fresnel interpolation was the washed-out TES4 mismatch fixed here.
    float alphaFresnel = max(asfloat(uNoiseParams.w), saturate(F));
    float fresneled = saturate(F);
    float3 color = lerp(body, refl, fresneled);

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
        // SAME UV SCALE as the NormalMap — re-verified against the asm 2026-08-07:
        //   asm 26: add   r2.xy, a6, c0/*Scroll*/          <- the NormalMap UV
        //   asm 27: texld r0, r2, s1/*NormalMap*/
        //   asm 40: mad   r1.xy, r3/*N*/, c4.xxxx, r2      <- DetailMap UV = N.xy*0.1 + THAT SAME r2
        //   asm 45: texld r1, r1, s2/*DetailMap*/
        // with def c4 = (0.1, 0.0002, 2496.0, 4.0), so the 0.1 normal offset is exact and there is
        // no additional tiling divisor anywhere in the program.
        // Previously this multiplied fMacro by 4.75, attributed to an ini [Water]
        // fTileTextureDivisor — but that key is MORROWIND's (docs/research/
        // morrowind_atmosphere_water_model.md lists TileTextureDivisor=4.75 under MORROWIND.ini),
        // not Oblivion's. It made the detail map tile 4.75x finer than retail. Inactive for Tamriel,
        // whose DefaultWater leaves TNAM empty, but wrong for every water that authors one.
        float2 detailUv = input.vWorldPos.xy * fMacro + uLegacySurface0.zw * t + N.xy * 0.1;
        float3 detail = gWaterTextures[NonUniformResourceIndex(uNormalIndices.y)]
            .Sample(gWaterSampler, detailUv).rgb;
        color = lerp(color, detail, oblivionLinearDistanceAtten * uLegacySurface1.z);
    }

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
}
