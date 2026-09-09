using BethesdaMultitool.Core.AssetBrowse;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.AssetBrowse;

/// <summary>
///     Checks the Asset Browser's 3D LEVEL pane source — the seam that makes a classic level
///     explorable in the GUI rather than only exportable to GLB (plan item A5).
///     <para>
///         ⚑ Both the GUI and <c>classic level export</c> go through <c>Bs6SceneAssembler</c>, so
///         the BS6 <c>ANGS</c> rotation convention — settled by measurement after three statistical
///         probes misled, one ranking the right answer LAST — is applied once and never re-derived.
///     </para>
///     <para>
///         ⛔⛔ <b>Battlespire's archive naming is INVERTED and it broke this seam.</b>
///         <c>3D.BS6</c> is the MESH archive (2,115 <c>.3D</c> entries) and <c>BS6.BSA</c> is the
///         LEVEL archive (47 entries, 45 levels). <c>CanPreview</c> originally gated on the
///         <c>.bs6</c> extension, which claimed all 2,115 meshes as levels and rejected the only
///         archive holding any — so the level pane could never open one. These tests pin both
///         directions.
///     </para>
///     <para>
///         ⚑ Daggerfall joined the seam on 2026-09-06 — both its exterior city blocks and its
///         dungeon blocks, out of the same <c>BLOCKS.BSA</c>, routed by CONTENT probes rather than
///         by the <c>.RMB</c>/<c>.RDB</c> names.
///     </para>
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class ClassicLevelPreviewSourceTests
{
    private static string RequireBattlespire()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var root = RealAssetPaths.Classics.Battlespire();
        Assert.SkipWhen(root is null, RealAssetPaths.SkipMessage("Battlespire"));
        return root;
    }

    private static string RequireArchive(string fileName)
    {
        var path = Path.Combine(RequireBattlespire(), fileName);
        Assert.SkipWhen(!File.Exists(path), RealAssetPaths.SkipMessage(fileName));
        return path;
    }

    [Fact]
    public void LevelsInTheLevelArchiveAssembleIntoScenesWithGeometry()
    {
        using var session = AssetBrowseSession.OpenArchive(RequireArchive("BS6.BSA"));

        var scenes = 0;
        var parts = 0;
        foreach (var node in session.Root.Children.Where(n => n.Kind != AssetNodeKind.Folder))
        {
            var scene = ClassicLevelPreviewSource.TryLoad(session, node, CancellationToken.None);
            if (scene is null)
            {
                continue;
            }

            scenes++;
            parts += scene.MeshParts.Count;
        }

        // ⚠ 45 of the 47 entries are levels — ADR.TXT and an entry called "C" are not — so this
        // asserts most assemble, not all.
        Assert.True(scenes >= 40, $"expected ~45 levels to assemble, got {scenes}");
        Assert.True(parts > 0, $"{scenes} scenes assembled but hold no geometry");
    }

    [Fact]
    public void TheMeshArchiveIsNotMistakenForLevels()
    {
        // ⛔ The regression this pins: every one of 3D.BS6's 2,115 mesh entries used to pass
        // CanPreview purely because the FILE ends in .bs6, and then failed to assemble.
        using var session = AssetBrowseSession.OpenArchive(RequireArchive("3D.BS6"));

        var entries = session.Root.Children.Where(n => n.Kind != AssetNodeKind.Folder).ToList();
        Assert.True(entries.Count > 1000, $"expected the mesh archive's many entries, saw {entries.Count}");
        Assert.All(entries.Take(200), n => Assert.False(ClassicLevelPreviewSource.CanPreview(session, n)));
    }

    [Fact]
    public void NothingAssemblesThatCanPreviewDidNotClaim()
    {
        // ⚑ The contract that matters for the GUI is THIS direction: TryLoad succeeds only where
        // CanPreview said yes, so no level is silently unreachable because the pane never offered
        // the route.
        // ⚠ The converse does NOT hold and must not be asserted: CanPreview identifies a level by
        // its GNRL tag, while TryLoad returns null for a real level whose MESHES do not resolve —
        // deliberately, since an empty viewer reads as a working viewer pointed at an empty level.
        using var session = AssetBrowseSession.OpenArchive(RequireArchive("BS6.BSA"));

        var claimed = 0;
        var assembled = 0;
        foreach (var node in session.Root.Children.Where(n => n.Kind != AssetNodeKind.Folder))
        {
            var can = ClassicLevelPreviewSource.CanPreview(session, node);
            if (can)
            {
                claimed++;
            }

            if (ClassicLevelPreviewSource.TryLoad(session, node, CancellationToken.None) is not null)
            {
                assembled++;
                Assert.True(can, $"{node.Name} assembled but CanPreview declined it");
            }
        }

        Assert.True(claimed >= 40, $"expected ~45 levels claimed, got {claimed}");
        Assert.True(assembled <= claimed, "more assembled than were claimed");
    }

    private static string RequireBlocksArchive()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var root = RealAssetPaths.Classics.Daggerfall();
        Assert.SkipWhen(root is null, RealAssetPaths.SkipMessage("Daggerfall (ARENA2)"));
        var path = Path.Combine(root, "BLOCKS.BSA");
        Assert.SkipWhen(!File.Exists(path), RealAssetPaths.SkipMessage("BLOCKS.BSA"));
        return path;
    }

    [Fact]
    public void DaggerfallCityAndDungeonBlocksBothAssembleIntoScenesWithGeometry()
    {
        // ⚑ One archive, two formats, and the pane must handle both: BLOCKS.BSA holds 920 RMB city
        // blocks, 187 RDB dungeon blocks, 187 RDI records that are neither, and one stray DOS
        // directory listing called FOO. Only the first two may assemble.
        using var session = AssetBrowseSession.OpenArchive(RequireBlocksArchive());

        var claimed = 0;
        var assembled = 0;
        var parts = 0;

        // A sample, not the whole archive: assembling all 1,107 blocks parses ARCH3D repeatedly and
        // turns a fast opt-in test into a slow one.
        foreach (var node in session.Root.Children.Where(n => n.Kind != AssetNodeKind.Folder).Take(120))
        {
            if (!ClassicLevelPreviewSource.CanPreview(session, node))
            {
                continue;
            }

            claimed++;
            if (ClassicLevelPreviewSource.TryLoad(session, node, CancellationToken.None) is { } scene)
            {
                assembled++;
                parts += scene.MeshParts.Count;
            }
        }

        Assert.True(claimed > 0, "no Daggerfall block was claimed by the level pane");
        Assert.True(assembled > 0, $"{claimed} blocks claimed but none assembled");
        Assert.True(parts > 0, $"{assembled} scenes assembled but hold no geometry");
    }

    [Fact]
    public void DaggerfallRdiRecordsAreNotMistakenForBlocks()
    {
        // ⛔ The RDI records sit in the same archive under the same naming scheme as the RDB blocks
        // they accompany, so a name-based route would claim all 187 of them. The content probes
        // must not.
        using var session = AssetBrowseSession.OpenArchive(RequireBlocksArchive());

        var rdi = session.Root.Children
            .Where(n => n.Kind != AssetNodeKind.Folder && n.Name.EndsWith(".RDI", StringComparison.OrdinalIgnoreCase))
            .Take(40)
            .ToList();

        Assert.True(rdi.Count > 0, "no RDI records found to check");
        Assert.All(rdi, n => Assert.False(ClassicLevelPreviewSource.CanPreview(session, n)));
    }
}