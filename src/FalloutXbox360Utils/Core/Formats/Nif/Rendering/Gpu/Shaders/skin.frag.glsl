#version 450

// SKIN2000.pso replica — Bethesda's face/skin pixel shader.
// Hemisphere ambient + diffuse + Fresnel rim light + bump mapping.

layout(set = 0, binding = 0) uniform Uniforms {
    mat4 uViewProj;
    mat4 uView;           // 3x3 view rotation (normals rotated in vertex shader)
    vec4 uLightDir;       // xyz = normalized light direction
    vec4 uHalfVec;        // xyz = half vector, w = HdotNegL
    vec4 uAmbient;        // x = skyAmbient, y = groundAmbient, z = lightIntensity, w = bumpStrength
    vec4 uMaterial;       // x = materialAlpha, y = envMapScale, z = alphaTestThreshold (0-1), w = alphaTestFunc
    vec4 uTintColor;      // rgb = tint, a = unused
    vec4 uFlags;          // x = bitfield (passed as float, cast to uint below)
};

layout(set = 1, binding = 0) uniform texture2D tDiffuse;
layout(set = 1, binding = 1) uniform sampler sDiffuse;
layout(set = 1, binding = 2) uniform texture2D tNormalMap;
layout(set = 1, binding = 3) uniform sampler sNormalMap;

layout(location = 0) in vec3 vWorldNormal;
layout(location = 1) in vec2 vTexCoord;
layout(location = 2) in vec4 vVertexColor;
layout(location = 3) in vec3 vTangent;
layout(location = 4) in vec3 vBitangent;
layout(location = 5) in float vDepth;

layout(location = 0) out vec4 fragColor;

// Flag bits
const uint HAS_TEXTURE     = 1u;
const uint HAS_NORMALS     = 2u;
const uint HAS_BUMP        = 4u;
const uint HAS_VCOL        = 8u;
const uint IS_EMISSIVE     = 16u;
const uint IS_DOUBLE_SIDED = 32u;
const uint HAS_ALPHA_BLEND = 64u;
const uint HAS_ALPHA_TEST  = 128u;
const uint IS_EYE_ENVMAP   = 256u;
const uint HAS_TINT        = 512u;

float computeShade(vec3 n, bool twoSidedLighting)
{
    vec3 lightDir = uLightDir.xyz;
    vec3 halfVec = uHalfVec.xyz;
    float hdotNegL = uHalfVec.w;
    float skyAmb = uAmbient.x;
    float gndAmb = uAmbient.y;
    float lightInt = uAmbient.z;

    // Hemisphere ambient: blend between ground and sky based on normal Y
    float hemiBlend = -n.y * 0.5 + 0.5;
    float ambient = gndAmb + (skyAmb - gndAmb) * hemiBlend;

    // NdotL — diffuse with wrap lighting to soften terminator on FaceGen creases
    float wrap = 0.25;
    float rawNdotL = dot(n, lightDir);
    // Two-sided lighting: thin surfaces (skirts, flags) are lit from both sides
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

    return clamp(directional + ambient, 0.0, 1.0);
}

void main()
{
    uint flags = uint(uFlags.x);
    vec3 normal = normalize(vWorldNormal);

    // Handle double-sided: flip normal for back faces and bias depth to prevent Z-fighting
    if ((flags & IS_DOUBLE_SIDED) != 0u && !gl_FrontFacing)
    {
        normal = -normal;
        gl_FragDepth = gl_FragCoord.z + 0.0001;
    }
    else
    {
        gl_FragDepth = gl_FragCoord.z;
    }

    // Bump mapping: perturb normal using normal map + TBN
    if ((flags & HAS_BUMP) != 0u)
    {
        vec3 mapN = texture(sampler2D(tNormalMap, sNormalMap), vTexCoord).rgb * 2.0 - 1.0;
        mapN.y = -mapN.y; // DirectX convention (Y-down normal maps)
        float bumpStr = uAmbient.w;
        mapN.xy *= bumpStr;

        vec3 T = normalize(vTangent);
        vec3 B = normalize(vBitangent);
        vec3 N = normal;
        mat3 TBN = mat3(T, B, N);
        normal = normalize(TBN * mapN);
    }

    // Compute shade
    float shade;
    if ((flags & IS_EMISSIVE) != 0u)
    {
        shade = 1.0;
    }
    else if ((flags & HAS_NORMALS) != 0u)
    {
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
    vec4 texColor = vec4(0.78, 0.78, 0.78, 1.0); // Default grey
    if ((flags & HAS_TEXTURE) != 0u)
    {
        texColor = texture(sampler2D(tDiffuse, sDiffuse), vTexCoord);
    }

    // Apply vertex alpha (always — CPU does this before alpha test for both tint and non-tint paths)
    if ((flags & HAS_VCOL) != 0u)
    {
        texColor.a *= vVertexColor.a;
    }

    // Alpha test — must run BEFORE material alpha multiplication (matches CPU ordering:
    // CPU tests raw texture_alpha * vertex_alpha, not texture_alpha * vertex_alpha * materialAlpha)
    if ((flags & HAS_ALPHA_TEST) != 0u)
    {
        float threshold = uMaterial.z;
        uint func = uint(uMaterial.w);
        bool pass = true;

        // Alpha test functions: 0=ALWAYS, 1=LESS, 2=EQUAL, 3=LEQUAL, 4=GREATER, 5=NOTEQUAL, 6=GEQUAL, 7=NEVER
        if (func == 1u) pass = texColor.a < threshold;
        else if (func == 2u) pass = abs(texColor.a - threshold) < 0.004;
        else if (func == 3u) pass = texColor.a <= threshold;
        else if (func == 4u) pass = texColor.a > threshold;
        else if (func == 5u) pass = abs(texColor.a - threshold) >= 0.004;
        else if (func == 6u) pass = texColor.a >= threshold;
        else if (func == 7u) pass = false;
        // func == 0: ALWAYS, pass = true (default)

        if (!pass) discard;
    }
    else if ((flags & HAS_ALPHA_BLEND) != 0u && (texColor.a == 0.0 || texColor.a < 16.0 / 255.0))
    {
        discard; // Skip fully transparent + DXT fringe on blended meshes
    }

    // Color modulation: tint and vertex color are MUTUALLY EXCLUSIVE on RGB.
    // CPU: when HasTintColor, only the GREEN vertex channel is used as a scalar in the
    // SM3002.pso tint formula, and RGB vertex color multiplication is skipped entirely.
    // When no tint, standard RGB vertex color modulation is applied.
    if ((flags & HAS_TINT) != 0u)
    {
        // SM3002.pso hair tint: tintedShade = 2 * (vc * (HairTint - 0.5) + 0.5)
        // vc = vertex color GREEN channel only (scalar), NOT full RGB
        vec3 tint = uTintColor.rgb;
        float vc = ((flags & HAS_VCOL) != 0u) ? vVertexColor.g : 1.0;
        texColor.rgb = 2.0 * (vc * (tint - 0.5) + 0.5) * texColor.rgb;
    }
    else if ((flags & HAS_VCOL) != 0u)
    {
        texColor.rgb *= vVertexColor.rgb;
    }

    // Apply shading + material alpha
    fragColor = vec4(texColor.rgb * shade, texColor.a * uMaterial.x);
}
