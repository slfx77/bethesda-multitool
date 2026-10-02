using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Modeling.RealAsset;

/// <summary>
///     Bucket B for the cut-1b cover manifest (plan slice 0): every manifest file resolves from its own container (the
///     manifest's primary source and entry, an archive under <c>Sample/Builds</c>) to bytes of the pinned size and
///     SHA-256, and every manifest <c>.kf</c> the reader reads resolves its skeleton companion from the source and
///     entry the walk-up pinned, to the skeleton manifest file's digest.
/// </summary>
/// <remarks>
///     <para>
///         Control: the same digest check is run on a copy of every resolved file with one byte flipped, and must
///         reject it, so a check that compared nothing (or compared the manifest with itself) fails here. The Python
///         side ran the same control on a copy of <c>Update.bsa</c> with one byte of <c>2hraim.kf</c> flipped:
///         <c>nif_cover_expectations.py --verify-only --no-fallback</c> reported it as a SHA-256 mismatch.
///     </para>
///     <para>
///         A container that is not on this machine is an unavailable fixture and skips the row; a container that is
///         present but yields other bytes fails it. Every source is spelled <c>Sample/...</c>, which resolves only
///         through a repository-relative <c>Sample/</c> directory or <c>BETHESDA_TEST_DATA_ROOT</c>: a git worktree has
///         no <c>Sample/</c>, so there every row skips unless the variable points at the main checkout's (or its
///         <c>Sample/</c> directory). A run that is meant to check the corpus must report zero skips here.
///     </para>
///     <para>
///         A provisional skeleton pin (see <see cref="Cut1bSkeletonCompanion" />) resolves every alternative copy as
///         well, each to its own digest, which must differ from the pinned copy's.
///     </para>
/// </remarks>
[Trait("Category", TestCategories.BucketB)]
[Collection(SequentialIntegrationGroup.Name)]
public sealed class Cut1bCoverResolutionTests
{
    [Theory]
    [MemberData(nameof(Cut1bCoverManifest.Rows), MemberType = typeof(Cut1bCoverManifest))]
    public void EveryManifestFile_ResolvesFromItsContainerToThePinnedDigest(string entry, string sha256)
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var file = Cut1bCoverManifest.Require(sha256);
        Assert.Equal(entry, file.Entry);
        var fixture = Cut1bFixtureResolver.TryResolveContainer(file, out var reason);
        Assert.SkipWhen(fixture is null && IsAbsent(reason),
            $"{file}: {reason}. " + RealAssetPaths.SkipMessage("the cut-1b cover corpus"));
        Assert.True(fixture is not null, $"{file}: {reason}");
        Assert.Equal(file.Size, fixture.Bytes.LongLength);
        Assert.Equal(file.Sha256, Cut1bFixtureResolver.Sha256(fixture.Bytes));
        Assert.True(Cut1bFixtureResolver.Matches(fixture.Bytes, file));

        var flipped = (byte[])fixture.Bytes.Clone();
        flipped[flipped.Length / 2] ^= 0x01;
        Assert.False(Cut1bFixtureResolver.Matches(flipped, file), $"{file}: a one-byte-flipped copy passed the check.");
    }

    [Theory]
    [MemberData(nameof(Cut1bCoverManifest.AnimationStreamRows), MemberType = typeof(Cut1bCoverManifest))]
    public void EveryManifestKf_ResolvesItsPinnedSkeleton(string entry, string sha256)
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var kf = Cut1bCoverManifest.Require(sha256);
        Assert.Equal(entry, kf.Entry);
        Assert.True(kf.Skeleton is not null, $"{kf}: no skeleton pinned ({kf.SkeletonMissing}).");
        var skeleton = Cut1bCoverManifest.Require(kf.Skeleton.Sha256);
        Assert.Contains(kf.Sha256, skeleton.SkeletonFor);
        var bytes = Cut1bFixtureResolver.TryReadVerified(new Cut1bCoverSource(kf.Skeleton.Source, kf.Skeleton.Entry),
            skeleton.Sha256, skeleton.Size, out var reason);
        Assert.SkipWhen(bytes is null && IsAbsent(reason),
            $"{kf}: {reason}. " + RealAssetPaths.SkipMessage("the cut-1b cover corpus"));
        Assert.True(bytes is not null, $"{kf}: the pinned skeleton does not resolve: {reason}");

        // A provisional pin: every other copy of the path resolves from its own archive to its own digest.
        foreach (var alternative in kf.Skeleton.Alternatives)
        {
            var copy = Cut1bCoverManifest.Require(alternative.Sha256);
            Assert.Contains(kf.Sha256, copy.SkeletonCandidateFor);
            var copyBytes = Cut1bFixtureResolver.TryReadVerified(
                new Cut1bCoverSource(alternative.Source, alternative.Entry), copy.Sha256, copy.Size, out var copyReason);
            Assert.SkipWhen(copyBytes is null && IsAbsent(copyReason),
                $"{kf}: {copyReason}. " + RealAssetPaths.SkipMessage("the cut-1b cover corpus"));
            Assert.True(copyBytes is not null, $"{kf}: the alternative skeleton does not resolve: {copyReason}");
            Assert.NotEqual(skeleton.Sha256, Cut1bFixtureResolver.Sha256(copyBytes));
        }
    }

    /// <summary>True when the reason says the container is not on this machine (an unavailable fixture).</summary>
    private static bool IsAbsent(string reason)
    {
        return reason.StartsWith(Cut1bFixtureResolver.AbsentPrefix, StringComparison.Ordinal);
    }
}
