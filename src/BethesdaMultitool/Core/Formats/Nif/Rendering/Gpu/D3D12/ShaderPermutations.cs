// ShaderMacro lives in Vortice.Direct3D (Vortice.DirectX), NOT Vortice.D3DCompiler.
using BethesdaMultitool.Core.Formats.Nif.Rendering.Vegetation;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Water;
using Vortice.Direct3D;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

/// <summary>One compilable shader configuration: a file, an entry point, a profile, and its macros.</summary>
/// <param name="File">Embedded shader file name, e.g. <c>reference.frag.hlsl</c>.</param>
/// <param name="EntryPoint">HLSL entry function.</param>
/// <param name="Profile">Shader model target, e.g. <c>ps_5_1</c>.</param>
/// <param name="Macros">Preprocessor defines that select the variant.</param>
/// <param name="Purpose">Why this permutation exists — shown in test failures.</param>
internal readonly record struct ShaderPermutation(
    string File,
    string EntryPoint,
    string Profile,
    ShaderMacro[] Macros,
    string Purpose);

/// <summary>
///     The authoritative inventory of every shader permutation the renderers compile.
///     <para>
///         It exists because coverage kept rotting: the test named
///         <c>EveryRemainingEmbeddedRenderingEntryPointCompiles</c> was in fact a HAND-MAINTAINED list
///         of 26 tuples, so any newly added shader silently had zero compile coverage until somebody
///         remembered to append to it. With CI not setting <c>RUN_SHADER_COMPILE_TESTS</c> at all,
///         "zero coverage" meant a broken shader reached whoever loaded that game — appearing as a
///         missing 3D view rather than a build failure.
///     </para>
///     <para>
///         Tests iterate <see cref="All" /> to compile everything, and separately assert that every
///         embedded entry-point shader appears here. Adding a shader without adding it here therefore
///         FAILS A TEST instead of quietly going unverified.
///     </para>
/// </summary>
internal static class ShaderPermutations
{
    private static readonly ShaderMacro[] None = [];

    /// <summary>Placed objects (REFRs). The pixel shader also has an alpha-to-coverage variant.</summary>
    internal static IReadOnlyList<ShaderPermutation> Reference { get; } =
    [
        new("reference.vert.hlsl", "main", "vs_5_1", None, "per-draw blended reference path"),
        new("reference_instanced.vert.hlsl", "main", "vs_5_1", None, "instanced opaque reference path"),
        new("reference_instanced.vert.hlsl", "main", "vs_5_1",
            [new ShaderMacro("SHADOW_CARD_LIGHT_FACING", "1")],
            "shadow-map replay: light-facing cards, wind compiled out"),
        new("reference.frag.hlsl", "main", "ps_5_1", None, "reference pixel shader"),
        new("reference.frag.hlsl", "main", "ps_5_1",
            [new ShaderMacro("ALPHA_TO_COVERAGE", "1")],
            "MSAA-only A2C variant; aliases the plain PSO when SceneSampleCount == 1"),
        new("shadow.frag.hlsl", "main", "ps_5_1", None, "shadow cutout discard"),
        // First per-game shader. Retail GRASS2020.vso / GRASS2002.pso light grass from the TERRAIN
        // normal with no surface normal at all, and compose ambient ADDITIVELY — neither expressible
        // as a uniform on the shared path. Selected by GrassShaderProfile.ForGame(Oblivion).
        new("reference_grass_oblivion.vert.hlsl", "main", "vs_5_1", None, "Oblivion grass (GRASS2020.vso)"),
        new("reference_grass_oblivion.frag.hlsl", "main", "ps_5_1", None, "Oblivion grass (GRASS2002.pso)"),
        // Per-game shader #2, and the first on the INSTANCED axis. Retail FO3/FNV lights grass from
        // terrain data baked per instance at placement time (land normal + land-colour luminance),
        // composes ambient additively without vertex colour, boosts the sun term 1.5x, and shadows
        // only that sun term. Selected by GrassShaderProfile.InstancedForGame(FalloutNewVegas/3).
        new("reference_grass_fnv.vert.hlsl", "main", "vs_5_1", None,
            "FO3/FNV grass (GRASS2002.vso on the instanced ABI)"),
        new("reference_grass_fnv.frag.hlsl", "main", "ps_5_1", None,
            "FO3/FNV grass (GRASS2002.pso: sun*shadow + ambient)"),
        new("reference_grass_fnv.frag.hlsl", "main", "ps_5_1",
            [new ShaderMacro("ALPHA_TO_COVERAGE", "1")],
            "FO3/FNV grass MSAA A2C variant; aliases the plain PSO when SceneSampleCount == 1"),
    ];

    /// <summary>
    ///     Water. The game is a per-FILE axis (<c>WaterProfile.PixelShaderFile</c>); each per-game
    ///     file is compiled twice — plain, and with WATER_HARDWARE_OCCLUSION for the read-only-DSV
    ///     path. FNV's retail WATER001 program is its own file (<c>water_fnv001.frag.hlsl</c>).
    /// </summary>
    internal static IReadOnlyList<ShaderPermutation> Water { get; } = BuildWater();

    /// <summary>Terrain, sky, post-process, overlays and the sprite/skin path.</summary>
    internal static IReadOnlyList<ShaderPermutation> Other { get; } =
    [
        new("terrain_textured.vert.hlsl", "main", "vs_5_1", None, "terrain"),
        new("terrain_textured.frag.hlsl", "main", "ps_5_1", None, "terrain"),
        new("sky_geo.vert.hlsl", "main", "vs_5_1", None, "sky gradient dome"),
        new("sky_geo.frag.hlsl", "main", "ps_5_1", None, "sky gradient dome"),
        new("sky_billboard.vert.hlsl", "main", "vs_5_1", None, "sun / moon billboards"),
        new("sky_billboard.frag.hlsl", "main", "ps_5_1", None, "sun / moon billboards"),
        new("tonemap.vert.hlsl", "main", "vs_5_1", None, "post-process fullscreen triangle"),
        new("tonemap.frag.hlsl", "main", "ps_5_1", None, "tonemap composite"),
        new("tonemap.frag.hlsl", "mainAvg", "ps_5_1", None, "log-average luminance reduction"),
        new("tonemap.frag.hlsl", "mainAdapt", "ps_5_1", None, "eye adaptation"),
        new("bloom.frag.hlsl", "mainDownsample16", "ps_5_1", None, "bloom downsample"),
        new("bloom.frag.hlsl", "main", "ps_5_1", None, "bloom bright-pass"),
        new("bloom.frag.hlsl", "mainBlur", "ps_5_1", None, "bloom separable blur"),
        new("cellgrid.vert.hlsl", "main", "vs_5_1", None, "navmesh / selection / cell-grid overlays"),
        new("cellgrid.frag.hlsl", "main", "ps_5_1", None, "navmesh / selection / cell-grid overlays"),
        new("collision_line.vert.hlsl", "main", "vs_5_1", None, "collision cage + export framing"),
        new("collision_line.frag.hlsl", "main", "ps_5_1", None, "collision cage + export framing"),
        new("skin.vert.hlsl", "main", "vs_5_1", None, "CLI sprite/skin renderer"),
        new("skin.frag.hlsl", "main", "ps_5_1", None, "CLI sprite/skin renderer"),
        // Not referenced by any renderer today. Kept compiling deliberately: they are the last
        // remaining pre-bindless terrain/dev shaders, and a permutation entry is the only thing that
        // stops them silently rotting into non-compiling source.
        new("terrain.vert.hlsl", "main", "vs_5_1", None, "UNREFERENCED legacy terrain"),
        new("terrain.frag.hlsl", "main", "ps_5_1", None, "UNREFERENCED legacy terrain"),
        new("triangle.vert.hlsl", "main", "vs_5_1", None, "UNREFERENCED dev smoke triangle"),
        new("triangle.frag.hlsl", "main", "ps_5_1", None, "UNREFERENCED dev smoke triangle"),
    ];

    /// <summary>Every permutation across every family.</summary>
    internal static IReadOnlyList<ShaderPermutation> All { get; } =
        [.. Reference, .. Water, .. Other];

    private static List<ShaderPermutation> BuildWater()
    {
        var list = new List<ShaderPermutation>
        {
            new("water.vert.hlsl", "main", "vs_5_1", None, "water surface"),
            new("water_noise.comp.hlsl", "mainScrollBlend", "cs_5_1", None, "FNV noise prepass: scroll+blend"),
            new("water_noise.comp.hlsl", "mainNormal", "cs_5_1", None, "FNV noise prepass: normal"),
        };

        // File axis (the game, per WaterProfile.PixelShaderFile) x occlusion axis (read-only DSV).
        // Written as a product rather than 8 literals so a new per-game file cannot be added to one
        // axis and forgotten on the other — the exact drift that left the water PSO table
        // asymmetric before.
        (string File, string Purpose)[] variants =
        [
            ("water_fnv.frag.hlsl", "FNV/FO3/Skyrim classic WATER000"),
            ("water_oblivion.frag.hlsl", "Oblivion WATER000: N.V body, single sun glint"),
            ("water_fo4.frag.hlsl", "FO4/FO76 BSWaterShader stand-in"),
            ("water_morrowind.frag.hlsl", "Morrowind fixed-function animated plane"),
        ];
        foreach (var (file, purpose) in variants)
        {
            foreach (var occlusion in (bool[])[false, true])
            {
                var macros = new List<ShaderMacro>();
                if (occlusion) macros.Add(new ShaderMacro("WATER_HARDWARE_OCCLUSION", "1"));
                list.Add(new ShaderPermutation(
                    file, "main", "ps_5_1", [.. macros],
                    occlusion ? $"{purpose} (hardware occlusion)" : purpose));
            }
        }

        // FNV WATER001 ships only in its hardware-occlusion form (the snapshot path needs the
        // read-only DSV), matching WaterRenderer12's single compile of it.
        list.Add(new ShaderPermutation(
            "water_fnv001.frag.hlsl", "main", "ps_5_1",
            [new ShaderMacro("WATER_HARDWARE_OCCLUSION", "1")],
            "FNV WATER001 opaque-snapshot refraction (its own retail program)"));

        // FO4/FO76 "modern" water: opt-in TECHNIQUE macro on the FO4 file, compiled lazily by
        // ModernWaterResources12.
        foreach (var occlusion in (bool[])[false, true])
        {
            var macros = new List<ShaderMacro>
            {
                new("FO4_WATER_ARCHITECTURAL", "1"),
            };
            if (occlusion) macros.Add(new ShaderMacro("WATER_HARDWARE_OCCLUSION", "1"));
            list.Add(new ShaderPermutation(
                "water_fo4.frag.hlsl", "main", "ps_5_1", [.. macros],
                occlusion
                    ? "FO4/FO76 architectural water (hardware occlusion)"
                    : "FO4/FO76 architectural water"));
        }

        foreach (var entryPoint in (string[])["mainBodyCoverage", "mainNormal", "mainGloss", "mainDepthLut"])
        {
            list.Add(new ShaderPermutation(
                "water_modern.comp.hlsl", entryPoint, "cs_5_1", None,
                "FO4/FO76 modern-water compute prepass"));
        }

        return list;
    }
}
