using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;
using BethesdaMultitool.Tests.Helpers;
using Slfx77.Multitool.Core.Models;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Modeling.RealAsset;

/// <summary>
///     Bucket B, hop A3-anim (cut-1b slice 9, plan section 3): the renderer's runtime reader
///     (<see cref="NifControllerSequenceNameTrackReader.ReadAll" /> and <see cref="NifBsplineTransformReader" />) against
///     the document the stage builds, for every cover-manifest file (<see cref="NifAnimationRuntimeOracle" />): each
///     Int16 B-spline control decoded as <c>bias + s / 32767f * mult</c> equals the runtime's float bit for bit, and key
///     times and values are equal. The two paths share only NifParser.
/// </summary>
/// <remarks>
///     <para>
///         Control that must fail, recorded in the receipt: one compact short changed (plus one, or minus one at the
///         maximum) in a copy of the document's controls of the existing Bucket-B B-spline file
///         (<c>nightstalker/h2hattackleft.kf</c>); the comparison then reports exactly that control.
///     </para>
///     <para>
///         Controlled blocks whose Node Name is NULL or blank (X360 files bind them through the palette) are skipped by
///         the name-targeted runtime reader and counted as not compared, never as a pass of their values. So is a whole
///         sequence the runtime reader refuses for a documented reason the probe's facts show (plan section 0.4: an empty
///         text-key label, which NifTextKeyReader rejects, as in <c>nvsecuritron/2hhholster.kf</c>; a negative frequency,
///         a clock that does not advance or an undefined cycle); any other missing runtime clip is a difference.
///     </para>
///     <para>Receipt: <c>TestOutput/cut1b-slice9-&lt;date&gt;/NifAnimationA3RuntimeOracleTests/</c>.</para>
/// </remarks>
[Trait("Category", TestCategories.BucketB)]
[Collection(SequentialIntegrationGroup.Name)]
public sealed class NifAnimationA3RuntimeOracleTests
{
    private const string Hop = nameof(NifAnimationA3RuntimeOracleTests);
    private const string CompactBsplineControl =
        "existing Bucket-B B-spline file (FnvNightstalkerBsplineAnimationRetailTests)";

    [Theory]
    [MemberData(nameof(Cut1bCoverManifest.Rows), MemberType = typeof(Cut1bCoverManifest))]
    public void EveryManifestFile_DocumentControlsAndKeysEqualTheRuntimeReader(string entry, string sha256)
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var read = Cut1bAnimationStage.Load(entry, sha256);

        var report = NifAnimationRuntimeOracle.Compare(read);

        var row = report.ToJson();
        row["fileKind"] = read.File.FileKind;
        Cut1bHopReceipt.Row(Hop, read.File, row);
        Assert.True(report.Diffs.Count == 0, report.Describe(read));
        Assert.True(
            report.NotCompared.Keys.All(static reason =>
                reason is NifAnimationRuntimeOracle.RuntimeSkipsReason
                    or NifAnimationRuntimeOracle.RuntimeEmptyTextKeyReason
                    or NifAnimationRuntimeOracle.RuntimeClockReason
                    or NifAnimationRuntimeOracle.RuntimeNameReason),
            report.Describe(read));
    }

    [Fact]
    public void Control_OneShortChangedInADocumentCopy_IsReportedAtThatControl()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var read = Cut1bAnimationStage.LoadControl(CompactBsplineControl);
        var faithful = NifAnimationRuntimeOracle.Compare(read);
        Assert.True(faithful.Diffs.Count == 0, faithful.Describe(read));

        var (clip, track, runtime) = FirstCompactTrack(read);
        var spline = track.Spline!;
        var changed = spline.QuantizedControlPoints.ToArray();
        changed[0] = changed[0] == short.MaxValue ? (short)(changed[0] - 1) : (short)(changed[0] + 1);
        var control = new NifAnimationDocumentOracleReport();
        NifAnimationRuntimeOracle.CompareControls(control, $"clip '{clip.Name}' {track.Property}", spline, changed,
            track.Property, runtime);

        var diff = Assert.Single(control.Diffs);
        Cut1bHopReceipt.Control(Hop, "oneShortChangedInADocumentCopy", new JsonObject
        {
            ["entry"] = read.File.Entry,
            ["sha256"] = read.File.Sha256,
            ["clip"] = clip.Name,
            ["property"] = track.Property.ToString(),
            ["original"] = spline.QuantizedControlPoints[0],
            ["changedTo"] = changed[0],
            ["detected"] = diff.Contains("control 0 component 0", StringComparison.Ordinal),
            ["diff"] = diff
        });
        Assert.Contains("control 0 component 0", diff, StringComparison.Ordinal);
    }

    /// <summary>The first compact B-spline transform track of a sequence clip, with the runtime channel data it matches.</summary>
    private static (SceneAnimation Clip, SceneTransformTrack Track, NifBsplineTransformData Runtime) FirstCompactTrack(
        Cut1bAnimationRead read)
    {
        var nif = NifParser.Parse(read.Bytes);
        Assert.NotNull(nif);
        var runtime = NifControllerSequenceNameTrackReader.ReadAll(read.Bytes, nif);
        var facts = NifAnimationExpectations.Facts(read.Expectation);
        foreach (var clip in read.Result.Clips)
        {
            var extras = NifAnimationDocumentOracle.Extras(clip);
            if (extras["source"]?.GetValue<string>() != "sequence" ||
                !facts.TryGetValue(extras["block"]!.GetValue<int>(), out var sequence) ||
                sequence["controlledBlocks"] is not JsonArray blocks)
            {
                continue;
            }

            var twin = runtime.FirstOrDefault(r => string.Equals(r.Name, clip.Name, StringComparison.Ordinal));
            if (twin?.BsplineTracks is not { } splines)
            {
                continue;
            }

            var positions = NifAnimationRuntimeOracle.RuntimePositions(nif, blocks);
            var entries = extras["tracks"]!.AsArray();
            for (var index = 0; index < entries.Count; index++)
            {
                var track = clip.TransformTracks[index];
                var ordinal = entries[index]!["controlledBlock"]!.GetValue<int>();
                if (track.Spline is { IsQuantized: true } &&
                    positions.TryGetValue(ordinal, out var position) && position.Bspline &&
                    position.Position < splines.Length &&
                    NifAnimationRuntimeOracle.RuntimeControls(splines[position.Position].Transform, track.Property)
                        is not null)
                {
                    return (clip, track, splines[position.Position].Transform);
                }
            }
        }

        Assert.Fail($"{read}: no compact B-spline transform track, so the control shows nothing.");
        return default;
    }
}
