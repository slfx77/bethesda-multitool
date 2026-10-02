using System.Globalization;
using System.Text.Json.Nodes;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Modeling.RealAsset;

/// <summary>
///     Bucket B, hop A8-sample (cut-1b slice 9, plan section 3): for every cover-manifest file, every keyed transform
///     track and every Euler track of the reader's document is sampled through Shared's public
///     <see cref="Slfx77.Multitool.Core.Models.ScenePoseEvaluator" /> at its key times and segment quarter points, and an
///     independent evaluator (<c>tools/scripts/nif_curve_eval.py</c>: normalized-segment Hermite, Kochanek-Bartels
///     interior tangents, open-uniform clamped Cox-de Boor, RE-17's counter-warped nlerp and RE-24's Squad, plain Python
///     and numpy) must agree (<see cref="NifCurveEvalHarness" />).
/// </summary>
/// <remarks>
///     <para>
///         Tolerance (stated in the evaluator's output and the receipt): a scalar component within 4 Float32 ulp of the
///         largest key, tangent or control magnitude of its segment; a rotation within 2e-4 degrees, measured as the
///         angle between the evaluator's rotation and the local matrix Shared published (Shared publishes a Float32
///         matrix built from a Float32 quaternion, whose rounding alone is below 2e-5 degrees).
///     </para>
///     <para>
///         Controls, each evaluated in the same run and required to exceed the tolerance at least 100 times on the file
///         the plan names: a B-spline evaluated with an unclamped (uniform) knot vector (the existing Bucket-B B-spline
///         file, <c>nightstalker/h2hattackleft.kf</c>); a TBC with continuity and bias exchanged
///         (<c>sneak2hhattackspin.kf</c> block 85, whose Squad inner points the evaluator recomputes through RE-24 from the
///         aligned keys and the stored T, C, B: the manifest's component TBC groups with continuity != bias all carry
///         tension 1 or within one ulp of it, where the exchange moves a tangent by less than the tolerance); a Hermite
///         with Forward and Backward exchanged (<c>fxgastrapblast.nif</c> scale and <c>dlcpittfireburst01.nif</c>
///         translation). The receipt records each file's worst oracle deviation and every control's worst ratio.
///     </para>
///     <para>
///         Receipt: <c>TestOutput/cut1b-slice9-&lt;date&gt;/NifAnimationA8SampleOracleTests/</c>; the evaluator's full
///         output per file under <c>eval/</c>.
///     </para>
/// </remarks>
[Trait("Category", TestCategories.BucketB)]
[Collection(SequentialIntegrationGroup.Name)]
public sealed class NifAnimationA8SampleOracleTests
{
    private const string Hop = nameof(NifAnimationA8SampleOracleTests);
    private const double ControlFactor = 100;
    private const string BsplineControl =
        "existing Bucket-B B-spline file (FnvNightstalkerBsplineAnimationRetailTests)";
    /// <summary>
    ///     The TBC control file. The plan's named TBC rotation control (sneak2hhattackspin.kf) keys its only TBC group
    ///     (block 85, 26 of 41 keys with continuity != bias) on a node the pinned skeleton lacks, so that group stays native
    ///     (targetNotInSkeleton) and nothing typed reaches the sampler; the manifest's only other TBC rotation group with
    ///     continuity != bias is typed: FO3 Broken Steel dlc03vertibirdafbstrafe03.nif, block 20 (4 of 12 keys).
    /// </summary>
    private const string TbcControlEntry = "meshes/dlc03/vehicles/dlc03vertibirdafbstrafe03.nif";

    private const string TbcControlSha256 = "f8f8b6fbdb575307948bf6342501a1e61cc3856c030a067f07b6091ecb63c89f";
    private const string ScaleHermiteControl = "Forward != Backward on a typed channel (scale)";
    private const string TranslationHermiteControl = "Forward != Backward on a typed channel (translation)";

    [Theory]
    [MemberData(nameof(Cut1bCoverManifest.Rows), MemberType = typeof(Cut1bCoverManifest))]
    public void EveryManifestFile_SharedSamplesMatchTheIndependentEvaluator(string entry, string sha256)
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var read = Cut1bAnimationStage.Load(entry, sha256);

        var (summary, result) = NifCurveEvalHarness.Run(read);

        var row = Row(read, summary, result);
        Cut1bHopReceipt.Row(Hop, read.File, row);
        Assert.True(summary["evaluatorErrors"]!.AsArray().Count == 0,
            $"{read}: Shared's evaluator refused a track: {summary["evaluatorErrors"]!.ToJsonString()}");

        // A file with no keyed transform or Euler track has nothing to sample (tracksSampled 0 in the receipt).
        var failures = result?["failures"]!.AsArray().Count ?? 0;
        Assert.True(failures == 0, $"{read}: {failures} tracks outside the tolerance: " +
                                   (result is null ? string.Empty : Failed(result)));
    }

    [Fact]
    public void Control_BsplineEvaluatedWithAnUnclampedKnotVector_ExceedsTheToleranceHundredfold()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        AssertControl(BsplineControl, "bsplineUnclamped");
    }

    [Fact]
    public void Control_TbcContinuityAndBiasExchanged_ExceedsTheToleranceHundredfold()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        AssertControl(Cut1bAnimationStage.Load(TbcControlEntry, TbcControlSha256), "tbcContinuityBiasExchanged",
            "TBC continuity != bias, typed Squad rotation");
    }

    [Fact]
    public void Control_HermiteForwardAndBackwardExchanged_ExceedsTheToleranceHundredfoldOnScale()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        AssertControl(ScaleHermiteControl, "hermiteForwardBackwardExchanged");
    }

    [Fact]
    public void Control_HermiteForwardAndBackwardExchanged_ExceedsTheToleranceHundredfoldOnTranslation()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        AssertControl(TranslationHermiteControl, "hermiteForwardBackwardExchanged");
    }

    /// <summary>
    ///     One control: the oracle passes on the named file (so the control's failure is the mutation's, not the file's),
    ///     and the control's worst ratio over the file's applicable tracks is at least <see cref="ControlFactor" />.
    /// </summary>
    private static void AssertControl(string controlName, string variant)
    {
        AssertControl(Cut1bAnimationStage.LoadControl(controlName), variant, controlName);
    }

    /// <summary>As <see cref="AssertControl(string, string)" />, on a given read labeled by <paramref name="controlName" />.</summary>
    private static void AssertControl(Cut1bAnimationRead read, string variant, string controlName)
    {
        var (summary, result) = NifCurveEvalHarness.Run(read);
        Assert.True(result is not null, $"{read}: no keyed track was sampled, so the control shows nothing.");
        Assert.True(result["failures"]!.AsArray().Count == 0, $"{read}: the oracle itself fails: {Failed(result)}");

        var control = result["controls"]![variant]!.AsObject();
        var applicable = control["applicableTracks"]!.GetValue<int>();
        var worst = control["worstRatio"]!.GetValue<double>();
        Cut1bHopReceipt.Control(Hop, $"{variant} ({controlName})", new JsonObject
        {
            ["entry"] = read.File.Entry,
            ["sha256"] = read.File.Sha256,
            ["variant"] = variant,
            ["applicableTracks"] = applicable,
            ["worstRatio"] = worst,
            ["track"] = control["track"]?.DeepClone(),
            ["requiredRatio"] = ControlFactor,
            ["detected"] = worst >= ControlFactor,
            ["oracleWorst"] = result["worst"]!.DeepClone(),
            ["samples"] = summary["samples"]!.DeepClone()
        });
        Assert.True(applicable > 0, $"{read}: no track the {variant} control applies to.");
        Assert.True(worst >= ControlFactor,
            $"{read}: the {variant} control's worst ratio is {worst.ToString(CultureInfo.InvariantCulture)}, below " +
            $"{ControlFactor.ToString(CultureInfo.InvariantCulture)} times the tolerance.");
    }

    private static JsonObject Row(Cut1bAnimationRead read, JsonObject summary, JsonObject? result)
    {
        var row = (JsonObject)summary.DeepClone();
        row["fileKind"] = read.File.FileKind;
        if (result is null)
        {
            return row;
        }

        Cut1bHopReceipt.Tool(Hop, "python", result["python"]);
        Cut1bHopReceipt.Tool(Hop, "numpy", result["numpy"]);
        Cut1bHopReceipt.Tool(Hop, "tolerance", result["tolerance"]);
        row["evalOutput"] = Cut1bHopReceipt.Artifact(Hop, "eval", read.File, result);
        row["worst"] = result["worst"]!.DeepClone();
        row["controls"] = result["controls"]!.DeepClone();
        row["failures"] = new JsonArray(result["failures"]!.AsArray().Take(20)
            .Select(static node => node?.DeepClone()).ToArray());
        return row;
    }

    private static string Failed(JsonObject result)
    {
        var failed = result["tracks"]!.AsArray()
            .Select(static node => node!.AsObject())
            .Where(static track => !track["passed"]!.GetValue<bool>())
            .Take(10)
            .Select(static track =>
                $"{track["id"]} ratio {track["worstRatio"]} deviation {track["worstDeviation"]} at t bits " +
                $"{track["worstAt"]} {track["error"]}");
        return string.Join("; ", failed);
    }
}
