using BethesdaMultitool.Core.Formats.Nif.Rendering.Viewer;
using BethesdaMultitool.Core.Formats.Xngine.Mesh;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.AssetBrowse;

/// <summary>
///     Loads a classic mesh out of a browse session and hands back a scene the native viewer can
///     display, so classic geometry is inspectable in the GUI rather than only exportable to GLB.
///     <para>
///         The scene is tagged <see cref="BethesdaViewerScenePurpose.ClassicMesh" /> rather than
///         <c>RawNif</c>. That is not cosmetic: <c>RawNif</c> opts a scene into the native sky pass,
///         which a classic mesh must not get.
///     </para>
/// </summary>
internal static class ClassicMeshPreviewSource
{
    /// <summary>
    ///     True for a mesh this build can actually display.
    ///     <para>
    ///         ⚠ <c>.3DC</c> is deliberately EXCLUDED. Redguard's compressed meshes parse, but their
    ///         point decoding is still an open item, so accepting them here would show a confidently
    ///         wrong shape instead of nothing. An honest empty pane beats plausible garbage.
    ///     </para>
    /// </summary>
    public static bool CanPreview(AssetNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return Path.GetExtension(node.Name).Equals(".3D", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Reads and adapts one mesh, or returns null when it is not a previewable mesh or will not
    ///     parse.
    /// </summary>
    public static BethesdaViewerScene? TryLoad(
        AssetBrowseSession session, AssetNode node, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(node);

        if (!CanPreview(node))
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var bytes = session.FileSystem.TryReadAllBytes(node.VirtualPath);
            if (bytes is null)
            {
                return null;
            }

            var mesh = XnGineMesh.Parse(bytes, 0, ResolveLayout(session));
            var scene = XnGineViewerSceneAdapter.ToViewerScene(XnGineMeshDecomposer.Decompose(mesh), node.Name);
            return BethesdaViewerSceneGlbAdapter.FromGlbScene(
                scene, node.Name, BethesdaViewerScenePurpose.ClassicMesh);
        }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException
                                     or IOException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    ///     Picks the plane-header layout from the OWNING GAME, not from the file's version tag.
    ///     <para>
    ///         ⚠ Battlespire and Redguard both ship <c>.3D</c> files that both label themselves
    ///         <c>v2.7</c>, yet Battlespire's plane header is 10 bytes and Redguard's is 8 — the same
    ///         as Daggerfall's. The tag cannot decide; the install has to.
    ///     </para>
    /// </summary>
    private static XnGineMeshLayout ResolveLayout(AssetBrowseSession session)
    {
        var game = ClassicGameLocator.DetectRootForFile(session.SourcePath)?.Profile.Game
                   ?? ClassicGameLocator.DetectFromDirectory(session.SourcePath)?.Game;

        return game == BethesdaGame.Battlespire ? XnGineMeshLayout.Battlespire : XnGineMeshLayout.Daggerfall;
    }
}
