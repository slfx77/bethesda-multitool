// v3 water fragment shader — a faithful port of FNV's PC water pixel shader (WATER000.pso),
// disassembled from Data/Shaders/shaderpackage019.sdp with fxc /dumpbin. Full analysis +
// raw disassembly: tools/GhidraProject/water_pc_pixel_shader_decompiled.txt.
//
// OBLIVION_WATER (compile-time define) selects Oblivion's WATER000.pso math where it genuinely
// diverges (tools/GhidraProject/oblivion_water_pixel_shader_decompiled.txt): the body color blends
// Deep→Shallow by the VIEW ANGLE (N·V), not the depth column, and the specular is a single
// sun glint (no sky-glint term). Everything else (Schlick fresnel with F0 = WATR Fresnel Amount,
// ReflectionColor × Reflectivity on the RT-free path, distance fog) is shared. The FNV permutation
// is textually unchanged when the define is absent.
//
// The engine shader composites: reflection RT, refraction RT, a depth-map water-column factor,
// a single NNAM normal tap, a Schlick fresnel, a dual sun/sky specular, and distance fog. Our
// viewer has no reflection/refraction/depth render targets, so we reproduce the engine's RT-free
// path exactly — which is what the engine itself does when those RTs are disabled:
//   * reflection  == ReflectionColor          (engine: lerp(ReflectionColor, RT, VarAmounts.y), VarAmounts.y=0)
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
    float4 uRenderOrigin; // xyz = camera-relative render origin (VS-only; declared for layout parity)
    // ---- FO4_WATER-only constants (appended at the end so every other variant's offsets are
    // untouched; sourced from the FO4 WATR DNAM — see WaterShaderVariant.Fo4Water). ----
    float4 uFo4Spec;     // x = Sun Specular Magnitude, y = Silt Amount, z = Shallow Alpha, w = Deep Alpha
    float4 uFo4Ranges;   // x/y = Color Shallow/Deep Range, z/w = Alpha Shallow/Deep Range
                         //       (multipliers of Depth Amount; retail authors them ≈1.0)
    float4 uFo4DarkSilt; // rgb = DNAM silt Dark Color (the FO4 PS's unshadowed ambient add),
                         // w = DNAM Depth Amount (world-unit column at which the depth ramps saturate)
};

// Shared scene atmosphere (b3). CPU mirror: WorldView3DControl.AtmosphereConstants,
// bound once per frame for the whole scene. Water reads the sun dir/color from here so its specular
// and body-lighting track the time-of-day/weather sun like the rest of the scene (P3). When lighting
// is disabled (uSunColorLighting.w == 0) it falls back to the static kSunDir/kSunColor below, so the
// water looks exactly as it did pre-atmosphere.
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
    float4 uFogFarColorMax;     // rgb = far-fog color, w = max powered fog amount
};

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
SamplerState gWaterSampler : register(s0);

// Noise scroll/blend is now fully DNAM-driven (RE-recovered, no tuned constants): each of the 3 noise
// layers scrolls at its own DNAM WindSpeed in noise-UV space along its WindDir(°), and is weighted by its
// DNAM fAmplitude — exactly the engine's ISNOISESCROLLANDBLEND prepass (TESWaterSystem::UpdateWaterNoise:
// texScroll += WindSpeed·dt·(cosθ,sinθ)). The composite is sampled once and the noise frequency comes
// from fNoiseScale/fUVScale (see main()). So the old kNoiseGain / kOctaves / kScrollWorldSpeed tuning
// constants are gone — their values are the real DNAM fAmplitude / fNoiseScale / WindSpeed.
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
//   IMPORTANT: `layer.x` (UvScale) and `layer.w` (AmpScale) are NOT used — those WATR-DNAM fields are the
//   FFT-DISPLACEMENT params (fHeightUVScale @172-180 / fAmplitude @184-192), which are ZERO in standard
//   water; reading them as noise params collapsed the UV to a constant texel (flat) at zero amplitude.
//   The noise normal is driven by ONE shared scale (`freq`, from fNoiseScale-era tiling) scrolled by the
//   real DNAM noise wind dir/speed (`layer.y` = WindDirDeg @100-108, `layer.z` = WindSpeed @112-120).
float3 SampleNoiseLayer(uint idx, float2 worldXy, float freq, float4 layer, float t)
{
    // layer = (UvScale[displacement fHeightUVScale, NOT used for noise], WindDirDeg, WindSpeed, fAmplitude).
    // Engine ISNOISESCROLLANDBLEND: each layer scrolls the noise in its own UV by WindSpeed·dt along WindDir°,
    // weighted by fAmplitude (UpdateWaterNoise, RE-recovered — no fudge multiplier on the scroll rate).
    float rad = radians(layer.y);
    float2 dir = float2(cos(rad), sin(rad));
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

float4 main(PSInput input) : SV_Target
{
    float t = uCamPosTime.w;
    // Scene sun: from the shared atmosphere CB when lighting is on (tracks time-of-day/weather),
    // else the static fallback so water is unchanged in the lighting-off state.
    bool lit = uSunColorLighting.w > 0.5;
    float3 sunDir = lit ? normalize(uSunDirIntensity.xyz) : kSunDir;
    float3 sunCol = lit ? uSunColorLighting.rgb : kSunColor;
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
    // view-space gap, and normalize by DepthFalloffStart/End (uSurface1.yz). Where opaque geometry
    // is NEARER than the water surface the fragment is occluded -> discard (no DSV is bound in this
    // PSO, so occlusion is done here). Falls back to a view-angle proxy when no depth SRV is set.
    uint depthIndex = uDepthParams.x;
    float depthT;
    float column = 0.0; // raw water column in world units (only meaningful when depth is sampled)
    if (depthIndex == 0xFFFFFFFFu)
    {
        depthT = saturate(dot(float3(0, 0, 1), V));
    }
    else
    {
        float near = asfloat(uDepthParams.y);
        float far = asfloat(uDepthParams.z);
        float sceneNdc = gWaterTextures[NonUniformResourceIndex(depthIndex)].Load(int3((int2)input.Position.xy, 0)).r;
        float sceneDist = LinearizeDepth(sceneNdc, near, far);
        float waterDist = LinearizeDepth(input.Position.z, near, far);
        column = sceneDist - waterDist;           // >0: water over a floor; <0: geometry occludes
        // 3D-2 tie-break: bias the occlusion test toward KEEPING the water (uDepthParams.w world units) so
        // a shoreline where water and terrain are ~coplanar (column ≈ 0 ± sub-ULP depth noise) resolves to
        // water instead of flickering. The bias is tiny vs DepthFalloff, so genuinely occluded water
        // (column far negative) is still discarded.
        clip(column + asfloat(uDepthParams.w));    // discard water hidden behind opaque geometry
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

    // FNV distance fade of ripples: full within 4096 world units, -> 0 at 8192.
    float noiseFade = saturate((8192.0 - distXY) / 4096.0);

    // FNV WATER000 surface normal — engine PS (WATER000.pso lines 90-96), reproduced exactly:
    //   r3 = noise.xyz*2-1 ; r3 = r3*depthT + (0,0,1) ; r3.xy *= distFade ; N = normalize(r3)
    // Adding the (0,0,1) z-bias IN THE SHADER (engine const c7.xxww) — rather than rebuilding
    // z = sqrt(1 - |xy|^2) — is the fix for the harsh "blips": when the summed perturbation magnitude
    // exceeded 1, the rebuilt z collapsed to 0, tilting the normal flat/horizontal and smearing the sun
    // glint into elongated streaks. The z-bias keeps the normal near-vertical, so the surface reads as
    // gentle ripples. The engine pre-composites its 3 noise layers (ISNOISESCROLLANDBLEND) into one
    // texture and taps it once; lacking that prepass the viewer sums the 3 DNAM layers here — each a tap
    // of the NNAM at noiseFreq, scrolled by its own WindDir/WindSpeed and weighted by its fAmplitude.
    float3 pert;
    if (noiseIndex == 0xFFFFFFFFu)
    {
        pert = float3(RipplePerturb(input.vWorldPos.xy, t), 0.0);
    }
    else
    {
        // TWO octaves of the full 3-layer blend so the fine ripple is at full authored amplitude (not a single
        // weak layer): the macro octave (tile=fUVScale) gives the broad, non-repeating structure; the detail
        // octave (tile=fUVScale/fNoiseScale) gives dense fine ripples — the engine's fNoiseScale normal detail.
        // Each layer carries WindDir(°)=.y, WindSpeed=.z, fAmplitude=.w. uLayerN.x (displacement) is unused.
        float3 macro = SampleNoiseLayer(noiseIndex, input.vWorldPos.xy, fMacro, uLayer1, t)
                     + SampleNoiseLayer(noiseIndex, input.vWorldPos.xy, fMacro, uLayer2, t)
                     + SampleNoiseLayer(noiseIndex, input.vWorldPos.xy, fMacro, uLayer3, t);
        float3 detail = SampleNoiseLayer(noiseIndex, input.vWorldPos.xy, fDetail, uLayer1, t)
                      + SampleNoiseLayer(noiseIndex, input.vWorldPos.xy, fDetail, uLayer2, t)
                      + SampleNoiseLayer(noiseIndex, input.vWorldPos.xy, fDetail, uLayer3, t);
        pert = macro + detail;
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

#if FO4_WATER
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
#else

#if OBLIVION_WATER
    // Oblivion body color: lerp(Deep, Shallow, N·V) — the view ANGLE picks the body tint
    // (grazing = deep, top-down = shallow); Oblivion's PS has no depth-column body term and no
    // body sun modulation (sun enters via the specular only).
    float3 body = lerp(uDeep.rgb, uShallow.rgb, ndotv);
#else
    // FNV body color: lerp(Shallow, Deep, depthT), softly lit by the key light.
    float3 body = lerp(uShallow.rgb, uDeep.rgb, depthT);
    body *= lerp(0.6, 1.0, saturate(dot(N, sunDir)));
#endif

    // Reflected view vector — used for both the sky-reflection tint and the sun specular below.
    float3 R = reflect(-V, N);

    // FNV reflection (RT-free path): with the skybox on (uSkyTopSkyEnabled.w), tint the reflection with
    // the atmosphere sky — horizon→top by the reflected ray's up component — so water mirrors the
    // time-of-day/weather sky (P5; one-way b3 read, no RTT loop). Otherwise the DNAM ReflectionColor.
    // Scaled by the reflectivity multiplier either way.
    float3 reflBase = uSkyTopSkyEnabled.w > 0.5
        ? lerp(uSkyHorizon.rgb, uSkyTopSkyEnabled.rgb, saturate(R.z))
        : uReflection.rgb;
    float3 refl = reflBase * reflectivity;

    // FNV Schlick fresnel: F0 + (1-F0)*(1-NdotV)^5, F0 = FresnelAmount. Reflection over body.
    float F = F0 + (1.0 - F0) * pow(1.0 - ndotv, 5.0);
#if OBLIVION_WATER
    // VarAmounts.z — the engine's runtime fresnel FLOOR (max(VarAmounts.z, Schlick), WATER000
    // asm 139). DECOMPILE-RESOLVED (Oblivion.exe FUN_00499570 fills the global; FUN_004ed660 is
    // the getter; the console "set water opacity" handler FUN_0050d8e0 writes the same slot):
    // VarAmounts.z = WATR ANAM Opacity / 100 — per water type. Vanilla: DefaultWater 100 (fully
    // floored/opaque), dungeon/sewer/oil waters 85. Floors both the color lerp and the alpha.
    float fresneled = max(asfloat(uNoiseParams.w), saturate(F));
#else
    float fresneled = saturate(F);
#endif
    float3 color = lerp(body, refl, fresneled);

#if OBLIVION_WATER
    // Oblivion single specular: pow(dot(reflect(-V,N), SunDir), Sun Power) × SunColor — no
    // sky-glint term in WATER000.pso.
    float sunSpec = pow(saturate(dot(R, sunDir)), specExp);
    color += sunSpec * sunCol;
#else
    // FNV dual specular: sharp sun glint off the reflected view vector + a fixed sky-glint term.
    float sunSpec = pow(saturate(dot(R, sunDir)), specExp);
    float skyGlint = pow(saturate(dot(float2(N.x, N.z), float2(-0.57, 0.82))), 100.0);
    color += (sunSpec + skyGlint) * sunCol;
#endif

#if OBLIVION_WATER
    // Oblivion WATER000 shore alpha (pkg019 asm 139-173; def c13=(0.25,-0.2,-0.55), c12.x=1/0.35):
    //   alphaBase = lerp(0.25, max(VarAmounts.z, F), depth)
    //   s         = saturate(1 − (depth − 0.2)/0.35)
    //   alpha     = alphaBase · (1 − s³)            — 0 below 0.2 column, cubic shore fade to 0.55,
    //                                                 then ≈floored fresnel (largely opaque + reflective)
    // The engine's DepthMap is a dedicated 0..1 water-depth target; the WATR fog distances canNOT
    // normalize it (DefaultWater's fog Near = −8192 would put the SHORELINE at depthT ≈ 0.89), so
    // the alpha depth uses the raw column over a fixed range — 512 world units puts the engine's
    // 0.2..0.55 fade band at ~100..280 units of water, calibrated against in-game shorelines.
    // Without a scene-depth SRV there is no column (the N·V proxy is unrelated and zero at grazing
    // angles, which would erase the surface) — fall back to deep-water behavior (aDepth = 1).
    float aDepth = (depthIndex == 0xFFFFFFFFu) ? 1.0 : saturate(column / 512.0);
    float alphaBase = lerp(0.25, fresneled, aDepth);
    float shoreS = saturate(1.0 - (aDepth - 0.2) * 2.857143);
    float alpha = alphaBase * (1.0 - shoreS * shoreS * shoreS);
#else
    float alpha = lerp(0.6, 0.95, saturate(F));
#endif
    return float4(ApplyFog(color, input.vWorldPos), alpha);
#endif // !FO4_WATER
}
