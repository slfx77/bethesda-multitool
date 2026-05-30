// SM 5.0 pixel shader — port of skin.frag.glsl.
// SKIN2000.pso replica — Bethesda's face/skin pixel shader.
// Hemisphere ambient + diffuse + Fresnel rim light + bump mapping.

cbuffer Uniforms : register(b0)
{
    float4x4 uViewProj;
    float4x4 uView;
    float4 uLightDir;
    float4 uHalfVec;
    float4 uAmbient;
    float4 uMaterial;
    float4 uTintColor;
    float4 uFlags;
};

Texture2D    tDiffuse   : register(t0);
SamplerState sDiffuse   : register(s0);
Texture2D    tNormalMap : register(t1);
SamplerState sNormalMap : register(s1);

struct PSInput
{
    float4 Position     : SV_Position;
    float3 vWorldNormal : TEXCOORD0;
    float2 vTexCoord    : TEXCOORD1;
    float4 vVertexColor : TEXCOORD2;
    float3 vTangent     : TEXCOORD3;
    float3 vBitangent   : TEXCOORD4;
    float  vDepth       : TEXCOORD5;
    bool   IsFrontFace  : SV_IsFrontFace;
};

struct PSOutput
{
    float4 fragColor : SV_Target0;
    float  depth     : SV_Depth;
};

// Flag bits
static const uint HAS_TEXTURE     = 1u;
static const uint HAS_NORMALS     = 2u;
static const uint HAS_BUMP        = 4u;
static const uint HAS_VCOL        = 8u;
static const uint IS_EMISSIVE     = 16u;
static const uint IS_DOUBLE_SIDED = 32u;
static const uint HAS_ALPHA_BLEND = 64u;
static const uint HAS_ALPHA_TEST  = 128u;
static const uint IS_EYE_ENVMAP   = 256u;
static const uint HAS_TINT        = 512u;
static const uint IS_FACEGEN      = 1024u;

float computeShade(float3 n, bool twoSidedLighting)
{
    float3 lightDir = uLightDir.xyz;
    float3 halfVec  = uHalfVec.xyz;
    float hdotNegL  = uHalfVec.w;
    float skyAmb    = uAmbient.x;
    float gndAmb    = uAmbient.y;
    float lightInt  = uAmbient.z;

    // Hemisphere ambient: blend between ground and sky based on normal Y
    float hemiBlend = -n.y * 0.5 + 0.5;
    float ambient = gndAmb + (skyAmb - gndAmb) * hemiBlend;

    // NdotL — diffuse with wrap lighting to soften terminator on FaceGen creases
    float wrap = 0.25;
    float rawNdotL = dot(n, lightDir);
    if (twoSidedLighting)
        rawNdotL = abs(rawNdotL);
    float NdotL = max(0.0, (rawNdotL + wrap) / (1.0 + wrap));

    // NdotH — for Fresnel rim light
    float NdotH = max(0.0, dot(n, halfVec));

    // SKIN2000 Fresnel: (1 - NdotH)^2 * dot(halfVec, -lightDir)
    float oneMinusNdotH = 1.0 - NdotH;
    float fresnel = max(0.0, hdotNegL) * oneMinusNdotH * oneMinusNdotH;

    // SKIN2000: min(lightColor * NdotL + lightColor * fresnel * 0.5, 1.0) + ambient
    float directional = min(lightInt * NdotL + lightInt * fresnel * 0.5, 1.0);

    return saturate(directional + ambient);
}

float computeTintedShade(float3 n)
{
    float3 lightDir = uLightDir.xyz;
    float skyAmb   = uAmbient.x;
    float gndAmb   = uAmbient.y;
    float lightInt = uAmbient.z;

    float hemiBlend = -n.y * 0.5 + 0.5;
    float ambientMid = (gndAmb + skyAmb) * 0.5;
    float ambient = ambientMid + (hemiBlend - 0.5) * 0.08;

    float wrap = 0.5;
    float rawNdotL = abs(dot(n, lightDir));
    float NdotL = max(0.0, (rawNdotL + wrap) / (1.0 + wrap));
    float directional = lightInt * NdotL;

    return saturate(directional + ambient);
}

PSOutput main(PSInput input)
{
    PSOutput o;
    uint flags = (uint)uFlags.x;
    float3 normal = normalize(input.vWorldNormal);

    // Handle double-sided: flip normal for back faces and bias depth to prevent Z-fighting.
    // input.Position.z carries gl_FragCoord.z equivalent (NDC depth after viewport).
    if ((flags & IS_DOUBLE_SIDED) != 0u && !input.IsFrontFace)
    {
        normal = -normal;
        o.depth = input.Position.z + 0.0001;
    }
    else
    {
        o.depth = input.Position.z;
    }

    // Bump mapping: perturb normal using normal map + TBN
    if ((flags & HAS_BUMP) != 0u)
    {
        float3 mapN = tNormalMap.Sample(sNormalMap, input.vTexCoord).rgb * 2.0 - 1.0;
        mapN.y = -mapN.y; // DirectX convention (Y-down normal maps)
        float bumpStr = uAmbient.w;
        mapN.xy *= bumpStr;

        float3 T = normalize(input.vTangent);
        float3 B = normalize(input.vBitangent);
        float3 N = normal;
        float3x3 TBN = float3x3(T, B, N);
        // float3x3 from row-vectors: row 0 = T, row 1 = B, row 2 = N. We want
        // mapN_x * T + mapN_y * B + mapN_z * N. Doing `mul(mapN, TBN)` gives the
        // row-vector convention: mapN.x * row0 + mapN.y * row1 + mapN.z * row2.
        normal = normalize(mul(mapN, TBN));
    }

    // Compute shade
    float shade;
    if ((flags & IS_EMISSIVE) != 0u)
    {
        shade = 1.0;
    }
    else if ((flags & HAS_NORMALS) != 0u)
    {
        if ((flags & HAS_TINT) != 0u)
            shade = computeTintedShade(normal);
        else
            shade = computeShade(normal, (flags & IS_DOUBLE_SIDED) != 0u);

        // Eye specular: approximate SLS2057.pso cubemap reflection
        if ((flags & IS_EYE_ENVMAP) != 0u)
        {
            float specNdotH = max(0.0, dot(normal, uHalfVec.xyz));
            shade = min(shade + pow(specNdotH, 16.0) * uMaterial.y * 0.6, 1.0);
        }
    }
    else
    {
        shade = 0.6; // Flat shade fallback
    }

    // Sample texture
    float4 texColor = float4(0.78, 0.78, 0.78, 1.0); // Default grey
    if ((flags & HAS_TEXTURE) != 0u)
    {
        texColor = tDiffuse.Sample(sDiffuse, input.vTexCoord);
    }

    // Apply vertex alpha (always — CPU does this before alpha test for both tint and non-tint paths)
    if ((flags & HAS_VCOL) != 0u)
    {
        texColor.a *= input.vVertexColor.a;
    }

    // Alpha test — must run BEFORE material alpha multiplication (matches CPU ordering:
    // CPU tests raw texture_alpha * vertex_alpha, not texture_alpha * vertex_alpha * materialAlpha)
    if ((flags & HAS_ALPHA_TEST) != 0u)
    {
        float threshold = uMaterial.z;
        uint func = (uint)uMaterial.w;
        // `pass` is a reserved keyword in HLSL (technique/pass) — use `keepPixel` instead.
        bool keepPixel = true;

        // Alpha test functions: 0=ALWAYS, 1=LESS, 2=EQUAL, 3=LEQUAL, 4=GREATER, 5=NOTEQUAL, 6=GEQUAL, 7=NEVER
        if (func == 1u) keepPixel = texColor.a < threshold;
        else if (func == 2u) keepPixel = abs(texColor.a - threshold) < 0.004;
        else if (func == 3u) keepPixel = texColor.a <= threshold;
        else if (func == 4u) keepPixel = texColor.a > threshold;
        else if (func == 5u) keepPixel = abs(texColor.a - threshold) >= 0.004;
        else if (func == 6u) keepPixel = texColor.a >= threshold;
        else if (func == 7u) keepPixel = false;
        // func == 0: ALWAYS, keepPixel = true (default)

        if (!keepPixel) discard;
    }
    else if ((flags & HAS_ALPHA_BLEND) != 0u && (texColor.a == 0.0 || texColor.a < 16.0 / 255.0))
    {
        discard; // Skip fully transparent + DXT fringe on blended meshes
    }

    // Color modulation: tint and vertex color are mutually exclusive on RGB.
    if ((flags & HAS_TINT) != 0u)
    {
        texColor.rgb *= 2.0 * uTintColor.rgb;
    }
    else if ((flags & HAS_VCOL) != 0u)
    {
        texColor.rgb *= input.vVertexColor.rgb;
    }

    // Match CPU export alpha semantics:
    // - opaque + cutout materials write solid alpha after any discard
    // - only true blended materials preserve texture/material alpha in the output
    float outAlpha = ((flags & HAS_ALPHA_BLEND) != 0u)
        ? texColor.a * uMaterial.x
        : 1.0;

    o.fragColor = float4(texColor.rgb * shade, outAlpha);
    return o;
}
