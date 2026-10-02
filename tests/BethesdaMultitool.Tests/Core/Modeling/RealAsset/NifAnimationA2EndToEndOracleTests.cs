using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Modeling;
using BethesdaMultitool.Core.Modeling.Nif;
using BethesdaMultitool.Tests.Core.Modeling.Nif;
using BethesdaMultitool.Tests.Helpers;
using Slfx77.Multitool.Core.Models.Sources;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Modeling.RealAsset;

/// <summary>
///     Bucket B, hop A2-anim end to end (cut-1b slice 10, plan section 4 row 10): every cover-manifest file read through
///     <see cref="NifModelReader" /> itself (the switch-over) yields the clips the slice-9 stage yields
///     (<see cref="Cut1bAnimationStage" />, same names in the same order), one <c>bmt.nif.animation.clip</c> row per
///     clip, and a document coverage whose every animation block carries the stage's classification
///     (<see cref="NifModelAnimationCoverage.ClassifyFile" />), with no 'later-cut(1b)' reason anywhere. A <c>.kf</c>
///     reads against its pinned skeleton: a walk-up pin through a data-root resolver holding the skeleton at its pinned
///     path (so the reader's own walk-up must find it), a Fallout 3 fallback pin through <c>bmt.skeleton</c>; the
///     skeleton provenance row must carry the pinned SHA-256.
/// </summary>
/// <remarks>
///     <para>
///         Two document classifications may legitimately differ from the stage's: an undecided animation block that no
///         footer root reaches keeps the cut-1a precedence (<see cref="NifModelCoverage.UnreachableReason" />), and an
///         object palette the cut-1a palette names typed for a display name is Typed although no binding used it.
///     </para>
///     <para>
///         Controls that must fail, recorded in the receipt: one expected clip removed, and one expected classification's
///         reason changed; each is reported by the same comparison.
///     </para>
///     <para>Receipt: <c>TestOutput/cut1b-slice10-&lt;date&gt;/NifAnimationA2EndToEndOracleTests/</c>.</para>
/// </remarks>
[Trait("Category", TestCategories.BucketB)]
[Collection(SequentialIntegrationGroup.Name)]
public sealed class NifAnimationA2EndToEndOracleTests
{
    private const string Hop = nameof(NifAnimationA2EndToEndOracleTests);
    private const string ControlName = "LOOP sequence";
    private const string RetiredReason = "later-cut(1b)";
    private const string MeshesPrefix = "meshes/";
    private const string ExplicitPrefix = "fo3-fallback/";

    [Theory]
    [MemberData(nameof(Cut1bCoverManifest.Rows), MemberType = typeof(Cut1bCoverManifest))]
    public async Task EveryManifestFile_TheReaderMatchesTheStage(string entry, string sha256)
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var stage = Cut1bAnimationStage.Load(entry, sha256);
        var expected = NifModelAnimationCoverage.ClassifyFile(stage.State, stage.Result,
            TestContext.Current.CancellationToken);
        var names = stage.Result.Clips.Select(static clip => clip.Name).ToList();

        var result = await ReadAsync(stage);
        var diffs = Compare(names, expected, result, stage);

        Cut1bHopReceipt.Row(Hop, stage.File, new JsonObject
        {
            ["clips"] = names.Count,
            ["animationBlocks"] = expected.Count,
            ["documentTyped"] = result.Coverage.TypedCount,
            ["documentNativeOnly"] = result.Coverage.NativeOnlyCount,
            ["diffCount"] = diffs.Count,
            ["diffs"] = new JsonArray(diffs.Take(40).Select(static d => (JsonNode?)JsonValue.Create(d)).ToArray())
        });
        Assert.True(diffs.Count == 0, $"{stage}:\n  " + string.Join("\n  ", diffs.Take(40)));
    }

    [Fact]
    public async Task Control_AClipOrAReasonChanged_IsReported()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var stage = Cut1bAnimationStage.LoadControl(ControlName);
        var expected = NifModelAnimationCoverage.ClassifyFile(stage.State, stage.Result,
            TestContext.Current.CancellationToken);
        var names = stage.Result.Clips.Select(static clip => clip.Name).ToList();
        var result = await ReadAsync(stage);
        Assert.Empty(Compare(names, expected, result, stage));
        Assert.NotEmpty(names);
        Assert.NotEmpty(expected);

        var clipDiffs = Compare(names.Take(names.Count - 1).ToList(), expected, result, stage);
        var altered = expected.ToList();
        altered[0] = altered[0] with
        {
            Kind = ModelSourceCoverageKind.NativeOnly,
            Reason = "a reason no reader writes",
            Code = "controlCode"
        };
        var reasonDiffs = Compare(names, altered, result, stage);

        Cut1bHopReceipt.Control(Hop, "clipOrReasonChanged", new JsonObject
        {
            ["entry"] = stage.File.Entry,
            ["sha256"] = stage.File.Sha256,
            ["clipDetected"] = clipDiffs.Count > 0,
            ["reasonDetected"] = reasonDiffs.Count > 0,
            ["clipDiffs"] = new JsonArray(clipDiffs.Select(static d => (JsonNode?)JsonValue.Create(d)).ToArray()),
            ["reasonDiffs"] = new JsonArray(reasonDiffs.Select(static d => (JsonNode?)JsonValue.Create(d)).ToArray())
        });
        Assert.NotEmpty(clipDiffs);
        Assert.NotEmpty(reasonDiffs);
    }

    /// <summary>
    ///     Reads the stage's file through the reader contract under the stage's platform; a <c>.kf</c> with its pinned
    ///     skeleton (see the type summary).
    /// </summary>
    private static async Task<ModelReadResult> ReadAsync(Cut1bAnimationRead stage)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [BethesdaModelRegistration.PlatformOption] = stage.Platform.OptionValue
        };
        var path = VirtualPath(stage.File.Entry);
        if (!stage.IsAnimationStream)
        {
            return NifModelTestSupport.ReadWith(stage.Bytes, null, path: path, options: options);
        }

        var pin = stage.File.Skeleton!;
        var skeletonBytes = Cut1bFixtureResolver.Require(Cut1bCoverManifest.Require(pin.Sha256));
        if (string.Equals(pin.Rule, Cut1bSkeletonCompanion.WalkUpRule, StringComparison.Ordinal))
        {
            await using var companions = NifModelTestSupport.Companions((VirtualPath(pin.Entry), skeletonBytes));
            return NifModelTestSupport.ReadWith(stage.Bytes, companions.ResolveAsync, path: path, options: options);
        }

        var name = ExplicitPrefix + VirtualPath(pin.Entry);
        options[BethesdaModelRegistration.SkeletonOption] = name;
        return NifModelTestSupport.ReadWith(stage.Bytes, NifModelKfFixtures.OneFile(name, skeletonBytes), path: path,
            options: options);
    }

    /// <summary>Every difference between the reader's document and the stage's clips and classifications.</summary>
    private static List<string> Compare(IReadOnlyList<string> expectedClips,
        IReadOnlyList<NifModelAnimationClassification> expected, ModelReadResult result, Cut1bAnimationRead stage)
    {
        var diffs = new List<string>();
        var document = result.Document;
        var actualClips = document.Animations.Select(static clip => clip.Name).ToList();
        if (!actualClips.SequenceEqual(expectedClips, StringComparer.Ordinal))
        {
            diffs.Add($"clips: the stage has [{string.Join(", ", expectedClips)}], the reader " +
                      $"[{string.Join(", ", actualClips)}]");
        }

        var clipRows = document.NativeStates.Count(static row =>
            string.Equals(row.Kind, NifModelAnimationNativeState.Kind, StringComparison.Ordinal));
        if (clipRows != actualClips.Count)
        {
            diffs.Add($"{clipRows} clip rows for {actualClips.Count} clips");
        }

        foreach (var classification in expected)
        {
            var row = result.Coverage.GetClassification(NifModelCoverage.Identity(classification.Block));
            if (row.Kind == classification.Kind && string.Equals(row.Reason, classification.Reason, StringComparison.Ordinal))
            {
                continue;
            }

            var type = stage.State.Blocks[classification.Block].Type;
            var unreachable = !classification.IsTyped &&
                              string.Equals(row.Reason, NifModelCoverage.UnreachableReason, StringComparison.Ordinal);
            var paletteName = row.Kind == ModelSourceCoverageKind.Typed &&
                              stage.State.Schema.Inherits(type, "NiAVObjectPalette");
            if (!unreachable && !paletteName)
            {
                diffs.Add($"block {classification.Block} ({type}): the stage says {classification.Kind} " +
                          $"'{classification.Reason}', the reader {row.Kind} '{row.Reason}'");
            }
        }

        foreach (var row in result.Coverage.Classifications)
        {
            if (row.Reason?.Contains(RetiredReason, StringComparison.Ordinal) == true)
            {
                diffs.Add($"{row.ElementIdentity}: the retired reason '{row.Reason}'");
            }
        }

        if (stage.IsAnimationStream)
        {
            var provenance = document.NativeStates.SingleOrDefault(static row =>
                string.Equals(row.Kind, NifModelSkeletonProvenance.Kind, StringComparison.Ordinal));
            var sha = provenance is null ? null : (string?)JsonNode.Parse(provenance.PayloadJson)?["sha256"];
            if (!string.Equals(sha, stage.SkeletonSha256, StringComparison.Ordinal))
            {
                diffs.Add($"skeleton provenance SHA-256 {sha ?? "(none)"}, pinned {stage.SkeletonSha256}");
            }
        }

        return diffs;
    }

    /// <summary>The manifest entry as a Data-relative virtual path (<c>meshes/...</c>).</summary>
    private static string VirtualPath(string entry)
    {
        return entry.StartsWith(MeshesPrefix, StringComparison.OrdinalIgnoreCase) ? entry : MeshesPrefix + entry;
    }
}
