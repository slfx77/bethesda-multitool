// ShaderMacro lives in Vortice.Direct3D (Vortice.DirectX), NOT Vortice.D3DCompiler.

using System.Globalization;
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
///         remembered to append to it. Before CI enabled <c>RUN_SHADER_COMPILE_TESTS</c>, "zero
///         coverage" meant a broken shader reached whoever loaded that game — appearing as a missing
///         3D view rather than a build failure.
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
            [new ShaderMacro("REFERENCE_MODERN_STANDARD", "1")],
            "modern standard compact stage-link signature: opaque"),
        new("reference_instanced.vert.hlsl", "main", "vs_5_1",
            [
                new ShaderMacro("REFERENCE_MODERN_STANDARD", "1"),
                new ShaderMacro("REFERENCE_MODERN_STANDARD_ALPHA_GREATER", "1")
            ],
            "modern standard compact stage-link signature: GREATER cutout"),
        new("reference_instanced.vert.hlsl", "main", "vs_5_1",
            [new ShaderMacro("REFERENCE_STARFIELD_DIFFUSE_LIT", "1")],
            "Starfield diffuse-lit compact stage-link signature: opaque"),
        new("reference_instanced.vert.hlsl", "main", "vs_5_1",
            [
                new ShaderMacro("REFERENCE_STARFIELD_DIFFUSE_LIT", "1"),
                new ShaderMacro("REFERENCE_STARFIELD_DIFFUSE_LIT_ALPHA_GREATER", "1")
            ],
            "Starfield diffuse-lit compact stage-link signature: GREATER cutout"),
        new("reference_instanced.vert.hlsl", "main", "vs_5_1",
            [new ShaderMacro("SHADOW_CARD_LIGHT_FACING", "1")],
            "shadow-map replay: light-facing cards, wind compiled out"),
        new("reference.frag.hlsl", "main", "ps_5_1", None, "reference pixel shader"),
        new("reference.frag.hlsl", "main", "ps_5_1",
            [new ShaderMacro("ALPHA_TO_COVERAGE", "1")],
            "MSAA-only A2C variant; aliases the plain PSO when SceneSampleCount == 1"),
        new("reference.frag.hlsl", "main", "ps_5_1",
            [new ShaderMacro("REFERENCE_MODERN_STANDARD", "1")],
            "modern standard material: single-sided opaque"),
        new("reference.frag.hlsl", "main", "ps_5_1",
            [
                new ShaderMacro("REFERENCE_MODERN_STANDARD", "1"),
                new ShaderMacro("REFERENCE_MODERN_STANDARD_ALPHA_GREATER", "1")
            ],
            "modern standard material: single-sided GREATER cutout"),
        new("reference.frag.hlsl", "main", "ps_5_1",
            [
                new ShaderMacro("REFERENCE_MODERN_STANDARD", "1"),
                new ShaderMacro("REFERENCE_MODERN_STANDARD_ALPHA_GREATER", "1"),
                new ShaderMacro("REFERENCE_MODERN_STANDARD_DOUBLE_SIDED", "1")
            ],
            "modern standard material: double-sided GREATER cutout"),
        new("reference.frag.hlsl", "main", "ps_5_1",
            [new ShaderMacro("REFERENCE_STARFIELD_DIFFUSE_LIT", "1")],
            "Starfield diffuse-lit material: single-sided opaque"),
        new("reference.frag.hlsl", "main", "ps_5_1",
            [
                new ShaderMacro("REFERENCE_STARFIELD_DIFFUSE_LIT", "1"),
                new ShaderMacro("REFERENCE_STARFIELD_DIFFUSE_LIT_DOUBLE_SIDED", "1")
            ],
            "Starfield diffuse-lit material: double-sided opaque"),
        new("reference.frag.hlsl", "main", "ps_5_1",
            [
                new ShaderMacro("REFERENCE_STARFIELD_DIFFUSE_LIT", "1"),
                new ShaderMacro("REFERENCE_STARFIELD_DIFFUSE_LIT_ALPHA_GREATER", "1")
            ],
            "Starfield diffuse-lit material: single-sided GREATER cutout"),
        new("reference.frag.hlsl", "main", "ps_5_1",
            [
                new ShaderMacro("REFERENCE_STARFIELD_DIFFUSE_LIT", "1"),
                new ShaderMacro("REFERENCE_STARFIELD_DIFFUSE_LIT_ALPHA_GREATER", "1"),
                new ShaderMacro("REFERENCE_STARFIELD_DIFFUSE_LIT_DOUBLE_SIDED", "1")
            ],
            "Starfield diffuse-lit material: double-sided GREATER cutout"),
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
            "FO3/FNV grass MSAA A2C variant; aliases the plain PSO when SceneSampleCount == 1")
    ];

    /// <summary>
    ///     Water. The game is a per-FILE axis (<c>WaterProfile.PixelShaderFile</c>); each per-game
    ///     file that reads scene depth is compiled twice — plain, and with WATER_HARDWARE_OCCLUSION
    ///     for the read-only-DSV path. FNV's retail WATER001 program is its own file
    ///     (<c>water_fnv001.frag.hlsl</c>), and the depth-free flat plane is a single compile.
    /// </summary>
    internal static IReadOnlyList<ShaderPermutation> Water { get; } = BuildWater();

    /// <summary>
    ///     Terrain. The axis is TERRAIN_BLEND_QUADS: a cell declares only the layer-weight quads its
    ///     active slot count reaches, and the vertex shader and input layout have to agree on that
    ///     number or PSO creation fails. Quad count 0 is vertex-only — the depth-only and shadow
    ///     passes, which have no pixel shader and so need no pixel permutation.
    /// </summary>
    internal static IReadOnlyList<ShaderPermutation> Terrain { get; } = BuildTerrain();

    /// <summary>Sky, post-process, overlays and the sprite/skin path.</summary>
    internal static IReadOnlyList<ShaderPermutation> Other { get; } =
    [
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
        new("triangle.frag.hlsl", "main", "ps_5_1", None, "UNREFERENCED dev smoke triangle")
    ];

    /// <summary>
    ///     Explicit <c>FALLOUT_VIEWER_SHADOW_COMPARISON_PCF=1</c> variants. This is the complete
    ///     cross-product for pixel shaders that actually consume the macro; shaders that ignore it
    ///     are intentionally omitted because their bytecode would be a duplicate.
    /// </summary>
    internal static IReadOnlyList<ShaderPermutation> ShadowComparisonPcf { get; } =
        BuildShadowComparisonPcf();

    /// <summary>Every permutation across every family.</summary>
    internal static IReadOnlyList<ShaderPermutation> All { get; } =
        [.. Reference, .. Water, .. Terrain, .. Other, .. ShadowComparisonPcf];

    private static List<ShaderPermutation> BuildShadowComparisonPcf()
    {
        static ShaderMacro Pcf() => new(ShadowComparisonPcf12.ShaderMacroName, "1");

        var list = new List<ShaderPermutation>
        {
            new("reference.frag.hlsl", "main", "ps_5_1", [Pcf()],
                "opt-in four-sample comparison PCF: reference"),
            new("reference.frag.hlsl", "main", "ps_5_1",
                [new ShaderMacro("ALPHA_TO_COVERAGE", "1"), Pcf()],
                "opt-in four-sample comparison PCF: reference A2C"),
            new("reference.frag.hlsl", "main", "ps_5_1",
                [
                    new ShaderMacro("REFERENCE_MODERN_STANDARD", "1"),
                    Pcf()
                ],
                "opt-in four-sample comparison PCF: modern standard single-sided opaque"),
            new("reference.frag.hlsl", "main", "ps_5_1",
                [
                    new ShaderMacro("REFERENCE_MODERN_STANDARD", "1"),
                    new ShaderMacro("REFERENCE_MODERN_STANDARD_ALPHA_GREATER", "1"),
                    Pcf()
                ],
                "opt-in four-sample comparison PCF: modern standard single-sided cutout"),
            new("reference.frag.hlsl", "main", "ps_5_1",
                [
                    new ShaderMacro("REFERENCE_MODERN_STANDARD", "1"),
                    new ShaderMacro("REFERENCE_MODERN_STANDARD_ALPHA_GREATER", "1"),
                    new ShaderMacro("REFERENCE_MODERN_STANDARD_DOUBLE_SIDED", "1"),
                    Pcf()
                ],
                "opt-in four-sample comparison PCF: modern standard double-sided cutout"),
            new("reference.frag.hlsl", "main", "ps_5_1",
                [
                    new ShaderMacro("REFERENCE_STARFIELD_DIFFUSE_LIT", "1"),
                    Pcf()
                ],
                "opt-in four-sample comparison PCF: Starfield diffuse-lit single-sided opaque"),
            new("reference.frag.hlsl", "main", "ps_5_1",
                [
                    new ShaderMacro("REFERENCE_STARFIELD_DIFFUSE_LIT", "1"),
                    new ShaderMacro("REFERENCE_STARFIELD_DIFFUSE_LIT_DOUBLE_SIDED", "1"),
                    Pcf()
                ],
                "opt-in four-sample comparison PCF: Starfield diffuse-lit double-sided opaque"),
            new("reference.frag.hlsl", "main", "ps_5_1",
                [
                    new ShaderMacro("REFERENCE_STARFIELD_DIFFUSE_LIT", "1"),
                    new ShaderMacro("REFERENCE_STARFIELD_DIFFUSE_LIT_ALPHA_GREATER", "1"),
                    Pcf()
                ],
                "opt-in four-sample comparison PCF: Starfield diffuse-lit single-sided cutout"),
            new("reference.frag.hlsl", "main", "ps_5_1",
                [
                    new ShaderMacro("REFERENCE_STARFIELD_DIFFUSE_LIT", "1"),
                    new ShaderMacro("REFERENCE_STARFIELD_DIFFUSE_LIT_ALPHA_GREATER", "1"),
                    new ShaderMacro("REFERENCE_STARFIELD_DIFFUSE_LIT_DOUBLE_SIDED", "1"),
                    Pcf()
                ],
                "opt-in four-sample comparison PCF: Starfield diffuse-lit double-sided cutout"),
            new("reference_grass_oblivion.frag.hlsl", "main", "ps_5_1", [Pcf()],
                "opt-in four-sample comparison PCF: Oblivion grass"),
            new("reference_grass_fnv.frag.hlsl", "main", "ps_5_1", [Pcf()],
                "opt-in four-sample comparison PCF: FO3/FNV grass"),
            new("reference_grass_fnv.frag.hlsl", "main", "ps_5_1",
                [new ShaderMacro("ALPHA_TO_COVERAGE", "1"), Pcf()],
                "opt-in four-sample comparison PCF: FO3/FNV grass A2C")
        };

        for (var quads = 1; quads <= 4; quads++)
        {
            list.Add(new ShaderPermutation(
                "terrain_textured.frag.hlsl", "main", "ps_5_1",
                [
                    new ShaderMacro(
                        "TERRAIN_BLEND_QUADS", quads.ToString(CultureInfo.InvariantCulture)),
                    Pcf()
                ],
                $"opt-in four-sample comparison PCF: terrain, {quads * 4}-slot cells"));
        }

        list.Add(new ShaderPermutation(
            "water_fo4.frag.hlsl", "main", "ps_5_1",
            [new ShaderMacro("FO4_WATER_ARCHITECTURAL", "1"), Pcf()],
            "opt-in four-sample comparison PCF: FO4/FO76 architectural water"));
        list.Add(new ShaderPermutation(
            "water_fo4.frag.hlsl", "main", "ps_5_1",
            [
                new ShaderMacro("FO4_WATER_ARCHITECTURAL", "1"),
                new ShaderMacro("WATER_HARDWARE_OCCLUSION", "1"),
                Pcf()
            ],
            "opt-in four-sample comparison PCF: FO4/FO76 architectural water, hardware occlusion"));
        return list;
    }

    private static List<ShaderPermutation> BuildTerrain()
    {
        var list = new List<ShaderPermutation>
        {
            new("terrain_textured.vert.hlsl", "main", "vs_5_1",
                [new ShaderMacro("TERRAIN_BLEND_QUADS", "0")],
                "terrain depth-only + sun-shadow: no layer weights fetched at all")
        };

        // 1..4 quads = 4..16 layer weights. Written as a loop rather than eight literals so a change
        // to the slot ceiling cannot add a vertex variant and forget its pixel companion — the two
        // must be compiled from the same number or PSInput will not match VSOutput.
        for (var quads = 1; quads <= 4; quads++)
        {
            var macros = (ShaderMacro[])[new ShaderMacro("TERRAIN_BLEND_QUADS", quads.ToString(CultureInfo.InvariantCulture))];
            var purpose = $"terrain, {quads * 4}-slot cells";
            list.Add(new ShaderPermutation("terrain_textured.vert.hlsl", "main", "vs_5_1", macros, purpose));
            list.Add(new ShaderPermutation("terrain_textured.frag.hlsl", "main", "ps_5_1", macros, purpose));
        }

        return list;
    }

    private static List<ShaderPermutation> BuildWater()
    {
        var list = new List<ShaderPermutation>
        {
            new("water.vert.hlsl", "main", "vs_5_1", None, "water surface"),
            new("water_noise.comp.hlsl", "mainScrollBlend", "cs_5_1", None, "FNV noise prepass: scroll+blend"),
            new("water_noise.comp.hlsl", "mainNormal", "cs_5_1", None, "FNV noise prepass: normal")
        };

        // File axis (the game, per WaterProfile.PixelShaderFile) x occlusion axis (read-only DSV).
        // Written as a product rather than literals so a new per-game file cannot be added to one
        // axis and forgotten on the other — the exact drift that left the water PSO table
        // asymmetric before.
        (string File, string Purpose)[] variants =
        [
            ("water_fnv.frag.hlsl", "FNV/FO3/Skyrim classic WATER000"),
            ("water_oblivion.frag.hlsl", "Oblivion WATER000: N.V body, single sun glint"),
            ("water_fo4.frag.hlsl", "FO4/FO76 BSWaterShader stand-in"),
            ("water_morrowind.frag.hlsl", "Morrowind fixed-function animated plane"),
            ("water_starfield.frag.hlsl", "Starfield WATR source-backed approximation")
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

        // FO76 shares the FO4 file but no longer shares its byte-identical permutation. The strict
        // 148-byte WATR route emits SV_Target1 for per-channel destination transmission, so both
        // the macro and the matching dual-source PSO are real runtime axes.
        foreach (var occlusion in (bool[])[false, true])
        {
            var macros = new List<ShaderMacro>
            {
                new("FO76_WATER_OPTICS", "1")
            };
            if (occlusion) macros.Add(new ShaderMacro("WATER_HARDWARE_OCCLUSION", "1"));
            list.Add(new ShaderPermutation(
                "water_fo4.frag.hlsl", "main", "ps_5_1", [.. macros],
                occlusion
                    ? "FO76 float-optics reference approximation (hardware occlusion)"
                    : "FO76 float-optics reference approximation"));
        }

        // The flat tinted plane for games with no recovered shader is off BOTH axes above: it reads
        // no scene depth, so there is no occlusion clip for WATER_HARDWARE_OCCLUSION to remove and
        // the second compile would be byte-identical (PermutationsAreDistinct would reject it).
        list.Add(new ShaderPermutation(
            "water_flat.frag.hlsl", "main", "ps_5_1", None,
            "flat tinted plane: no recovered or source-backed shader (Unknown)"));

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
                new("FO4_WATER_ARCHITECTURAL", "1")
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
