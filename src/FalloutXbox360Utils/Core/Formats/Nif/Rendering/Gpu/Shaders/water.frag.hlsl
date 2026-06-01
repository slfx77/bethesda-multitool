// v3 Phase 2a water pixel shader — outputs the per-draw water colour as-is. Phase 4+ can
// extend this with WATR-tinted shallow/deep blends.

struct PSInput
{
    float4 Position : SV_Position;
    float4 vColor   : COLOR0;
};

float4 main(PSInput input) : SV_Target
{
    return input.vColor;
}
