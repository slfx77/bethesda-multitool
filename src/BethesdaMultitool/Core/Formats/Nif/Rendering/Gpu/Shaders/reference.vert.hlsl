// v3 Phase 3 placed-object vertex shader. Projects mesh-local vertices through the per-draw
// world matrix and per-frame viewProj. Passes the world-space normal + UV + vertex color to
// the pixel shader for Lambert + texture sampling.
//
// Same matrix-byte convention as terrain: System.Numerics is row-major in memory; HLSL
// cbuffer reads column-major; CPU side does NOT transpose, so `mul(M, v)` in HLSL produces
// the same result as `M * v` on the CPU. This means a CPU-side row-vector world matrix
// composed as `S * Rx * Ry * Rz * T` is consumed in HLSL as a column-vector matrix doing
// `T * Rz * Ry * Rx * S` applied to (col-vector) position — same end transform.

cbuffer PerFrame : register(b0)
{
    float4x4 uViewProj;
};

// Shared atmosphere CB (b3). Geometry stays camera-relative because the CPU folds the render origin into
// uWorld's translation. TallGrass alone adds uCameraOrigin back to the INSTANCE ORIGIN for its absolute
// phase seed; it never applies that large value to individual vertex positions. The leading 9 float4 are
// the sun/sky/fog/camera fields this VS does not otherwise use.
cbuffer Atmosphere : register(b3)
{
    float4 uSunDirIntensity; // xyz = world direction toward the Sun
    float4 uAtmospherePad[8];
    float4 uCameraOrigin; // absolute snapped render origin; restores TallGrass placement phase identity
};

cbuffer PerDraw : register(b1)
{
    float4x4 uWorld;
    // uAlphaState: x = alpha-test threshold, y = alpha-test function, z = material alpha,
    // w = blended-mode flag. Unused in VS; kept for PerDraw cbuffer layout parity with PS.
    float4 uAlphaState;
    // uRenderState: x = double-sided, y = has bump map, z = bump strength, w = unlit/emissive.
    // Unused in VS; cull state is handled on the CPU side.
    float4 uRenderState;
    // uTextureState: x = BC5/ATI2 normal map; y = billboard/SpeedTree route; z = exact flags
    // (bit0 spec map, bit1 clamp U, bit2 clamp V, bit3 Lighting30 glow, bit4 Lighting30 route,
    // bit5 TallGrassShaderProperty);
    // w = gradient-map row (<0 disabled).
    float4 uTextureState;
    // 4a — bindless TexIndices: .x diffuse, .y normal, .z FO4 specular, classic environment
    // mask, or classic simple-parallax height, .w gradient or Lighting30 glow
    // (uTextureState disambiguates both unions). Mirrors the instanced VS's per-instance struct;
    // the PS reads vTexIndices regardless of path.
    uint4  uTexIndices;
    // 1A — specular: xyz = tint, w = Phong exponent (0 = no specular). Matches PerDrawConstants.
    float4 uSpecular;
    // Camera world-space basis for per-card leaf billboards (same source as the instanced VS). Only read
    // when uTextureState.y marks a leaf submesh — used by baked particle clouds drawn on the blended path.
    float4 uCameraRight;
    float4 uCameraUp;
    // BGEM effect terms: uEffectTint.rgb multiplies the source texture (baseColor × scale);
    // .w > 0.5 enables the |N·V| opacity falloff in uEffectFalloff =
    // (startAngle, stopAngle, startOpacity, stopOpacity), all stored as cosines/opacities. On the
    // mutually-exclusive Lighting30 route this slot is raw emission rgb + material multiplier.
    float4 uEffectTint;
    float4 uEffectFalloff;
    // FO4 cubemap environment mapping: x = cube bindless slot (< 0 = none/not yet resident),
    // y = envMapScale (fo76utils envScale × specular strength), z = material smoothness 0–1.
    float4 uEnvMap;
    // NiUVController scroll: xy = fractional UV phase this frame (0 = static); z = CPU-computed
    // classic direct-sun specular LOD fade; w unused.
    // Must stay layout-matched with PerDrawConstants in ReferenceRenderer12.cs.
    float4 uUvScroll;
    // Scene-depth blend pass: x = 0 disables, +(slot+1) = Texture2D, -(slot+1) = Texture2DMS,
    // y/z = perspective near/far, |w| = soft falloff depth. w>0 fades output alpha;
    // w<0 fades output RGB. w=0 keeps ordinary alpha geometry hard-intersecting. On the mutually
    // exclusive FNV TallGrass route this register instead carries GRASS2000 WindData.
    float4 uSoftParticle;
};

struct VSInput
{
    float3 aPosition    : TEXCOORD0;
    float3 aNormal      : TEXCOORD1;
    float2 aTexCoord    : TEXCOORD2;
    float4 aVertexColor : TEXCOORD3;
    float3 aTangent     : TEXCOORD4;
    float3 aBitangent   : TEXCOORD5;
};

bool IsTallGrassMaterial(float packedState)
{
    return (((uint)round(packedState)) & 32u) != 0u;
}

bool IsFnvActiveAdtBaseMaterial(float packedState)
{
    return (((uint)round(packedState)) & 1024u) != 0u;
}

// PC-final package013 SLS2000.vso (active ID193/BSSM_ADT) transforms the directional light through
// the authored T/B/N basis and normalizes that complete tangent-space vector per vertex. The viewer
// starts with a world-space light, so remove only uniform placement scale; do not normalize the
// three basis vectors independently.
float3 ResolveFnvActiveAdtBaseLight(float3 tangent, float3 bitangent, float3 normal, float uniformScale)
{
    if (uniformScale <= 1e-8)
    {
        return 0.0.xxx;
    }

    float3 lightTs = float3(
        dot(tangent, uSunDirIntensity.xyz),
        dot(bitangent, uSunDirIntensity.xyz),
        dot(normal, uSunDirIntensity.xyz)) / uniformScale;
    float lengthSquared = dot(lightTs, lightTs);
    if (!all(isfinite(lightTs)) || !isfinite(lengthSquared) || lengthSquared <= 1e-8)
    {
        return 0.0.xxx;
    }

    return normalize(lightTs);
}

float WrapTallGrassPhase(float phase)
{
    const float Pi = 3.14159265358979323846;
    const float TwoPi = 6.28318530717958647692;
    phase = fmod(phase + Pi, TwoPi);
    if (phase < 0.0) phase += TwoPi;
    return phase - Pi;
}

struct VSOutput
{
    float4 Position     : SV_Position;
    float3 vWorldNormal : TEXCOORD0;
    float2 vTexCoord    : TEXCOORD1;
    float4 vVertexColor : TEXCOORD2;
    float3 vTangent     : TEXCOORD3;
    float3 vBitangent   : TEXCOORD4;
    nointerpolation float4 vAlphaState  : TEXCOORD5;
    nointerpolation float4 vRenderState : TEXCOORD6;
    nointerpolation float4 vTextureState : TEXCOORD7;
    nointerpolation uint4  vTexIndices  : TEXCOORD8;
    float3 vWorldPos    : TEXCOORD9;  // world-space position for per-pixel distance fog
    nointerpolation float4 vSpecular   : TEXCOORD10; // xyz = tint, w = Phong exponent
    nointerpolation float4 vEffectTint    : TEXCOORD11; // rgb = BGEM tint, w = falloff enabled
    nointerpolation float4 vEffectFalloff : TEXCOORD12; // startAngle/stopAngle/startOp/stopOp
    nointerpolation float4 vEnvMap        : TEXCOORD13; // x = cube slot (<0 none), y = scale, z = smoothness, w = blend operation
    nointerpolation float4 vSoftParticle  : TEXCOORD14;
    nointerpolation float vSpecularLodFade : TEXCOORD15;
    float3 vFnvActiveAdtBaseLight : TEXCOORD16;
};

VSOutput main(VSInput input)
{
    VSOutput o;
    bool isTallGrass = IsTallGrassMaterial(uTextureState.z);
    float tallGrassWindWeight = isTallGrass ? saturate(input.aVertexColor.a) : 0.0;
    float4 worldPos;
    if (uTextureState.y > 0.5)
    {
        // Per-card leaf billboard (baked particle quads): the vertex carries the card CENTER (aTangent)
        // and the signed 2D card-space offset (aBitangent.xy). Rebuild the quad facing the camera around
        // the world-space center, scaled by the uniform REFR scale. Same math as reference_instanced.vert.hlsl;
        // particles have no wind weight so the sway term is omitted.
        // uWorld already carries the CPU-folded camera-relative origin, so this center is camera-relative.
        float3 worldCenter = mul(uWorld, float4(input.aTangent, 1.0)).xyz;
        float scale = length(float3(uWorld[0].x, uWorld[0].y, uWorld[0].z));
        worldPos = float4(
            worldCenter
                + uCameraRight.xyz * (input.aBitangent.x * scale)
                + uCameraUp.xyz    * (input.aBitangent.y * scale),
            1.0);
    }
    else
    {
        // uWorld's translation is CPU-folded to the render origin, so this is already the camera-relative
        // position (absolute when renderOrigin == 0). The prior post-multiply "-= uCameraOrigin" is gone.
        worldPos = mul(uWorld, float4(input.aPosition, 1.0));
    }

    // The per-draw path uses a layout-preserving union: uSoftParticle is GRASS2000 WindData for
    // an actual FNV GRAS placement. Apply displacement in WORLD XY after the object transform,
    // and recover absolute phase XY through uCameraOrigin.
    float3 relativePlacement = mul(uWorld, float4(0.0, 0.0, 0.0, 1.0)).xyz;
    if (isTallGrass && uSoftParticle.z != 0.0)
    {
        float2 absolutePlacement = relativePlacement.xy + uCameraOrigin.xy;
        float phase = WrapTallGrassPhase(
            (absolutePlacement.x + absolutePlacement.y) / 128.0 + uSoftParticle.w);
        float weightedOffset = sin(phase) * uSoftParticle.z *
            tallGrassWindWeight * tallGrassWindWeight;
        worldPos.xy += uSoftParticle.xy * weightedOffset;
    }
    o.Position = mul(uViewProj, worldPos);
    o.vWorldPos = worldPos.xyz; // camera-relative world pos (matches the shader camera = 0 for fog/spec)
    // Uniform scale only — pass the normal through the world rotation (3x3 sub-matrix). For
    // non-uniform scale we'd want the inverse-transpose, but Bethesda REFR.Scale is uniform.
    o.vWorldNormal = mul((float3x3)uWorld, input.aNormal);
    // STLEAF per-corner normal puff for leaf cards on the blended path — same formula as
    // reference_instanced.vert.hlsl (uCameraRight.w = LeafLighting.y adjust); inert for the
    // emissive baked particle clouds that also take this branch.
    if (uTextureState.y > 0.5 && uCameraRight.w > 0.0 && dot(input.aBitangent.xy, input.aBitangent.xy) > 1e-8)
    {
        float3 cornerDir = normalize(uCameraRight.xyz * input.aBitangent.x + uCameraUp.xyz * input.aBitangent.y);
        o.vWorldNormal = normalize(normalize(o.vWorldNormal) + cornerDir * uCameraRight.w);
    }
    o.vTexCoord = input.aTexCoord + uUvScroll.xy;
    o.vVertexColor = input.aVertexColor;
    if (isTallGrass)
    {
        // Vertex alpha is wind weight only; diffuse texture alpha remains the sole cutout input.
        o.vVertexColor.a = 1.0;
    }
    o.vTangent = mul((float3x3)uWorld, input.aTangent);
    o.vBitangent = mul((float3x3)uWorld, input.aBitangent);
    float activeAdtUniformScale = length(mul((float3x3)uWorld, float3(1.0, 0.0, 0.0)));
    o.vFnvActiveAdtBaseLight = IsFnvActiveAdtBaseMaterial(uTextureState.z)
        ? ResolveFnvActiveAdtBaseLight(
            o.vTangent, o.vBitangent, o.vWorldNormal, activeAdtUniformScale)
        : 0.0.xxx;
    o.vAlphaState = uAlphaState;
    o.vRenderState = uRenderState;
    o.vTextureState = uTextureState;
    o.vTexIndices = uTexIndices;
    o.vSpecular = uSpecular;
    o.vEffectTint = uEffectTint;
    o.vEffectFalloff = uEffectFalloff;
    o.vEnvMap = uEnvMap;
    o.vSoftParticle = isTallGrass ? 0.0 : uSoftParticle;
    o.vSpecularLodFade = uUvScroll.z;
    return o;
}
