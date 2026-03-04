#version 450

// Unified vertex shader for all NIF rendering.
// Transforms vertices by orthographic view-projection and passes
// interpolated attributes to the fragment shader.

layout(set = 0, binding = 0) uniform Uniforms {
    mat4 uViewProj;
    mat4 uView;           // 3x3 view rotation (used for normals/tangents)
    vec4 uLightDir;       // xyz = normalized light direction, w = unused
    vec4 uHalfVec;        // xyz = half vector, w = HdotNegL
    vec4 uAmbient;        // x = skyAmbient, y = groundAmbient, z = lightIntensity, w = bumpStrength
    vec4 uMaterial;       // x = materialAlpha, y = envMapScale, z = alphaTestThreshold, w = unused
    vec4 uTintColor;      // rgb = tint, a = unused
    vec4 uFlags;          // x = bitfield (passed as float, cast to uint in frag shader)
};

layout(location = 0) in vec3 aPosition;
layout(location = 1) in vec3 aNormal;
layout(location = 2) in vec2 aTexCoord;
layout(location = 3) in vec4 aVertexColor;
layout(location = 4) in vec3 aTangent;
layout(location = 5) in vec3 aBitangent;

layout(location = 0) out vec3 vWorldNormal;
layout(location = 1) out vec2 vTexCoord;
layout(location = 2) out vec4 vVertexColor;
layout(location = 3) out vec3 vTangent;
layout(location = 4) out vec3 vBitangent;
layout(location = 5) out float vDepth;

void main()
{
    gl_Position = uViewProj * vec4(aPosition, 1.0);

    // Rotate normals/tangents/bitangents into view space using the 3x3 part of uView.
    // This matches the CPU renderer which pre-rotates all geometry into view space,
    // so the lighting computation (in view space) produces identical results.
    mat3 viewRot = mat3(uView);
    vWorldNormal = viewRot * aNormal;
    vTangent = viewRot * aTangent;
    vBitangent = viewRot * aBitangent;

    vTexCoord = aTexCoord;
    vVertexColor = aVertexColor;
    vDepth = aPosition.z; // For back-to-front sorting diagnostics
}
