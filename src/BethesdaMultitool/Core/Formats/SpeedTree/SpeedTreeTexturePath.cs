namespace BethesdaMultitool.Core.Formats.SpeedTree;

/// <summary>
///     Maps the dev-machine absolute texture paths embedded in a <c>.spt</c> (e.g.
///     <c>C:\Noah\Fallout\Trees\WastelandShrub01\WastelandShrub01Bark.tga</c>) to the game-relative
///     paths the asset pipeline actually ships, confirmed against the extracted texture BSAs:
///     <list type="bullet">
///         <item>bark   → <c>textures\trees\branches\&lt;name&gt;bark.dds</c></item>
///         <item>foliage → <c>textures\trees\leaves\&lt;name&gt;foliage.dds</c> (one shared atlas;
///               the <c>.spt</c>'s FoliageNN names all resolve to it)</item>
///         <item>other  → <c>textures\trees\&lt;name&gt;.dds</c></item>
///     </list>
/// </summary>
public static class SpeedTreeTexturePath
{
    /// <summary>
    ///     Map a <c>TREE</c> record's <c>ICON</c> value (the AUTHORITATIVE leaf atlas the engine applies,
    ///     overriding the <c>.spt</c>'s dev-era material) to a game-relative path. FNV stores a bare name
    ///     (e.g. <c>WhiteOakLeaves01.dds</c>) → <c>textures\trees\leaves\whiteoakleaves01.dds</c>; a value
    ///     that already carries folders is normalized under <c>textures\</c>. Returns null when empty.
    /// </summary>
    public static string? IconToLeafPath(string? icon)
    {
        if (string.IsNullOrWhiteSpace(icon))
        {
            return null;
        }

        var v = icon.Replace('/', '\\').Trim().ToLowerInvariant();
        if (v.StartsWith(@"textures\", StringComparison.Ordinal))
        {
            return v;
        }

        if (v.Contains('\\'))
        {
            return @"textures\" + v;
        }

        var dot = v.LastIndexOf('.');
        var stem = dot >= 0 ? v[..dot] : v;
        return $@"textures\trees\leaves\{stem}.dds";
    }

    /// <summary>Convert a <c>.spt</c>-embedded texture path to a game-relative <c>textures\trees\...</c> path.</summary>
    public static string? ToGamePath(string? devPath)
    {
        if (string.IsNullOrWhiteSpace(devPath))
        {
            return null;
        }

        // Normalize separators and take the base file name without extension.
        var normalized = devPath.Replace('/', '\\');
        var slash = normalized.LastIndexOf('\\');
        var fileName = slash >= 0 ? normalized[(slash + 1)..] : normalized;
        var dot = fileName.LastIndexOf('.');
        var stem = (dot >= 0 ? fileName[..dot] : fileName).ToLowerInvariant().Trim();
        if (stem.Length == 0)
        {
            return null;
        }

        if (stem.Contains("bark", StringComparison.Ordinal))
        {
            return $@"textures\trees\branches\{stem}.dds";
        }

        if (stem.Contains("foliage", StringComparison.Ordinal) ||
            stem.Contains("leaf", StringComparison.Ordinal) ||
            stem.Contains("leaves", StringComparison.Ordinal))
        {
            // Shrub "…foliageNN" atlases ship as ONE un-numbered "…foliage.dds" (the per-region
            // FoliageNN names collapse to it), so strip the trailing index there. Numbered leaf atlases
            // ("…leavesNN.dds", e.g. pineleaves01) keep their index, so leave those alone.
            var trimmed = stem;
            if (stem.Contains("foliage", StringComparison.Ordinal))
            {
                var stripped = stem.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
                if (stripped.EndsWith("foliage", StringComparison.Ordinal))
                {
                    trimmed = stripped;
                }
            }

            return $@"textures\trees\leaves\{trimmed}.dds";
        }

        return $@"textures\trees\{stem}.dds";
    }
}
