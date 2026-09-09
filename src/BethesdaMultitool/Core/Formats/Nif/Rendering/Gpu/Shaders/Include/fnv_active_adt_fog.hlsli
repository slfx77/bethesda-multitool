// FNV package013 SLS2000.vso and the owned installed-runtime c14 writer:
// c14 = (end, end-start, power, 0), c15 = (RGB, 0).
// CPU admission proves finite unrepaired positive-range source. This function adds no
// range repair, power floor or max-opacity multiplier and is evaluated BEFORE interpolation.
#ifndef FNV_ACTIVE_ADT_FOG_HLSLI
#define FNV_ACTIVE_ADT_FOG_HLSLI

float EvaluateFnvActiveAdtVertexFog(float4 reversedClipPosition)
{
    // CameraState.ReverseZ changes only z to w-z. Preserve clip XYZ before division by W;
    // this recovers the viewer's forward projection, not independently matched retail FOV.
    float3 forwardClip = float3(reversedClipPosition.xy, reversedClipPosition.w - reversedClipPosition.z);
    float distance = length(forwardClip);
    float inverseRange = rcp(uAtmosphereParams.z - uAtmosphereParams.y);
    float linearAmount = 1.0 - saturate((uAtmosphereParams.z - distance) * inverseRange);
    return pow(linearAmount, uCameraPosFogPower.w);
}

#endif
