using System.Security.Cryptography;
using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Modeling.Nif;
using Slfx77.Multitool.Core.Models;
using Xunit;
using static BethesdaMultitool.Tests.Core.Modeling.Nif.NifModelAnimationTargetTestSupport;

namespace BethesdaMultitool.Tests.Core.Modeling.Nif;

/// <summary>
///     Cut-1b slice 3, the skeleton provenance for the future <c>bmt.nif.animation.skeleton</c> row
///     (<see cref="NifModelSkeletonProvenance" />): the chosen path, the SHA-256 of the skeleton bytes, the rule name, the
///     candidates examined, and the matched and unmatched names with their typed reasons.
/// </summary>
public sealed class NifModelSkeletonProvenanceTests
{
    private const string KfPath = "meshes/test/creature/idleanims/special.kf";
    private const string OwnDirectory = "meshes/test/creature/idleanims/skeleton.nif";
    private const string Parent = "meshes/test/creature/skeleton.nif";

    /// <summary>
    ///     The provenance keeps the path, an independently computed digest, the rule, both candidates, and each target name
    ///     once (bound, or with its code and reason), and becomes a Document row Shared accepts. Control: one changed
    ///     skeleton byte changes the digest.
    /// </summary>
    [Fact]
    public void Provenance_RecordsPathDigestRuleCandidatesAndNames()
    {
        var (lookup, _) = MemoryLookup(Parent);
        var resolution = NifModelSkeletonResolver.Resolve(KfPath, null, lookup);
        var targets = NifModelTargetNames.Build(NifModelTargetScope.Skeleton, [], Names("Bip01", "Bip01 R Hand"),
            OneOccurrenceEach(2));
        var matches = new[] { "Bip01", "##NifRound", "Bip01", "Bip01 R Hand", "bip01" }
            .Select(name => targets.Match(Latin1(name)))
            .ToArray();
        byte[] skeletonBytes = [0x47, 0x61, 0x6D, 0x65, 0xE9];

        var provenance = NifModelSkeletonProvenance.FromBytes(resolution, skeletonBytes, matches);

        Assert.Equal(Parent, provenance.Path);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(skeletonBytes)), provenance.Sha256);
        Assert.Equal("nearest-ancestor", provenance.RuleName);
        Assert.Equal(new[] { OwnDirectory, Parent }, provenance.Candidates.Select(static c => c.Path));
        Assert.Equal(new[] { "Bip01", "Bip01 R Hand" },
            provenance.MatchedNames.Select(static name => System.Text.Encoding.Latin1.GetString(name.Span)));
        Assert.Equal(new[] { "##NifRound", "bip01" }, provenance.Unmatched.Select(static match => match.Name));
        Assert.All(provenance.Unmatched,
            static match => Assert.Equal(NifModelTargetBlock.TargetNotInSkeleton, match.Block));

        var row = provenance.ToNativeState();
        Assert.Equal("bmt.nif.animation.skeleton", row.Kind);
        Assert.Equal(NifModelSkeletonProvenance.PayloadVersion, row.Version);
        Assert.Equal(SceneElementKind.Document, row.Target.Kind);
        var payload = JsonNode.Parse(row.PayloadJson)!.AsObject();
        Assert.Equal("nearest-ancestor", (string)payload["rule"]!);
        Assert.Equal(Parent, (string)payload["path"]!);
        Assert.Equal(provenance.Sha256, (string)payload["sha256"]!);
        Assert.Equal(KfPath, (string)payload["animationPath"]!);
        Assert.Equal("missing", (string)payload["candidates"]![0]!["outcome"]!);
        Assert.Equal("found", (string)payload["candidates"]![1]!["outcome"]!);
        Assert.Equal("Bip01 R Hand", (string)payload["matched"]![1]!["text"]!);
        Assert.Equal("##NifRound", (string)payload["unmatched"]![0]!["name"]!["text"]!);
        Assert.Equal("targetNotInSkeleton", (string)payload["unmatched"]![0]!["code"]!);
        Assert.Equal(NifModelTargetNames.TargetNotInSkeletonReason, (string)payload["unmatched"]![0]!["reason"]!);

        byte[] changed = [0x47, 0x61, 0x6D, 0x65, 0xE8];
        Assert.NotEqual(provenance.Sha256, NifModelSkeletonProvenance.FromBytes(resolution, changed, matches).Sha256);
    }

    /// <summary>
    ///     Only a resolved skeleton has provenance, and a supplied digest must be 64 lowercase hex digits. Control: the
    ///     same digest in lowercase is accepted as given.
    /// </summary>
    [Fact]
    public void Provenance_RefusesAnUnresolvedSkeletonAndAMalformedDigest()
    {
        var (missing, _) = MemoryLookup();
        var unresolved = NifModelSkeletonResolver.Resolve(KfPath, null, missing);
        var digest = new string('a', 64);
        Assert.Throws<ArgumentException>(() => NifModelSkeletonProvenance.FromDigest(unresolved, digest, []));

        var (lookup, _) = MemoryLookup(Parent);
        var resolved = NifModelSkeletonResolver.Resolve(KfPath, null, lookup);
        Assert.Throws<ArgumentException>(() =>
            NifModelSkeletonProvenance.FromDigest(resolved, digest.ToUpperInvariant(), []));
        Assert.Throws<ArgumentException>(() => NifModelSkeletonProvenance.FromDigest(resolved, digest[1..], []));

        Assert.Equal(digest, NifModelSkeletonProvenance.FromDigest(resolved, digest, []).Sha256);
    }
}
