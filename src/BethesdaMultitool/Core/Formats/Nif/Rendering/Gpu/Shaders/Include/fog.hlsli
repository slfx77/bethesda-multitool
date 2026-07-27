// Shared distance-fog core (Skyrim BSLightingShader constant fog): q is the linear near→far
// distance, then the powered amount is capped by FNAM max opacity. That SAME amount blends
// near→far fog color and the surface→fog result. Legacy weather binds far=near/max=1, reducing
// exactly to its old single-color powered fog.
//
// Reads the shared Atmosphere cbuffer's fog/camera fields. The water shaders (water_common.hlsli's
// ApplyFog) deliberately do NOT use this header: water renders in absolute world space, so its fog
// distance comes from water's OWN absolute camera (uCamPosTime.xyz) rather than uCameraPosFogPower.xyz.
#ifndef FOG_HLSLI
#define FOG_HLSLI

#include "atmosphere.hlsli"

// Powered, FNAM-capped fog amount for a distance from the fog camera.
float FogAmountAtDistance(float dist)
{
    float q = saturate((dist - uAtmosphereParams.y) / max(uAtmosphereParams.z - uAtmosphereParams.y, 1.0));
    return min(pow(q, max(uCameraPosFogPower.w, 0.01)), saturate(uFogFarColorMax.w));
}

// Near→far fog color blend for an amount produced by FogAmountAtDistance.
float3 FogColorForAmount(float amount)
{
    return lerp(uFogColorFogEnabled.rgb, uFogFarColorMax.rgb, amount);
}

// Standard opaque fog blend for a precomputed camera distance (identity when fog is disabled).
float3 ApplyFogAtDistance(float3 color, float dist)
{
    if (uFogColorFogEnabled.w < 0.5)
    {
        return color;
    }

    float amount = FogAmountAtDistance(dist);
    return lerp(color, FogColorForAmount(amount), amount);
}

// Standard 2-arg form: distance measured from the shared atmosphere camera (uCameraPosFogPower.xyz).
float3 ApplyFog(float3 color, float3 worldPos)
{
    return ApplyFogAtDistance(color, length(worldPos - uCameraPosFogPower.xyz));
}

#endif // FOG_HLSLI
