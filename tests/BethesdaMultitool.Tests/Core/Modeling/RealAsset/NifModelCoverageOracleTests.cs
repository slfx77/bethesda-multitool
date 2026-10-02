using System.Globalization;
using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Modeling.Nif;
using BethesdaMultitool.Tests.Core.Modeling.Nif;
using BethesdaMultitool.Tests.Helpers;
using Slfx77.Multitool.Core.Models.Sources;
using Xunit;
using static BethesdaMultitool.Tests.Core.Modeling.RealAsset.NifModelOracleSupport;

namespace BethesdaMultitool.Tests.Core.Modeling.RealAsset;

/// <summary>
///     Hop A2 (design section 7.2; plan section 6, slices 2 and 11) over the whole cut-1a cover manifest, big-endian
///     files included: the reader's <see cref="ModelSourceCoverage" /> census equals the header block table the
///     independent Python probe recorded (count and the type name of every block index), every census element is
///     classified Typed or NativeOnly with a reason and nothing is Dropped; a file the probe declines is declined by the
///     reader's probe in the same category (<c>later-cut(2)</c> for a 20.0.0.4 scene graph, <c>other version</c> for the
///     NetImmerse 3.x/4.x markers) and refused by its read. Since cut 2 the little-endian 20.0.0.4 <c>.kf</c> at user
///     10 or 11, BS 11 is admitted like every other stream (<see cref="LegacyKf_IsAdmitted_AndItsSceneGraphTwinStaysDeclined" />). Since cut-1b slice 10 the reader admits every <c>.kf</c> animation stream
///     at 20.2.0.7, user 11 (the BS 24-33 streams the cut-1a probe declines by BS version included): its probe is
///     Supported with the <c>.kf</c> evidence, its read here (an in-memory source holding the file alone, so no
///     skeleton) refuses it with owner ruling D4's reason, and its header block table still matches. On a
///     big-endian file the probe tagged as carrying BSPackedAdditionalGeometryData, at least one packed block is Typed
///     (slice 10: every retail packed block on both consoles matches one of the six measured layouts and decodes
///     exactly under the default assumed platform, TestOutput/packed-semantics-20260924) and any packed block kept
///     native carries one of the slice-10 reasons, never a blanket one.
/// </summary>
/// <remarks>
///     <para>
///         Control: dropping one element from the reader's census is detected by the same block-table comparison, and
///         Shared's own coverage constructor rejects a classification list one element short. For a declined file with
///         no block table (NetImmerse 3.x/4.x) the control is the decline category: the reader's reason must not start
///         with a different category. For the packed branch, the same reason predicate rejects a synthetic packed
///         classification carrying the generic geometry reason and one carrying the retired "later-cut slice 10"
///         literal, so a reader that regressed to a blanket native reason would be caught.
///     </para>
///     <para>
///         Fixtures resolve through <see cref="Cut1aFixtureResolver" /> (source, alsoIn, then the Steam Final build and
///         installed Steam Data folders, each verified by SHA-256) and the expectations through
///         <see cref="Cut1aProbeExpectations" />.
///     </para>
/// </remarks>
[Trait("Category", TestCategories.BucketB)]
[Collection(SequentialIntegrationGroup.Name)]
public sealed class NifModelCoverageOracleTests
{
    private const string LaterCutOneB = "later-cut(1b)";
    private const string LaterCutTwo = "later-cut(2)";
    private const string OtherVersion = "other version";

    /// <summary>The reason slice 10 retired: a packed block is now typed or carries a precise reason, never this one.</summary>
    private const string RetiredPackedReason = "later-cut slice 10: packed console geometry";

    /// <summary>
    ///     The reasons a BSPackedAdditionalGeometryData block may carry when it stays native after slice 10, each named
    ///     by where it arises: the stream table matches none of the six layouts
    ///     (<see cref="NifModelCoverage.PackedLayoutUnknownReason" />, NifPackedGeometryDecoder); the payload is empty,
    ///     the structure is not the measured one, the counts disagree or a repeated shape vertex changes position
    ///     (<see cref="NifModelCoverage.PackedPayloadReason" />, the decoder and NifPackedGeometryReader); a skinned
    ///     shape on a weightless layout (<see cref="NifModelCoverage.PackedSkinnedStaticLayoutReason" />, the reader);
    ///     zero vertices or no kept triangle (<see cref="NifModelCoverage.EmptyGeometryReason" />, the decoder and the
    ///     reader); a packed block no placed geometry's data names
    ///     (<see cref="NifModelCoverage.UnusedGeometryDataReason" />, NifModelCoverage's category table); and a block
    ///     nothing references from the footer roots (<see cref="NifModelCoverage.UnreachableReason" />, the reachability
    ///     pass).
    /// </summary>
    private static readonly HashSet<string> PackedNativeReasons = new(StringComparer.Ordinal)
    {
        NifModelCoverage.PackedLayoutUnknownReason,
        NifModelCoverage.PackedPayloadReason,
        NifModelCoverage.PackedSkinnedStaticLayoutReason,
        NifModelCoverage.EmptyGeometryReason,
        NifModelCoverage.UnusedGeometryDataReason,
        NifModelCoverage.UnreachableReason
    };

    [Theory]
    [MemberData(nameof(Cut1aCoverManifest.Rows), MemberType = typeof(Cut1aCoverManifest))]
    public void Coverage_MatchesTheProbeBlockTable(string entry, string sha256)
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var file = Cut1aCoverManifest.Require(sha256);
        Assert.Equal(entry, file.Entry);
        var expectation = Cut1aProbeExpectations.Require(file);
        var bytes = Cut1aFixtureResolver.Require(file);
        Assert.Equal(sha256, Cut1aFixtureResolver.Sha256(bytes));

        var declined = Text(expectation["declined"]);
        if (file.IsAnimationStream &&
            (declined is null || string.Equals(DeclineCategory(declined), LaterCutOneB, StringComparison.Ordinal)))
        {
            AssertAnimationStreamNeedsASkeleton(file, bytes, expectation);
        }
        else if (declined is not null)
        {
            AssertDeclined(file, bytes, expectation, declined);
        }
        else
        {
            AssertCoverage(file, bytes, expectation);
        }
    }

    /// <summary>
    ///     Cut 2: the manifest's 20.0.0.4 <c>.kf</c> rows (talk_handsatside_still2.kf and the uv11 key's cover, eatidle.kf)
    ///     are walked by the probe and admitted by the reader's probe with the .kf evidence, and the manifest's 20.0.0.4
    ///     scene graph (collisionboxstatic.nif, the same identity's decline control) is declined by both in the
    ///     later-cut(2) category and refused by the read with that reason. Each is the other's control: the version key is
    ///     the same, only the roots differ.
    /// </summary>
    [Fact]
    public void LegacyKf_IsAdmitted_AndItsSceneGraphTwinStaysDeclined()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var legacy = Cut1aCoverManifest.Files.Where(f => f.Key.StartsWith("20.0.0.4/", StringComparison.Ordinal))
            .ToList();
        var streams = legacy.Where(static f => f.IsAnimationStream).ToList();
        var sceneGraphs = legacy.Where(static f => !f.IsAnimationStream).ToList();
        Assert.NotEmpty(streams);
        var twin = Assert.Single(sceneGraphs);
        Assert.EndsWith("collisionboxstatic.nif", twin.Entry, StringComparison.Ordinal);
        Assert.True(twin.IsDeclinedControl);

        foreach (var stream in streams)
        {
            Assert.False(stream.IsDeclinedControl, $"{stream}: still a decline control.");
            var expectation = Cut1aProbeExpectations.Require(stream);
            Assert.Null(expectation["declined"]);
            var bytes = Cut1aFixtureResolver.Require(stream);
            var probe = NifModelTestSupport.Probe(bytes);
            Assert.Equal(ModelProbeKind.Supported, probe.Kind);
            Assert.Equal(ModelProbeConfidence.Confirmed, probe.Confidence);
            Assert.StartsWith("NIF 20.0.0.4, user ", probe.Evidence!.Description, StringComparison.Ordinal);
            Assert.EndsWith(NifModelProbe.AnimationStreamSuffix, probe.Evidence.Description, StringComparison.Ordinal);
            var failure = Assert.Throws<NotSupportedException>(() => Read(bytes, stream));
            Assert.StartsWith(NifModelSkeletonResolver.NoSkeletonReason, failure.Message, StringComparison.Ordinal);
        }

        var twinExpectation = Cut1aProbeExpectations.Require(twin);
        var twinDeclined = Text(twinExpectation["declined"]);
        Assert.NotNull(twinDeclined);
        Assert.Equal(LaterCutTwo, DeclineCategory(twinDeclined));
        var twinBytes = Cut1aFixtureResolver.Require(twin);
        var twinProbe = NifModelTestSupport.Probe(twinBytes);
        Assert.Equal(ModelProbeKind.Unsupported, twinProbe.Kind);
        Assert.StartsWith(LaterCutTwo, twinProbe.Reason, StringComparison.Ordinal);
        Assert.Equal("NIF 20.0.0.4, user 11, BS 11, little-endian", twinProbe.Evidence!.Description);
        var twinFailure = Assert.Throws<NotSupportedException>(() => Read(twinBytes, twin));
        Assert.StartsWith(LaterCutTwo, twinFailure.Message, StringComparison.Ordinal);
    }

    /// <summary>The probe declines the file: the reader's probe declines it in the same category and its read refuses it.</summary>
    private static void AssertDeclined(Cut1aCoverFile file, byte[] bytes, JsonObject expectation, string declined)
    {
        Assert.True(file.IsDeclinedControl, $"{file}: the probe declines a manifest cover file ({declined}).");
        var category = DeclineCategory(declined);
        var probe = NifModelTestSupport.Probe(bytes);
        Assert.Equal(ModelProbeKind.Unsupported, probe.Kind);
        Assert.NotNull(probe.Reason);
        Assert.StartsWith(category, probe.Reason, StringComparison.Ordinal);

        var failure = Record.Exception(() => Read(bytes, file));
        Assert.True(failure is NotSupportedException or InvalidDataException,
            $"{file}: the read of a declined file threw {failure?.GetType().Name ?? "nothing"}: {failure?.Message}");

        var names = expectation["blockTypeNames"]!.AsArray();
        if (names.Count > 0)
        {
            var types = ParserBlockTypes(bytes);
            Assert.Null(FirstBlockTableMismatch(types, expectation));
            Assert.NotNull(FirstBlockTableMismatch(types.Take(types.Count - 1).ToList(), expectation));
        }
        else
        {
            // No block table to compare (NetImmerse 3.x/4.x): the control is the category itself.
            var wrong = string.Equals(category, OtherVersion, StringComparison.Ordinal) ? LaterCutOneB : OtherVersion;
            Assert.False(probe.Reason.StartsWith(wrong, StringComparison.Ordinal),
                $"{file}: the decline category comparison cannot discriminate ('{probe.Reason}').");
        }

        TestContext.Current.TestOutputHelper?.WriteLine($"{file}: declined by both ({category}); {names.Count} block types.");
    }

    /// <summary>
    ///     A .kf stream (cut-1b slice 10): Supported by the probe with the .kf evidence, refused by a read with no skeleton
    ///     in reach with D4's reason (never the retired 'later-cut(1b)' one), its header block table still matching the
    ///     probe's.
    /// </summary>
    private static void AssertAnimationStreamNeedsASkeleton(Cut1aCoverFile file, byte[] bytes, JsonObject expectation)
    {
        var probe = NifModelTestSupport.Probe(bytes);
        Assert.Equal(ModelProbeKind.Supported, probe.Kind);
        Assert.EndsWith(NifModelProbe.AnimationStreamSuffix, probe.Evidence!.Description, StringComparison.Ordinal);
        var failure = Assert.Throws<NotSupportedException>(() => Read(bytes, file));
        Assert.StartsWith(NifModelSkeletonResolver.NoSkeletonReason, failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(LaterCutOneB, failure.Message, StringComparison.Ordinal);

        var types = ReaderCensusTypes(bytes);
        Assert.Null(FirstBlockTableMismatch(types, expectation));
        Assert.Equal(Int(expectation["header"]!["blockCount"]), types.Count);

        // Control: one element dropped from the census is detected by the same comparison.
        Assert.NotNull(FirstBlockTableMismatch(types.Take(types.Count - 1).ToList(), expectation));
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"{file}: animation stream admitted and refused for want of a skeleton; {types.Count} blocks match the " +
            "probe's table.");
    }

    /// <summary>A supported model: the reader's coverage census equals the probe's block table and is fully classified.</summary>
    private static void AssertCoverage(Cut1aCoverFile file, byte[] bytes, JsonObject expectation)
    {
        var result = Read(bytes, file);
        var coverage = result.Coverage;
        var types = coverage.Elements.Select(e => e.Kind).ToList();
        Assert.Null(FirstBlockTableMismatch(types, expectation));
        Assert.Equal(Int(expectation["header"]!["blockCount"]), coverage.TotalCount);
        for (var i = 0; i < coverage.Elements.Count; i++)
        {
            Assert.Equal(string.Create(CultureInfo.InvariantCulture, $"block:{i}"), coverage.Elements[i].Identity);
        }

        Assert.Equal(0, coverage.DroppedCount);
        Assert.Equal(coverage.TotalCount, coverage.TypedCount + coverage.NativeOnlyCount);
        foreach (var classification in coverage.Classifications)
        {
            Assert.True(classification.Kind is ModelSourceCoverageKind.Typed or ModelSourceCoverageKind.NativeOnly,
                $"{file}: {classification.ElementIdentity} is {classification.Kind}.");
            if (classification.Kind == ModelSourceCoverageKind.NativeOnly)
            {
                Assert.False(string.IsNullOrWhiteSpace(classification.Reason),
                    $"{file}: {classification.ElementIdentity} is NativeOnly without a reason.");
            }
        }

        if (file.IsBigEndian && HasTag(expectation, PackedGeometryTag))
        {
            AssertPackedBlocksClassified(file, coverage);
        }

        // Control 1: one element dropped from the census is detected by the same comparison.
        Assert.NotNull(FirstBlockTableMismatch(types.Take(types.Count - 1).ToList(), expectation));

        // Control 2: Shared's coverage refuses a classification list that omits one census element.
        Assert.Throws<ArgumentException>(() => new ModelSourceCoverage(coverage.Source, coverage.CensusEvidence,
            coverage.Elements, coverage.Classifications.Take(coverage.Classifications.Count - 1)));

        TestContext.Current.TestOutputHelper?.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{file}: {coverage.TotalCount} blocks match the probe's table; {coverage.TypedCount} typed, " +
            $"{coverage.NativeOnlyCount} native-only, {result.Document.Diagnostics.Count} diagnostic(s)."));
    }

    /// <summary>
    ///     The packed blocks of a big-endian file the probe tagged: at least one is Typed, and every one kept native
    ///     carries a reason from <see cref="PackedNativeReasons" />. Control: the same predicate rejects a synthetic
    ///     packed classification with the generic geometry reason and one with the retired slice-10 literal.
    /// </summary>
    private static void AssertPackedBlocksClassified(Cut1aCoverFile file, ModelSourceCoverage coverage)
    {
        var kinds = coverage.Elements.ToDictionary(e => e.Identity, e => e.Kind, StringComparer.Ordinal);
        var packed = coverage.Classifications
            .Where(c => string.Equals(kinds[c.ElementIdentity], NifModelGeometryReader.PackedAdditionalDataType,
                StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(packed);
        Assert.Contains(packed, c => c.Kind == ModelSourceCoverageKind.Typed);
        foreach (var classification in packed)
        {
            if (classification.Kind == ModelSourceCoverageKind.NativeOnly)
            {
                Assert.True(IsPackedNativeReason(classification),
                    $"{file}: {classification.ElementIdentity} is NativeOnly with a reason outside slice 10's: " +
                    $"'{classification.Reason}'.");
            }
        }

        // Control: a blanket native reason on a packed block fails the same predicate.
        var identity = packed[0].ElementIdentity;
        Assert.False(IsPackedNativeReason(new ModelSourceClassification(identity, ModelSourceCoverageKind.NativeOnly,
            NifModelCoverage.OtherGeometryReason)), "the generic geometry reason passed the packed predicate");
        Assert.False(IsPackedNativeReason(new ModelSourceClassification(identity, ModelSourceCoverageKind.NativeOnly,
            RetiredPackedReason)), "the retired slice-10 reason passed the packed predicate");
    }

    /// <summary>True when a native-only classification's reason is one slice 10 assigns to a packed block.</summary>
    private static bool IsPackedNativeReason(ModelSourceClassification classification)
    {
        return classification.Reason is { } reason && PackedNativeReasons.Contains(reason);
    }

    /// <summary>The reader's decline category that the probe's decline reason predicts.</summary>
    private static string DeclineCategory(string declined)
    {
        if (declined.Contains("BS version", StringComparison.Ordinal))
        {
            return LaterCutOneB;
        }

        return declined.Contains("20.0.0.4", StringComparison.Ordinal) ? LaterCutTwo : OtherVersion;
    }
}
