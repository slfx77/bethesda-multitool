using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;
using BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Animation;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Modeling.RealAsset;

/// <summary>
///     Bucket B, hop A0 on the extended runtime readers (cut-1b slice 1, owner ruling D5): for every file of the cut-1b
///     cover manifest, the lossless views decode every payload and the slice-1 animation facts exactly as the independent
///     Python probe recorded them (<see cref="NifAnimationViewOracle" />), bit for bit; the renderer's key-group
///     projections return exactly what the pre-view reader returned on every retail key-data block; and RE-17 leaves
///     every retail LINEAR, CONST and TBC rotation key unit and sign-aligned.
/// </summary>
/// <remarks>
///     <para>
///         Controls that must fail, each on the file the plan names for it: TBC continuity and bias exchanged
///         (<c>sneak2hhattackspin.kf</c> block 85), quaternion components read X, Y, Z, W instead of W, X, Y, Z
///         (<c>h2hrecoil.kf</c>), Forward and Backward exchanged (<c>libertyprime/talking.kf</c> block 2, signed zeros
///         only a bit comparison sees), and one compact B-spline short changed in the bytes
///         (<c>nightstalker/h2hattackleft.kf</c>, whose NiBSplineData holds 6,615 compact points). The
///         negative control: the TBC exchange passes on a file whose TBC parameters are all zero, which shows only groups
///         with continuity != bias can discriminate it.
///     </para>
///     <para>
///         Cut 2: the five FNV 20.0.0.4 <c>.kf</c> (the former decline controls) are compared like every other row,
///         their sequences through the Oblivion view and their NiStringPalette through the palette view;
///         <see cref="LegacyKfRows_TheOblivionViewReadsWhatTheModernViewRefuses" /> pins that the two version-keyed
///         sequence views discriminate on them. A container absent from this machine skips its row.
///     </para>
/// </remarks>
[Trait("Category", TestCategories.BucketB)]
[Collection(SequentialIntegrationGroup.Name)]
public sealed class NifAnimationViewOracleTests
{
    private const string TbcOrderControl = "TBC continuity != bias, rotation (A0 order control)";
    private const string TbcAllZeroControl = "TBC rotation (all parameters zero)";
    private const string ChainControl = "dot < 0, unit, clip types whole";
    private const string SignedZeroTangentControl = "Forward != Backward, signed zero only (A0 only)";
    private const string CompactBsplineControl =
        "existing Bucket-B B-spline file (FnvNightstalkerBsplineAnimationRetailTests)";
    private const string NonUnitControl = "Non-unit quaternion";

    /// <summary>One theory row per manifest file the probe walked (every file but a decline control, of which there is none since cut 2).</summary>
    public static TheoryData<string, string> WalkedRows()
    {
        var rows = new TheoryData<string, string>();
        foreach (var file in Cut1bCoverManifest.Files)
        {
            if (!file.IsDeclinedControl)
            {
                rows.Add(file.Entry, file.Sha256);
            }
        }

        return rows;
    }

    /// <summary>One theory row per 20.0.0.4 manifest file (the five FNV .kf carried since cut 2).</summary>
    public static TheoryData<string, string> LegacyKfRows()
    {
        var rows = new TheoryData<string, string>();
        foreach (var file in Cut1bCoverManifest.Files)
        {
            if (file.Key.StartsWith("20.0.0.4/", StringComparison.Ordinal))
            {
                rows.Add(file.Entry, file.Sha256);
            }
        }

        return rows;
    }

    [Theory]
    [MemberData(nameof(Cut1bCoverManifest.Rows), MemberType = typeof(Cut1bCoverManifest))]
    public void EveryManifestFile_ViewsMatchTheProbeBitForBit(string entry, string sha256)
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var (file, record, bytes) = Load(entry, sha256);
        Assert.False(file.IsDeclinedControl, $"{file}: a decline control; the cut-1b manifest carries none since cut 2.");
        Assert.Null(record["declined"]);

        var report = NifAnimationViewOracle.Compare(bytes, record, NifAnimationViewJsonOptions.Faithful);

        Assert.True(report.Diffs.Count == 0, report.Describe(file));
        Assert.True(report.ExpectedPayloads == report.ComparedPayloads, report.Describe(file));

        // Every fact block was either compared or is of a type declared out of scope; nothing else may be skipped.
        Assert.True(report.ExpectedAnimationBlocks == report.ComparedAnimationBlocks + report.NotCompared.Values.Sum(),
            report.Describe(file));
        Assert.True(report.NotCompared.Keys.All(NifAnimationViewOracle.OutOfScopeFactTypes.Contains),
            report.Describe(file));
    }

    [Theory]
    [MemberData(nameof(WalkedRows))]
    public void EveryKeyDataBlock_ProjectsTheSameRendererValuesAsTheLegacyReader(string entry, string sha256)
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var (file, _, bytes) = Load(entry, sha256);
        var nif = Parse(file, bytes);
        var be = nif.IsBigEndian;
        var compared = 0;
        foreach (var block in nif.Blocks)
        {
            var start = block.DataOffset;
            var end = block.DataOffset + block.Size;
            switch (block.TypeName)
            {
                case "NiTransformData":
                case "NiKeyframeData":
                {
                    var rotation = Check(file, block,
                        NifKeyGroupProjectionSignatures.Quat(bytes, start, end, be, nif.BinaryVersion));
                    var translation = Check(file, block,
                        NifKeyGroupProjectionSignatures.Vector3(bytes, rotation.Position, end, be));
                    Check(file, block, NifKeyGroupProjectionSignatures.Float(bytes, translation.Position, end, be));
                    compared++;
                    break;
                }
                case "NiFloatData":
                    Check(file, block, NifKeyGroupProjectionSignatures.Float(bytes, start, end, be));
                    compared++;
                    break;
                case "NiPosData":
                    Check(file, block, NifKeyGroupProjectionSignatures.Vector3(bytes, start, end, be));
                    compared++;
                    break;
            }
        }

        var expectedBlocks = nif.Blocks.Count(static block =>
            block.TypeName is "NiTransformData" or "NiKeyframeData" or "NiFloatData" or "NiPosData");
        Assert.Equal(expectedBlocks, compared);
    }

    [Theory]
    [MemberData(nameof(WalkedRows))]
    public void EveryRotationGroup_IsUnitAndSignAlignedAfterTheEngineRule(string entry, string sha256)
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var (file, _, bytes) = Load(entry, sha256);
        var nif = Parse(file, bytes);
        foreach (var block in nif.Blocks.Where(static block => NifKeyframeDataTrackReader.IsTrackDataBlock(block.TypeName)))
        {
            Assert.True(NifKeyframeDataTrackReader.TryReadView(bytes, nif, block, out var view),
                $"{file}: block {block.Index} does not read.");
            var keys = view.Rotation.Keys;
            if (view.Rotation.IsEuler || keys.Count == 0 || !NifRotationKeyEngineRule.AppliesTo(keys.KeyType))
            {
                continue;
            }

            Assert.True(NifRotationKeyEngineRule.TryApply(keys, out var result));
            for (var index = 0; index < result.Values.Length; index++)
            {
                Assert.True(IsUnitRotation(result.Values[index]),
                    $"{file}: block {block.Index} key {index} is not unit after the rule.");
                if (index > 0)
                {
                    Assert.True(SignAligned(result.Values[index - 1], result.Values[index]),
                        $"{file}: block {block.Index} keys {index - 1} and {index} have a negative dot after the rule.");
                }
            }
        }
    }

    [Fact]
    public void Control_TbcContinuityAndBiasExchanged_FailsOnSneak2hhAttackSpinBlock85()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var (file, record, bytes) = LoadControl(TbcOrderControl);

        var faithful = NifAnimationViewOracle.Compare(bytes, record, NifAnimationViewJsonOptions.Faithful);
        Assert.True(faithful.Diffs.Count == 0, faithful.Describe(file));
        Assert.True(faithful.TbcKeysWithContinuityNotBias > 0, faithful.Describe(file));

        var exchanged = NifAnimationViewOracle.Compare(bytes, record,
            new NifAnimationViewJsonOptions(SwapContinuityAndBias: true));
        Assert.Contains(exchanged.Diffs, static diff =>
            diff.StartsWith("payload 85 NiTransformData.rotation.keys[", StringComparison.Ordinal) &&
            diff.Contains("].tbcBits[1]", StringComparison.Ordinal));
    }

    [Fact]
    public void NegativeControl_TbcExchangePassesWhereEveryParameterIsZero()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var (file, record, bytes) = LoadControl(TbcAllZeroControl);

        var exchanged = NifAnimationViewOracle.Compare(bytes, record,
            new NifAnimationViewJsonOptions(SwapContinuityAndBias: true));

        Assert.True(exchanged.TbcKeys > 0, exchanged.Describe(file));
        Assert.Equal(0, exchanged.TbcKeysWithContinuityNotBias);
        Assert.True(exchanged.Diffs.Count == 0, exchanged.Describe(file));
    }

    [Fact]
    public void Control_QuaternionComponentsReadXyzw_FailOnH2hRecoil()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var (file, record, bytes) = LoadControl(ChainControl);

        var faithful = NifAnimationViewOracle.Compare(bytes, record, NifAnimationViewJsonOptions.Faithful);
        Assert.True(faithful.Diffs.Count == 0, faithful.Describe(file));

        var reordered = NifAnimationViewOracle.Compare(bytes, record,
            new NifAnimationViewJsonOptions(QuaternionXyzw: true));
        Assert.Contains(reordered.Diffs, static diff =>
            diff.StartsWith("payload 15 NiTransformData.rotation.keys[", StringComparison.Ordinal) &&
            diff.Contains("].valueBits[", StringComparison.Ordinal));
    }

    [Fact]
    public void Control_ForwardAndBackwardExchanged_FailOnLibertyPrimeTalkingBlock2()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var (file, record, bytes) = LoadControl(SignedZeroTangentControl);

        var faithful = NifAnimationViewOracle.Compare(bytes, record, NifAnimationViewJsonOptions.Faithful);
        Assert.True(faithful.Diffs.Count == 0, faithful.Describe(file));

        var exchanged = NifAnimationViewOracle.Compare(bytes, record,
            new NifAnimationViewJsonOptions(SwapForwardAndBackward: true));
        Assert.Contains(exchanged.Diffs, static diff =>
            diff.StartsWith("payload 2 NiPosData.keys[", StringComparison.Ordinal) &&
            diff.Contains("].forwardBits[", StringComparison.Ordinal));
    }

    [Fact]
    public void Control_OneCompactBsplineShortChanged_FailsExactlyThere()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var (file, record, bytes) = LoadControl(CompactBsplineControl);
        var nif = Parse(file, bytes);
        var store = nif.Blocks.First(block =>
            NifBsplineTransformReader.TryReadDataView(bytes, nif, block, out var candidate) &&
            candidate.CompactControlPointCount > 0);
        Assert.True(NifBsplineTransformReader.TryReadDataView(bytes, nif, store, out var view));

        var changed = (byte[])bytes.Clone();
        var offset = view.CompactControlPointsOffset;
        var original = view.CompactControlPoint(0);
        var replacement = original == short.MaxValue ? (short)(original - 1) : (short)(original + 1);
        if (nif.IsBigEndian)
        {
            BinaryPrimitives.WriteInt16BigEndian(changed.AsSpan(offset), replacement);
        }
        else
        {
            BinaryPrimitives.WriteInt16LittleEndian(changed.AsSpan(offset), replacement);
        }

        var report = NifAnimationViewOracle.Compare(changed, record, NifAnimationViewJsonOptions.Faithful);

        var diff = Assert.Single(report.Diffs);
        Assert.StartsWith($"payload {store.Index} NiBSplineData.compactControlPoints[0]:", diff,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Control_RawNonUnitKeysFailTheUnitCheckTheRuleRepairs()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var (file, _, bytes) = LoadControl(NonUnitControl);
        var nif = Parse(file, bytes);
        var rawFailures = 0;
        var ruleFailures = 0;
        foreach (var block in nif.Blocks.Where(static block => NifKeyframeDataTrackReader.IsTrackDataBlock(block.TypeName)))
        {
            Assert.True(NifKeyframeDataTrackReader.TryReadView(bytes, nif, block, out var view));
            var keys = view.Rotation.Keys;
            if (!NifRotationKeyEngineRule.TryApply(keys, out var result))
            {
                continue;
            }

            for (var index = 0; index < keys.Count; index++)
            {
                var raw = new Quaternion(keys.Value(index, 1), keys.Value(index, 2), keys.Value(index, 3),
                    keys.Value(index, 0));
                rawFailures += IsUnitRotation(raw) ? 0 : 1;
                ruleFailures += IsUnitRotation(result.Values[index]) ? 0 : 1;
            }
        }

        Assert.True(rawFailures > 0, $"{file}: no raw key fails the unit check, so the control shows nothing.");
        Assert.Equal(0, ruleFailures);
    }

    [Fact]
    public void Control_ChainAgainstPairwise_OnH2hRecoilBlock15()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var (file, _, bytes) = LoadControl(ChainControl);
        var nif = Parse(file, bytes);
        Assert.True(NifKeyframeDataTrackReader.TryReadView(bytes, nif, nif.Blocks[15], out var view));
        var keys = view.Rotation.Keys;
        Assert.Equal(51, keys.Count);

        Assert.True(NifRotationKeyEngineRule.TryApply(keys, out var result));

        var pairwise = 0;
        for (var index = 0; index + 1 < keys.Count; index++)
        {
            var dot = NifRotationKeyEngineRule.ChainDot(
                keys.Value(index, 0), keys.Value(index, 1), keys.Value(index, 2), keys.Value(index, 3),
                keys.Value(index + 1, 0), keys.Value(index + 1, 1), keys.Value(index + 1, 2), keys.Value(index + 1, 3));
            pairwise += dot < 0f ? 1 : 0;
        }

        Assert.Equal(1, pairwise);
        Assert.Equal(23, result.NegatedCount);
    }

    private static (Cut1bCoverFile File, JsonObject Record, byte[] Bytes) Load(string entry, string sha256)
    {
        var file = Cut1bCoverManifest.Require(sha256);
        Assert.Equal(entry, file.Entry);
        var record = Cut1bProbeExpectations.Require(file);
        var bytes = Cut1bFixtureResolver.Require(file);
        return (file, record, bytes);
    }

    private static (Cut1bCoverFile File, JsonObject Record, byte[] Bytes) LoadControl(string control)
    {
        var file = Cut1bCoverManifest.RequireControl(control);
        return Load(file.Entry, file.Sha256);
    }

    private static NifInfo Parse(Cut1bCoverFile file, byte[] bytes)
    {
        var nif = NifParser.Parse(bytes);
        Assert.True(nif is not null, $"{file}: NifParser could not parse the file.");
        return nif;
    }

    /// <summary>
    ///     Cut 2, the five 20.0.0.4 rows: the probe walked them (payloads and a sequence fact are present, no decline),
    ///     the Oblivion sequence view reads every sequence exactly with the palette-resolved Controller Type the binder
    ///     tests for, and, the control, the 20.2.0.7 sequence view still refuses every one of those sequences (the two
    ///     version-keyed views discriminate on retail bytes).
    /// </summary>
    [Theory]
    [MemberData(nameof(LegacyKfRows))]
    public void LegacyKfRows_TheOblivionViewReadsWhatTheModernViewRefuses(string entry, string sha256)
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var (file, record, bytes) = Load(entry, sha256);
        Assert.False(file.IsDeclinedControl, $"{file}: still a decline control.");
        Assert.True(file.NeedsSkeleton, $"{file}: not a .kf the reader reads.");
        Assert.Null(record["declined"]);
        Assert.NotEmpty(record["payloads"]!.AsArray());
        var nif = Parse(file, bytes);
        Assert.Equal(NifVersions.Gamebryo20004, nif.BinaryVersion);
        var sequences = nif.Blocks.Where(static block => block.TypeName == "NiControllerSequence").ToList();
        Assert.NotEmpty(sequences);
        foreach (var block in sequences)
        {
            Assert.True(NifControllerSequenceNameTrackReader.TryReadOblivionSequenceView(bytes, nif, block, out var view),
                $"{file}: the Oblivion view refused block {block.Index}.");
            Assert.True(view.TailExact, $"{file}: block {block.Index} does not end exactly.");
            Assert.All(view.ControlledBlocks, static controlled =>
                Assert.Equal("NiTransformController", controlled.ControllerType));
            Assert.False(NifControllerSequenceNameTrackReader.TryReadSequenceView(bytes, nif, block, out _),
                $"{file}: the 20.2.0.7 view read a 20.0.0.4 sequence.");
        }
    }

    private static NifKeyGroupProjectionComparison Check(
        Cut1bCoverFile file, BlockInfo block, NifKeyGroupProjectionComparison comparison)
    {
        Assert.True(comparison.Matches,
            $"{file}: block {block.Index} {block.TypeName}\nlegacy {comparison.Legacy}\nview   {comparison.View}");
        return comparison;
    }

    /// <summary>Shared's Float32 unit check: |X*X + Y*Y + Z*Z + W*W - 1| &lt;= 1e-4, in Float32.</summary>
    private static bool IsUnitRotation(Quaternion value)
    {
        var squared = value.X * value.X + value.Y * value.Y;
        squared += value.Z * value.Z;
        squared += value.W * value.W;
        return MathF.Abs(squared - 1f) <= 0.0001f;
    }

    /// <summary>A non-negative dot in both the sequential X, Y, Z, W order and the pairwise order a vector dot uses.</summary>
    private static bool SignAligned(Quaternion a, Quaternion b)
    {
        var sequential = a.X * b.X + a.Y * b.Y;
        sequential += a.Z * b.Z;
        sequential += a.W * b.W;
        var pairwise = (a.X * b.X + a.Y * b.Y) + (a.Z * b.Z + a.W * b.W);
        return sequential >= 0f && pairwise >= 0f;
    }
}
