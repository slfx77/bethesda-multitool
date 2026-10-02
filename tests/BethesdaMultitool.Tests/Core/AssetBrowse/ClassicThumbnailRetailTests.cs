using BethesdaMultitool.Core.AssetBrowse;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.AssetBrowse;

/// <summary>
///     Opt-in checks that a retail Travels install's art reaches the browser's gallery
///     (<c>RUN_BUCKET_B=1</c>).
///     <para>
///         ⚠ These drive <see cref="AssetThumbnailSource" /> — the thing the gallery actually calls
///         — rather than the decoders underneath it. A decoder can be correct and tested and still
///         be unreachable, which is exactly what was true of Shadowkey's texture banks: the tree
///         classified <c>.ztx</c> as a texture, so the browser offered it, and nothing could decode
///         it. A file that presents as previewable and then previews nothing is worse than one that
///         is not offered at all.
///     </para>
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class ClassicThumbnailRetailTests
{
    /// <summary>Zones the retail Shadowkey install ships; each has exactly one texture bank.</summary>
    private const int RetailZoneCount = 21;

    /// <summary>Every Shadowkey wall/floor texture is this square.</summary>
    private const int BankTextureEdge = 128;

    private static AssetBrowseSession OpenShadowkey()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var root = RealAssetPaths.Travels.ShadowkeyRoot();
        Assert.SkipWhen(root is null, RealAssetPaths.SkipMessage("TES Travels: Shadowkey"));

        var session = AssetBrowseSession.TryOpenGameRoot(root);
        Assert.SkipWhen(session is null, "The Shadowkey directory is present but not detected as an install root.");
        return session;
    }

    private static AssetBrowseSession OpenStormhold()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var jar = RealAssetPaths.Travels.StormholdJar();
        Assert.SkipWhen(jar is null, RealAssetPaths.SkipMessage("TES Travels: Stormhold"));

        var session = AssetBrowseSession.TryOpenGameArchive(jar);
        Assert.SkipWhen(session is null, "The Stormhold JAR is present but not detected as a game.");
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
    ///     Every zone's texture bank thumbnails. The count is pinned rather than merely non-empty:
    ///     a regression in tree enumeration that found one bank would otherwise still pass.
    /// </summary>
    [Fact]
    public void EveryShadowkeyTextureBankThumbnailsThroughTheGalleryPath()
    {
        using var session = OpenShadowkey();

        var banks = LeavesWithExtension(session, ".ztx");
        Assert.Equal(RetailZoneCount, banks.Count);

        foreach (var bank in banks)
        {
            Assert.True(
                AssetThumbnailSource.CanRender(bank),
                $"{bank.Name} is classified as a texture but the gallery refuses to render it.");

            var thumbnail = AssetThumbnailSource.TryRender(session, bank, BankTextureEdge, cancellationToken: TestContext.Current.CancellationToken);
            // RgbaThumbnail is a record STRUCT, so a null test cannot narrow it.
            Assert.True(thumbnail.HasValue, $"{bank.Name} was offered a thumbnail and produced none.");

            var image = thumbnail.Value;
            Assert.True(image.Width > 0 && image.Height > 0, $"{bank.Name} rendered an empty thumbnail.");
        }
    }

    /// <summary>
    ///     ⚠ A bank carries no palette; it indexes the zone's <c>.pal</c>. If that resolution
    ///     failed the decode would still succeed and hand back a uniformly black image — which
    ///     looks like a rendering bug rather than a missing palette. So this demands real colour.
    /// </summary>
    [Fact]
    public void AShadowkeyTextureBankResolvesItsZonePaletteAndIsNotBlank()
    {
        using var session = OpenShadowkey();

        var bank = LeavesWithExtension(session, ".ztx")[0];
        var thumbnail = AssetThumbnailSource.TryRender(session, bank, BankTextureEdge, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(thumbnail.HasValue, $"{bank.Name} produced no thumbnail.");

        var distinct = new HashSet<uint>();
        var rgba = thumbnail.Value.Rgba;
        for (var i = 0; i + 3 < rgba.Length; i += 4)
        {
            distinct.Add((uint)(rgba[i] | (rgba[i + 1] << 8) | (rgba[i + 2] << 16) | (rgba[i + 3] << 24)));
            if (distinct.Count > 4)
            {
                break;
            }
        }

        Assert.True(
            distinct.Count > 4,
            $"{bank.Name} rendered {distinct.Count} distinct colour(s) — its zone palette did not resolve.");
    }

    /// <summary>
    ///     Stormhold's sprites needed no new decoder — only a way to open the JAR. This pins that
    ///     the archive-as-install route actually feeds the gallery.
    /// </summary>
    [Fact]
    public void StormholdSpritesThumbnailFromTheJar()
    {
        using var session = OpenStormhold();

        var sprites = LeavesWithExtension(session, ".cus");
        Assert.NotEmpty(sprites);

        var rendered = sprites.Count(s => AssetThumbnailSource.TryRender(session, s, 64).HasValue);

        Assert.Equal(sprites.Count, rendered);
    }
}
