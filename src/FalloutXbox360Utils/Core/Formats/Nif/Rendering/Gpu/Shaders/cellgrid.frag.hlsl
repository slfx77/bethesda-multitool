// v3 Phase 1 placeholder pixel shader — passes through the interpolated line color.

struct PSInput
{
    float4 Position : SV_Position;
    float4 vColor   : COLOR0;
};

float4 main(PSInput input) : SV_Target
{
    return input.vColor;
}
