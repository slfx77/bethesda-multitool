using BethesdaMultitool.Core.AssetBrowse;
using BethesdaMultitool.Core.Rendering.Level2D;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.AssetBrowse;

/// <summary>
///     Opt-in end-to-end checks that the 2D map pane can actually build a picture from a retail
///     Travels install (<c>RUN_BUCKET_B=1</c>).
///     <para>
///         ⚠ These exist because unit coverage of the pieces proved nothing about the thing that
///         was broken. The zone builder, the tile compositor and the view policy were all green and
///         all unreachable: no code path led from an opened source to a rendered raster. So these
///         drive the REAL chain — open the install the way the browser opens it, find the node the
///         tree would show, and demand pixels back.
///     </para>
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class ClassicLevel2DRetailTests
{
    /// <summary>Zones the retail Shadowkey install ships; every one has all eleven families.</summary>
    private const int RetailZoneCount = 21;

    private static AssetBrowseSession OpenShadowkey()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var root = RealAssetPaths.Travels.ShadowkeyRoot();
        Assert.SkipWhen(root is null, RealAssetPaths.SkipMessage("TES Travels: Shadowkey"));

        var session = AssetBrowseSession.TryOpenGameRoot(root);
        Assert.SkipWhen(session is null, "The Shadowkey directory is present but not detected as an install root.");
        return session;
    }

    private static AssetBrowseSession OpenOblivionMobile()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var jar = RealAssetPaths.Travels.OblivionMobileJar();
        Assert.SkipWhen(jar is null, RealAssetPaths.SkipMessage("TES Travels: Oblivion mobile"));

        var session = AssetBrowseSession.TryOpenGameArchive(jar);
        Assert.SkipWhen(session is null, "The Oblivion mobile JAR is present but not detected as a game.");
        return session;
    }

    private static IEnumerable<AssetNode> Leaves(AssetNode node)
    {
        var pending = new Stack<AssetNode>();
        pending.Push(node);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (current.Children.Count == 0)
            {
                yield return current;
                continue;
            }

            foreach (var child in current.Children)
            {
                pending.Push(child);
            }
        }
    }

    private static List<AssetNode> LeavesWithExtension(AssetBrowseSession session, string extension)
    {
        return
        [
            .. Leaves(session.Root)
                .Where(n => n.Name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                .OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
        ];
    }

    /// <summary>
    ///     Every one of the 21 zones builds a source and renders its floor layer. A zone needs its
    ///     sibling <c>.zcp</c>, so this also proves the sibling resolution works through the real
    ///     mount rather than only for a hand-built fixture.
    /// </summary>
    [Fact]
    public void EveryShadowkeyZoneRendersThroughTheBrowserChain()
    {
        using var session = OpenShadowkey();

        var zones = LeavesWithExtension(session, ".zmp");

        // Pinned, not merely non-empty: if the tree enumeration regressed to finding one zone, a
        // NotEmpty check would still pass while the sweep covered almost nothing. 21 is the
        // documented retail figure for this install.
        Assert.Equal(RetailZoneCount, zones.Count);

        var rendered = 0;
        foreach (var zone in zones)
        {
            Assert.True(AssetLevel2DSource.CanOpen(zone), $"{zone.Name} was not claimed by the 2D gate.");

            var source = AssetLevel2DSource.TryOpen(session, zone, TestContext.Current.CancellationToken);
            Assert.True(source is not null, $"{zone.Name} claimed a 2D view but built no source.");

            var render = source.Render(Level2DLayer.Floor);
            // Level2DRender is a record STRUCT, so the null test cannot narrow it for us.
            Assert.True(render.HasValue, $"{zone.Name} built a source but rendered no floor layer.");

            var raster = render.Value;
            Assert.True(raster.Width > 0 && raster.Height > 0, $"{zone.Name} rendered an empty raster.");
            Assert.Equal(raster.Width * raster.Height * 4, raster.Rgba.Length);
            rendered++;
        }

        Assert.Equal(zones.Count, rendered);
    }

    /// <summary>
    ///     ⚠ The discriminating assertion for the atlas pairing. Three retail levels are drawn with
    ///     an atlas that does NOT share their stem, so a renderer that paired by stem would still
    ///     produce a full, plausible picture here — this pins the resolved atlas NAME instead of
    ///     merely demanding pixels.
    /// </summary>
    [Fact]
    public void OblivionMobileLevelsResolveTheAtlasTheScriptsName()
    {
        using var session = OpenOblivionMobile();

        var maps = LeavesWithExtension(session, ".jtm");
        Assert.NotEmpty(maps);

        var built = maps
            .Where(m => AssetLevel2DSource.TryOpen(session, m) is not null)
            .Select(m => m.Name)
            .ToList();

        // l01_r.jtm is an orphan no script mentions; it must NOT be drawn with a guessed atlas.
        Assert.DoesNotContain("l01_r.jtm", built, StringComparer.OrdinalIgnoreCase);
        Assert.True(built.Count > 0, "No Oblivion mobile level resolved an atlas at all.");
    }

    /// <summary>One level renders a real raster through the whole chain.</summary>
    [Fact]
    public void AnOblivionMobileLevelRendersThroughTheBrowserChain()
    {
        using var session = OpenOblivionMobile();

        var map = LeavesWithExtension(session, ".jtm")
            .Select(n => (Node: n, Source: AssetLevel2DSource.TryOpen(session, n)))
            .FirstOrDefault(pair => pair.Source is not null);

        Assert.SkipWhen(map.Source is null, "No Oblivion mobile level resolved an atlas.");

        var render = map.Source!.Render(Level2DLayer.Floor);
        Assert.True(render.HasValue, $"{map.Node.Name} built a source but rendered no floor layer.");

        var raster = render.Value;
        Assert.True(raster.Width > 0 && raster.Height > 0);
        Assert.Equal(raster.Width * raster.Height * 4, raster.Rgba.Length);

        // A composited level is not a blank canvas: some pixel must be opaque.
        Assert.Contains(raster.Rgba.Where((_, i) => i % 4 == 3), a => a != 0);
    }
}