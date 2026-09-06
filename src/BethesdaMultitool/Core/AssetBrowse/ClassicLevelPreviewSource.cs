using BethesdaMultitool.Core.Formats.Battlespire;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Viewer;
using BethesdaMultitool.Core.Formats.Xngine.Mesh;

namespace BethesdaMultitool.Core.AssetBrowse;

/// <summary>
///     Assembles a whole classic LEVEL — many placed meshes — into a viewer scene, so a level can be
///     explored in the GUI rather than only exported to GLB.
///     <para>
///         Battlespire is the first backer because its level geometry is held BY REFERENCE: a
///         <c>.BS6</c> level carries a mesh list (LFIL) plus placements that index it (OBJD.IDFI
///         with POSI/ANGS), and <c>Bs6SceneAssembler</c> already resolves those against the mesh
///         archives. Reusing it means the GUI and <c>classic level export</c> place geometry
///         identically — the ANGS convention in particular was settled by measurement and must not
///         be re-derived here.
///     </para>
///     <para>
///         ⚠ Meshes resolve against the archives sitting BESIDE the opened file, so this works for
///         a <c>.BS6</c> opened from a real install and not for one copied somewhere on its own.
///         That is the same rule <c>classic level export</c> follows.
///     </para>
/// </summary>
internal static class ClassicLevelPreviewSource
{
    /// <summary>True when a node names a level this can assemble.</summary>
    public static bool CanPreview(AssetBrowseSession session, AssetNode node)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(node);

        // A level lives inside a BS6 archive, so the session must BE that archive: the entry names
        // the level, and the archive's own directory locates the meshes.
        return node.Kind != AssetNodeKind.Folder
               && Path.GetExtension(session.SourcePath).Equals(".bs6", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Assembles the level, or returns null when it is not one or resolves no geometry.
    ///     <para>
    ///         A level that resolves NONE of its placements returns null rather than an empty scene:
    ///         an empty viewer looks like a working viewer pointed at an empty level, which is a
    ///         worse report than saying nothing loaded.
    ///     </para>
    /// </summary>
    public static BethesdaViewerScene? TryLoad(
        AssetBrowseSession session, AssetNode node, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(node);

        if (!CanPreview(session, node))
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

            var level = Bs6File.Parse(bytes, node.Name);
            var meshDirectory = Path.GetDirectoryName(Path.GetFullPath(session.SourcePath));
            if (meshDirectory is null)
            {
                return null;
            }

            using var meshes = BattlespireMeshLibrary.Open(meshDirectory);
            var assembly = Bs6SceneAssembler.Assemble(level, meshes.Resolve);
            if (assembly.Instances.Count == 0)
            {
                return null;
            }

            cancellationToken.ThrowIfCancellationRequested();

            var stem = Path.GetFileNameWithoutExtension(level.Name).ToUpperInvariant();
            var scene = XnGineViewerSceneAdapter.ToViewerScene(stem, assembly.Instances);
            return BethesdaViewerSceneGlbAdapter.FromGlbScene(
                scene, stem, BethesdaViewerScenePurpose.ClassicMesh);
        }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException
                                     or IOException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }
}
