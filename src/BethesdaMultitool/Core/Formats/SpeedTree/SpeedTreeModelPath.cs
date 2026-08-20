namespace BethesdaMultitool.Core.Formats.SpeedTree;

/// <summary>
///     Resolves a TREE record's MODL value to the archive path a SpeedTree <c>.spt</c> ships under.
///     Unlike NIFs (stored under <c>meshes\</c>), <c>.spt</c> trees live at the BSA root under
///     <c>trees\</c>, and the MODL is usually a bare name with a leading backslash
///     (e.g. <c>\WastelandShrub01.spt</c> → <c>trees\WastelandShrub01.spt</c>).
/// </summary>
public static class SpeedTreeModelPath
{
    /// <summary>Returns true when the path has a <c>.spt</c> SpeedTree extension (case-insensitive).</summary>
    public static bool IsSpt(string path)
    {
        return path.EndsWith(".spt", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Normalize a TREE MODL value to its <c>trees\...spt</c> archive lookup path.</summary>
    public static string ToArchivePath(string modelPath)
    {
        var spt = modelPath.Replace('/', '\\').Trim().TrimStart('\\');

        if (spt.StartsWith("meshes\\", StringComparison.OrdinalIgnoreCase))
        {
            spt = spt["meshes\\".Length..];
        }

        return spt.StartsWith("trees\\", StringComparison.OrdinalIgnoreCase) ? spt : "trees\\" + spt;
    }
}
