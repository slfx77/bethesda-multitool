// Analytic coverage for the collision-overlay line quads (see collision_line.vert.hlsl): the
// interpolated DIP distances feather the edge over uLineParams.w DIPs, giving the same apparent
// line width at every composition scale — no fixed-function line AA involved.

cbuffer Uniforms : register(b0)
{
    float4x4 uViewProj;
    float4   uLineColor;
    float4   uLineParams;   // x,y = viewport size DIPs; z = core half-width DIPs; w = feather DIPs
};

struct PSInput
{
    float4 Position : SV_Position;
    noperspective float2 vDistDip : TEXCOORD0;
    nointerpolation float vSegLenDip : TEXCOORD1;
};

float4 main(PSInput input) : SV_Target
{
    float feather = max(uLineParams.w, 1e-3);
    float lat = abs(input.vDistDip.x);
    float lon = max(max(-input.vDistDip.y, input.vDistDip.y - input.vSegLenDip), 0.0);
    float d = max(lat, lon);                              // squared-off feathered caps
    float coverage = saturate((uLineParams.z + uLineParams.w - d) / feather);
    return float4(uLineColor.rgb, uLineColor.a * coverage);
}
