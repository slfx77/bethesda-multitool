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
///         <item>
///             foliage → <c>textures\trees\leaves\&lt;name&gt;foliage.dds</c> (one shared atlas;
///             the <c>.spt</c>'s FoliageNN names all resolve to it)
///         </item>
///         <item>other  → <c>textures\trees\&lt;name&gt;.dds</c></item>
///     </list>
/// </summary>
public static class SpeedTreeTexturePath
{
    /// <summary>
    ///     The season tokens Oblivion appends to a tree's leaf atlas (TreeXLeaves<b>SU</b>.dds, …FA, …).
    ///     A numeric per-card index that sits BETWEEN "leaves" and one of these (e.g. <c>…leaves01su</c>) is a
    ///     dev-era artifact: every numbered card collapses to the one shipped <c>…leavessu.dds</c>.
    /// </summary>
    private static readonly string[] LeafSeasonSuffixes = ["su", "fa", "wi", "sp"];

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
    ///     For a leaf-kind card, collapse a per-card numeric index to the shared atlas name. Two shapes:
    ///     <list type="bullet">
    ///         <item>
    ///             Oblivion MEDIAL index before a season suffix — <c>treewillowoakleaves01su</c> /
    ///             <c>…02su</c> / <c>…03su</c> → <c>treewillowoakleavessu</c>. Oblivion ships ONE combined
    ///             leaf atlas per tree+season; the .spt's per-card materials all index into it (the token
    ///             10002 UVs pick each card's region), so a numbered file never shipped and every card
    ///             would otherwise resolve to a missing texture and render untextured (white).
    ///         </item>
    ///         <item>
    ///             TRAILING index on a non-"leaves" stem — <c>wastelandundergrowthbranches01</c> /
    ///             <c>…branches02</c> → <c>…branches</c>, matching the single shipped <c>…branches.ddx</c>.
    ///         </item>
    ///     </list>
    ///     A TRAILING index on a "leaf"/"leaves" stem with NO season suffix is kept: FNV ships those per-number
    ///     (<c>pineleaves01.dds</c>, <c>whiteoakleaves01.dds</c>).
    /// </summary>
    private static string CollapseLeafCardIndex(string stem)
    {
        var seasonCollapsed = CollapseMedialLeafSeasonIndex(stem);
        if (seasonCollapsed != stem)
        {
            return seasonCollapsed;
        }

        if (stem.Contains("leaf", StringComparison.Ordinal) ||
            stem.Contains("leaves", StringComparison.Ordinal))
        {
            return stem;
        }

        var stripped = stem.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
        return stripped.Length > 0 ? stripped : stem;
    }

    /// <summary>
    ///     Drop a digit run that sits immediately after "leaves"/"leaf" and is followed only by a season
    ///     suffix (the Oblivion <c>…leaves01su</c> → <c>…leavessu</c> case). Returns the stem unchanged when there
    ///     is no such medial index, so FNV's trailing-numbered atlases and every non-leaf stem are untouched.
    /// </summary>
    private static string CollapseMedialLeafSeasonIndex(string stem)
    {
        var marker = stem.LastIndexOf("leaves", StringComparison.Ordinal);
        var markerLen = 6;
        if (marker < 0)
        {
            marker = stem.LastIndexOf("leaf", StringComparison.Ordinal);
            markerLen = 4;
        }

        if (marker < 0)
        {
            return stem;
        }

        var afterMarker = marker + markerLen;
        var i = afterMarker;
        while (i < stem.Length && char.IsAsciiDigit(stem[i]))
        {
            i++;
        }

        if (i == afterMarker)
        {
            return stem; // no digits right after the leaf marker
        }

        var suffix = stem[i..];
        foreach (var season in LeafSeasonSuffixes)
        {
            if (suffix.Equals(season, StringComparison.Ordinal))
            {
                return stem[..afterMarker] + suffix; // drop the digit run, keep "leaves" + season
            }
        }

        return stem;
    }
}
