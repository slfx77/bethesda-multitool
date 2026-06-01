// v3 Phase 2a terrain pixel shader — simple Lambert with a hardcoded sun direction and
// vertex-color modulation. Phase 2b will replace this with the multi-layer LTEX/BTXT/ATXT
// blend shader; the interface (world-space normal + per-vertex color in COLOR0) is
// preserved so Phase 2b can layer texture sampling on top without reshaping the VS.

struct PSInput
{
    float4 Position     : SV_Position;
    float3 vWorldNormal : TEXCOORD0;
    float4 vVertexColor : TEXCOORD1;
};

float4 main(PSInput input) : SV_Target
{
    float3 normal = normalize(input.vWorldNormal);
    float3 lightDir = normalize(float3(0.5, 0.5, 1.0));
    float lambert = saturate(dot(normal, lightDir));

    float ambient = 0.4;
    float diffuse = 0.6;
    float shade = ambient + diffuse * lambert;

    return float4(input.vVertexColor.rgb * shade, 1.0);
}
