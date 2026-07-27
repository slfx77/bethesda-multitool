// Placed-object instancing vertex shader. Per-reference world matrices are bound as a
// root SRV at t8; per-batch material/texture state rides in the InstanceDraw cbuffer
// (identical across a batch — uploading it per instance was pure redundancy). Material
// textures use the shared bindless pixel-shader table.

cbuffer PerFrame : register(b0)
{
    float4x4 uViewProj;
#ifdef SHADOW_CARD_LIGHT_FACING
    // Sun-shadow pass only: the leaf-card billboard basis, PERPENDICULAR TO THE LIGHT. A card that
    // faces the camera can be edge-on to the sun and rasterize nothing into the shadow map — leaf
    // shadows then vanish at the camera pitches where the two directions disagree. Facing the
    // LIGHT keeps the cast footprint present and stable at every camera angle (the engine's own
    // trees are solid geometry, so any billboard shadow is an approximation either way).
    float4 uShadowCardRight;
    float4 uShadowCardUp;
#endif
    // SpeedTree wind v2 — the 4 slow whole-canopy sway matrices (BSTreeManager::UpdateWindMatrices →
    // SpeedTreeShader::SetMatrixRotation: YawPitchRoll(0.61·S·sinOsc, 0.61·S·cosOsc, 0) — small tilts
    // about the tree's model-space horizontal axes). Each leaf lerps its MODEL-space card center
    // toward the tilted position by its per-vertex weight (design doc B.2: orig + (M·orig − orig)·w).
    // Only read when uWindMatrixValid (InstanceDraw) is set, so a stale/short PerFrame CB from a
    // caller that doesn't upload them can never deform geometry.
    float4x4 uWindMatrices[4];
};

// Shared atmosphere CB (b3). Geometry stays camera-relative because the CPU folds the render origin into
// each instance world matrix. TallGrass alone adds uCameraOrigin back to the INSTANCE ORIGIN for its
// absolute phase seed; it never applies that large value to individual vertex positions. The leading
// camera-position register is exposed for classic specular LOD; all positions are in the same
// CPU-rebased space as the uploaded instance worlds.
cbuffer Atmosphere : register(b3)
{
    float4 uSunDirIntensity; // xyz = world direction toward the Sun
    float4 uAtmospherePadBeforeCamera[6];
    float4 uCameraPosFogPower;
    float4 uAtmospherePadAfterCamera;
    float4 uCameraOrigin; // absolute snapped render origin; restores TallGrass placement phase identity
};

// Per-batch (one DrawIndexedInstanced) constants. TextureState.x marks BC5/ATI2 normal
// decode; TexIndices.x = diffuse bindless slot, .y = normal bindless slot. uInstanceBase
// is the start offset of this batch's worlds inside the shared instance buffer.
cbuffer InstanceDraw : register(b1)
{
    float4 uAlphaState;
    float4 uRenderState;
    float4 uTextureState; // .x BC5; .y leaf/ST; .z material flags (classic env/parallax); .w palette row
    uint4  uTexIndices;
    uint   uInstanceBase;
    // Per-submesh UV scroll offset, CPU-wrapped (frac(velocity × animClock)) so precision never
    // drifts. Packs into the former uint3 padding — same 16-byte register, layout unchanged; the
    // CPU zero-fills it for static submeshes, so the add below is a no-op until an animated
    // submesh writes a real offset.
    float2 uUvScroll;
    // Nonzero when this frame's PerFrame CB carries live uWindMatrices (the wind-v2 sway layer).
    // Repurposes the former pad — every pre-v2 writer zero-fills it, so the sway path stays inert
    // against renderers that never upload the matrices.
    uint   uWindMatrixValid;
    float4 uSpecular; // xyz = specular tint, w = Phong exponent (0 = no specular highlight)
    // Camera world-space basis for per-card leaf billboards (from the inverse view matrix, same source
    // as SkyBillboardRenderer12). Only read when uTextureState.y marks a leaf submesh.
    float4 uCameraRight;
    float4 uCameraUp;
    // SpeedTree leaf rock/rustle (engine STLEAF model — tools/GhidraProject/speedtree_wind_design.md):
    // x = rockAmount (RockParams.x), y = rockPhase (RockParams.y), z = rustleAmount, w = rustlePhase.
    // The engine's RockParams.z/RustleParams.z scalars are constructor-1.0 and omitted. All-zero = static.
    float4 uWind;
    // BGEM effect terms: uEffectTint.rgb multiplies the source texture (baseColor × scale);
    // .w > 0.5 enables the |N·V| opacity falloff in uEffectFalloff =
    // (startAngle, stopAngle, startOpacity, stopOpacity). On the mutually-exclusive Lighting30
    // route this slot is raw emission rgb + material multiplier.
    float4 uEffectTint;
    float4 uEffectFalloff;
    // FO4 cubemap environment mapping: x = cube bindless slot (< 0 = none/not yet resident),
    // y = envMapScale (fo76utils envScale × specular strength), z = material smoothness 0–1.
    float4 uEnvMap;
    // Opaque/instanced draws always upload x = 0. Declared for pixel-shader interface parity with
    // the blended per-draw path, which can bind an R32 scene-depth SRV after opaque rendering.
    float4 uSoftParticle;
    // FNV GRASS2000 specialized route. Wind = fixed world +Y in xy, recovered setting-interpolated
    // magnitude.z, wrapped time phase radians.w.
    float4 uTallGrassWind;
    // Classic FNV direct-sun specular LOD. Bounds are in baked mesh-root-local coordinates;
    // params = start fade, end fade, global LOD adjust, enabled.
    float4 uSpecularLodBounds;
    float4 uSpecularLodParams;
};

// Per-instance data is now JUST the world matrix (64 bytes). Everything else is per-batch.
StructuredBuffer<float4x4> uInstanceWorlds : register(t8);

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

// PC-final package013 SLS2000.vso (active ID193/BSSM_ADT) normalizes the complete transformed
// tangent-space directional light per vertex. Remove only uniform placement scale before that
// normalization; authored basis magnitudes remain part of the transform.
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

float ClassicSpecularLodFade(float4x4 world)
{
    if (uSpecularLodParams.w < 0.5 || uSpecularLodParams.y <= 0.0)
    {
        return 1.0;
    }

    float3 worldCenter = mul(world, float4(uSpecularLodBounds.xyz, 1.0)).xyz;
    // CPU System.Numerics rows become HLSL columns under this shader's established matrix-byte
    // convention. Read those conceptual columns so the opaque GPU route uses the same conservative
    // largest-basis radius scale as the direct/blended CPU route, including malformed non-uniform input.
    float scaleX = length(float3(world[0].x, world[1].x, world[2].x));
    float scaleY = length(float3(world[0].y, world[1].y, world[2].y));
    float scaleZ = length(float3(world[0].z, world[1].z, world[2].z));
    float worldRadius = uSpecularLodBounds.w * max(scaleX, max(scaleY, scaleZ));
    float distance = (length(worldCenter - uCameraPosFogPower.xyz) - worldRadius) *
        uSpecularLodParams.z;
    if (distance < uSpecularLodParams.x)
    {
        return 1.0;
    }

    if (distance >= uSpecularLodParams.y)
    {
        return 0.0;
    }

    return 1.0 - (distance - uSpecularLodParams.x) /
        (uSpecularLodParams.y - uSpecularLodParams.x);
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

VSOutput main(VSInput input, uint instanceId : SV_InstanceID)
{
    float4x4 world = uInstanceWorlds[uInstanceBase + instanceId];
#ifdef SHADOW_CARD_LIGHT_FACING
    // Shadow replay shares this VS/ABI but never evaluates a camera-distance shading term.
    float specularLodFade = 1.0;
#else
    float specularLodFade = ClassicSpecularLodFade(world);
#endif
    bool isTallGrass = IsTallGrassMaterial(uTextureState.z);
    float tallGrassWindWeight = isTallGrass ? saturate(input.aVertexColor.a) : 0.0;

    // SpeedTree bark/frond payload. The SPT builder preserves the ordinary orthonormal TBN
    // directions while encoding the wind-matrix index and authored branch weight in their lengths:
    //   |T| = matrixIndex + 1, |B| = weight + 1.
    // Decode before either deformation or normal-map shading; this keeps GpuVertex unchanged and is
    // scoped by the explicit negative TextureState marker, so ordinary NIF tangents are never treated
    // as wind data. The recovered STBRANCH transform is the same weighted matrix interpolation used
    // by the leaf path: p' = lerp(p, M[index] * p, weight).
    float3 modelPosition = input.aPosition;
    float3 modelNormal = input.aNormal;
    float3 modelTangent = input.aTangent;
    float3 modelBitangent = input.aBitangent;
    if (uTextureState.y < -0.5)
    {
        float tangentLength = length(modelTangent);
        float bitangentLength = length(modelBitangent);
        int windIdx = (int)min(max(floor(tangentLength + 0.5) - 1.0, 0.0), 3.0);
        float windWeight = saturate(bitangentLength - 1.0);

        modelTangent = tangentLength > 1e-8 ? modelTangent / tangentLength : float3(1.0, 0.0, 0.0);
        modelBitangent = bitangentLength > 1e-8 ? modelBitangent / bitangentLength : float3(0.0, 1.0, 0.0);
#ifndef SHADOW_CARD_LIGHT_FACING
        if (uWindMatrixValid != 0 && windWeight > 0.0)
        {
            float3x3 windRotation = (float3x3)uWindMatrices[windIdx];
            float3 swayedPosition = mul(uWindMatrices[windIdx], float4(modelPosition, 1.0)).xyz;
            modelPosition = lerp(modelPosition, swayedPosition, windWeight);
            modelNormal = normalize(lerp(modelNormal, mul(windRotation, modelNormal), windWeight));
            modelTangent = normalize(lerp(modelTangent, mul(windRotation, modelTangent), windWeight));
            modelBitangent = normalize(lerp(modelBitangent, mul(windRotation, modelBitangent), windWeight));
        }
#endif
    }

    VSOutput o;
    float4 worldPos;
    if (uTextureState.y > 0.5)
    {
        // Per-card leaf billboard: the vertex carries the card CENTER (aTangent) and the signed 2D
        // card-space offset (aBitangent.xy). Rebuild the quad facing the camera around the world-space
        // center, scaled by the instance's uniform REFR scale. (SpeedTree builds leaf cards CPU-side as
        // flat 2D offsets around a center and re-faces them to the camera each frame — CLeafGeometry; we
        // do that same transform here in the VS.)
        // aBitangent.z packs the engine's STLEAF v3.z, widened for the wind-matrix slot:
        // integer = LeafBase phase slot (0..47, per-corner) + 48·windMatrixIndex (0..3);
        // fraction = min(dimmerByte/255, .99), consumed as BOTH lighting and phase jitter.
        // Engine v3.x's independent wind-matrix weight is encoded as |aNormal|-1; normalizing
        // aNormal restores the authored lighting direction.
        float packedSlot = floor(input.aBitangent.z);
        float dimmerFraction = frac(input.aBitangent.z);
        float encodedNormalLength = length(input.aNormal);
        float windWeight = saturate(encodedNormalLength - 1.0);
        float slot = fmod(packedSlot, 48.0);
        int windIdx = (int)(packedSlot * (1.0 / 48.0));

        // The recovered STLEAF order is corner construction FIRST, wind-matrix lerp SECOND. Camera
        // billboard axes arrive in world space, so convert them into model space with the inverse
        // of the instance's uniform-scale rotation. This lets every corner follow
        //   final = lerp(modelCorner, WindMatrix[index] * modelCorner, weight)
        // instead of the old center-only approximation (which left large card edges rigid).
        float3 modelCenter = input.aTangent;
        float scale = length(float3(world[0].x, world[0].y, world[0].z)); // uniform REFR scale
        float3x3 worldLinear = (float3x3)world;
        float3x3 worldRotation = worldLinear / max(scale, 1e-8);

        // SpeedTree leaf ROCK/RUSTLE — the engine STLEAF vertex math, ported verbatim from the
        // PC STLEAF000-003.vso disasm (design doc A.6). Each corner slot lands a slightly
        // different phase (Δ = π/48), the authentic per-card shear.
        //   rock   = in-plane spin of the card offset;
        //   rustle = yaw of the billboard basis about the tree's up axis.
        float phase = (slot + dimmerFraction) * (1.0 / 48.0);
        float rockA   = uWind.x * sin(3.14159265 * (phase + uWind.y));
        float rustleA = uWind.z * sin(3.14159265 * (phase + uWind.w));
        float sK, cK; sincos(rockA, sK, cK);
        float sR, cR; sincos(rustleA, sR, cR);
        float2 c  = input.aBitangent.xy;
        float2 ck = float2(cK * c.x - sK * c.y, sK * c.x + cK * c.y);
#ifdef SHADOW_CARD_LIGHT_FACING
        float3 cardRightWorld = uShadowCardRight.xyz; // light-perpendicular basis (see PerFrame)
        float3 cardUpWorld    = uShadowCardUp.xyz;
#else
        float3 cardRightWorld = uCameraRight.xyz;
        float3 cardUpWorld    = uCameraUp.xyz;
#endif
        // For an orthonormal R, inverse(R)·v == transpose(R)·v. HLSL's row-vector mul(v,R)
        // expresses that transpose application for the column-vector world transform used below.
        float3 cardRight = normalize(mul(cardRightWorld, worldRotation));
        float3 cardUp = normalize(mul(cardUpWorld, worldRotation));
        // Rustle is a yaw around the tree's MODEL-space Z axis, not world Z.
        float3 Rr = float3(cR * cardRight.x - sR * cardRight.y,
                           sR * cardRight.x + cR * cardRight.y, cardRight.z);
        float3 Ur = float3(cR * cardUp.x - sR * cardUp.y,
                           sR * cardUp.x + cR * cardUp.y, cardUp.z);

        float3 modelCorner = modelCenter + Rr * ck.x + Ur * ck.y;
#ifndef SHADOW_CARD_LIGHT_FACING
        if (uWindMatrixValid != 0)
        {
            float3 swayedCorner = mul(uWindMatrices[windIdx], float4(modelCorner, 1.0)).xyz;
            modelCorner = lerp(modelCorner, swayedCorner, windWeight);
        }
#endif
        worldPos = mul(world, float4(modelCorner, 1.0));
        // STLEAF per-corner normal puff (PC STLEAF000.vso: N = normalize(normalize(cornerDir) ·
        // LeafLighting.y + leafNormal)): each corner's normal leans outward along its card offset,
        // so the card shades like a rounded leaf cluster instead of a flat plate. uCameraRight.w
        // carries the LeafLighting.y adjust (0 = flat per-leaf normal). Pivot corners can sit at a
        // zero offset — guard the normalize.
        float3 leafN = encodedNormalLength > 1e-8
            ? input.aNormal / encodedNormalLength
            : float3(0.0, 0.0, 1.0);
        float2 cornerOff = input.aBitangent.xy;
        if (uCameraRight.w > 0.0 && dot(cornerOff, cornerOff) > 1e-8)
        {
            float3 cornerDir = normalize(cardRight * cornerOff.x + cardUp * cornerOff.y);
            leafN = normalize(leafN + cornerDir * uCameraRight.w);
        }
#ifndef SHADOW_CARD_LIGHT_FACING
        if (uWindMatrixValid != 0 && windWeight > 0.0)
        {
            float3 windLeafN = mul((float3x3)uWindMatrices[windIdx], leafN);
            leafN = normalize(lerp(leafN, windLeafN, windWeight));
        }
#endif
        leafN = normalize(mul(worldLinear, leafN));
        o.vWorldNormal = leafN;
    }
    else
    {
        // world's translation is CPU-folded to the render origin, so this is already the camera-relative
        // position (absolute when renderOrigin == 0). The prior post-multiply "-= uCameraOrigin" is gone.
        worldPos = mul(world, float4(modelPosition, 1.0));
        o.vWorldNormal = mul((float3x3)world, modelNormal);
    }

    // Retail GRASS2000 applies wind in WORLD XY after the card's scale/fit-to-slope transform and
    // immediately before placement translation. Adding it to modelPosition would incorrectly
    // rotate or tilt the weather direction per blade. The phase seed uses ABSOLUTE placement XY;
    // adding uCameraOrigin makes it invariant under the renderer's snapped-origin rebasing.
    float3 relativePlacement = mul(world, float4(0.0, 0.0, 0.0, 1.0)).xyz;
    if (isTallGrass && uTallGrassWind.z != 0.0)
    {
        float2 absolutePlacement = relativePlacement.xy + uCameraOrigin.xy;
        float phase = WrapTallGrassPhase(
            (absolutePlacement.x + absolutePlacement.y) / 128.0 + uTallGrassWind.w);
        float weightedOffset = sin(phase) * uTallGrassWind.z *
            tallGrassWindWeight * tallGrassWindWeight;
        worldPos.xy += uTallGrassWind.xy * weightedOffset;
    }

    o.Position = mul(uViewProj, worldPos);
    o.vWorldPos = worldPos.xyz; // camera-relative world pos (matches the shader camera = 0 for fog/spec)
    o.vTexCoord = input.aTexCoord + uUvScroll;
    o.vVertexColor = input.aVertexColor;
    if (isTallGrass)
    {
        // Raw authored alpha is wind data only. Coverage remains texture-only in both the main
        // reference PS and the shadow cutout PS.
        o.vVertexColor.a = 1.0;
    }
    if (uTextureState.y > 0.5)
    {
        // Retail STLEAF multiplies the lit vertex color by frc(v3.z), not by a repurposed copy.
        // Alpha remains the authored opaque vertex alpha; the diffuse texture supplies cutout alpha.
        float leafDimmer = frac(input.aBitangent.z);
        o.vVertexColor.rgb = float3(leafDimmer, leafDimmer, leafDimmer);
    }
    o.vTangent = mul((float3x3)world, modelTangent);
    o.vBitangent = mul((float3x3)world, modelBitangent);
    float activeAdtUniformScale = length(mul((float3x3)world, float3(1.0, 0.0, 0.0)));
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
    o.vSoftParticle = uSoftParticle;
    o.vSpecularLodFade = specularLodFade;
    return o;
}
