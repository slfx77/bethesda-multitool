// Shared scene atmosphere constant buffer (b3). CPU mirror: WorldView3DControl.AtmosphereConstants,
// uploaded once per frame and bound for the whole scene (terrain/reference/grass/water all read it).
//
// Consumers that previously declared only a prefix of these fields take the full declaration:
// member offsets of the consumed fields are identical, and b3 is bound as a root CBV, so the
// trailing members change only the reflected cbuffer size (root CBVs carry no size check).
#ifndef ATMOSPHERE_HLSLI
#define ATMOSPHERE_HLSLI

cbuffer Atmosphere : register(b3)
{
    float4 uSunDirIntensity;    // xyz = sun world dir (toward sun), w = intensity
    float4 uSunColorLighting;   // rgb = sun color, w = lightingEnabled (0/1)
    float4 uAmbientColor;       // rgb = ambient, w = per-game GameProfile.AmbientLightScale
                                // (0 = slot unset; shaders then fall back to the engine value 1.0)
    float4 uSkyTopSkyEnabled;   // rgb = sky-top color, w = skyEnabled (0/1)
    float4 uSkyHorizon;         // rgb = sky-horizon color, w = effective HDR active (0/1) — the
                                // explicit HDR branch used by classic Lighting30 (never overloads
                                // EmissiveMult == 0, which is valid authored IMGS data)
    float4 uFogColorFogEnabled; // rgb = fog color, w = fogEnabled (0/1)
    float4 uAtmosphereParams;   // x = gameHour, y = fogNear, z = fogFar, w = placed-light count
    float4 uCameraPosFogPower;  // xyz = camera world pos (0 in camera-relative mode), w = fog power (1 = linear)
    float4 uFogFarColorMax;     // rgb = far-fog color, w = max powered fog amount
    float4 uCameraOrigin;       // xyz = camera-relative render origin (VS-consumed; layout parity),
                                // w = IMGS EmissiveMult for Lighting30 (explicit 1 when inactive);
                                // NoLighting does not consume it
    // Sun shadow CASCADES, near→far (appended after the 10-float4 prefix above — the append-safe
    // contract the CPU mirror documents). Each matrix: origin-relative world → that cascade's
    // shadow clip (xy ±1, z reversed 0..1); each params: x = enabled, y = texel UV size,
    // z = bias, w = SRV slot.
    float4x4 uShadowMatrix0;
    float4x4 uShadowMatrix1;
    float4x4 uShadowMatrix2;
    float4x4 uShadowMatrix3;
    float4 uShadowParams0;
    float4 uShadowParams1;
    float4 uShadowParams2;
    float4 uShadowParams3;
    // Full WTHR DALC directional-ambient cube. PositiveX.w is the explicit presence flag; legacy
    // weather keeps all six zero and shaders use uAmbientColor.rgb unchanged.
    float4 uAmbientPositiveX;  // w = full DALC cube present
    float4 uAmbientNegativeX;
    float4 uAmbientPositiveY;
    float4 uAmbientNegativeY;
    float4 uAmbientPositiveZ;
    float4 uAmbientNegativeZ;
    // Appended: the classic FO3/FNV GRASS sun term. Retail hands TallGrassShader the sun light's
    // NiLight m_kDiffuseColor RAW — the imagespace SunlightDimmer that ordinary geometry receives
    // through BSShaderLightingProperty is never applied to grass — and multiplies the grass sun
    // term by the imagespace GrassDimmer instead (written into the grass VS AddlParams.x / c7.x,
    // consumed as `mul oT3.xyz, r1, c7.x`). uSunColorLighting.rgb already has SunlightDimmer folded
    // in, so grass needs both lanes below rather than either alone.
    float4 uGrassSunColorScale; // rgb = sun colour WITHOUT SunlightDimmer, w = IMGS GrassDimmer
                                // (w == 0 marks the lane unset; consumers then fall back)
    // Appended: half-space clip plane in origin-relative vWorldPos space (mirror passes trim
    // below-plane geometry). Neutral default (0,0,0,1) ⇒ clip(+1) never fires — branchless no-op
    // for the main pass. Consumers: terrain + reference PS entry.
    float4 uClipPlane;
};

#endif // ATMOSPHERE_HLSLI
