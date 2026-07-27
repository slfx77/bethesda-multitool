// Sky-GEOMETRY fragment shader. One shader, three modes (selected per draw via uScrollMode.z), each
// paired with its own blend-state PSO:
//   mode 0 = atmosphere : authored BlendColor weights, or the fallback horizon->top gradient, OPAQUE.
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
    float4 uScrollMode;    // xy = cloud UV scroll, z = mode, w = authored atmosphere blend weights
    uint4  uTexIndex;      // x = bindless diffuse index
    float4 uSkyUpper;      // recovered SKY BlendColor[2]
    float4 uSkyLower;      // recovered SKY BlendColor[1]
    float4 uSkyHorizon;    // recovered SKY BlendColor[0]
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
        if (uScrollMode.w > 0.5)
        {
            // Fallout 3/FNV SKY.vso is exactly:
            //   BlendColor[0] * vertex.r + BlendColor[1] * vertex.g + BlendColor[2] * vertex.b.
            // Vertex RGB therefore stores three blend WEIGHTS, not a literal color. Vertex alpha is the
            // authored horizon coverage. Opaque compositing over BlendColor[0] is equivalent to the
            // retail alpha-blended result while keeping this renderer's depth-free background contract.
            float3 weighted = (input.vColor.r * uSkyHorizon.rgb)
                            + (input.vColor.g * uSkyLower.rgb)
                            + (input.vColor.b * uSkyUpper.rgb);
            return float4(lerp(uSkyHorizon.rgb, weighted, input.vColor.a), 1.0);
        }

        // Missing-asset fallback only: shaped horizon -> authored sky upper by elevation.
        float3 sky = lerp(uTintParam.rgb, uSkyUpper.rgb, saturate(dir.z));
        return float4(sky, 1.0);
    }

    // Stars / clouds: the NIF's OWN authored UVs (no projection => no tiling stretch, no horizon seam).
    float2 uv = input.vUv + uScrollMode.xy;
    float4 tex = textures[NonUniformResourceIndex(uTexIndex.x)].Sample(sSky, uv);

    // Retail SKY*.vso treats vertex RGB as weights for three BlendColor rows. Stars/clouds select one
    // row, but that row is not consistently R across games/shapes: Oblivion's first Clouds.nif cap uses
    // R while most vertices in its second cap use B. Collapse the one-hot selector to its magnitude;
    // hard-coding R turns that second cap black. Multiplying literal RGB here would instead turn the
    // retail (1,0,0)/(0,0,1) blend selectors into a red/blue tint. The cloud dome's
    // horizon fade is BAKED INTO that vertex alpha (verified in clouds.nif: cloudcloudy's alpha runs ~2 at
    // the rim/horizon to 255 overhead; CloudClear ~70 -> 255; the horizon bands ~170 flat). So the fade is
    // the mesh's `input.vColor.a`, not a guessed shader smoothstep — and stacked caps blend overhead while
    // their rims vanish into the hazy horizon.
    float vertexWeight = max(input.vColor.r, max(input.vColor.g, input.vColor.b));
    if (mode == 1)
    {
        // Stars: additive (PSO SrcAlpha/One). Texture + vertex + night-fade alpha gate the add.
        return float4(tex.rgb * uTintParam.rgb * vertexWeight, tex.a * uTintParam.a * input.vColor.a);
    }

    // Clouds: alpha (PSO SrcAlpha/InvSrcAlpha), tinted by daylight, opacity-scaled, vertex-alpha faded.
    // Clouds: alpha (PSO SrcAlpha/InvSrcAlpha). Color = cloud texture × the weather's PNAM tint (the
    // engine's per-draw cloud color uniform). The per-vertex RGB is intentionally NOT applied — these cap
    // meshes carry a blue-ish vertex tint that reads as a strong red cast, and the PNAM color is the
    // authoritative cloud color. Alpha = texture α × cloudOpacity × the mesh's baked vertex-alpha horizon
    // fade (cloudcloudy ~2 at the rim → 255 overhead), so clouds dense overhead, clean toward the horizon.
    return float4(tex.rgb * uTintParam.rgb * vertexWeight,
                  saturate(tex.a * uTintParam.a * input.vColor.a));
}
