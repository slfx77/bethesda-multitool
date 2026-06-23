namespace BethesdaMultitool.Core.Formats.SpeedTree;

/// <summary>The role a SpeedTree texture plays, used to route it to the correct shipped folder.</summary>
public enum SpeedTreeTextureKind
{
    /// <summary>Infer bark vs leaf vs other from keywords in the file name (legacy behavior).</summary>
    Auto,

    /// <summary>Bark/branch texture → <c>textures\trees\branches\</c>.</summary>
    Bark,

    /// <summary>Leaf-card atlas → <c>textures\trees\leaves\</c>.</summary>
    Leaf
}

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

    /// <summary>
    ///     Convert a <c>.spt</c>-embedded texture path to a game-relative <c>textures\trees\...</c> path.
    ///     <paramref name="kind" /> disambiguates the destination folder when the file name alone can't:
    ///     a leaf card named like <c>WastelandUndergrowthBranches01.tga</c> contains none of the
    ///     bark/foliage/leaf keywords, so <see cref="SpeedTreeTextureKind.Auto" /> would mis-file it under
    ///     <c>textures\trees\</c> (untextured). The bark vs leaf call sites in <c>SptGeometryBuilder</c> know
    ///     which role the texture plays and pass <see cref="SpeedTreeTextureKind.Bark" /> /
    ///     <see cref="SpeedTreeTextureKind.Leaf" />.
    /// </summary>
    public static string? ToGamePath(string? devPath, SpeedTreeTextureKind kind = SpeedTreeTextureKind.Auto)
    {
        if (string.IsNullOrWhiteSpace(devPath))
        {
            return null;
        }

        // Some .spt embed an ALREADY game-relative shipped path rather than a dev abspath — e.g.
        // Oblivion's TreeKvatchBurnt bark is "C:\Oblivion\Data\Textures\Trees\Branches\TreeKvatchBurnt.dds"
        // (note: no "bark" in the stem, already .dds, real \trees\branches\ subfolder). The stem heuristic
        // below would mis-file it under \trees\ (untextured), so honor an embedded "textures\trees\..."
        // segment verbatim (for every kind). Dev abspaths use "…\Trees\<TreeName>\…" (no "textures\trees\")
        // and fall through.
        var lower = devPath.Replace('/', '\\').ToLowerInvariant();
        var embedded = lower.IndexOf(@"textures\trees\", StringComparison.Ordinal);
        if (embedded >= 0)
        {
            var rel = lower[embedded..];
            var extDot = rel.LastIndexOf('.');
            return (extDot >= 0 ? rel[..extDot] : rel) + ".dds";
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

        return kind switch
        {
            // Bark always lives under \trees\branches\, regardless of whether the stem says "bark".
            SpeedTreeTextureKind.Bark => $@"textures\trees\branches\{stem}.dds",
            // Leaf cards always live under \trees\leaves\. The leaf material the engine actually ships may
            // collapse several per-card variants to one shared atlas — see CollapseLeafCardIndex.
            SpeedTreeTextureKind.Leaf => $@"textures\trees\leaves\{CollapseLeafCardIndex(stem)}.dds",
            _ => ToGamePathByKeyword(stem)
        };
    }

    /// <summary>
    ///     Legacy <see cref="SpeedTreeTextureKind.Auto" /> routing: infer bark vs leaf vs other from
    ///     keywords in the file name. Used when the caller doesn't know the texture's role.
    /// </summary>
    private static string ToGamePathByKeyword(string stem)
    {
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

    /// <summary>
    ///     For a leaf-kind card, collapse a trailing per-card numeric index to the shared atlas name
    ///     (e.g. <c>wastelandundergrowthbranches01</c> / <c>…branches02</c> → <c>…branches</c>, matching the
    ///     single shipped <c>…branches.ddx</c>), UNLESS the stem is a genuinely numbered "leaf"/"leaves"
    ///     atlas (e.g. <c>pineleaves01</c>) that the engine ships per-number.
    /// </summary>
    private static string CollapseLeafCardIndex(string stem)
    {
        if (stem.Contains("leaf", StringComparison.Ordinal) ||
            stem.Contains("leaves", StringComparison.Ordinal))
        {
            return stem;
        }

        var stripped = stem.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
        return stripped.Length > 0 ? stripped : stem;
    }
}
