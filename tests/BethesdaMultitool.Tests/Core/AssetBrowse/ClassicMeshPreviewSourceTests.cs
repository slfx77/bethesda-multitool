using BethesdaMultitool.Core.AssetBrowse;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.AssetBrowse;

/// <summary>
///     Checks the Asset Browser's single-MESH preview route.
///     <para>
///         ⛔⛔
///         <b>
///             It used to gate on the <c>.3D</c> extension, and that rejected every Daggerfall
///             mesh.
///         </b>
///         <c>ARCH3D.BSA</c> is a NUMBER-record archive: all 10,251 entries are named by
///         object id (<c>44005</c>) and NOT ONE ends in <c>.3D</c>, so the Mesh Viewer had no route
///         to any of them — the same failure the level pane had with <c>.bs6</c>.
///     </para>
///     <para>
///         ⚠ A content probe ALONE would be wrong in the other direction: Redguard's 147
///         <c>.3DC</c> files open with the same <c>v2.6</c> tag and are deliberately excluded,
///         because they parse as a <c>.3D</c> without error and hand back the WRONG points. Both
///         tests below exist so neither direction regresses.
///     </para>
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class ClassicMeshPreviewSourceTests
{
    private static string RequireArena2()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var root = RealAssetPaths.Classics.Daggerfall();
        Assert.SkipWhen(root is null, RealAssetPaths.SkipMessage("Daggerfall (ARENA2)"));
        return root;
    }

    [Fact]
    public void NumberedArch3dEntriesArePreviewableDespiteHavingNoExtension()
    {
        var arena2 = RequireArena2();
        var path = Path.Combine(arena2, "ARCH3D.BSA");
        Assert.SkipWhen(!File.Exists(path), RealAssetPaths.SkipMessage("ARCH3D.BSA"));

        using var session = AssetBrowseSession.OpenArchive(path);
        var entries = session.Root.Children.Where(n => n.Kind != AssetNodeKind.Folder).Take(40).ToList();

        Assert.True(entries.Count > 10, $"expected many ARCH3D entries, saw {entries.Count}");

        // The regression: not one of these is named "*.3D".
        Assert.DoesNotContain(entries, n => n.Name.EndsWith(".3D", StringComparison.OrdinalIgnoreCase));
        Assert.All(entries, n => Assert.True(
            ClassicMeshPreviewSource.CanPreview(session, n), $"{n.Name} was not offered to the mesh viewer"));
    }

    [Fact]
    public void ANumberedEntryActuallyLoadsAsAScene()
    {
        var arena2 = RequireArena2();
        var path = Path.Combine(arena2, "ARCH3D.BSA");
        Assert.SkipWhen(!File.Exists(path), RealAssetPaths.SkipMessage("ARCH3D.BSA"));

        using var session = AssetBrowseSession.OpenArchive(path);
        var loaded = 0;
        var parts = 0;

        foreach (var node in session.Root.Children.Where(n => n.Kind != AssetNodeKind.Folder).Take(12))
        {
            if (ClassicMeshPreviewSource.TryLoad(session, node, CancellationToken.None) is { } scene)
            {
                loaded++;
                parts += scene.MeshParts.Count;
            }
        }

        Assert.True(loaded > 0, "no ARCH3D mesh loaded into the viewer");
        Assert.True(parts > 0, $"{loaded} meshes loaded but hold no geometry");
    }

    [Fact]
    public void RedguardAnimatedMeshesStayExcludedEvenThoughTheyShareTheVersionTag()
    {
        // ⚠ .3DC opens with v2.6 exactly like a .3D, so the content probe alone would claim all 147.
        // They must stay excluded: they parse without error and return the wrong points.
        var redguard = RealAssetPaths.Classics.Redguard();
        Assert.SkipWhen(redguard is null, RealAssetPaths.SkipMessage("Redguard"));

        var folder = Directory.EnumerateFiles(redguard, "*.3DC", SearchOption.AllDirectories)
            .Select(Path.GetDirectoryName)
            .FirstOrDefault(d => d is not null);
        Assert.SkipWhen(folder is null, RealAssetPaths.SkipMessage("Redguard .3DC meshes"));

        using var session = AssetBrowseSession.OpenFolder(folder);
        var animated = session.Root.Children
            .Where(n => n.Kind != AssetNodeKind.Folder
                        && n.Name.EndsWith(".3DC", StringComparison.OrdinalIgnoreCase))
            .Take(10)
            .ToList();

        Assert.True(animated.Count > 0, "no .3DC nodes found to check");
        Assert.All(animated, n => Assert.False(
            ClassicMeshPreviewSource.CanPreview(session, n),
            $"{n.Name} is a .3DC and must not be offered to the mesh viewer"));
    }
}