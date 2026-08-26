using BethesdaMultitool.Core.Formats.SpeedTree;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering;

/// <summary>
///     Turns a record's authored <c>MODL</c> into the path an archive is keyed by.
///     <para>
///         Pure string work, deliberately kept out of the D3D12 renderer (which compiles only under
///         <c>WINDOWS_GUI</c> and so cannot be unit-tested): getting this wrong is silently
///         expensive. An archive lookup that misses is not retried — the decoder records a permanent
///         negative, so the object never renders again for the rest of the session even though its
///         mesh ships in the game.
///     </para>
/// </summary>
internal static class ReferenceModelPath
{
    private const string MeshesRoot = "meshes\\";
    private const string DataPrefix = "data\\";

    /// <summary>
    ///     Normalizes <paramref name="modelPath" /> to an archive-rooted path: separators unified,
    ///     a leading <c>Data\</c> removed, and <c>meshes\</c> applied exactly once. SpeedTree
    ///     <c>.spt</c> models route to <c>trees\</c> instead — they live at the archive root, so
    ///     prefixing them with <c>meshes\</c> would miss every tree.
    /// </summary>
    public static string Normalize(string modelPath)
    {
        var normalized = modelPath.Replace('/', '\\').Trim();

        if (SpeedTreeModelPath.IsSpt(normalized))
        {
            return SpeedTreeModelPath.ToArchivePath(normalized);
        }

        normalized = normalized.TrimStart('\\');

        // A handful of records author from the game folder rather than the archive root
        // ("Data\meshes\setdressing\..."). Archive entries never carry that prefix, so leaving it
        // produced "meshes\Data\meshes\..." and the lookup missed — confirmed on Fallout 76's
        // Clarksburg sign patches, which sat in the decoded-mesh cache as found=0.
        if (normalized.StartsWith(DataPrefix, StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[DataPrefix.Length..].TrimStart('\\');
        }

        return normalized.StartsWith(MeshesRoot, StringComparison.OrdinalIgnoreCase)
            ? normalized
            : MeshesRoot + normalized;
    }
}
