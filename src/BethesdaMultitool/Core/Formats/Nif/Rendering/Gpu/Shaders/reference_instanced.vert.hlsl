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
};

// Shared atmosphere CB (b3). References no longer read uCameraOrigin here: the render origin is folded
// into each instance world matrix's translation on the CPU (ReferenceRenderer12.Render's renderOrigin), so
// mul(world, pos) already yields the camera-relative position — keeping float32 precision far from the
// world origin (subtracting a ~52,000 absolute position AFTER the multiply lost it, Z-fighting distant
// architecture). The cbuffer is still declared for b3 layout parity (terrain/water read uCameraOrigin);
// the leading 8 float4 are the sun/sky/fog/camera fields this VS does not use.
cbuffer Atmosphere : register(b3)
{
    float4 uAtmospherePad[8];
    float4 uCameraOrigin; // retained for CB layout parity; references fold the origin CPU-side instead
};

// Per-batch (one DrawIndexedInstanced) constants. TextureState.x marks BC5/ATI2 normal
// decode; TexIndices.x = diffuse bindless slot, .y = normal bindless slot. uInstanceBase
// is the start offset of this batch's worlds inside the shared instance buffer.
cbuffer InstanceDraw : register(b1)
{
    float4 uAlphaState;
    float4 uRenderState;
    float4 uTextureState; // .x = BC5 normal decode, .y = leaf-billboard mode (>0.5)
    uint4  uTexIndices;
    uint   uInstanceBase;
    // Per-submesh UV scroll offset, CPU-wrapped (frac(velocity × animClock)) so precision never
    // drifts. Packs into the former uint3 padding — same 16-byte register, layout unchanged; the
    // CPU zero-fills it for static submeshes, so the add below is a no-op until an animated
    // submesh writes a real offset.
    float2 uUvScroll;
    uint   uInstanceDrawPad;
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
    // (startAngle, stopAngle, startOpacity, stopOpacity).
    float4 uEffectTint;
    float4 uEffectFalloff;
    // FO4 cubemap environment mapping: x = cube bindless slot (< 0 = none/not yet resident),
    // y = envMapScale (fo76utils envScale × specular strength), z = material smoothness 0–1.
    float4 uEnvMap;
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
    nointerpolation float4 vEnvMap        : TEXCOORD13; // x = cube slot (<0 none), y = scale, z = smoothness
};

VSOutput main(VSInput input, uint instanceId : SV_InstanceID)
{
    float4x4 world = uInstanceWorlds[uInstanceBase + instanceId];

    VSOutput o;
    float4 worldPos;
    if (uTextureState.y > 0.5)
    {
        // Per-card leaf billboard: the vertex carries the card CENTER (aTangent) and the signed 2D
        // card-space offset (aBitangent.xy). Rebuild the quad facing the camera around the world-space
        // center, scaled by the instance's uniform REFR scale. (SpeedTree builds leaf cards CPU-side as
        // flat 2D offsets around a center and re-faces them to the camera each frame — CLeafGeometry; we
        // do that same transform here in the VS.)
        float4 worldCenterAbs = mul(world, float4(input.aTangent, 1.0)); // world is CPU-folded camera-relative
        float3 worldCenter = worldCenterAbs.xyz;
        float scale = length(float3(world[0].x, world[0].y, world[0].z)); // uniform REFR scale

        // SpeedTree leaf ROCK/RUSTLE — the engine STLEAF vertex math, ported verbatim from the
        // PC STLEAF000-003.vso disasm (design doc A.6). aBitangent.z packs the engine's v3.z:
        // integer = the LeafBase phase slot (0..47, per-corner), fraction = the wind-matrix lerp
        // weight (unconsumed in v1 — the 4 wind matrices are the deferred v2). Each corner slot
        // lands a slightly different phase (Δ = π/48), the authentic per-card shear.
        //   rock   = in-plane spin of the card offset;
        //   rustle = yaw of the billboard basis about the tree's up axis.
        float slot = floor(input.aBitangent.z);
        float phase = slot * (1.0 / 48.0);
        float rockA   = uWind.x * sin(3.14159265 * (phase + uWind.y));
        float rustleA = uWind.z * sin(3.14159265 * (phase + uWind.w));
        float sK, cK; sincos(rockA, sK, cK);
        float sR, cR; sincos(rustleA, sR, cR);
        float2 c  = input.aBitangent.xy * scale;
        float2 ck = float2(cK * c.x - sK * c.y, sK * c.x + cK * c.y);
#ifdef SHADOW_CARD_LIGHT_FACING
        float3 cardRight = uShadowCardRight.xyz; // light-perpendicular basis (see PerFrame)
        float3 cardUp    = uShadowCardUp.xyz;
#else
        float3 cardRight = uCameraRight.xyz;
        float3 cardUp    = uCameraUp.xyz;
#endif
        float3 Rr = float3(cR * cardRight.x - sR * cardRight.y,
                           sR * cardRight.x + cR * cardRight.y, cardRight.z);
        float3 Ur = float3(cR * cardUp.x - sR * cardUp.y,
                           sR * cardUp.x + cR * cardUp.y, cardUp.z);

        worldPos = float4(worldCenter + Rr * ck.x + Ur * ck.y, 1.0);
        // STLEAF per-corner normal puff (PC STLEAF000.vso: N = normalize(normalize(cornerDir) ·
        // LeafLighting.y + leafNormal)): each corner's normal leans outward along its card offset,
        // so the card shades like a rounded leaf cluster instead of a flat plate. uCameraRight.w
        // carries the LeafLighting.y adjust (0 = flat per-leaf normal). Pivot corners can sit at a
        // zero offset — guard the normalize.
        float3 leafN = normalize(mul((float3x3)world, input.aNormal));
        float2 cornerOff = input.aBitangent.xy;
        if (uCameraRight.w > 0.0 && dot(cornerOff, cornerOff) > 1e-8)
        {
            float3 cornerDir = normalize(uCameraRight.xyz * cornerOff.x + uCameraUp.xyz * cornerOff.y);
            leafN = normalize(leafN + cornerDir * uCameraRight.w);
        }
        o.vWorldNormal = leafN;
    }
    else
    {
        // world's translation is CPU-folded to the render origin, so this is already the camera-relative
        // position (absolute when renderOrigin == 0). The prior post-multiply "-= uCameraOrigin" is gone.
        worldPos = mul(world, float4(input.aPosition, 1.0));
        o.vWorldNormal = mul((float3x3)world, input.aNormal);
    }

    o.Position = mul(uViewProj, worldPos);
    o.vWorldPos = worldPos.xyz; // camera-relative world pos (matches the shader camera = 0 for fog/spec)
    o.vTexCoord = input.aTexCoord + uUvScroll;
    o.vVertexColor = input.aVertexColor;
    o.vTangent = mul((float3x3)world, input.aTangent);
    o.vBitangent = mul((float3x3)world, input.aBitangent);
    o.vAlphaState = uAlphaState;
    o.vRenderState = uRenderState;
    o.vTextureState = uTextureState;
    o.vTexIndices = uTexIndices;
    o.vSpecular = uSpecular;
    o.vEffectTint = uEffectTint;
    o.vEffectFalloff = uEffectFalloff;
    o.vEnvMap = uEnvMap;
    return o;
}
