using System.Numerics;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;

/// <summary>
///     The shader path the water renderer compiles + selects for a game. FNV's <c>WATER000.pso</c> and
///     Skyrim's <c>BSWaterShader</c> (disassembled from <c>Skyrim - Shaders.bsa</c> →
///     <c>shaders001.fxp</c>; see <c>tools/GhidraProject/skyrim_water_pixel_shader_decompiled.txt</c>)
///     share the recovered RT-free color core — Shallow→Deep body, Schlick fresnel, and dual sun/sky
///     specular — so the current FO3/FNV/Skyrim implementation shares <see cref="FnvWater000" />.
///     This is not a claim of full shader/input identity: Skyrim keeps three independent authored normal
///     inputs and Creation-era constants, while FNV uses its dedicated noise prepass and legacy output
///     contract. Oblivion's <c>WATER000.pso</c> genuinely diverges
///     (<c>tools/GhidraProject/oblivion_water_pixel_shader_decompiled.txt</c>): the body blends
///     Deep→Shallow by view angle (N·V) rather than the depth column, and the specular is a single sun
///     glint — hence its own variant.
/// </summary>
public enum WaterShaderVariant
{
    /// <summary>The shared recovered RT-free color core used by FO3/FNV and the bounded Skyrim path;
    /// game-specific normal/prepass/input contracts remain outside this variant selector.</summary>
    FnvWater000,

    /// <summary>Oblivion's <c>WATER000.pso</c> on the RT-free path: view-angle (N·V) Deep→Shallow body,
    /// single sun specular. Compiled from its own file, <c>water_oblivion.frag.hlsl</c>.</summary>
    OblivionWater000,

    /// <summary>FO4's <c>BSWaterShader</c> (D3D11 ps_5_0, disassembled from
    /// <c>Fallout4 - Shaders.ba2</c> → <c>ShadersFX\Shaders011.fxp</c> group 5; see
    /// <c>tools/GhidraProject/fo4_water_pixel_shader_decompiled.txt</c>): Oren-Nayar sun diffuse +
    /// transmission/backscatter, normalized Kelemen/Schlick Blinn specular (F0 = 0.2), analytic
    /// Shallow→Deep color/alpha ramps by water column (the engine's baked depth LUT), reflection ×
    /// lighting composite. Compiled from its own file, <c>water_fo4.frag.hlsl</c>.</summary>
    Fo4Water,

    /// <summary>Morrowind's fixed-function water — NetImmerse 4.0.0.2 predates water pixel shaders,
    /// so there is nothing to disassemble: the engine draws an animated tiled diffuse plane
    /// (<c>Morrowind.ini [Water]</c>: <c>textures\water\water00–31.dds</c> cycling at
    /// <c>SurfaceFPS=12</c>, surface opacity <c>World Alpha=0.75</c>). Compiled from its own file,
    /// <c>water_morrowind.frag.hlsl</c>; the optional <c>[PixelWater]</c> terrain-reflection
    /// path is a follow-up. Derivation: <c>docs/research/morrowind_atmosphere_water_model.md</c>.</summary>
    MorrowindWater,
}

/// <summary>
///     How the legacy shared <c>textures\water\water00–31.dds</c> animation binds to the water
///     surface. Morrowind's fixed-function plane samples the frames as its diffuse; Oblivion's
///     WATER000 samples them as the global animated NormalMap input (WATR TNAM is per-water detail
///     content, not this slot). Every other game has no frame cycle.
/// </summary>
public enum LegacySurfaceFrameRole
{
    /// <summary>No legacy frame cycle — the shader-variant games drive water from WATR/noise inputs.</summary>
    None,

    /// <summary>Morrowind: frames are the fixed-function diffuse surface.</summary>
    Diffuse,

    /// <summary>Oblivion: frames are WATER000's global animated NormalMap input.</summary>
    GlobalNormal,
}

/// <summary>
///     Per-game configuration for the v3 water renderer, mirroring the per-game
///     <see cref="SkyMoonProfile" /> pattern. The water shader (<c>water_fnv.frag.hlsl</c>) is a faithful
///     RT-free port of FNV's <c>WATER000</c> pixel shader; this profile is the single place the
///     FNV-specific tuning constants and the per-game shader variant are decided from
///     <see cref="BethesdaGame" />, so those values stop being silently universal across games.
///     <para>
///         Shader <em>math</em> (Schlick fresnel exponent, dual sun/sky specular, the fixed sky-glint
///         direction, the ripple distance fades) lives inside the per-game shader file — for the
///         FNV variant those are the engine-exact literals in <c>water_fnv.frag.hlsl</c>. This profile carries
///         only the renderer-side, data-driven values (the variant selector, the NNAM noise tile size, the
///         depth tie-break bias, and the no-WATR fallback tints). Per the binary-RE-only grounding policy,
///         every game without its own decompiled water shader resolves to <see cref="Fnv" />.
///     </para>
/// </summary>
public sealed record WaterProfile
{
    /// <summary>Which shader path / PSO the renderer compiles and selects for this game.</summary>
    public WaterShaderVariant ShaderVariant { get; init; }

    /// <summary>
    ///     The per-game water pixel-shader FILE for <see cref="ShaderVariant" /> (the
    ///     <see cref="GrassShaderProfile" /> pattern: game identity is a file, technique axes stay
    ///     preprocessor macros — <c>WATER_HARDWARE_OCCLUSION</c>, <c>FO4_WATER_ARCHITECTURAL</c>).
    ///     FNV's separately compiled WATER001 program (<c>water_fnv001.frag.hlsl</c>) is not selected
    ///     here: it is a draw-time route inside the FNV variant, not a per-game profile decision.
    /// </summary>
    public string PixelShaderFile => ShaderVariant switch
    {
        WaterShaderVariant.OblivionWater000 => "water_oblivion.frag.hlsl",
        WaterShaderVariant.Fo4Water => "water_fo4.frag.hlsl",
        WaterShaderVariant.MorrowindWater => "water_morrowind.frag.hlsl",
        _ => "water_fnv.frag.hlsl",
    };

    /// <summary>World units per NNAM normal-map tile at the base octave (<c>uNoiseParams.y</c>); the shader
    /// samples 3 octaves at ×1/×2.2/×4.7 this frequency, so the finest ripple ≈ this/4.7. 512 → ripple
    /// detail down to ~110 world units (FNV cells are 4096). Larger = broader swell, smaller = finer/busier;
    /// this is the single spatial-frequency knob (the recovered VS's absolute <c>TexScale</c>).</summary>
    public uint NoiseTilingWorldUnits { get; init; }

    /// <summary>Depth-sample occlusion tie-break (world units) so a shoreline where water and terrain are
    /// ~coplanar resolves in the water's favour instead of z-fighting (3D-2). Tiny vs the DepthFalloff.</summary>
    public float DepthTieBiasWorldUnits { get; init; }

    /// <summary>Frames/second of the legacy <c>water00..31.dds</c> cycle. Morrowind samples it as
    /// the fixed-function diffuse surface; Oblivion samples it as WATER000's global NormalMap.
    /// Both shipped INIs specify 12 FPS. Zero means no frame cycle.</summary>
    public float SurfaceFrameFps { get; init; }

    /// <summary>How (and whether) the legacy <c>water00–31.dds</c> frames bind for this game —
    /// the texture-slot side of <see cref="SurfaceFrameFps" />.</summary>
    public LegacySurfaceFrameRole LegacyFrames { get; init; } = LegacySurfaceFrameRole.None;

    /// <summary>True when the WATR TNAM detail texture is sampled as a per-water diffuse layer
    /// (Oblivion only; other games' TNAM is unused by the renderer).</summary>
    public bool UsesWatrDetailTexture { get; init; }

    /// <summary>Surface opacity of the <see cref="WaterShaderVariant.MorrowindWater" /> plane
    /// (<c>[Water] World Alpha</c>). Unused by the shader-variant games (their alpha is derived
    /// in-shader from fresnel/depth ramps).</summary>
    public float SurfaceAlpha { get; init; }

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
        SurfaceFrameFps = 12f, // Oblivion_default.ini [Water] uSurfaceFPS=12
        LegacyFrames = LegacySurfaceFrameRole.GlobalNormal,
        UsesWatrDetailTexture = true,
    };

    /// <summary>
    ///     Fallout 4's profile — its <c>BSWaterShader</c> pixel shader was disassembled from the
    ///     shipped D3D11 bytecode (<c>Shaders011.fxp</c>) and genuinely diverges from the FNV-family
    ///     RT-free math (Oren-Nayar diffuse, normalized Kelemen/Schlick specular, depth-LUT body), so
    ///     it gets its own shader variant. Renderer-side tuning stays FNV's: FO4's noise layers carry
    ///     their own DNAM wind/amplitude values and its NAM2 noise texture rides the same NNAM slot.
    ///     FO76 has a separately verified profile below.
    /// </summary>
    public static readonly WaterProfile Fallout4 = Fnv with
    {
        ShaderVariant = WaterShaderVariant.Fo4Water,
    };

    /// <summary>
    ///     Fallout 76's shipped <c>Shaders012.fxp</c> names its Water group and contains 23 VS plus
    ///     525 PS variants. The baseline PS retains FO4's Creation-era dynamic-water architecture:
    ///     normalized specular (1/pi and 0.25 energy factors), Schlick F0=0.2, dynamic water maps,
    ///     cubemap reflection, and the point-light loop. It therefore routes to the existing
    ///     <see cref="WaterShaderVariant.Fo4Water" /> port rather than the unrelated FNV WATER000
    ///     fallback. FO76's shorter WATR DNAM is parsed separately and supplies the common fields.
    /// </summary>
    public static readonly WaterProfile Fallout76 = Fallout4 with { };

    /// <summary>
    ///     Morrowind's profile — fixed-function animated diffuse plane per <c>Morrowind.ini [Water]</c>
    ///     (see <see cref="WaterShaderVariant.MorrowindWater" />). The tile size reads
    ///     <c>SurfaceTileCount=10</c> as tiles per 8192-unit cell (819 world units/tile) — a plausible
    ///     reading of the INI, flagged TO-CONFIRM against an OpenMW render oracle in the derivation
    ///     doc. The default tint doubles as the no-texture fallback (Vvardenfell ocean green-blue).
    /// </summary>
    public static readonly WaterProfile Morrowind = Fnv with
    {
        ShaderVariant = WaterShaderVariant.MorrowindWater,
        NoiseTilingWorldUnits = 819,  // [Water] SurfaceTileCount=10 per 8192 cell — TO-CONFIRM vs oracle
        SurfaceFrameFps = 12f,        // [Water] SurfaceFPS=12
        SurfaceAlpha = 0.75f,         // [Water] World Alpha=0.75
        LegacyFrames = LegacySurfaceFrameRole.Diffuse,
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
        BethesdaGame.Morrowind => Morrowind,
        BethesdaGame.Oblivion => Oblivion,
        BethesdaGame.Fallout4 => Fallout4,
        BethesdaGame.Fallout76 => Fallout76,
        _ => Fnv,
    };
}
