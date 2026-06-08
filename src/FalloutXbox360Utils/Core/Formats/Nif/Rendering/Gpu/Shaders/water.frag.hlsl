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
//   * depthT      == a view-angle proxy        (engine samples DepthMap; DepthFalloff is plumbed for
//                                               the eventual D32-depth-SRV upgrade)
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
};

// Bindless texture table (slot 4, space1) shared with terrain/references. The NNAM normal map
// lives at uNoiseParams.x (FNV NoiseMap, sampler s2). s0 is the shared anisotropic-wrap sampler.
Texture2D gWaterTextures[] : register(t0, space1);
SamplerState gWaterSampler : register(s0);

// Wind-speed (DNAM units) -> UV/sec; lives in the engine vertex shader. Tuned for gentle swell.
static const float kScrollScale = 0.01;
// FNV passes the live scene SunDir (c12) / SunColor (c13); the viewer has no scene sun, so a fixed
// warm key light stands in (same direction the terrain shader uses).
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

float4 main(PSInput input) : SV_Target
{
    float t = uCamPosTime.w;
    uint noiseIndex = uNoiseParams.x;
    float baseUv = 1.0 / max((float)uNoiseParams.y, 1.0);
    float normalsScale = max(uSurface0.x, 1e-4);
    float F0 = saturate(uSurface0.y);            // FNV FresnelRI.x
    float reflectivity = saturate(uSurface0.z);  // FNV FresnelRI.w (reflection multiplier)
    float specExp = max(uSurface0.w, 1.0);       // FNV VarAmounts.x (sun-specular exponent)

    float3 eye = uCamPosTime.xyz - input.vWorldPos; // surface -> camera (FNV EyePos - worldPos)
    float distXY = length(eye.xy);
    float3 V = normalize(eye);

    // FNV depthT comes from the depth map (water-column thickness). No depth RT here -> a
    // view-angle proxy: straight-down sees the full column (deep + developed ripples), grazing
    // sees the surface (shallow + flat + reflection-dominated). DepthFalloff (uSurface1.yz) is the
    // real range, applied once the D32 depth buffer is exposed as an SRV.
    float depthT = saturate(dot(float3(0, 0, 1), V));

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
    body *= lerp(0.6, 1.0, saturate(dot(N, kSunDir)));

    // FNV reflection (RT-free path): ReflectionColor scaled by the reflectivity multiplier.
    float3 refl = uReflection.rgb * reflectivity;

    // FNV Schlick fresnel: F0 + (1-F0)*(1-NdotV)^5, F0 = FresnelAmount. Reflection over body.
    float F = F0 + (1.0 - F0) * pow(1.0 - ndotv, 5.0);
    float3 color = lerp(body, refl, saturate(F));

    // FNV dual specular: sharp sun glint off the reflected view vector + a fixed sky-glint term.
    float3 R = reflect(-V, N);
    float sunSpec = pow(saturate(dot(R, kSunDir)), specExp);
    float skyGlint = pow(saturate(dot(float2(N.x, N.z), float2(-0.57, 0.82))), 100.0);
    color += (sunSpec + skyGlint) * kSunColor;

    float alpha = lerp(0.6, 0.95, saturate(F));
    return float4(saturate(color), alpha);
}
