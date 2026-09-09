// Installed WATERDISPLACE000/001/002/003/005/006/007 and WATERHMAP005/006.
// EXE 74DB8031...18EA, shaderpackage019 3FA97184...BFBA. No phase4.
// New b0 packing is explicit; this is not the original D3D9 CTAB register layout.
cbuffer ReplayConstants : register(b0)
{
    float4 TexRatio0;
    float4 Translation0;
    float4 Translation1;
    float4 Simulation; // Force, Velocity, Falloff, fDamp
    float4 Surface;    // BlendAmount, TextureOffset.xy, recorded HMAP fResolution
    float4 Sampling;   // x: 0 linear wrap, 1 linear clamp
};
Texture2D<float4> Height0 : register(t0);
Texture2D<float4> Height1 : register(t1);
SamplerState LinearWrap : register(s0);
SamplerState LinearClamp : register(s1);

struct ReplayVertex { float4 Position : POSITION; float2 Uv : TEXCOORD0; };
struct ReplayPixel { float4 Position : SV_Position; float2 Uv : TEXCOORD0; };

ReplayPixel vsWadingStamp(ReplayVertex input)
{
    ReplayPixel output;
    float3 homogeneousPosition = float3(input.Position.xy, 1.0);
    output.Position = float4(dot(homogeneousPosition, Translation0.xyz), dot(homogeneousPosition, Translation1.xyz), input.Position.zw);
    output.Uv = input.Uv;
    return output;
}

ReplayPixel vsRainStamp(ReplayVertex input)
{
    ReplayPixel output;
    output.Position = float4(input.Position.xy * TexRatio0.xy + TexRatio0.zw, input.Position.zw);
    output.Uv = input.Uv;
    return output;
}

ReplayPixel vsQuad(ReplayVertex input)
{
    ReplayPixel output;
    output.Position = input.Position;
    output.Uv = input.Uv * TexRatio0.xy + TexRatio0.zw;
    return output;
}

float4 sample0(float2 uv)
{
    if (Sampling.x > 0.5) return Height0.Sample(LinearClamp, uv);
    return Height0.Sample(LinearWrap, uv);
}

float4 psWadingStamp(ReplayPixel input) : SV_Target { return float4(0.9, 0.5, 0.5, 0.5); }
float4 psRainStamp(ReplayPixel input) : SV_Target { return float4(0.9, 0.5, 0.5, 0.5); }

float4 evolve(float2 uv)
{
    // Literal step belongs to the 256-square DISPLACE programs, not the source texture width.
    const float step = 0.00390625;
    float north = sample0(uv + float2(0, -step)).r;
    float south = sample0(uv + float2(0, step)).r;
    float west = sample0(uv + float2(-step, 0)).r;
    float east = sample0(uv + float2(step, 0)).r;
    float4 center = sample0(uv);
    float laplacian = ((north + south) + west) + east - 4.0 * center.r;
    float4 value = center - 0.5;
    value.g = Simulation.x * laplacian + value.g;
    value.r = Simulation.y * value.g + value.r; // Updated green, then all-channel damping.
    return value * Simulation.z + 0.5;
}

float4 psWadingEvolution(ReplayPixel input) : SV_Target { return evolve(input.Uv); }
float4 psRainEvolution(ReplayPixel input) : SV_Target { return evolve(input.Uv); }

float4 normalFromAbsoluteHeight(float2 uv, float step, float dampener)
{
    float west = abs(sample0(uv + float2(-step, 0)).r);
    float east = abs(sample0(uv + float2(step, 0)).r);
    float north = abs(sample0(uv + float2(0, -step)).r);
    float south = abs(sample0(uv + float2(0, step)).r);
    float northwest = abs(sample0(uv + float2(-step, -step)).r);
    float northeast = abs(sample0(uv + float2(step, -step)).r);
    float southwest = abs(sample0(uv + float2(-step, step)).r);
    float southeast = abs(sample0(uv + float2(step, step)).r);
    // Keep the installed arithmetic sequence instead of factoring dampener outside a sum.
    float eastScaled = east * dampener;
    float westScaled = west * dampener;
    float gx = westScaled + westScaled;
    gx = mad(dampener, -northwest, -gx);
    gx = mad(dampener, -southwest, gx);
    gx = mad(dampener, northeast, gx);
    gx = mad(2.0, eastScaled, gx);
    gx = mad(dampener, southeast, gx);
    float northScaled = north * dampener;
    float gy = northScaled + northScaled;
    gy = mad(dampener, -northwest, -gy);
    gy = mad(dampener, -northeast, gy);
    gy = mad(dampener, southwest, gy);
    float southScaled = south * dampener;
    gy = mad(2.0, southScaled, gy);
    gy = mad(dampener, southeast, gy);
    float3 direction = float3(-gx, gy, 1.0);
    float inverseLength = rsqrt(dot(direction, direction));
    return float4(float3(direction.xy * inverseLength, inverseLength) * 0.5 + 0.5, 1.0);
}

float4 psNormal(ReplayPixel input) : SV_Target
{
    return normalFromAbsoluteHeight(input.Uv, 0.00390625, Simulation.w);
}

float4 psMixedHeight(ReplayPixel input) : SV_Target
{
    // Only the FFT endpoint receives .8/fDamp. Target conversion happens AFTER this write.
    float first = abs(Height0.Sample(LinearWrap, input.Uv).r);
    float second = abs(Height1.Sample(LinearWrap, input.Uv).r);
    float scale = rcp(Simulation.w) * 0.8;
    float delta = mad(scale, -first, second) * Surface.x;
    float mixed = mad(scale, first, delta);
    return float4(mixed, mixed, mixed, 1.0);
}

float4 psRecenter(ReplayPixel input) : SV_Target
{
    // Fade uses the pre-offset UV. The offset changes only the sampled position.
    float fade = saturate((length(input.Uv - 0.5) - 0.4) * 10.0);
    return lerp(sample0(input.Uv + Surface.yz), float4(0.5, 0.5, 0.5, 1.0), fade);
}

float4 psFftNormal(ReplayPixel input) : SV_Target
{
    // WATERHMAP005 has its own multiply/MAD order and literal 1.6. Factoring .8 into
    // DISPLACE005's doubled cardinal taps changes rounding before the subsequent MADs.
    float2 uv = input.Uv;
    float step = Surface.w; // Recorded c2.x; do not infer it from the target dimensions.
    float northeast = abs(sample0(uv + float2(step, -step)).r);
    float east = abs(sample0(uv + float2(step, 0)).r);
    float west = abs(sample0(uv + float2(-step, 0)).r);
    float northwest = abs(sample0(uv - step).r);
    float southwest = abs(sample0(uv + float2(-step, step)).r);
    float southeast = abs(sample0(uv + step).r);
    float north = abs(sample0(uv + float2(0, -step)).r);
    float south = abs(sample0(uv + float2(0, step)).r);
    float northwestScaled = northwest * 0.8;
    float gx = mad(west, -1.6, -northwestScaled);
    gx = mad(southwest, -0.8, gx);
    gx = mad(northeast, 0.8, gx);
    gx = mad(east, 1.6, gx);
    gx = mad(southeast, 0.8, gx);
    float gy = mad(north, -1.6, -northwestScaled);
    gy = mad(northeast, -0.8, gy);
    gy = mad(southwest, 0.8, gy);
    gy = mad(south, 1.6, gy);
    gy = mad(southeast, 0.8, gy);
    float3 direction = float3(-gx, gy, 1.0);
    float inverseLength = rsqrt(dot(direction, direction));
    return float4(float3(direction.xy * inverseLength, inverseLength) * 0.5 + 0.5, 1.0);
}

float4 psFftAbsoluteHeight(ReplayPixel input) : SV_Target
{
    float height = abs(Height0.Sample(LinearWrap, input.Uv).r);
    return float4(height, height, height, 1.0);
}
