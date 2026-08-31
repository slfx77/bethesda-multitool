// Flat tinted water plane — what the viewer draws for a game whose water shader has NOT been
// recovered. Selected by WaterProfile.PixelShaderFile for WaterShaderVariant.FlatTinted
// (WaterProfile.Flat: BethesdaGame.Unknown and any game added to the enum later without its own
// recovered or explicitly source-backed route).
//
// Deliberately NOT a port of anything, and deliberately the shortest program in the family. The
// previous stand-in for these games was water_fnv.frag.hlsl — FNV's recovered WATER000 — which
// applies FNV's engine math (the NNAM noise octaves and their scroll, the DNAM FogNear/FogFar
// lanes, the WATER000 coverage algebra, the dual sun/sky specular) to records it was never derived
// from. For an un-recovered game every one of those terms is an unfounded fidelity claim, and none
// of them is free — they all hang off the scene-depth SRV and its per-sample MSAA resolve. This
// program samples no texture, reads no depth, and animates nothing: one tint, one alpha, plus the
// scene's own distance fog.
//
// Occlusion is entirely the hardware's. Every water PSO keeps the reversed-Z GreaterEqual depth
// test against the host's DSV (read-only while the scene depth doubles as an SRV), which rejects
// per SAMPLE on an MSAA target — the antialiased silhouette the shader-side clip could not give.
// So this file has no occlusion clip to compile out, needs no WATER_HARDWARE_OCCLUSION axis, and
// ships as a single PSO rather than the plain/occlusion pair every other variant carries.

#include "water_common.hlsli"

float4 main(PSInput input) : SV_Target
{
    // The tint: the WATR DNAM ShallowColor when the ESM resolved a water appearance, else the
    // profile's DefaultShallow (WaterProfile.Flat's plain blue). WaterRenderer12 has already
    // chosen between the two, so the shader reads one uniform either way. Shallow — not Deep — is
    // the authored SURFACE color, and this plane is only ever a surface: with no depth read there
    // is no water column to ramp Shallow->Deep along.
    float3 tint = uShallow.rgb;

    // Alpha is WaterProfile.SurfaceAlpha, or the record's own ANAM opacity when it authored one
    // (WaterRenderer12 picks; both arrive here as uNoiseParams.w). Lava is the one exception: it
    // keeps the flat treatment but renders opaque, because a see-through blue-tinted plane over a
    // lava flow reads wrong in a way a flat tint does not. uSurface1.w is the WATR-derived lava
    // flag — set per RECORD, not per game, so it can reach this variant — and uShallow already
    // carries the molten crust color in that case.
    float alpha = uSurface1.w > 0.5 ? 1.0 : saturate(asfloat(uNoiseParams.w));

    // ApplyFog is the one shared term worth keeping: distant water recedes into the weather haze
    // like the rest of the scene, instead of reading as a hard sheet of blue over fogged terrain.
    return float4(ApplyFog(tint, input.vWorldPos), alpha);
}
