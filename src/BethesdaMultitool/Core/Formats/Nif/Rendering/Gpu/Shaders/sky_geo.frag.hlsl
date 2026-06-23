// Sky-GEOMETRY fragment shader. One shader, three modes (selected per draw via uScrollMode.z), each
// paired with its own blend-state PSO:
//   mode 0 = atmosphere : the horizon->top gradient (b3 sky colors), OPAQUE background.
//   mode 1 = stars      : the star texture on the dome's OWN UVs, ADDITIVE, night-faded.
//   mode 2 = clouds      : the cloud texture on the dome's OWN UVs, ALPHA, tinted + scrolled.
// Sampling the NIF's authored UVs (no gnomonic/stereographic projection) is what kills the tiling and
// the horizon seam. +Z is up.

Texture2D    textures[] : register(t0, space1);
SamplerState sSky       : register(s0);

cbuffer SkyGeo : register(b0)
{
    float4x4 uViewProj;
    float4 uCamPosScale;
    float4 uTintParam;     // rgb = layer tint, a = layer fade/opacity
    float4 uScrollMode;    // xy = cloud UV scroll, z = mode, w unused
    uint4  uTexIndex;      // x = bindless diffuse index
};

// Shared scene atmosphere (b3, bound once per frame before the sky draws). Only the sky colors are read.
cbuffer Atmosphere : register(b3)
{
    float4 uSunDirIntensity;
    float4 uSunColorLighting;
    float4 uAmbientColor;
    float4 uSkyTopSkyEnabled;   // rgb = sky-top color
    float4 uSkyHorizon;         // rgb = sky-horizon color
};

struct PSInput
{
    float4 Position : SV_Position;
    float3 vDir     : TEXCOORD0;
    float2 vUv      : TEXCOORD1;
    float4 vColor   : COLOR0;    // NIF per-vertex RGBA; ALPHA = the artist-baked cloud-dome horizon fade
};

float4 main(PSInput input) : SV_Target
{
    float3 dir = normalize(input.vDir);
    int mode = (int)(uScrollMode.z + 0.5);

    if (mode == 0)
    {
        // Atmosphere gradient dome: horizon (dir.z~0) -> top (dir.z~1). Opaque background fill.
        float3 sky = lerp(uSkyHorizon.rgb, uSkyTopSkyEnabled.rgb, saturate(dir.z));
        return float4(sky, 1.0);
    }

    // Stars / clouds: the NIF's OWN authored UVs (no projection => no tiling stretch, no horizon seam).
    float2 uv = input.vUv + uScrollMode.xy;
    float4 tex = textures[NonUniformResourceIndex(uTexIndex.x)].Sample(sSky, uv);

    // The engine draws the sky meshes as texture * per-vertex color, alpha-blended. The cloud dome's
    // horizon fade is BAKED INTO that vertex alpha (verified in clouds.nif: cloudcloudy's alpha runs ~2 at
    // the rim/horizon to 255 overhead; CloudClear ~70 -> 255; the horizon bands ~170 flat). So the fade is
    // the mesh's `input.vColor.a`, not a guessed shader smoothstep — and stacked caps blend overhead while
    // their rims vanish into the hazy horizon. vColor.rgb applies the artist tint (white on these caps).
    if (mode == 1)
    {
        // Stars: additive (PSO SrcAlpha/One). Texture + vertex + night-fade alpha gate the add.
        return float4(tex.rgb * uTintParam.rgb * input.vColor.rgb, tex.a * uTintParam.a * input.vColor.a);
    }

    // Clouds: alpha (PSO SrcAlpha/InvSrcAlpha), tinted by daylight, opacity-scaled, vertex-alpha faded.
    return float4(tex.rgb * uTintParam.rgb * input.vColor.rgb,
                  saturate(tex.a * uTintParam.a * input.vColor.a));
}
