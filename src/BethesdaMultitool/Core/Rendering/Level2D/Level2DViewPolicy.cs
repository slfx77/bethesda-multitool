// Ported from NeversoftMultitool (https://github.com/slfx77/NeversoftMultitool, MIT License) —
//   src/NeversoftMultitool/Core/Rendering/Level2d/Level2dViewPolicy.cs. Upstream asks a single GBA
//   source whether it supports a file; here the question is answered from the classic map formats
//   this repo reads. License texts are collected centrally in THIRD_PARTY_LICENSES.

namespace BethesdaMultitool.Core.Rendering.Level2D;

/// <summary>
///     Whether a file can be shown as a picture, and whether it should be by default.
/// </summary>
internal static class Level2DViewPolicy
{
    /// <summary>Daggerfall's world heightmap. Its name is fixed by the game.</summary>
    private const string WoodsFileName = "WOODS.WLD";

    /// <summary>Whether this file has an authored 2D picture to show at all.</summary>
    public static bool Supports(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);

        var name = Path.GetFileName(fileName);
        if (name.Equals(WoodsFileName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return Path.GetExtension(name).ToUpperInvariant() switch
        {
            // Arena's voxel maps: a .MIF level and an .RMD wilderness chunk are both grids.
            ".MIF" or ".RMD" => true,
            // Daggerfall's CLIMATE/POLITIC overlays.
            ".PAK" => true,
            _ => false
        };
    }

    /// <summary>
    ///     Whether the 2D view is the one to open on.
    ///     <para>
    ///         2D is the default exactly where the level WAS authored as a grid — the 3D surface is
    ///         then the derived view, not the original. Every other format here is geometry first
    ///         and has no picture to prefer, so it opens in 3D.
    ///     </para>
    /// </summary>
    public static bool DefaultsToTwoDimensional(string fileName)
    {
        return Supports(fileName);
    }

    /// <summary>A short label for a layer, for the picker and for export stems.</summary>
    public static string LayerLabel(Level2DLayer layer)
    {
        return layer switch
        {
            Level2DLayer.Floor => "Floor",
            Level2DLayer.Walls => "Walls",
            Level2DLayer.Ceiling => "Upper storey",
            Level2DLayer.Overlay => "Overlay",
            _ => layer.ToString()
        };
    }

    /// <summary>The suffix a layer's exported PNG carries, so the file says which it is.</summary>
    public static string LayerStemSuffix(Level2DLayer layer)
    {
        return layer switch
        {
            // The floor is the base picture, so it takes the bare stem — matching what
            // `classic map export` already writes for a single-layer render.
            Level2DLayer.Floor => string.Empty,
            Level2DLayer.Walls => "_walls",
            Level2DLayer.Ceiling => "_upper",
            Level2DLayer.Overlay => "_overlay",
            _ => "_" + layer.ToString().ToLowerInvariant()
        };
    }
}
