using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;
using BethesdaMultitool.Core.Modeling;
using BethesdaMultitool.Core.Modeling.Nif;
using BethesdaMultitool.Tests.Helpers;
using Slfx77.Multitool.Core.Models.Sources;
using Xunit;
using static BethesdaMultitool.Tests.Core.Modeling.Nif.NifModelAnimationTargetTestSupport;

namespace BethesdaMultitool.Tests.Core.Modeling.Nif;

/// <summary>
///     Cut-1b slice 3, skeleton resolution (<see cref="NifModelSkeletonResolver" />; plan section 1.7, owner rulings D4 and
///     D12): the nearest ancestor <c>skeleton.nif</c> wins with no compatibility gate, an explicit skeleton always wins,
///     and every candidate examined is reported. Lookups are in memory, or the real VFS companion resolver over an
///     in-memory file system; no archive is read.
/// </summary>
public sealed class NifModelSkeletonResolverTests
{
    private const string KfPath = "meshes/test/creature/idleanims/special.kf";
    private const string OwnDirectory = "meshes/test/creature/idleanims/skeleton.nif";
    private const string Parent = "meshes/test/creature/skeleton.nif";
    private const string Grandparent = "meshes/test/skeleton.nif";
    private const string MeshesRoot = "meshes/skeleton.nif";
    private const string DataRoot = "skeleton.nif";
    private const string ExplicitPath = "meshes/other/skeleton.nif";

    /// <summary>
    ///     The walk starts in the <c>.kf</c>'s own directory and ends at the data root, either separator giving the same
    ///     walk; a rooted or traversing path is refused. Control: a skeleton beside the <c>.kf</c> is found first, which a
    ///     walk starting at the parent directory would miss.
    /// </summary>
    [Fact]
    public void CandidatePaths_WalkFromTheOwnDirectoryToTheDataRoot()
    {
        Assert.True(NifModelSkeletonResolver.TryGetCandidatePaths(KfPath, out var candidates));
        Assert.Equal(new[] { OwnDirectory, Parent, Grandparent, MeshesRoot, DataRoot }, candidates);

        Assert.True(NifModelSkeletonResolver.TryGetCandidatePaths(@"meshes\test\\creature\idleanims\special.kf",
            out var backslashed));
        Assert.Equal(candidates, backslashed);

        Assert.False(NifModelSkeletonResolver.TryGetCandidatePaths("C:/Data/meshes/a.kf", out _));
        Assert.False(NifModelSkeletonResolver.TryGetCandidatePaths("meshes/../a.kf", out _));
        Assert.False(NifModelSkeletonResolver.TryGetCandidatePaths(" ", out _));

        var (lookup, _) = MemoryLookup(OwnDirectory, Parent);
        Assert.Equal(OwnDirectory, NifModelSkeletonResolver.Resolve(KfPath, null, lookup).SkeletonPath);
    }

    /// <summary>
    ///     With skeletons in the parent and the grandparent, the parent's wins, the grandparent's is never asked about, and
    ///     the candidates examined are reported in order. Control: the farther skeleton is present and reachable (with the
    ///     nearer one removed the walk takes it), so a rule that chose it would be seen here.
    /// </summary>
    [Fact]
    public void WalkUp_ChoosesTheNearestSkeleton()
    {
        var (lookup, asked) = MemoryLookup(Parent, Grandparent);

        var resolution = NifModelSkeletonResolver.Resolve(KfPath, null, lookup);

        Assert.Equal(NifModelSkeletonResolutionStatus.Resolved, resolution.Status);
        Assert.Equal(NifModelSkeletonRule.NearestAncestor, resolution.Rule);
        Assert.Equal(NifModelSkeletonResolver.NearestAncestorRuleName, resolution.RuleName);
        Assert.Equal(Parent, resolution.SkeletonPath);
        Assert.Equal(Parent, resolution.Skeleton!.Reference.Path);
        Assert.Null(resolution.ExplicitPath);
        Assert.Null(resolution.FailureReason);
        Assert.Equal(
            new[]
            {
                new NifModelSkeletonCandidate(OwnDirectory, NifModelSkeletonCandidateOutcome.Missing, 0),
                new NifModelSkeletonCandidate(Parent, NifModelSkeletonCandidateOutcome.Found, 1)
            },
            resolution.Candidates);
        Assert.Equal(new[] { OwnDirectory, Parent }, asked);

        var (fartherOnly, _) = MemoryLookup(Grandparent);
        Assert.Equal(Grandparent, NifModelSkeletonResolver.Resolve(KfPath, null, fartherOnly).SkeletonPath);
    }

    /// <summary>
    ///     Through the production lookup (the VFS companion resolver over an in-memory data root), the nearest skeleton is
    ///     chosen although it lacks the '##NifRound' attachment node: only that target stays unbound, with the attachment
    ///     reason. Path case is the VFS's business (the data root is case-insensitive). Control: the renderer's rule
    ///     (<see cref="NifModelFamilyAnimationResolver" />: skip a skeleton unless every name is present) passes over the
    ///     nearest skeleton on the same files and takes the farther one.
    /// </summary>
    [Fact]
    public async Task WalkUp_HasNoCompatibilityGate()
    {
        var files = new MemoryGameFileSystem("memory-data",
            ("meshes/test/weapon/skeleton.nif", new byte[] { 1 }),
            ("meshes/test/skeleton.nif", new byte[] { 2 }),
            ("meshes/test/weapon/reload.kf", new byte[] { 9 }));
        await using var companions = new BethesdaTextureCompanions(files, "memory-data");
        var (owner, input) = NifModelTestSupport.Open([0]);
        await using var ownerInput = input;
        var lookup = NifModelSkeletonResolver.FromCompanionResolver(companions.ResolveAsync, owner,
            TestContext.Current.CancellationToken);

        var resolution = NifModelSkeletonResolver.Resolve("Meshes/Test/Weapon/Reload.kf", null, lookup);

        Assert.Equal(NifModelSkeletonResolutionStatus.Resolved, resolution.Status);
        Assert.Equal("Meshes/Test/Weapon/skeleton.nif", resolution.SkeletonPath);
        Assert.Equal("meshes/test/weapon/skeleton.nif", resolution.Skeleton!.Reference.Path);
        Assert.Equal("memory-data", resolution.Skeleton.Entry.Provenance);
        Assert.Single(resolution.Candidates);
        var targets = NifModelTargetNames.Build(NifModelTargetScope.Skeleton, [], Names("Bip01", "Bip01 R Hand"),
            OneOccurrenceEach(2));
        Assert.True(targets.Match(Latin1("Bip01 R Hand")).IsResolved);
        var attachment = targets.Match(Latin1("##NifRound"));
        Assert.Equal(NifModelTargetBlock.TargetNotInSkeleton, attachment.Block);
        Assert.Equal(NifModelTargetNames.TargetNotInSkeletonReason, attachment.Reason);

        var inspector = new NifModelFamilyRigInspectorStub(["Bip01 R Hand", "##NifRound"],
            new Dictionary<byte, string[]>
            {
                [1] = ["Bip01", "Bip01 R Hand"],
                [2] = ["Bip01", "Bip01 R Hand", "##NifRound"]
            });
        var gated = NifModelFamilyAnimationResolver.Resolve(files, "meshes/test/weapon/reload.kf", new byte[] { 9 },
            inspector);
        Assert.Equal(NifModelFamilyAnimationResolutionStatus.Resolved, gated.Status);
        Assert.Equal(@"meshes\test\skeleton.nif", gated.Skeleton!.VirtualPath);
    }

    /// <summary>
    ///     An explicit skeleton wins over a walk-up skeleton beside the <c>.kf</c>: it is the only candidate asked about, and
    ///     it wins even when the <c>.kf</c> path could not be walked. Control: without it the walk-up chooses a different
    ///     skeleton, so the win is observable.
    /// </summary>
    [Fact]
    public void ExplicitSkeleton_WinsOverTheWalkUp()
    {
        var (lookup, asked) = MemoryLookup(ExplicitPath, OwnDirectory);

        var resolution = NifModelSkeletonResolver.Resolve(KfPath, ExplicitPath, lookup);

        Assert.Equal(NifModelSkeletonResolutionStatus.Resolved, resolution.Status);
        Assert.Equal(NifModelSkeletonRule.Explicit, resolution.Rule);
        Assert.Equal(NifModelSkeletonResolver.ExplicitRuleName, resolution.RuleName);
        Assert.Equal(ExplicitPath, resolution.ExplicitPath);
        Assert.Equal(ExplicitPath, resolution.SkeletonPath);
        Assert.Equal(
            new[] { new NifModelSkeletonCandidate(ExplicitPath, NifModelSkeletonCandidateOutcome.Found, 1) },
            resolution.Candidates);
        Assert.Equal(new[] { ExplicitPath }, asked);
        Assert.Equal(ExplicitPath,
            NifModelSkeletonResolver.Resolve("C:/Data/meshes/a.kf", ExplicitPath, lookup).SkeletonPath);

        Assert.Equal(OwnDirectory, NifModelSkeletonResolver.Resolve(KfPath, null, lookup).SkeletonPath);
    }

    /// <summary>
    ///     A missing explicit skeleton is reported as such and the walk-up is not tried in its place. Control: the walk-up
    ///     alone resolves a skeleton on the same files, so a fallback would have been visible.
    /// </summary>
    [Fact]
    public void ExplicitSkeleton_MissingDoesNotFallBackToTheWalkUp()
    {
        var (lookup, asked) = MemoryLookup(OwnDirectory);

        var resolution = NifModelSkeletonResolver.Resolve(KfPath, ExplicitPath, lookup);

        Assert.Equal(NifModelSkeletonResolutionStatus.ExplicitSkeletonMissing, resolution.Status);
        Assert.Equal(NifModelSkeletonResolver.ExplicitSkeletonMissingReason, resolution.FailureReason);
        Assert.Null(resolution.Skeleton);
        Assert.Null(resolution.SkeletonPath);
        Assert.Equal(new[] { ExplicitPath }, asked);
        Assert.Throws<ArgumentException>(() => NifModelSkeletonResolver.Resolve(KfPath, " ", lookup));

        Assert.True(NifModelSkeletonResolver.Resolve(KfPath, null, lookup).IsResolved);
    }

    /// <summary>
    ///     With no skeleton anywhere up the walk the status is 'no skeleton' (owner ruling D4) and every ancestor was
    ///     examined. Control: a skeleton at the data root itself is found, so the walk above did not stop early.
    /// </summary>
    [Fact]
    public void NoSkeleton_ReportsNoSkeletonAfterTheWholeWalk()
    {
        var (lookup, asked) = MemoryLookup();

        var resolution = NifModelSkeletonResolver.Resolve(KfPath, null, lookup);

        Assert.Equal(NifModelSkeletonResolutionStatus.NoSkeleton, resolution.Status);
        Assert.Equal("no skeleton found; pass --skeleton", resolution.FailureReason);
        Assert.False(resolution.IsResolved);
        Assert.Null(resolution.Skeleton);
        Assert.Equal(new[] { OwnDirectory, Parent, Grandparent, MeshesRoot, DataRoot },
            resolution.Candidates.Select(static candidate => candidate.Path));
        Assert.All(resolution.Candidates,
            static candidate => Assert.Equal(NifModelSkeletonCandidateOutcome.Missing, candidate.Outcome));
        Assert.Equal(resolution.Candidates.Select(static candidate => candidate.Path), asked);

        var (rootOnly, _) = MemoryLookup(DataRoot);
        Assert.Equal(DataRoot, NifModelSkeletonResolver.Resolve(KfPath, null, rootOnly).SkeletonPath);
    }

    /// <summary>
    ///     A candidate naming two occurrences stops the walk as ambiguous: neither copy is chosen and the farther skeleton is
    ///     not asked about. Control: with one occurrence the same candidate is chosen.
    /// </summary>
    [Fact]
    public void AmbiguousCandidate_StopsTheWalk()
    {
        var source = new InMemoryAssetSource("data");
        var parent = new ModelSourceItem(source, source.Add(Parent, [1]));
        var farther = new ModelSourceItem(source, source.Add(Grandparent, [2]));
        var asked = new List<string>();
        NifModelSkeletonLookup ambiguous = path =>
        {
            asked.Add(path);
            return path switch
            {
                Parent => new[] { parent, parent },
                Grandparent => new[] { farther },
                _ => Array.Empty<ModelSourceItem>()
            };
        };

        var resolution = NifModelSkeletonResolver.Resolve(KfPath, null, ambiguous);

        Assert.Equal(NifModelSkeletonResolutionStatus.AmbiguousSkeleton, resolution.Status);
        Assert.Equal(NifModelSkeletonResolver.AmbiguousSkeletonReason, resolution.FailureReason);
        Assert.Null(resolution.Skeleton);
        Assert.Equal(new NifModelSkeletonCandidate(Parent, NifModelSkeletonCandidateOutcome.Ambiguous, 2),
            resolution.Candidates[^1]);
        Assert.Equal(new[] { OwnDirectory, Parent }, asked);

        NifModelSkeletonLookup single = path => path == Parent ? new[] { parent } : Array.Empty<ModelSourceItem>();
        Assert.Same(parent, NifModelSkeletonResolver.Resolve(KfPath, null, single).Skeleton);
    }

    /// <summary>
    ///     A <c>.kf</c> path that cannot be walked asks the lookup nothing. Control: the same file under its relative
    ///     virtual path resolves.
    /// </summary>
    [Fact]
    public void InvalidAnimationPath_AsksNothing()
    {
        var (lookup, asked) = MemoryLookup(OwnDirectory);

        var resolution = NifModelSkeletonResolver.Resolve("C:/Data/" + KfPath, null, lookup);

        Assert.Equal(NifModelSkeletonResolutionStatus.InvalidAnimationPath, resolution.Status);
        Assert.Equal(NifModelSkeletonResolver.InvalidAnimationPathReason, resolution.FailureReason);
        Assert.Empty(resolution.Candidates);
        Assert.Empty(asked);

        Assert.True(NifModelSkeletonResolver.Resolve(KfPath, null, lookup).IsResolved);
    }
}
