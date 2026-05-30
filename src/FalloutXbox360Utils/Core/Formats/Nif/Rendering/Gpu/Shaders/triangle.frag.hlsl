// Phase 0b placeholder pixel shader — just outputs the interpolated vertex color.

struct PSInput
{
    float4 Position : SV_Position;
    float4 vColor   : COLOR0;
};

float4 main(PSInput input) : SV_Target
{
    return input.vColor;
}
