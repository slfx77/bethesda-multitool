using Xunit;

namespace BethesdaMultitool.Tests.Helpers;

/// <summary>
///     Checks that the legacy <c>Sample/Full_Builds/…</c> rewrite actually lands on files that
///     exist in the generated corpus.
///     <para>
///         Worth its own suite because nothing else exercises it: every in-tree caller was updated
///         to the new spelling, so the rewrite only ever fires for code this repository has not
///         updated — another session's in-flight edit, or a path pasted from an older note. Without
///         these, it could silently degrade to a no-op (it did exactly that twice while the
///         migration was being written, when a repo-wide path sweep rewrote the mapping's own
///         legacy keys) and nothing would notice.
///     </para>
/// </summary>
public sealed class SampleCorpusTests
{
    /// <summary>
    ///     The regroupings that are not per-build are a fixed table, so they rewrite with or
    ///     without a corpus present.
    /// </summary>
    [Theory]
    [InlineData(@"Sample\MemoryDump\Fallout_Debug.xex.dmp", @"Sample\MemoryDumps\Fallout_Debug.xex.dmp")]
    [InlineData(@"Sample\PDB\Aug_22_MemDebug\types_full.txt",
        @"Sample\DebugSymbols\Fallout - New Vegas (X360)\Aug_22_MemDebug\types_full.txt")]
    public void FixedRegroupings_RewriteWithoutNeedingTheCorpus(string legacy, string expected)
    {
        Assert.Equal(expected, SampleCorpus.Rewrite(legacy));
    }

    /// <summary>A path the migration never touched must rewrite to null, not to itself.</summary>
    [Theory]
    [InlineData(@"Sample\ESM\pc_final\FalloutNV.esm")]
    [InlineData(@"Sample\Builds\Fallout 3 (2026-2-15, Steam - Final)\Data\Fallout3.esm")]
    [InlineData("")]
    public void UnrelatedPaths_AreNotRewritten(string path)
    {
        Assert.Null(SampleCorpus.Rewrite(path));
    }

    /// <summary>
    ///     Candidates always offers the caller's own spelling first, so a resolver that consumes it
    ///     never changes behaviour for a path that needs no rewrite.
    /// </summary>
    [Fact]
    public void Candidates_YieldsTheOriginalSpellingFirst()
    {
        const string path = @"Sample\ESM\pc_final\FalloutNV.esm";
        Assert.Equal(path, Assert.Single(SampleCorpus.Candidates(path)));
    }

    /// <summary>
    ///     The per-build half of the map comes from the generated <c>catalog.json</c>, so it needs
    ///     the corpus. Each rewritten path is checked against the filesystem — a mapping that
    ///     resolves to nothing is the failure this is here to catch.
    ///     <para>
    ///         ⚠ Only fixtures that stayed in the build tree belong here. A legacy path naming
    ///         original media (a JAR, a disc image) now resolves under <c>Sample/Media</c>, which
    ///         this build-relative rewrite deliberately does not cover — <c>RealAssetPaths</c>
    ///         names those directly.
    ///     </para>
    /// </summary>
    [Theory]
    [InlineData(@"Full_Builds\Fallout New Vegas (PC Final)\Data\Fallout - Meshes.bsa")]
    [InlineData(@"Full_Builds\Fallout 3 (PC Final)\Data\Fallout3.esm")]
    [InlineData(@"Full_Builds\Fallout New Vegas (July 21, 2010)\FalloutNV\Data\FalloutNV.esm")]
    [InlineData(@"Full_Builds\Arena_Disc")]
    [InlineData(@"Full_Builds\Redguard_Disc1_iso\DATA1.CAB")]
    [InlineData(@"Full_Builds\Van Buren (Dec 9 2003)\data")]
    public void MigratedFixtures_RewriteToSomethingThatExists(string legacyRelative)
    {
        var rewritten = SampleCorpus.Rewrite(legacyRelative);
        Assert.SkipWhen(rewritten is null,
            $"No corpus catalog available to map '{legacyRelative}'. " +
            "Run tools/corpus/SampleGenerator to generate Sample/Builds.");

        var resolved = RealAssetPaths.SampleFile(rewritten) ?? RealAssetPaths.SampleDirectory(rewritten);
        Assert.SkipWhen(resolved is null,
            $"'{legacyRelative}' maps to '{rewritten}', which is not present. " +
            "That is expected only when this build was never staged on this machine.");

        Assert.StartsWith("Builds", rewritten, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     The deepest legacy name must win: <c>Redguard_Disc1_iso</c> and
    ///     <c>Redguard_Disc1_extracted</c> both start with <c>Redguard_Disc1</c>, and a
    ///     shortest-match walk would file all three under the same directory.
    /// </summary>
    [Fact]
    public void OverlappingLegacyNames_ResolveToTheLongestMatch()
    {
        var iso = SampleCorpus.Rewrite(@"Full_Builds\Redguard_Disc1_iso");
        var extracted = SampleCorpus.Rewrite(@"Full_Builds\Redguard_Disc1_extracted");
        var disc = SampleCorpus.Rewrite(@"Full_Builds\Redguard_Disc1");
        Assert.SkipWhen(iso is null || extracted is null || disc is null,
            "No corpus catalog available. Run tools/corpus/SampleGenerator to generate Sample/Builds.");

        Assert.EndsWith(@"iso", iso, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(@"extracted", extracted, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(@"Disc 1 (Install)", disc, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(iso, extracted);
        Assert.NotEqual(iso, disc);
    }
}