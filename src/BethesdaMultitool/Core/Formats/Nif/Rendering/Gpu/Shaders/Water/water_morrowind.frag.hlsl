// Morrowind fixed-function water — NetImmerse 4.0.0.2 has no water pixel shader to port: the
// engine draws an animated tiled diffuse plane (Morrowind.ini [Water]: water00-31.dds cycling
// at SurfaceFPS; see docs/research/morrowind_atmosphere_water_model.md). The shared prologue's
// depth block still discards occluded fragments (and keeps the lava branch for prologue parity
// with the shader family); the surface itself is the early-return diffuse path below.
// Selected by WaterProfile.PixelShaderFile for BethesdaGame.Morrowind.

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
}
