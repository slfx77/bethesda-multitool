// v3 water fragment shader — a faithful port of FNV's PC water pixel shader (WATER000.pso),
// disassembled from Data/Shaders/shaderpackage019.sdp with fxc /dumpbin. Full analysis +
// raw disassembly: tools/GhidraProject/water_pc_pixel_shader_decompiled.txt.
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
    uint4 uNoiseParams;  // x = NNAM bindless index (0xFFFFFFFF = none), y = world units/tile
    float4 uSurface0;    // NormalsUvScale, FresnelAmount(=F0), ReflectivityAmount, Shininess(spec exp)
    float4 uSurface1;    // SunPower, DepthFalloffStart, DepthFalloffEnd, spare (depth-SRV upgrade hook)
    float4 uLayer1;      // per noise layer: UvScale, WindDirDeg, WindSpeed, AmpScale
    float4 uLayer2;
    float4 uLayer3;
    uint4 uDepthParams;  // x = scene-depth SRV bindless index (0xFFFFFFFF = none), y/z = near/far bits
};

// Shared scene atmosphere (b3). CPU mirror: WorldView3DControl.AtmosphereConstants (7×float4),
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
};

// Engine distance fog (grounded in Sky::UpdateFog): a linear near→far ramp toward the resolved fog
// color, raised to the weather's fog power — the same helper terrain/reference use, so distant water
// recedes into the same haze. fogEnabled (uFogColorFogEnabled.w) gates it.
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
    float f = saturate((dist - uAtmosphereParams.y) / max(uAtmosphereParams.z - uAtmosphereParams.y, 1.0));
    f = pow(f, max(uCameraPosFogPower.w, 0.01));
    return lerp(color, uFogColorFogEnabled.rgb, f);
}

// Bindless texture table (slot 4, space1) shared with terrain/references. The NNAM normal map
// lives at uNoiseParams.x (FNV NoiseMap, sampler s2). s0 is the shared anisotropic-wrap sampler.
Texture2D gWaterTextures[] : register(t0, space1);
SamplerState gWaterSampler : register(s0);

// Wind-speed (DNAM units) -> UV/sec; lives in the engine vertex shader. Tuned for gentle swell.
static const float kScrollScale = 0.01;
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

// One scrolling NNAM layer -> its tangent-space xy perturbation, weighted by the layer amplitude.
// The engine combines its 3 DNAM noise layers in the vertex/displacement stage and the pixel shader
// then samples once; here we sum the layers directly (the viewer has no engine vertex shader).
float2 SampleLayerPerturb(uint idx, float2 worldXy, float baseUv, float normalsScale, float4 layer, float t)
{
    float scale = baseUv * normalsScale * max(layer.x, 1e-4);
    float rad = radians(layer.y);
    float2 dir = float2(cos(rad), sin(rad));
    float2 uv = worldXy * scale + dir * (layer.z * kScrollScale * t);
    float2 n = gWaterTextures[NonUniformResourceIndex(idx)].Sample(gWaterSampler, uv).xy * 2.0 - 1.0;
    return n * layer.w;
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
    float baseUv = 1.0 / max((float)uNoiseParams.y, 1.0);
    float normalsScale = max(uSurface0.x, 1e-4);
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
        float column = sceneDist - waterDist;     // >0: water over a floor; <0: geometry occludes
        clip(column);                              // discard water hidden behind opaque geometry
        float start = uSurface1.y;                 // DepthFalloffStart
        float end = uSurface1.z;                   // DepthFalloffEnd
        depthT = saturate((column - start) / max(end - start, 1e-3));
    }

    // FNV distance fade of ripples: full within 4096 world units, -> 0 at 8192.
    float noiseFade = saturate((8192.0 - distXY) / 4096.0);

    // Perturbation xy (3 scrolled NNAM layers, or procedural fallback), then the FNV pixel shader's
    // depth-scale (shallow water reads flat) + distance fade. z rebuilt so the normal is robust for
    // RGB and BC5-style maps.
    float2 pxy;
    if (noiseIndex == 0xFFFFFFFFu)
    {
        pxy = RipplePerturb(input.vWorldPos.xy, t);
    }
    else
    {
        pxy = SampleLayerPerturb(noiseIndex, input.vWorldPos.xy, baseUv, normalsScale, uLayer1, t)
            + SampleLayerPerturb(noiseIndex, input.vWorldPos.xy, baseUv, normalsScale, uLayer2, t)
            + SampleLayerPerturb(noiseIndex, input.vWorldPos.xy, baseUv, normalsScale, uLayer3, t);
        pxy *= 0.5;
    }
    float2 nxy = pxy * depthT * noiseFade;
    float nz = sqrt(saturate(1.0 - dot(nxy, nxy)));
    float3 N = normalize(float3(nxy, nz));

    float ndotv = saturate(dot(N, V));

    // FNV body color: lerp(Shallow, Deep, depthT), softly lit by the key light.
    float3 body = lerp(uShallow.rgb, uDeep.rgb, depthT);
    body *= lerp(0.6, 1.0, saturate(dot(N, sunDir)));

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
    float3 color = lerp(body, refl, saturate(F));

    // FNV dual specular: sharp sun glint off the reflected view vector + a fixed sky-glint term.
    float sunSpec = pow(saturate(dot(R, sunDir)), specExp);
    float skyGlint = pow(saturate(dot(float2(N.x, N.z), float2(-0.57, 0.82))), 100.0);
    color += (sunSpec + skyGlint) * sunCol;

    float alpha = lerp(0.6, 0.95, saturate(F));
    return float4(ApplyFog(saturate(color), input.vWorldPos), alpha);
}
