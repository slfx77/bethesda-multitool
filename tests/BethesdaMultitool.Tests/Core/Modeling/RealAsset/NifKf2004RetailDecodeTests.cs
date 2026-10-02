using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Formats.Nif.Decoding;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;
using BethesdaMultitool.Core.Formats.Nif.Schema;
using BethesdaMultitool.Core.Modeling;
using BethesdaMultitool.Core.Modeling.Nif;
using BethesdaMultitool.Tests.Core.Modeling.Nif;
using BethesdaMultitool.Tests.Helpers;
using Slfx77.Multitool.Core.Models;
using Slfx77.Multitool.Core.Models.Sources;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Modeling.RealAsset;

/// <summary>
///     Cut-2 preparation (TestOutput/cut2-prep-20260928/kf2004) and carry (kf2004-carry): the five FNV-shipped
///     20.0.0.4 <c>.kf</c> files (the cut-1b decline controls until cut 2, byte-identical across the FNV PC, PS3, X360
///     and FO3 PC Meshes BSAs, and the only five 20.0.0.4 <c>.kf</c> among the 4,316 in the FNV PC Meshes BSA) decode
///     through the one decode path:
///     <see cref="NifHeaderLayout" />'s legacy branch, <see cref="NifBlockDecoder" /> strict over every block with the
///     layout tiling to the footer, and <see cref="NifControllerSequenceNameTrackReader.TryReadOblivionSequenceView" />.
///     Every pinned fact was measured independently by the Python walker in that directory (report.txt, facts.json),
///     whose exact-tiling check the counter-shape controls there prove discriminating (a ControlledBlock without the
///     Priority byte and the 20.2.0.7 ControlledBlock shape each desync all five files).
/// </summary>
/// <remarks>
///     Controls: the 20.2.0.7 sequence view still refuses every file (as the cut-1b view oracle also asserts); a
///     corrupted BS version field must keep the decoder's refusal. Since cut 2 the model probe admits the files as
///     <c>.kf</c> streams and <see cref="NifModelReader" /> carries each end to end with the manifest's pinned skeleton
///     (<see cref="Read_CarriesTheFile_WithThePinnedSkeleton" />); the renderer's runtime reader carries their B-spline
///     tracks (<see cref="RuntimeReader_CarriesTheBsplineTracks" />).
/// </remarks>
[Trait("Category", TestCategories.BucketB)]
[Collection(SequentialIntegrationGroup.Name)]
public sealed class NifKf2004RetailDecodeTests
{
    private sealed record Expected(
        string Sha256,
        int Blocks,
        int HeaderEnd,
        string SequenceName,
        uint RawCycle,
        float StopTime,
        int TextKeysRef,
        int TextKeyCount,
        byte[] Priorities,
        (string Type, int Count)[] Census);

    private static readonly (string Type, int Count)[] BsplineCensusStill2 =
    [
        ("NiBSplineBasisData", 2), ("NiBSplineCompTransformInterpolator", 57), ("NiBSplineData", 1),
        ("NiControllerSequence", 1), ("NiStringPalette", 1), ("NiTextKeyExtraData", 1),
        ("NiTransformData", 3), ("NiTransformInterpolator", 16)
    ];

    private static readonly (string Type, int Count)[] BsplineCensusMoving =
    [
        ("NiBSplineBasisData", 2), ("NiBSplineCompTransformInterpolator", 59), ("NiBSplineData", 1),
        ("NiControllerSequence", 1), ("NiStringPalette", 1), ("NiTextKeyExtraData", 1),
        ("NiTransformData", 1), ("NiTransformInterpolator", 14)
    ];

    private static readonly Dictionary<string, Expected> Files = new(StringComparer.Ordinal)
    {
        ["meshes/characters/_male/idleanims/talk_handsatside_still2.kf"] = new(
            "ae67a97ca68d4c5ca06d1aea412ab873fa6211f906ac924f7e0dcf64c1fd5d2f", 82, 0x1A4,
            "SpecialIdle_Talk_HandsAtSide_Still2", 0u, 3f, 81, 3, [23], BsplineCensusStill2),
        ["meshes/characters/_male/idleanims/talk_handsatside_moving.kf"] = new(
            "1f9eefa220b814722068f614e6c9941aa23ce5a4660f1156967a2db555b2d213", 80, 0x1A0,
            "SpecialIdle_Talk_HandsAtSide_Moving", 0u, 3f, 79, 3, [23], BsplineCensusMoving),
        ["meshes/characters/_male/idleanims/talk_handsatside_moving2.kf"] = new(
            "1b4799551f26b02524ae9a6bf6c0066361bbf68440494ba95711baa75de3892f", 80, 0x1A0,
            "SpecialIdle_Talk_HandsAtSide_Moving2", 0u, 3f, 79, 3, [23], BsplineCensusMoving),
        ["meshes/characters/_male/idleanims/eatidle.kf"] = new(
            "b08b722812a2403f5e8c445dedc04080cf0d1e0a2401e3940f827fd2ece28b62", 95, 0x1BE,
            "SpecialIdle_eatIdle", 2u, 1.8666667f, 94, 10, [13, 20, 24, 33, 93],
            [
                ("NiBSplineBasisData", 1), ("NiBSplineCompTransformInterpolator", 16), ("NiBSplineData", 1),
                ("NiControllerSequence", 1), ("NiStringPalette", 1), ("NiTextKeyExtraData", 1),
                ("NiTransformData", 17), ("NiTransformInterpolator", 57)
            ]),
        ["meshes/characters/_male/idleanims/pistolvariant01.kf"] = new(
            "d680b381b02175de02b55f984ace8a580c94f1cce086ec596ec91546242c9a79", 142, 0x1CF,
            "SpecialIdle", 2u, 5.0000005f, 141, 3, [21],
            [
                ("NiControllerSequence", 1), ("NiStringPalette", 1), ("NiTextKeyExtraData", 1),
                ("NiTransformData", 66), ("NiTransformInterpolator", 73)
            ])
    };

    /// <summary>One theory row per file: (entry, SHA-256).</summary>
    public static TheoryData<string, string> Rows()
    {
        var rows = new TheoryData<string, string>();
        foreach (var (entry, expected) in Files)
        {
            rows.Add(entry, expected.Sha256);
        }

        return rows;
    }

    /// <summary>
    ///     The whole file decodes exactly: legacy header, every block strict and complete, blocks and footer tiling
    ///     the file with the sequence as the only root, the header census as measured, the sequence view's names,
    ///     clock and palette-resolved targets as measured, and the text keys as measured.
    /// </summary>
    [Theory]
    [MemberData(nameof(Rows))]
    public void LegacyKf_DecodesExactly(string entry, string sha256)
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var (expected, file, bytes) = Load(entry, sha256);
        var info = NifParser.Parse(bytes);
        Assert.NotNull(info);
        Assert.Equal(expected.Blocks, info.BlockCount);
        Assert.Equal(expected.Blocks, info.Blocks.Count);

        var decoder = new NifBlockDecoder(NifSchema.LoadEmbedded(), info, bytes);
        Assert.Equal(expected.HeaderEnd, decoder.Header.HeaderEnd);
        Assert.Equal(0, decoder.Header.Strings.Count);
        var footer = decoder.ValidateLayout();
        Assert.Equal(new[] { 0 }, footer.Roots.ToArray());
        for (var i = 0; i < decoder.BlockCount; i++)
        {
            var block = decoder.Decode(i, NifDecodeMode.Strict);
            Assert.True(block.IsComplete, $"{file}: block {i} ({block.Type}) did not decode completely.");
        }

        var census = info.Blocks.GroupBy(static b => b.TypeName)
            .Select(static g => (Type: g.Key, Count: g.Count()))
            .OrderBy(static g => g.Type, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expected.Census, census);

        Assert.True(NifControllerSequenceNameTrackReader.TryReadOblivionSequenceView(
            bytes, info, info.Blocks[0], out var view), $"{file}: the Oblivion sequence view refused the sequence.");
        Assert.Equal(expected.SequenceName, view.Name);
        Assert.Equal("Bip01", view.AccumRootName);
        Assert.Equal(73, view.ControlledBlocks.Length);
        Assert.Equal(1u, view.ArrayGrowBy);
        Assert.Equal(BitConverter.SingleToUInt32Bits(1f), view.WeightBits);
        Assert.Equal(expected.RawCycle, view.RawCycle);
        Assert.Equal(BitConverter.SingleToUInt32Bits(1f), view.FrequencyBits);
        Assert.Equal(BitConverter.SingleToUInt32Bits(0f), view.StartTimeBits);
        Assert.Equal(BitConverter.SingleToUInt32Bits(expected.StopTime), view.StopTimeBits);
        Assert.Equal(-1, view.ManagerRef);
        Assert.Equal(2, view.StringPaletteRef);
        Assert.Equal(expected.TextKeysRef, view.TextKeysRef);
        Assert.True(view.TailExact, $"{file}: the sequence tail does not end at the block end.");

        Assert.Equal(
            new[] { "Bip01", "Bip01 Pelvis", "Bip01 Spine", "Bip01 Spine1" },
            view.ControlledBlocks.Take(4).Select(static b => b.NodeName).ToArray());
        Assert.Equal("Bip01 NonAccum", view.ControlledBlocks[^1].NodeName);
        Assert.All(view.ControlledBlocks, static b => Assert.Equal("NiTransformController", b.ControllerType));
        Assert.Equal(
            expected.Priorities,
            view.ControlledBlocks.Select(static b => b.Priority).Distinct().Order().ToArray());

        Assert.True(NifTextKeyReader.TryReadExact(
            bytes, info, info.Blocks[expected.TextKeysRef], out var textKeys),
            $"{file}: the text keys did not read exactly.");
        Assert.Equal(expected.TextKeyCount, textKeys.Length);
        Assert.Equal(0f, textKeys[0].Time);
        Assert.Equal("start", textKeys[0].Label);
        Assert.Equal(expected.StopTime, textKeys[^1].Time);
        Assert.Equal("end", textKeys[^1].Label);
    }

    /// <summary>
    ///     The B-spline census of eatidle.kf, pinned block by block: the measured control-point counts of the one
    ///     NiBSplineData/NiBSplineBasisData pair, and the first sampled NiTransformData's key counts.
    /// </summary>
    [Fact]
    public void EatIdle_PinsTheSplineAndKeyCounts()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var entry = "meshes/characters/_male/idleanims/eatidle.kf";
        var (_, file, bytes) = Load(entry, Files[entry].Sha256);
        var info = NifParser.Parse(bytes);
        Assert.NotNull(info);
        var decoder = new NifBlockDecoder(NifSchema.LoadEmbedded(), info, bytes);

        Assert.Equal("NiTransformData", info.Blocks[6].TypeName);
        var transformData = decoder.Decode(6, NifDecodeMode.Strict).Root;
        Assert.Equal(55, transformData.Get<NifIntegerValue>("Num Rotation Keys").Value);
        Assert.Equal(1, transformData.Get<NifIntegerValue>("Rotation Type").Value);

        Assert.Equal("NiBSplineData", info.Blocks[12].TypeName);
        var splineData = decoder.Decode(12, NifDecodeMode.Strict).Root;
        Assert.Equal(0, splineData.Get<NifIntegerValue>("Num Float Control Points").Value);
        Assert.Equal(3712, splineData.Get<NifIntegerValue>("Num Compact Control Points").Value);

        Assert.Equal("NiBSplineBasisData", info.Blocks[13].TypeName);
        var basisData = decoder.Decode(13, NifDecodeMode.Strict).Root;
        Assert.Equal(58, basisData.Get<NifIntegerValue>("Num Control Points").Value);
    }

    /// <summary>Control: the 20.2.0.7 sequence view must keep refusing every one of the five files.</summary>
    [Theory]
    [MemberData(nameof(Rows))]
    public void ModernSequenceView_StillRefuses(string entry, string sha256)
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var (_, _, bytes) = Load(entry, sha256);
        var info = NifParser.Parse(bytes);
        Assert.NotNull(info);
        Assert.False(NifControllerSequenceNameTrackReader.TryReadSequenceView(bytes, info, info.Blocks[0], out _));
    }

    /// <summary>Control: a corrupted BS version field keeps the decoder's refusal on the real bytes.</summary>
    [Fact]
    public void CorruptedBsVersion_StillDeclines()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var entry = "meshes/characters/_male/idleanims/talk_handsatside_still2.kf";
        var (_, _, bytes) = Load(entry, Files[entry].Sha256);
        var corrupted = (byte[])bytes.Clone();
        var newline = Array.IndexOf(corrupted, (byte)0x0A);
        corrupted[newline + 14] = 12; // the BS Version field: version(4) + endian(1) + user(4) + blocks(4) after the line
        var info = NifParser.Parse(corrupted);
        Assert.NotNull(info);
        Assert.Equal(12u, info.BsVersion);
        Assert.Throws<NotSupportedException>(() =>
            new NifBlockDecoder(NifSchema.LoadEmbedded(), info, corrupted));
    }

    /// <summary>
    ///     Cut 2: the model probe admits the five files as <c>.kf</c> animation streams (Supported, Confirmed, the .kf
    ///     evidence with the file's own user version), and the manifest carries them as rows the reader reads, each
    ///     with a pinned skeleton. Control: the same bytes with the endian byte flipped to big-endian are outside the
    ///     key and stay later-cut(2).
    /// </summary>
    [Theory]
    [MemberData(nameof(Rows))]
    public void Probe_AdmitsTheFile(string entry, string sha256)
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var (_, file, bytes) = Load(entry, sha256);
        Assert.False(file.IsDeclinedControl, $"{file}: still a manifest decline control; the cut-2 carry flips it.");
        Assert.True(file.NeedsSkeleton, $"{file}: not a .kf the reader reads.");
        Assert.NotNull(file.Skeleton);
        var info = NifParser.Parse(bytes);
        Assert.NotNull(info);

        var result = NifModelTestSupport.Probe(bytes);
        Assert.Equal(ModelProbeKind.Supported, result.Kind);
        Assert.Equal(ModelProbeConfidence.Confirmed, result.Confidence);
        Assert.Null(result.Reason);
        Assert.Equal($"NIF 20.0.0.4, user {info.UserVersion}, BS 11, little-endian{NifModelProbe.AnimationStreamSuffix}",
            result.Evidence!.Description);

        var bigEndian = (byte[])bytes.Clone();
        bigEndian[Array.IndexOf(bigEndian, (byte)0x0A) + 5] = 0;
        var control = NifModelTestSupport.Probe(bigEndian);
        Assert.Equal(ModelProbeKind.Unsupported, control.Kind);
        Assert.StartsWith(NifModelProbe.LaterCutTwoCategory, control.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Cut 2, end to end: <see cref="NifModelReader" /> reads each file with the manifest's pinned skeleton (given as
    ///     the explicit skeleton) into a document whose one clip is the sequence, with the measured clock, the measured
    ///     text keys as events, weight 1 and the accumulation root 'Bip01' bound to a node, transform tracks on skeleton
    ///     nodes only, every block Typed or NativeOnly with a reason (the NiStringPalette Typed through the bindings, the
    ///     sequence and its text keys Typed), the clip row carrying the stored palette offsets, and the skeleton
    ///     provenance naming every distinct target once. Control: a skeleton with no Bip01 node keeps every track native
    ///     and the read refuses the file for want of an expressible clip.
    /// </summary>
    [Theory]
    [MemberData(nameof(Rows))]
    public void Read_CarriesTheFile_WithThePinnedSkeleton(string entry, string sha256)
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var (expected, file, bytes) = Load(entry, sha256);
        Assert.NotNull(file.Skeleton);
        var skeletonBytes = Cut1bFixtureResolver.Require(Cut1bCoverManifest.Require(file.Skeleton.Sha256));
        var skeletonName = "pinned/" + file.Skeleton.Entry;
        var options = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [BethesdaModelRegistration.SkeletonOption] = skeletonName
        };
        var info = NifParser.Parse(bytes);
        Assert.NotNull(info);
        Assert.True(NifControllerSequenceNameTrackReader.TryReadOblivionSequenceView(bytes, info, info.Blocks[0], out var view));
        var targets = view.ControlledBlocks.Select(static b => b.NodeName!).Distinct(StringComparer.Ordinal).ToList();

        var result = NifModelTestSupport.ReadWith(bytes, NifModelKfFixtures.OneFile(skeletonName, skeletonBytes),
            path: entry, options: options);

        var document = result.Document;
        var clip = Assert.Single(document.Animations);
        Assert.Equal(expected.SequenceName, clip.Name);
        var clock = Assert.IsType<SceneAnimationClock>(clip.Clock);
        Assert.Equal(1f, clock.Frequency);
        Assert.Equal(0f, clock.StartSeconds);
        Assert.Equal(expected.StopTime, clock.StopSeconds);
        Assert.Equal(expected.RawCycle == 0u ? SceneAnimationCycle.Loop : SceneAnimationCycle.Clamp, clock.Cycle);
        Assert.Equal(expected.TextKeyCount, clip.Events.Count);
        Assert.Equal(("start", 0f), (clip.Events[0].Text, clip.Events[0].TimeSeconds));
        Assert.Equal(("end", expected.StopTime), (clip.Events[^1].Text, clip.Events[^1].TimeSeconds));
        var policy = Assert.IsType<SceneAnimationSourcePolicy>(clip.SourcePolicy);
        Assert.Equal(1f, policy.Weight);
        Assert.Equal("Bip01", policy.AccumulationRoot!.SourceName);
        Assert.NotNull(policy.AccumulationRoot.NodeIndex);
        Assert.NotEmpty(clip.TransformTracks);
        Assert.All(clip.TransformTracks, track => Assert.InRange(track.NodeIndex, 0, document.Nodes.Count - 1));

        var coverage = result.Coverage;
        Assert.Equal(expected.Blocks, coverage.TotalCount);
        Assert.Equal(0, coverage.DroppedCount);
        Assert.Equal(ModelSourceCoverageKind.Typed, coverage.GetClassification("block:0").Kind);
        Assert.Equal(ModelSourceCoverageKind.Typed, coverage.GetClassification("block:2").Kind);
        Assert.Equal(ModelSourceCoverageKind.Typed, coverage.GetClassification($"block:{expected.TextKeysRef}").Kind);
        Assert.All(coverage.Classifications, c => Assert.True(
            c.Kind == ModelSourceCoverageKind.Typed || !string.IsNullOrWhiteSpace(c.Reason),
            $"{file}: {c.ElementIdentity} is {c.Kind} without a reason."));

        var extras = JsonNode.Parse(clip.ExtrasJson!)!.AsObject()[NifModelAnimationExtras.Key]!.AsObject();
        var palette = extras["stringPalette"]!.AsObject();
        Assert.Equal(2, (int)palette["sequencePalette"]!);
        var stored = palette["controlledBlocks"]!.AsArray();
        Assert.Equal(73, stored.Count);
        Assert.Equal(0u, (uint)stored[0]!["nodeNameOffset"]!);
        Assert.All(stored, b => Assert.Equal(2, (int)b!["palette"]!));
        Assert.Equal(73, extras["controlledBlocks"]!.AsArray().Count);
        Assert.All(extras["controlledBlocks"]!.AsArray(),
            b => Assert.Equal("NiTransformController", (string)b!["controllerType"]!));

        var provenance = JsonNode.Parse(Assert.Single(NifModelTestSupport.Rows(document, NifModelSkeletonProvenance.Kind))
            .PayloadJson)!.AsObject();
        var matched = provenance["matched"]!.AsArray().Select(static n => (string)n!["text"]!).ToList();
        var unmatched = provenance["unmatched"]!.AsArray().Select(static n => (string)n!["name"]!["text"]!).ToList();
        Assert.NotEmpty(matched);
        Assert.Equal(targets.OrderBy(static t => t, StringComparer.Ordinal),
            matched.Concat(unmatched).OrderBy(static t => t, StringComparer.Ordinal));
        SceneValidation.ValidateStructure(document);

        var other = new NifTestFileBuilder(false, 34);
        NifModelTestSupport.AddNode(other, other.AddString("Unrelated Root"), []);
        var refusal = Assert.Throws<NotSupportedException>(() => NifModelTestSupport.ReadWith(bytes,
            NifModelKfFixtures.OneFile(skeletonName, other.Build()), path: entry, options: options));
        Assert.StartsWith(NifModelAnimationStreamReader.NoClipExpressibleReason, refusal.Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    ///     Cut 2: the renderer's runtime reader carries the B-spline transform tracks of the five files as it does on a
    ///     20.2.0.7 stream, one per NiBSplineCompTransformInterpolator of the header census, counting none of them
    ///     unsupported; the NiTransformInterpolator tracks are read as before. pistolvariant01.kf, with no B-spline
    ///     block, is the control that the count is the census's and not a constant.
    /// </summary>
    [Theory]
    [MemberData(nameof(Rows))]
    public void RuntimeReader_CarriesTheBsplineTracks(string entry, string sha256)
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var (expected, file, bytes) = Load(entry, sha256);
        var info = NifParser.Parse(bytes);
        Assert.NotNull(info);
        var splines = expected.Census.SingleOrDefault(static c => c.Type == "NiBSplineCompTransformInterpolator").Count;

        var clip = Assert.Single(NifControllerSequenceNameTrackReader.ReadAll(bytes, info));

        Assert.Equal(expected.SequenceName, clip.Name);
        Assert.Equal(0, clip.UnsupportedTransformTrackCount);
        Assert.Equal(splines, clip.BsplineTracks?.Length ?? 0);
        Assert.True(clip.Tracks.Length + splines <= 73, $"{file}: {clip.Tracks.Length} tracks and {splines} splines.");
        Assert.All(clip.BsplineTracks ?? [], static track => Assert.StartsWith("Bip01", track.NodeName, StringComparison.Ordinal));
    }

    private static (Expected Expected, Cut1bCoverFile File, byte[] Bytes) Load(string entry, string sha256)
    {
        var expected = Files[entry];
        Assert.Equal(expected.Sha256, sha256);
        var file = Cut1bCoverManifest.Require(sha256);
        Assert.Equal(entry, file.Entry);
        var bytes = Cut1bFixtureResolver.Require(file);
        Assert.Equal(file.Size, bytes.LongLength);
        return (expected, file, bytes);
    }
}
