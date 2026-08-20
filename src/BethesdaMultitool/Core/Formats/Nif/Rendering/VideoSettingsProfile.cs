using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering;

/// <summary>
///     Which retail-style VIDEO settings apply to a loaded game — the per-game capability table
///     behind the 3D viewer's "Video" section (mirrors the game's own launcher/in-game video
///     options; option names in the flags' docs are the engine INI settings each toggle stands
///     for). Follows the <see cref="Water.WaterProfile.ForGame" /> /
///     <c>GrassScatterProfile.ForGame</c> pattern: pure data, one arm per game, UI visibility is
///     driven from here (never hard-coded game compares in the panel).
/// </summary>
/// <param name="HasTonemapSelector">
///     Any post-processing selector applies (everything but
///     Morrowind, whose engine has no HDR/bloom/imagespace stage — the panel's whole tonemap UI
///     hides, matching the existing TonemapSection rule).
/// </param>
/// <param name="HasClassicHdrTriple">
///     The launcher's three-way None/Bloom/HDR radio
///     (<c>bDoHighDynamicRange:BlurShaderHDR</c> / <c>bUseBlurShader:BlurShader</c>) — Oblivion,
///     FO3, FNV ship the identical launcher block. Games without it show HDR/None only.
/// </param>
/// <param name="HasWaterReflections">
///     Water reflection toggle (<c>bUseWaterReflections:Water</c>)
///     — games whose recovered water shader consumes a reflection input.
/// </param>
/// <param name="HasWaterRipples">
///     Water ripple toggle (<c>bUseWaterDisplacements:Water</c>; the
///     viewer maps it to the animated surface normal field — USER ruling 2026-08-18).
/// </param>
/// <param name="HasWindowReflections">
///     Window reflection toggle
///     (<c>bDynamicWindowReflections:Display</c>) — classic Gamebryo window env-map materials.
/// </param>
/// <param name="HasGrassShadows">
///     Grass shadow-receive toggle (<c>bShadowsOnGrass:Display</c>;
///     retail grass RECEIVES canopy shadows and never casts).
/// </param>
/// <param name="HasCanopyShadows">
///     Tree canopy shadow toggle (<c>bDoCanopyShadowPass:Display</c>;
///     the viewer's real cascaded shadow map stands in for retail's projected canopy blobs, so the
///     toggle gates tree casters).
/// </param>
internal readonly record struct VideoSettingsProfile(
    bool HasTonemapSelector,
    bool HasClassicHdrTriple,
    bool HasWaterReflections,
    bool HasWaterRipples,
    bool HasWindowReflections,
    bool HasGrassShadows,
    bool HasCanopyShadows)
{
    /// <summary>The Video expander shows when any of its controls apply.</summary>
    internal bool ShowVideoSection =>
        HasTonemapSelector || HasWaterReflections || HasWaterRipples ||
        HasWindowReflections || HasGrassShadows || HasCanopyShadows;

    internal static VideoSettingsProfile ForGame(BethesdaGame game)
    {
        return game switch
        {
            // The full retail option set this feature mirrors (launcher triple verified in
            // OblivionLauncher.exe strings; ini names verified in Oblivion_default.ini +
            // Oblivion.exe strings — bUseWaterReflections/bUseWaterDisplacements/
            // bDynamicWindowReflections/bShadowsOnGrass/bDoCanopyShadowPass).
            BethesdaGame.Oblivion => new VideoSettingsProfile(
                true, true,
                true, true,
                true, true, true),

            // FO3/FNV ship the identical launcher triple (Fallout3Launcher/FalloutNVLauncher —
            // backlog shared-sky-lighting.md pins the string blocks), classic window env-map
            // materials (bit-21 ENVMAP_W flows through the same shader path), shadowed grass
            // (the FNV grass shader's recovered sun-shadow term), and SpeedTree canopies.
            BethesdaGame.Fallout3 or BethesdaGame.FalloutNewVegas => new VideoSettingsProfile(
                true, true,
                true, true,
                true, true, true),

            // Creation-era: tonemap selector (HDR/None) + the water rows; window/grass/canopy
            // toggles are classic-Gamebryo features their materials do not route through.
            BethesdaGame.Skyrim or BethesdaGame.Fallout4 or BethesdaGame.Fallout76 => new VideoSettingsProfile(
                true, false,
                true, true,
                false, false, false),

            // Morrowind has no engine HDR/bloom stage and a diffuse-animation water with no
            // reflection input; Starfield's water/material recovery is too incomplete to offer
            // toggles that could not act. The section hides entirely.
            _ => new VideoSettingsProfile(
                false, false,
                false, false,
                false, false, false)
        };
    }
}
