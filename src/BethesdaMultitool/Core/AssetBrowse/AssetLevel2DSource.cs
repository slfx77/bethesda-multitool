using BethesdaMultitool.Core.Formats.Arena;
using BethesdaMultitool.Core.Formats.Daggerfall;
using BethesdaMultitool.Core.Rendering.Level2D;

namespace BethesdaMultitool.Core.AssetBrowse;

/// <summary>
///     Decides which classic level a browsed file is, and builds the matching
///     <see cref="ILevel2DSource" /> for the 2D map pane.
///     <para>
///         This is the "top-down view of the geometry" route — the equivalent of the terrain layer
///         with meshes drawn on. It exists because several classic level formats ARE grids: an
///         Arena <c>.MIF</c> is a voxel grid and Daggerfall's <c>WOODS.WLD</c> is a heightmap, so
///         building a 3D surface to look at one is a detour through a lossier representation.
///     </para>
///     <para>
///         ⚠ Layer colours are DIAGNOSTIC — a stable hue per voxel id, 0 = black — not the game's
///         textures. Resolving real textures needs the level's <c>.INF</c> plus tables still inside
///         the packed <c>A.EXE</c>. A viewer that presented these as the game's own art would be
///         lying about what has been decoded.
///     </para>
/// </summary>
internal static class AssetLevel2DSource
{
    /// <summary>True for a file this build can show as a 2D level.</summary>
    public static bool CanOpen(AssetNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        var extension = Path.GetExtension(node.Name);
        return extension.Equals(".mif", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".rmd", StringComparison.OrdinalIgnoreCase)
               || IsDaggerfallWorldMap(node.Name);
    }

    /// <summary>Builds the source for a node, or returns null when it is not a level this can show.</summary>
    public static ILevel2DSource? TryOpen(
        AssetBrowseSession session, AssetNode node, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(node);

        if (!CanOpen(node))
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var bytes = session.FileSystem.TryReadAllBytes(node.VirtualPath);
            if (bytes is null || bytes.Length == 0)
            {
                return null;
            }

            var extension = Path.GetExtension(node.Name);
            if (extension.Equals(".mif", StringComparison.OrdinalIgnoreCase))
            {
                return ArenaMapLevel2DSource.ForMifLevel(ArenaMifFile.Parse(bytes, node.Name), 0);
            }

            if (extension.Equals(".rmd", StringComparison.OrdinalIgnoreCase))
            {
                return ArenaMapLevel2DSource.ForRmd(ArenaRmdFile.Parse(bytes, node.Name), node.Name);
            }

            // WOODS is the heightmap; a PAK is a single-plane overlay over the same grid.
            return Path.GetExtension(node.Name).Equals(".wld", StringComparison.OrdinalIgnoreCase)
                ? new DaggerfallMapLevel2DSource(DaggerfallWoodsFile.Parse(bytes, node.Name), null)
                : new DaggerfallMapLevel2DSource(null, DaggerfallPakFile.Parse(bytes, node.Name));
        }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException
                                     or IOException or ArgumentException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>
    ///     Daggerfall's world-map files are named by convention, not extension:
    ///     <c>WOODS.WLD</c> is the 1000x500 heightmap and <c>CLIMATE/POLITIC.PAK</c> are its RLE
    ///     overlays.
    ///     <para>
    ///         ⚠ Redguard also ships <c>.WLD</c> files and they are a DIFFERENT format (a fixed
    ///         263,432-byte, 8-layer container), so the name has to be matched rather than the
    ///         extension alone.
    ///     </para>
    /// </summary>
    private static bool IsDaggerfallWorldMap(string fileName)
    {
        var name = Path.GetFileName(fileName);
        return name.Equals("WOODS.WLD", StringComparison.OrdinalIgnoreCase)
               || name.Equals("CLIMATE.PAK", StringComparison.OrdinalIgnoreCase)
               || name.Equals("POLITIC.PAK", StringComparison.OrdinalIgnoreCase);
    }
}
