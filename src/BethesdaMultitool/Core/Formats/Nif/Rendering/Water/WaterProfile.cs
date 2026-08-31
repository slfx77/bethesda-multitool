using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Atmosphere;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Vegetation;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Water;

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
    /// <summary>
    ///     The shared recovered RT-free color core used by FO3/FNV and the bounded Skyrim path;
    ///     game-specific normal/prepass/input contracts remain outside this variant selector.
    /// </summary>
    FnvWater000,

    /// <summary>
    ///     Oblivion's <c>WATER000.pso</c> on the RT-free path: view-angle (N·V) Deep→Shallow body,
    ///     single sun specular. Compiled from its own file, <c>water_oblivion.frag.hlsl</c>.
    /// </summary>
    OblivionWater000,

    /// <summary>
    ///     FO4's <c>BSWaterShader</c> (D3D11 ps_5_0, disassembled from
    ///     <c>Fallout4 - Shaders.ba2</c> → <c>ShadersFX\Shaders011.fxp</c> group 5; see
    ///     <c>tools/GhidraProject/fo4_water_pixel_shader_decompiled.txt</c>): Oren-Nayar sun diffuse +
    ///     transmission/backscatter, normalized Kelemen/Schlick Blinn specular (F0 = 0.2), analytic
    ///     Shallow→Deep color/alpha ramps by water column (the engine's baked depth LUT), reflection ×
    ///     lighting composite. Compiled from its own file, <c>water_fo4.frag.hlsl</c>.
    /// </summary>
    Fo4Water,

    /// <summary>
    ///     Morrowind's fixed-function water — NetImmerse 4.0.0.2 predates water pixel shaders,
    ///     so there is nothing to disassemble: the engine draws an animated tiled diffuse plane
    ///     (<c>Morrowind.ini [Water]</c>: <c>textures\water\water00–31.dds</c> cycling at
    ///     <c>SurfaceFPS=12</c>, surface opacity <c>World Alpha=0.75</c>). Compiled from its own file,
    ///     <c>water_morrowind.frag.hlsl</c>; the optional <c>[PixelWater]</c> terrain-reflection
    ///     path is a follow-up. Derivation: <c>docs/research/morrowind_atmosphere_water_model.md</c>.
    /// </summary>
    MorrowindWater,

    /// <summary>
    ///     A dedicated, source-backed Starfield approximation. It consumes the exact typed WATR
    ///     normal-layer, roughness, depth, velocity, displacement, fog, absorption, and concentration
    ///     values plus shipped global water textures. It is not the CE2 Water shader: global texture
    ///     slots are inferred, and CUR3/materialsbeta.cdb/DXIL optical equations remain unresolved.
    ///     Compiled from <c>water_starfield.frag.hlsl</c> so telemetry and code cannot confuse it with
    ///     either the old flat fallback or a recovered Bethesda program.
    /// </summary>
    StarfieldWaterApprox,

    /// <summary>
    ///     No recovered shader: a flat, uniformly tinted transparent plane — one authored color, one
    ///     constant alpha, scene fog, nothing else. This is the honest stand-in for a game whose water
    ///     shader has not been disassembled, and it makes no fidelity claim; the alternative was to run
    ///     FNV's <see cref="FnvWater000" /> engine math (noise octaves, DNAM fog lanes, WATER000
    ///     coverage algebra) over records it was never derived from. It also has no scene-depth read,
    ///     so it cannot produce the silhouette fringe the depth-driven variants get from the
    ///     nearest-sample MSAA depth resolve. Compiled from its own file, <c>water_flat.frag.hlsl</c>.
    /// </summary>
    FlatTinted
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
    GlobalNormal
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
///         a game whose water shader has NOT been recovered gets no engine math at all: it resolves to
///         <see cref="Flat" />, a plainly-labelled tinted plane.
///     </para>
/// </summary>
public sealed record WaterProfile
{
    /// <summary>
    ///     The canonical FNV profile, shared by FO3 (identical <c>shaderpackage019.sdp</c> water set) and
    ///     the bounded Skyrim path (RE-confirmed same RT-free core). It is no longer the catch-all for
    ///     un-recovered games — those take <see cref="Flat" />. Values are the exact constants previously
    ///     hardcoded in <c>WaterRenderer12</c>, so FNV/FO3 render byte-identically.
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
        DefaultReflection = new Vector3(0.22f, 0.32f, 0.40f)
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
        UsesWatrDetailTexture = true
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
        ShaderVariant = WaterShaderVariant.Fo4Water
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
        NoiseTilingWorldUnits = 819, // [Water] SurfaceTileCount=10 per 8192 cell — TO-CONFIRM vs oracle
        SurfaceFrameFps = 12f, // [Water] SurfaceFPS=12
        SurfaceAlpha = 0.75f, // [Water] World Alpha=0.75
        LegacyFrames = LegacySurfaceFrameRole.Diffuse
    };

    /// <summary>
    ///     The stand-in for every game whose water shader has NOT been recovered — a flat transparent
    ///     plane, tinted by the WATR DNAM color when the ESM resolves one and by
    ///     <see cref="DefaultShallow" /> otherwise (see <see cref="WaterShaderVariant.FlatTinted" />).
    ///     <para>
    ///         The tint is a plain viewer blue, NOT an engine value: it is deliberately a touch
    ///         brighter than <see cref="Fnv" />'s <see cref="DefaultShallow" />, which is authored to be
    ///         lit and then composited with a reflection and a specular lobe — used raw on an unlit
    ///         plane it reads near-black. The 0.6 alpha is likewise a readability choice, overridden
    ///         by the record's own ANAM opacity whenever one is authored.
    ///     </para>
    ///     <para>
    ///         The FNV tuning inherited below (<see cref="NoiseTilingWorldUnits" />,
    ///         <see cref="DepthTieBiasWorldUnits" />, <see cref="DefaultDeep" />,
    ///         <see cref="DefaultReflection" />) is inert here: the flat shader samples no noise, reads
    ///         no scene depth, and has no depth column to ramp Shallow→Deep along.
    ///     </para>
    /// </summary>
    public static readonly WaterProfile Flat = Fnv with
    {
        ShaderVariant = WaterShaderVariant.FlatTinted,
        SurfaceAlpha = 0.6f,
        DefaultShallow = new Vector3(0.10f, 0.28f, 0.42f)
    };

    /// <summary>
    ///     Starfield's first dedicated water route. The default tint and alpha remain explicitly
    ///     viewer-authored because Starfield WATR has no classic shallow/deep surface colours and
    ///     xEdit labels ANAM "Opacity (unused)". Authentic WATR inputs drive the animated normal and
    ///     roughness/depth lanes; unresolved CE2 colour/transmission/reflection lanes stay neutral.
    /// </summary>
    public static readonly WaterProfile Starfield = Flat with
    {
        ShaderVariant = WaterShaderVariant.StarfieldWaterApprox
    };

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
        WaterShaderVariant.StarfieldWaterApprox => "water_starfield.frag.hlsl",
        WaterShaderVariant.FlatTinted => "water_flat.frag.hlsl",
        _ => "water_fnv.frag.hlsl"
    };

    /// <summary>
    ///     World units per NNAM normal-map tile at the base octave (<c>uNoiseParams.y</c>); the shader
    ///     samples 3 octaves at ×1/×2.2/×4.7 this frequency, so the finest ripple ≈ this/4.7. 512 → ripple
    ///     detail down to ~110 world units (FNV cells are 4096). Larger = broader swell, smaller = finer/busier;
    ///     this is the single spatial-frequency knob (the recovered VS's absolute <c>TexScale</c>).
    /// </summary>
    public uint NoiseTilingWorldUnits { get; init; }

    /// <summary>
    ///     Depth-sample occlusion tie-break (world units) so a shoreline where water and terrain are
    ///     ~coplanar resolves in the water's favour instead of z-fighting (3D-2). Tiny vs the DepthFalloff.
    /// </summary>
    public float DepthTieBiasWorldUnits { get; init; }

    /// <summary>
    ///     Frames/second of the legacy <c>water00..31.dds</c> cycle. Morrowind samples it as
    ///     the fixed-function diffuse surface; Oblivion samples it as WATER000's global NormalMap.
    ///     Both shipped INIs specify 12 FPS. Zero means no frame cycle.
    /// </summary>
    public float SurfaceFrameFps { get; init; }

    /// <summary>
    ///     How (and whether) the legacy <c>water00–31.dds</c> frames bind for this game —
    ///     the texture-slot side of <see cref="SurfaceFrameFps" />.
    /// </summary>
    public LegacySurfaceFrameRole LegacyFrames { get; init; } = LegacySurfaceFrameRole.None;

    /// <summary>
    ///     True when the WATR TNAM detail texture is sampled as a per-water diffuse layer
    ///     (Oblivion only; other games' TNAM is unused by the renderer).
    /// </summary>
    public bool UsesWatrDetailTexture { get; init; }

    /// <summary>
    ///     Profile-authored surface opacity: <see cref="WaterShaderVariant.MorrowindWater" />'s
    ///     fixed-function surface (<c>[Water] World Alpha</c>),
    ///     <see cref="WaterShaderVariant.FlatTinted" />'s stand-in when WATR has no ANAM, and the
    ///     explicitly approximate Starfield route (whose ANAM is documented as unused). Unused by
    ///     the recovered shader variants — their alpha is derived in-shader from fresnel/depth ramps.
    /// </summary>
    public float SurfaceAlpha { get; init; }

    /// <summary>
    ///     Fallback Shallow/Deep/Reflection tints when the worldspace has no resolvable WATR
    ///     appearance (DNAM colors).
    /// </summary>
    public Vector3 DefaultShallow { get; init; }

    /// <inheritdoc cref="DefaultShallow" />
    public Vector3 DefaultDeep { get; init; }

    /// <inheritdoc cref="DefaultShallow" />
    public Vector3 DefaultReflection { get; init; }

    /// <summary>
    ///     The water profile for the loaded game. FNV/FO3 ship the identical <c>WATER000</c> set and
    ///     Skyrim's RT-free water is the same shader (RE-confirmed — see
    ///     <see cref="WaterShaderVariant" />), so those three resolve to <see cref="Fnv" />; Oblivion,
    ///     FO4 and FO76 resolve to their own recovered variants. Starfield resolves to its explicitly
    ///     labelled source-backed approximation; <see cref="BethesdaGame.Unknown" /> and any game
    ///     added later still resolve to <see cref="Flat" />. The binary-RE-only policy forbids passing
    ///     an approximation off as recovered engine math, and running FNV's shader over foreign data
    ///     was exactly that mistake wearing a recovered shader's name.
    /// </summary>
    public static WaterProfile ForGame(BethesdaGame game)
    {
        return game switch
        {
            BethesdaGame.Morrowind => Morrowind,
            BethesdaGame.Oblivion => Oblivion,
            BethesdaGame.Fallout4 => Fallout4,
            BethesdaGame.Fallout76 => Fallout76,
            BethesdaGame.Starfield => Starfield,
            BethesdaGame.Fallout3 or BethesdaGame.FalloutNewVegas or BethesdaGame.Skyrim => Fnv,
            _ => Flat
        };
    }
}
