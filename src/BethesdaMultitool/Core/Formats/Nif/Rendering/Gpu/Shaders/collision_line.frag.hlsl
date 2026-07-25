// Analytic coverage for the collision-overlay line quads (see collision_line.vert.hlsl): the
// interpolated pixel-space distances feather the edge over uLineParams.w pixels, giving the same
// antialiased line on every GPU/monitor/DPI — no fixed-function line AA involved.

cbuffer Uniforms : register(b0)
{
    float4x4 uViewProj;
    float4   uLineColor;
    float4   uLineParams;   // x,y = viewport size px; z = core half-width px; w = feather px
};

struct PSInput
{
    float4 Position : SV_Position;
    noperspective float2 vDistPx : TEXCOORD0;
    nointerpolation float vSegLenPx : TEXCOORD1;
};

float4 main(PSInput input) : SV_Target
{
    float feather = max(uLineParams.w, 1e-3);
    float lat = abs(input.vDistPx.x);
    float lon = max(max(-input.vDistPx.y, input.vDistPx.y - input.vSegLenPx), 0.0);
    float d = max(lat, lon);                              // squared-off feathered caps
    float coverage = saturate((uLineParams.z + uLineParams.w - d) / feather);
    return float4(uLineColor.rgb, uLineColor.a * coverage);
}
