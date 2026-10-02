using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Modeling.RealAsset;

/// <summary>
///     Bucket B for the cut-1c cover manifest (plan <c>docs/design/cut1c-xngine-reader-plan-20260925.md</c>, section 8
///     slice 1): every manifest row resolves from its primary container to bytes of the pinned payload size and
///     SHA-256, with an LZSS entry's stored bytes verified against the stored SHA-256 on the way, the pinned tag
///     read back off the payload's opening bytes, a ROB row's pinned segment type enforced (with the wrong-type
///     control rejected), and every <c>alsoIn</c> candidate that is on this machine resolves to the same payload.
/// </summary>
/// <remarks>
///     <para>
///         The plan's two slice-1 controls, each of which must fail: a copy of the row whose SHA-256 has ONE hex
///         digit altered is rejected by the same resolver call that accepted the real row (so a resolver that stopped
///         hashing, or compared the manifest with itself, fails here); and the 3D.BSA namesake of BARSTEP1.3D
///         (directory index 438, measured different bytes at the SAME 1,540-byte payload size) is rejected against the
///         3D.BS6 cover row's digest while resolving cleanly against its own, so an entry-name (or size) lookup cannot
///         stand in for the digest check.
///     </para>
///     <para>
///         A container that is not on this machine is an unavailable fixture and skips the row; a container that is
///         present but yields other bytes fails it. The id-5090 group additionally proves the resolver addresses by
///         DIRECTORY INDEX: the six records share one id, and an id lookup would hand every row the same (last) copy,
///         which only two of the six digests accept.
///     </para>
/// </remarks>
[Trait("Category", TestCategories.BucketB)]
[Collection(SequentialIntegrationGroup.Name)]
public sealed class Cut1cCoverResolutionTests
{
    [Theory]
    [MemberData(nameof(Cut1cCoverManifest.Rows), MemberType = typeof(Cut1cCoverManifest))]
    public void EveryManifestRow_ResolvesFromItsPrimaryToThePinnedDigests(string name, string sha256)
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var file = Cut1cCoverManifest.Require(name);
        Assert.Equal(sha256, file.Sha256);
        var fixture = Cut1cFixtureResolver.TryResolveContainer(file, out var reason);
        Assert.SkipWhen(fixture is null && IsAbsent(reason),
            $"{file}: {reason}. " + RealAssetPaths.SkipMessage("the cut-1c cover corpus"));
        Assert.True(fixture is not null, $"{file}: {reason}");
        Assert.Equal(file.Size, fixture.Bytes.LongLength);
        Assert.Equal(file.Sha256, Cut1cFixtureResolver.Sha256(fixture.Bytes));
        Assert.True(Cut1cFixtureResolver.Matches(fixture.Bytes, file));

        // The pinned tag is the payload's own opening bytes: 4 ASCII bytes for a version tag, 2 for the MZ
        // stray, and a null tag exactly for the zero-byte empty segment. Reading it back off the resolved
        // bytes is what makes the manifest's tag column a checked pin rather than a stored comment.
        if (file.Tag is null)
        {
            Assert.Equal(0, file.Size);
            Assert.Empty(fixture.Bytes);
        }
        else
        {
            Assert.True(fixture.Bytes.Length >= file.Tag.Length, $"{file}: payload shorter than its tag.");
            Assert.Equal(file.Tag, System.Text.Encoding.ASCII.GetString(fixture.Bytes, 0, file.Tag.Length));
        }

        // A ROB row's pinned segment type is verified by the resolver; the control that must fail is the same
        // read with the OTHER retail type value, which only the segment-type check can reject (name, index,
        // size and digest all still match the real segment).
        if (file.SegmentType is { } segmentType)
        {
            var wrongType = file with { SegmentType = segmentType == 0 ? 512 : 0 };
            Assert.Null(Cut1cFixtureResolver.TryResolveContainer(wrongType, out var typeReason));
            Assert.Contains($"has type {segmentType}", typeReason, StringComparison.Ordinal);
        }

        // Slice-1 control: the identical read against a digest with ONE altered hex digit must fail.
        var altered = file with { Sha256 = AlterOneHexDigit(file.Sha256) };
        Assert.Null(Cut1cFixtureResolver.TryResolveContainer(altered, out var alteredReason));
        Assert.Contains("mismatch", alteredReason, StringComparison.Ordinal);

        // And a one-byte-flipped copy of the payload must fail the same Matches check that just passed
        // (the empty segment has no byte to flip; its control is the altered digest above).
        if (fixture.Bytes.Length > 0)
        {
            var flipped = (byte[])fixture.Bytes.Clone();
            flipped[flipped.Length / 2] ^= 0x01;
            Assert.False(Cut1cFixtureResolver.Matches(flipped, file),
                $"{file}: a one-byte-flipped copy passed the check.");
        }
    }

    [Theory]
    [MemberData(nameof(Cut1cCoverManifest.Rows), MemberType = typeof(Cut1cCoverManifest))]
    public void EveryAlsoInCandidate_YieldsTheSamePayload(string name, string sha256)
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var file = Cut1cCoverManifest.Require(name);
        Assert.Equal(sha256, file.Sha256);
        Assert.SkipWhen(file.AlsoIn.Count == 0, $"{file}: no alsoIn candidates.");
        var absent = new List<string>();
        foreach (var candidate in file.AlsoIn)
        {
            var bytes = Cut1cFixtureResolver.TryReadVerified(candidate, file.Sha256, file.Size, out var reason);
            if (bytes is null && IsAbsent(reason))
            {
                absent.Add(reason);
                continue;
            }

            Assert.True(bytes is not null, $"{file}: {reason}");
        }

        Assert.SkipWhen(absent.Count == file.AlsoIn.Count,
            $"{file}: every alsoIn container is absent ({string.Join("; ", absent)}). " +
            RealAssetPaths.SkipMessage("the cut-1c cover corpus"));
    }

    [Fact]
    public void Barstep1Namesake_IsRejectedAgainstTheCoverDigest()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var cover = Cut1cCoverManifest.RequireControl("barstep1-bs6-cover");
        var namesake = Cut1cCoverManifest.RequireControl("barstep1-3dbsa-namesake");
        var namesakeCandidate = namesake.Candidates.First();
        var own = Cut1cFixtureResolver.TryReadVerified(namesakeCandidate, namesake.Sha256, namesake.Size,
            out var ownReason);
        Assert.SkipWhen(own is null && IsAbsent(ownReason),
            $"{namesake}: {ownReason}. " + RealAssetPaths.SkipMessage("the cut-1c cover corpus"));
        Assert.True(own is not null, $"{namesake}: {ownReason}");

        // The control that must fail: the namesake resolves by name, index and even payload SIZE
        // (both copies are 1,540 bytes), and only the digest rejects it against the cover row.
        Assert.Equal(cover.Size, namesake.Size);
        var crossed = Cut1cFixtureResolver.TryReadVerified(namesakeCandidate, cover.Sha256, cover.Size,
            out var crossedReason);
        Assert.Null(crossed);
        Assert.Contains("payload SHA-256 mismatch", crossedReason, StringComparison.Ordinal);

        // And the two payloads really are different bytes of one length.
        var coverFixture = Cut1cFixtureResolver.TryResolveContainer(cover, out var coverReason);
        Assert.SkipWhen(coverFixture is null && IsAbsent(coverReason),
            $"{cover}: {coverReason}. " + RealAssetPaths.SkipMessage("the cut-1c cover corpus"));
        Assert.True(coverFixture is not null, $"{cover}: {coverReason}");
        Assert.Equal(coverFixture.Bytes.Length, own.Length);
        Assert.False(coverFixture.Bytes.AsSpan().SequenceEqual(own));
    }

    [Fact]
    public void Id5090Group_ResolvesEachIndexToItsOwnBytes()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var group = Cut1cCoverManifest.RequireGroup("id-5090-duplicate-group");
        var byIndex = new Dictionary<int, byte[]>();
        foreach (var file in group)
        {
            var fixture = Cut1cFixtureResolver.TryResolveContainer(file, out var reason);
            Assert.SkipWhen(fixture is null && IsAbsent(reason),
                $"{file}: {reason}. " + RealAssetPaths.SkipMessage("the cut-1c cover corpus"));
            Assert.True(fixture is not null, $"{file}: {reason}");
            byIndex[file.Index!.Value] = fixture.Bytes;
        }

        // An id lookup would return one copy six times; index addressing yields 5 distinct payloads
        // with exactly the measured identical pair (5007, 8903).
        Assert.Equal(6, byIndex.Count);
        Assert.Equal(5, byIndex.Values.Select(bytes => Cut1cFixtureResolver.Sha256(bytes)).Distinct(StringComparer.Ordinal).Count());
        Assert.True(byIndex[5007].AsSpan().SequenceEqual(byIndex[8903]),
            "indices 5007 and 8903 must be byte-identical.");
        Assert.False(byIndex[5007].AsSpan().SequenceEqual(byIndex[10238]),
            "index 10238 (the path-lookup winner) must differ from the 5007 twin.");
    }

    [Fact]
    public void EmptyRobSegment_ResolvesToZeroBytes()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var file = Cut1cCoverManifest.RequireControl("empty-rob-segment");
        var fixture = Cut1cFixtureResolver.TryResolveContainer(file, out var reason);
        Assert.SkipWhen(fixture is null && IsAbsent(reason),
            $"{file}: {reason}. " + RealAssetPaths.SkipMessage("the cut-1c cover corpus"));
        Assert.True(fixture is not null, $"{file}: {reason}");
        Assert.Empty(fixture.Bytes);
    }

    private static bool IsAbsent(string reason)
    {
        return reason.StartsWith(Cut1cFixtureResolver.AbsentPrefix, StringComparison.Ordinal);
    }

    private static string AlterOneHexDigit(string sha256)
    {
        var altered = sha256.ToCharArray();
        altered[0] = altered[0] == '0' ? '1' : '0';
        return new string(altered);
    }
}
