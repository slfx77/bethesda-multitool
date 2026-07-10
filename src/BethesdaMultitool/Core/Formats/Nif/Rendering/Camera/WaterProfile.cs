using System.Numerics;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;

/// <summary>
///     The shader path the water renderer compiles + selects for a game. FNV's <c>WATER000.pso</c> and
///     Skyrim's <c>BSWaterShader</c> (disassembled from <c>Skyrim - Shaders.bsa</c> →
///     <c>shaders001.fxp</c>; see <c>tools/GhidraProject/skyrim_water_pixel_shader_decompiled.txt</c>)
///     are the SAME shader on their RT-free path — identical Shallow→Deep body, Schlick fresnel, dual
///     sun/sky specular — so FO3/Skyrim(+) share <see cref="FnvWater000" /> and their per-game fidelity
///     is the WATR DNAM parse. Oblivion's <c>WATER000.pso</c> genuinely DIVERGES
///     (<c>tools/GhidraProject/oblivion_water_pixel_shader_decompiled.txt</c>): the body blends
///     Deep→Shallow by view angle (N·V) rather than the depth column, and the specular is a single sun
///     glint — hence its own variant.
/// </summary>
public enum WaterShaderVariant
{
    /// <summary>The RT-free <c>BSWaterShader</c> math (FNV PC <c>WATER000.pso</c>) — FO3/FNV/Skyrim+,
    /// and the fallback for games without their own decompiled water shader.</summary>
    FnvWater000,

    /// <summary>Oblivion's <c>WATER000.pso</c> on the RT-free path: view-angle (N·V) Deep→Shallow body,
    /// single sun specular. Compiled from the same HLSL with the <c>OBLIVION_WATER</c> define.</summary>
    OblivionWater000,

    /// <summary>FO4's <c>BSWaterShader</c> (D3D11 ps_5_0, disassembled from
    /// <c>Fallout4 - Shaders.ba2</c> → <c>ShadersFX\Shaders011.fxp</c> group 5; see
    /// <c>tools/GhidraProject/fo4_water_pixel_shader_decompiled.txt</c>): Oren-Nayar sun diffuse +
    /// transmission/backscatter, normalized Kelemen/Schlick Blinn specular (F0 = 0.2), analytic
    /// Shallow→Deep color/alpha ramps by water column (the engine's baked depth LUT), reflection ×
    /// lighting composite. Compiled from the same HLSL with the <c>FO4_WATER</c> define.</summary>
    Fo4Water,
}

/// <summary>
///     Per-game configuration for the v3 water renderer, mirroring the per-game
///     <see cref="SkyMoonProfile" /> pattern. The water shader (<c>water.frag.hlsl</c>) is a faithful
///     RT-free port of FNV's <c>WATER000</c> pixel shader; this profile is the single place the
///     FNV-specific tuning constants and the per-game shader variant are decided from
///     <see cref="BethesdaGame" />, so those values stop being silently universal across games.
///     <para>
///         Shader <em>math</em> (Schlick fresnel exponent, dual sun/sky specular, the fixed sky-glint
///         direction, the ripple distance fades) lives inside the shader's per-variant branch — for the
///         FNV variant those are the engine-exact literals in <c>water.frag.hlsl</c>. This profile carries
///         only the renderer-side, data-driven values (the variant selector, the NNAM noise tile size, the
///         depth tie-break bias, and the no-WATR fallback tints). Per the binary-RE-only grounding policy,
///         every game without its own decompiled water shader resolves to <see cref="Fnv" />.
///     </para>
/// </summary>
public sealed record WaterProfile
{
    /// <summary>Which shader path / PSO the renderer compiles and selects for this game.</summary>
    public WaterShaderVariant ShaderVariant { get; init; }

    /// <summary>World units per NNAM normal-map tile at the base octave (<c>uNoiseParams.y</c>); the shader
    /// samples 3 octaves at ×1/×2.2/×4.7 this frequency, so the finest ripple ≈ this/4.7. 512 → ripple
    /// detail down to ~110 world units (FNV cells are 4096). Larger = broader swell, smaller = finer/busier;
    /// this is the single spatial-frequency knob (the recovered VS's absolute <c>TexScale</c>).</summary>
    public uint NoiseTilingWorldUnits { get; init; }

    /// <summary>Depth-sample occlusion tie-break (world units) so a shoreline where water and terrain are
    /// ~coplanar resolves in the water's favour instead of z-fighting (3D-2). Tiny vs the DepthFalloff.</summary>
    public float DepthTieBiasWorldUnits { get; init; }

    /// <summary>Fallback Shallow/Deep/Reflection tints when the worldspace has no resolvable WATR
    /// appearance (DNAM colors).</summary>
    public Vector3 DefaultShallow { get; init; }

    /// <inheritdoc cref="DefaultShallow" />
    public Vector3 DefaultDeep { get; init; }

    /// <inheritdoc cref="DefaultShallow" />
    public Vector3 DefaultReflection { get; init; }

    /// <summary>
    ///     The canonical FNV (and FO3 — identical <c>shaderpackage019.sdp</c> water set) profile, and the
    ///     binary-RE-only fallback for every other game. Values are the exact constants previously hardcoded
    ///     in <c>WaterRenderer12</c>, so FNV/FO3 render byte-identically.
    /// </summary>
    public static readonly WaterProfile Fnv = new()
    {
        ShaderVariant = WaterShaderVariant.FnvWater000,
        // Base octave tile. Was 2048 (~2 tiles/cell) → coarse, gridded swell with no fine detail; the
        // shader now octaves this (×1/×2.2/×4.7) so 512 yields fine ripples (~110–512 world-unit detail).
        NoiseTilingWorldUnits = 512,
        // Shoreline coplanar-tie bias (3D-2): keeps water over ~coplanar terrain to stop sub-ULP depth flicker.
        // Was 8 world units, which also over-drew water across FLOATING props near the surface (ice floes
        // z-fighting / hidden under water). Shoreline noise is sub-unit, so 1 unit still kills the flicker
        // while letting a proud ice floe (>1 unit above the waterline) win the depth test.
        DepthTieBiasWorldUnits = 1f,
        DefaultShallow = new Vector3(0.12f, 0.24f, 0.32f),
        DefaultDeep = new Vector3(0.03f, 0.09f, 0.16f),
        DefaultReflection = new Vector3(0.22f, 0.32f, 0.40f),
    };

    /// <summary>
    ///     Oblivion's profile — its <c>WATER000.pso</c> was disassembled from
    ///     <c>shaderpackage019.sdp</c> (identical bytes in 009/013/017) and diverges from the shared
    ///     RT-free math, so it gets its own shader variant. The renderer-side tuning matches FNV's:
    ///     Oblivion has no NNAM (the engine scrolls the <c>textures\water\water00-31.dds</c> sequence),
    ///     so the noise tile/bias values only feed the shared procedural/normal fallback paths.
    /// </summary>
    public static readonly WaterProfile Oblivion = Fnv with
    {
        ShaderVariant = WaterShaderVariant.OblivionWater000,
    };

    /// <summary>
    ///     Fallout 4's profile — its <c>BSWaterShader</c> pixel shader was disassembled from the
    ///     shipped D3D11 bytecode (<c>Shaders011.fxp</c>) and genuinely diverges from the FNV-family
    ///     RT-free math (Oren-Nayar diffuse, normalized Kelemen/Schlick specular, depth-LUT body), so
    ///     it gets its own shader variant. Renderer-side tuning stays FNV's: FO4's noise layers carry
    ///     their own DNAM wind/amplitude values and its NAM2 noise texture rides the same NNAM slot.
    ///     FO76 is NOT routed here until its water shader is verified against FO4's (binary-RE-only).
    /// </summary>
    public static readonly WaterProfile Fallout4 = Fnv with
    {
        ShaderVariant = WaterShaderVariant.Fo4Water,
    };

    /// <summary>
    ///     The water profile for the loaded game. FNV/FO3 ship the identical <c>WATER000</c> set and
    ///     Skyrim's RT-free water is the same shader (RE-confirmed — see
    ///     <see cref="WaterShaderVariant" />), so they resolve to <see cref="Fnv" />, as does every game
    ///     without its own decompiled water shader (binary-RE-only policy). Oblivion's shader genuinely
    ///     diverges and resolves to <see cref="Oblivion" />. Per-game color/scalar fidelity comes from
    ///     the per-game WATR data parse either way.
    /// </summary>
    public static WaterProfile ForGame(BethesdaGame game) => game switch
    {
        BethesdaGame.Oblivion => Oblivion,
        BethesdaGame.Fallout4 => Fallout4,
        _ => Fnv,
    };
}
