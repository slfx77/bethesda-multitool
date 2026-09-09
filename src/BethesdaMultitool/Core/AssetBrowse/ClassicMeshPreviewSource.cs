using System.Text;
using BethesdaMultitool.Core.Formats.Battlespire;
using BethesdaMultitool.Core.Formats.Dds;
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
    ///     Largest payload this will probe.
    ///     <para>
    ///         ⚠ This is a CAP, not a read length: <c>TryReadAllBytesBounded</c> returns null when
    ///         the entry is BIGGER than the budget. A 64-byte budget therefore rejected every mesh
    ///         rather than reading its first four bytes — 40 of 40 ARCH3D entries failed the probe.
    ///         Retail meshes reach ~200 KB, so the cap is generous and only excludes payloads far
    ///         too large to be one.
    ///     </para>
    /// </summary>
    private const int MeshProbeBudget = 4 * 1024 * 1024;

    /// <summary>
    ///     True for a mesh this build can actually display.
    ///     <para>
    ///         ⚠ <c>.3DC</c> is deliberately EXCLUDED. Redguard's compressed meshes parse, but their
    ///         point decoding is still an open item, so accepting them here would show a confidently
    ///         wrong shape instead of nothing. An honest empty pane beats plausible garbage.
    ///     </para>
    /// </summary>
    public static bool CanPreview(AssetBrowseSession session, AssetNode node)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(node);

        if (node.Kind == AssetNodeKind.Folder)
        {
            return false;
        }

        // ⛔⛔ Do NOT gate on the `.3D` extension. Daggerfall's ARCH3D.BSA is a NUMBER-record
        // archive: all 10,251 of its meshes are named by object id ("44005"), and NOT ONE ends in
        // `.3D`. The extension test therefore rejected every Daggerfall mesh, which is why the GUI
        // Mesh Viewer had no route to them at all. Same failure the level pane had with `.bs6`.
        // ⚠ But a content probe alone is wrong too: Redguard's `.3DC` files open with the SAME
        // version tag (v2.6) and are deliberately excluded — they parse as a `.3D` without error
        // and hand back the WRONG points. Both conditions are needed.
        if (Path.GetExtension(node.Name).Equals(".3DC", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var head = session.FileSystem.TryReadAllBytesBounded(node.VirtualPath, MeshProbeBudget);
        return head is not null && LooksLikeMesh(head.Data);
    }

    /// <summary>Whether the bytes open with an XnGine mesh version tag.</summary>
    private static bool LooksLikeMesh(byte[] bytes)
    {
        if (bytes.Length < 4)
        {
            return false;
        }

        var tag = Encoding.Latin1.GetString(bytes, 0, 4);
        return tag is "v2.5" or "v2.6" or "v2.7";
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

        if (!CanPreview(session, node))
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

            var layout = ResolveLayout(session);
            var mesh = XnGineMesh.Parse(bytes, 0, layout);
            var decomposed = XnGineMeshDecomposer.Decompose(mesh);
            if (layout != XnGineMeshLayout.Battlespire)
            {
                var untextured = XnGineViewerSceneAdapter.ToViewerScene(decomposed, node.Name);
                return BethesdaViewerSceneGlbAdapter.FromGlbScene(
                    untextured, node.Name, BethesdaViewerScenePurpose.ClassicMesh);
            }

            // ⚑ Battlespire meshes are TEXTURED (2026-09-08): each plane's 32-bit key names its BSI
            // image in base 40 (GAME.EXE FUN_00075154), resolved out of the BSI.BSA beside the
            // opened source and handed to the viewer as generated textures.
            var meshDirectory = Path.GetDirectoryName(Path.GetFullPath(session.SourcePath));
            using var textures = BattlespireTextureResolver.Open(meshDirectory ?? string.Empty);
            var generated = new Dictionary<string, DecodedTexture>(StringComparer.OrdinalIgnoreCase);
            var scene = XnGineViewerSceneAdapter.ToViewerScene(decomposed, node.Name, (archive, record) =>
            {
                var path = BattlespireTextureResolver.TexturePathFor(archive, record);
                if (path is null)
                {
                    return null;
                }

                if (!generated.TryGetValue(path, out var texture))
                {
                    texture = textures.ResolveDecoded(archive, record);
                    if (texture is null)
                    {
                        return null;
                    }

                    generated[path] = texture;
                }

                return new XnGineViewerTexture(path, texture.Width, texture.Height);
            });

            var viewerScene = BethesdaViewerSceneGlbAdapter.FromGlbScene(
                scene, node.Name, BethesdaViewerScenePurpose.ClassicMesh);
            foreach (var (path, texture) in generated)
            {
                viewerScene.AddGeneratedTexture(path, texture);
            }

            return viewerScene;
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
