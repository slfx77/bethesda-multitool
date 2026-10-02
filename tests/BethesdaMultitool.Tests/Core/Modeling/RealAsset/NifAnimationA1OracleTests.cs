using System.Text.Json.Nodes;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Modeling.RealAsset;

/// <summary>
///     Bucket B, hop A1-anim (cut-1b slice 9, plan section 3): for every file of the cut-1b cover manifest the animation
///     stage (<see cref="Cut1bAnimationStage" />: <c>ReadNif</c> with the cut-1a property targets, or <c>ReadKf</c> with
///     the manifest's pinned skeleton) types tracks, clocks and events that equal what the independent probe's record
///     gives through only the declared conversions (<see cref="NifAnimationDocumentOracle" />), bit for bit: the rotation
///     permutation, scale replication, the Hermite triple, the TBC naming with RE-19's endpoints, the state rule, RE-17's
///     keys, the Euler axis curves, the Squad keys after chain alignment and W clamp with inner points from a test-side
///     RE-24 computation, the per-target and whole-vector morph curves and the property curves.
/// </summary>
/// <remarks>
///     <para>
///         Controls that must fail, each on the file the plan names, each recorded in the receipt with the difference it
///         produced: Forward and Backward exchanged in the Hermite triple on FO3 <c>meshes/traps/fxgastrapblast.nif</c>
///         (the scale data, block 7) and FNV PS3 <c>meshes/dlcpitt/effects/dlcpittfireburst01.nif</c> (the translation
///         data, block 31); the sentinel static typed as Constant (fxgastrapblast's undriven translation and rotation);
///         the controller clock applied inside a sequence (the sequence tracks of the multi-sequence and sequence-morph
///         controls, whose controlled blocks reference an NiVisController or NiGeomMorpherController with a clock); the
///         event CRLF trimmed (<c>swimmtleft.kf</c>).
///     </para>
///     <para>
///         Receipt: <c>TestOutput/cut1b-slice9-&lt;date&gt;/NifAnimationA1OracleTests/</c> (<see cref="Cut1bHopReceipt" />).
///         A decline control, a probe-declined file and a skeleton-less <c>.kf</c> skip with the reason.
///     </para>
/// </remarks>
[Trait("Category", TestCategories.BucketB)]
[Collection(SequentialIntegrationGroup.Name)]
public sealed class NifAnimationA1OracleTests
{
    private const string Hop = nameof(NifAnimationA1OracleTests);
    private const string ScaleHermiteControl = "Forward != Backward on a typed channel (scale)";
    private const string TranslationHermiteControl = "Forward != Backward on a typed channel (translation)";
    private const string CrlfControl = "event text: CRLF";
    private const string MultiSequenceControl = "Multi-sequence";
    private const string SequenceMorphControl = "Sequence morph that differs";

    [Theory]
    [MemberData(nameof(Cut1bCoverManifest.Rows), MemberType = typeof(Cut1bCoverManifest))]
    public void EveryManifestFile_TypedTracksMatchTheProbeThroughTheDeclaredConversions(string entry, string sha256)
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var read = Cut1bAnimationStage.Load(entry, sha256);

        var report = NifAnimationDocumentOracle.Compare(read, NifAnimationDocumentOracleOptions.Faithful);

        var row = report.ToJson();
        row["fileKind"] = read.File.FileKind;
        row["platform"] = read.Platform.OptionValue;
        row["skeletonSha256"] = read.SkeletonSha256;
        row["decisions"] = read.Result.Decisions.Count;
        Cut1bHopReceipt.Row(Hop, read.File, row);
        Assert.True(report.Diffs.Count == 0, report.Describe(read));
        Assert.True(
            report.NotCompared.Keys.All(static reason =>
                string.Equals(reason, NifAnimationDocumentOracle.UvDataReason, StringComparison.Ordinal)),
            report.Describe(read));
    }

    [Fact]
    public void Control_HermiteForwardAndBackwardExchanged_FailsOnFxGasTrapBlastScale()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        AssertHermiteControl(ScaleHermiteControl, 7);
    }

    [Fact]
    public void Control_HermiteForwardAndBackwardExchanged_FailsOnDlcPittFireBurstTranslation()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        AssertHermiteControl(TranslationHermiteControl, 31);
    }

    [Fact]
    public void Control_SentinelStaticTypedAsConstant_Fails()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var read = Cut1bAnimationStage.LoadControl(ScaleHermiteControl);
        var faithful = NifAnimationDocumentOracle.Compare(read, NifAnimationDocumentOracleOptions.Faithful);
        Assert.True(faithful.Diffs.Count == 0, faithful.Describe(read));

        var options = new NifAnimationDocumentOracleOptions(SentinelStaticAsConstant: true);
        var control = NifAnimationDocumentOracle.Compare(read, options);

        var hits = control.Diffs.Where(static diff =>
            diff.Contains("state expected Constant, actual NotDriven", StringComparison.Ordinal)).ToList();
        Record(options.Label, read, control, hits);
        Assert.NotEmpty(hits);
    }

    [Fact]
    public void Control_ControllerClockAppliedInsideASequence_Fails()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var options = new NifAnimationDocumentOracleOptions(ControllerClockInSequence: true);
        var detected = 0;
        var outcomes = new JsonArray();
        foreach (var name in new[] { MultiSequenceControl, SequenceMorphControl })
        {
            var read = Cut1bAnimationStage.LoadControl(name);
            var faithful = NifAnimationDocumentOracle.Compare(read, NifAnimationDocumentOracleOptions.Faithful);
            Assert.True(faithful.Diffs.Count == 0, faithful.Describe(read));
            var control = NifAnimationDocumentOracle.Compare(read, options);
            var hits = control.Diffs.Where(static diff =>
                diff.Contains(" clock: expected (", StringComparison.Ordinal) &&
                diff.Contains("actual no clock", StringComparison.Ordinal)).ToList();
            detected += hits.Count > 0 ? 1 : 0;
            outcomes.Add(new JsonObject
            {
                ["control"] = name,
                ["entry"] = read.File.Entry,
                ["sha256"] = read.File.Sha256,
                ["detected"] = hits.Count > 0,
                ["matchingDiffs"] = hits.Count,
                ["first"] = hits.FirstOrDefault()
            });
        }

        Cut1bHopReceipt.Control(Hop, options.Label, new JsonObject
        {
            ["mutation"] = "each sequence track given its controlled block's controller clock (RE-22 rule 3)",
            ["files"] = outcomes,
            ["detected"] = detected > 0
        });
        Assert.True(detected > 0, outcomes.ToJsonString());
    }

    [Fact]
    public void Control_EventCrlfTrimmed_FailsOnSwimMtLeft()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var read = Cut1bAnimationStage.LoadControl(CrlfControl);
        var faithful = NifAnimationDocumentOracle.Compare(read, NifAnimationDocumentOracleOptions.Faithful);
        Assert.True(faithful.Diffs.Count == 0, faithful.Describe(read));

        var options = new NifAnimationDocumentOracleOptions(TrimEventCrlf: true);
        var control = NifAnimationDocumentOracle.Compare(read, options);

        var hits = control.Diffs.Where(static diff =>
            diff.Contains(": text expected ", StringComparison.Ordinal) &&
            diff.Contains("\\r\\n", StringComparison.Ordinal)).ToList();
        Record(options.Label, read, control, hits);
        Assert.NotEmpty(hits);
    }

    /// <summary>
    ///     The Hermite control: the faithful comparison passes on the file, the exchanged one fails on the values of the
    ///     named data block.
    /// </summary>
    private static void AssertHermiteControl(string controlName, int dataBlock)
    {
        var read = Cut1bAnimationStage.LoadControl(controlName);
        var faithful = NifAnimationDocumentOracle.Compare(read, NifAnimationDocumentOracleOptions.Faithful);
        Assert.True(faithful.Diffs.Count == 0, faithful.Describe(read));

        var options = new NifAnimationDocumentOracleOptions(SwapHermiteForwardBackward: true);
        var control = NifAnimationDocumentOracle.Compare(read, options);

        var marker = $" data {dataBlock})";
        var hits = control.Diffs.Where(diff =>
            diff.Contains(marker, StringComparison.Ordinal) &&
            diff.Contains(" values[", StringComparison.Ordinal)).ToList();
        Record($"{options.Label} ({controlName})", read, control, hits);
        Assert.True(hits.Count > 0, control.Describe(read));
    }

    private static void Record(string name, Cut1bAnimationRead read, NifAnimationDocumentOracleReport control,
        List<string> hits)
    {
        Cut1bHopReceipt.Control(Hop, name, new JsonObject
        {
            ["entry"] = read.File.Entry,
            ["sha256"] = read.File.Sha256,
            ["detected"] = hits.Count > 0,
            ["matchingDiffs"] = hits.Count,
            ["diffCount"] = control.Diffs.Count,
            ["first"] = hits.FirstOrDefault()
        });
    }
}
