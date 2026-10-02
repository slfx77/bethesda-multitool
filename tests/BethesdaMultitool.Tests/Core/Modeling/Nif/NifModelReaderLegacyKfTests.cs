using System.Security.Cryptography;
using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Modeling;
using BethesdaMultitool.Core.Modeling.Nif;
using BethesdaMultitool.Tests.Helpers;
using Slfx77.Multitool.Core.Models;
using Slfx77.Multitool.Core.Models.Sources;
using Xunit;
using static BethesdaMultitool.Tests.Core.Modeling.Nif.NifModelKfFixtures;
using static BethesdaMultitool.Tests.Core.Modeling.Nif.NifModelTestSupport;

namespace BethesdaMultitool.Tests.Core.Modeling.Nif;

/// <summary>
///     Cut 2, the 20.0.0.4 <c>.kf</c> carry (TestOutput/cut2-prep-20260928/kf2004-carry): <see cref="NifModelReader" />
///     reads the synthetic little-endian 20.0.0.4 <c>.kf</c> (<see cref="NifKf2004Fixtures" />, the retail form with
///     'NiTransformController' in the palette) end to end through the same <c>.kf</c> path as a 20.2.0.7 stream: the
///     skeleton by the <c>bmt.skeleton</c> option or the walk-up, the sequence through the Oblivion view mapped onto the
///     reader's synthesized string table, the clock, curve, event and policy mappings unchanged, the clip extras and
///     native row carrying the stored palette offsets, and the NiStringPalette Typed through the binding.
/// </summary>
/// <remarks>
///     Every test carries a control that fails: the default fixture (no Controller Type in the palette) is admitted by the
///     probe but yields no expressible clip; a skeleton without the target keeps the track native and the read refuses it
///     the same way; the 20.0.0.4 scene graph of the same identity is refused with later-cut(2) at the read, as is a
///     stream of that identity with a block type outside the eight the reader measured; and a 20.2.0.7 <c>.kf</c> read
///     through the same path carries no palette member in its extras, so the legacy record is the legacy stream's alone.
/// </remarks>
public sealed class NifModelReaderLegacyKfTests
{
    private const string KfPath = "meshes/characters/_male/idleanims/idle2004.kf";
    private const string NearSkeleton = "meshes/characters/_male/skeleton.nif";
    private const string ExplicitSkeleton = "skel/custom.nif";

    /// <summary>
    ///     With <c>bmt.skeleton</c> the document is the skeleton's nodes plus the one clip 'Idle2004': translation and
    ///     rotation Keyed from the NiTransformData (one key each), scale Constant from the static transform, all on node 0
    ///     ('Bip01'), the CLAMP clock 0 to 1 at frequency 1, the two events, weight 1 and the accumulation root bound to
    ///     node 0. Every block of the five is Typed, the NiStringPalette through the binding. The clip row and extras carry
    ///     the stored palette offsets and the synthesized indices. Control: a skeleton whose nodes carry other names keeps
    ///     the track native and the read is refused for want of an expressible clip.
    /// </summary>
    [Fact]
    public void ExplicitSkeleton_ReadsTheLegacyKfEndToEnd()
    {
        var kf = NifKf2004Fixtures.Build(controllerType: NifKf2004Fixtures.TransformControllerType);
        var skeleton = Skeleton(false);
        var options = SkeletonOptions(ExplicitSkeleton);

        var result = ReadWith(kf, OneFile(ExplicitSkeleton, skeleton), path: KfPath, options: options);

        var document = result.Document;
        Assert.Equal(new[] { RootName, PelvisName, SpineName }, document.Nodes.Select(static node => node.Name));
        Assert.Empty(document.Meshes);
        var clip = Assert.Single(document.Animations);
        Assert.Equal(NifKf2004Fixtures.SequenceName, clip.Name);
        Assert.Equal(3, clip.TransformTracks.Count);
        Assert.All(clip.TransformTracks, static track => Assert.Equal(0, track.NodeIndex));
        var translation = Assert.Single(clip.TransformTracks, static t => t.Property == SceneTransformProperty.Translation);
        var rotation = Assert.Single(clip.TransformTracks, static t => t.Property == SceneTransformProperty.Rotation);
        var scale = Assert.Single(clip.TransformTracks, static t => t.Property == SceneTransformProperty.Scale);
        Assert.Equal(SceneAnimationChannelState.Keyed, translation.State);
        Assert.Equal(SceneAnimationChannelState.Keyed, rotation.State);
        Assert.Equal(SceneAnimationChannelState.Constant, scale.State);
        Assert.Equal(new[] { 0f }, translation.Times);

        var clock = Assert.IsType<SceneAnimationClock>(clip.Clock);
        Assert.Equal(1f, clock.Frequency);
        Assert.Equal(0f, clock.StartSeconds);
        Assert.Equal(1f, clock.StopSeconds);
        Assert.Equal(SceneAnimationCycle.Clamp, clock.Cycle);
        Assert.Equal(new[] { ("start", 0f), ("end", 1f) },
            clip.Events.Select(static e => (e.Text, e.TimeSeconds)));
        var policy = Assert.IsType<SceneAnimationSourcePolicy>(clip.SourcePolicy);
        Assert.Equal(1f, policy.Weight);
        Assert.Equal(NifKf2004Fixtures.TargetName, policy.AccumulationRoot!.SourceName);
        Assert.Equal(0, policy.AccumulationRoot.NodeIndex);

        Assert.Equal(5, result.Coverage.TotalCount);
        Assert.Equal(0, result.Coverage.DroppedCount);
        for (var block = 0; block < 5; block++)
        {
            Assert.Equal(ModelSourceCoverageKind.Typed, result.Coverage.GetClassification($"block:{block}").Kind);
        }

        var extras = JsonNode.Parse(clip.ExtrasJson!)!.AsObject()[NifModelAnimationExtras.Key]!.AsObject();
        var stringPalette = extras["stringPalette"]!.AsObject();
        Assert.Equal(NifModelAnimationExtras.SynthesizedStringTableNote, (string)stringPalette["stringTable"]!);
        Assert.Equal(2, (int)stringPalette["sequencePalette"]!);
        var storedBlock = Assert.Single(stringPalette["controlledBlocks"]!.AsArray())!.AsObject();
        Assert.Equal(2, (int)storedBlock["palette"]!);
        Assert.Equal(0u, (uint)storedBlock["nodeNameOffset"]!);
        Assert.Equal((uint)(NifKf2004Fixtures.TargetName.Length + 1), (uint)storedBlock["controllerTypeOffset"]!);
        Assert.Equal(uint.MaxValue, (uint)storedBlock["propertyTypeOffset"]!);
        var controlled = Assert.Single(extras["controlledBlocks"]!.AsArray())!.AsObject();
        Assert.Equal(NifKf2004Fixtures.TargetName, (string)controlled["nodeName"]!);
        Assert.Equal(NifKf2004Fixtures.TransformControllerType, (string)controlled["controllerType"]!);
        Assert.Null(controlled["propertyType"]);
        Assert.Equal(-1, (int)controlled["propertyTypeIndex"]!);
        Assert.Equal(7, (int)controlled["priority"]!);

        var row = Assert.Single(Rows(document, NifModelAnimationNativeState.Kind));
        var payload = JsonNode.Parse(row.PayloadJson)!.AsObject();
        Assert.Equal(NifKf2004Fixtures.SequenceName, (string)payload["name"]!["text"]!);
        var strings = Assert.Single(payload["controlledBlockStrings"]!.AsArray())!.AsObject();
        Assert.Equal(NifKf2004Fixtures.TargetName, (string)strings["nodeName"]!["text"]!);
        Assert.Equal(2, payload["events"]!.AsArray().Count);

        var provenance = JsonNode.Parse(Assert.Single(Rows(document, NifModelSkeletonProvenance.Kind)).PayloadJson)!
            .AsObject();
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(skeleton)), (string)provenance["sha256"]!);
        Assert.Equal(new[] { NifKf2004Fixtures.TargetName },
            provenance["matched"]!.AsArray().Select(static name => (string)name!["text"]!));
        Assert.Empty(provenance["unmatched"]!.AsArray());
        SceneValidation.ValidateStructure(document);

        var other = new NifTestFileBuilder(false, 34);
        AddNode(other, other.AddString("Unrelated Root"), []);
        var refusal = Assert.Throws<NotSupportedException>(() =>
            ReadWith(kf, OneFile(ExplicitSkeleton, other.Build()), path: KfPath, options: options));
        Assert.StartsWith(NifModelAnimationStreamReader.NoClipExpressibleReason, refusal.Message,
            StringComparison.Ordinal);
        Assert.Contains(NifModelTargetNames.TargetNotInSkeletonReason, refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Without <c>bmt.skeleton</c> the walk-up finds the nearest ancestor <c>skeleton.nif</c> of the legacy stream's
    ///     path, exactly as for a 20.2.0.7 stream, and the document reads. Control: the default fixture, whose controlled
    ///     block names no Controller Type (the empty sentinel), is admitted by the probe but refused by the read for want
    ///     of an expressible clip, the block being kept native as not a transform block.
    /// </summary>
    [Fact]
    public async Task WalkUp_FindsTheSkeleton_AndTheSentinelControllerTypeYieldsNoClip()
    {
        var kf = NifKf2004Fixtures.Build(controllerType: NifKf2004Fixtures.TransformControllerType);
        await using var companions = Companions((NearSkeleton, Skeleton(false)));

        var result = ReadWith(kf, companions.ResolveAsync, path: KfPath);

        var provenance = JsonNode.Parse(Assert.Single(Rows(result.Document, NifModelSkeletonProvenance.Kind)).PayloadJson)!
            .AsObject();
        Assert.Equal(NifModelSkeletonResolver.NearestAncestorRuleName, (string)provenance["rule"]!);
        Assert.Equal(NearSkeleton, (string)provenance["path"]!);
        Assert.Single(result.Document.Animations);

        var sentinel = NifKf2004Fixtures.Build();
        Assert.Equal(ModelProbeKind.Supported, Probe(sentinel).Kind);
        var refusal = Assert.Throws<NotSupportedException>(() => ReadWith(sentinel, companions.ResolveAsync, path: KfPath));
        Assert.StartsWith(NifModelAnimationStreamReader.NoClipExpressibleReason, refusal.Message,
            StringComparison.Ordinal);
        Assert.Contains(NifModelAnimationReasons.NotTransformBlock, refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A 20.0.0.4 scene graph of the same identity is refused by the read with the later-cut(2) reason, from its real
    ///     footer (the probe decided from block 0). Control: the <c>.kf</c> of that identity reads with the same skeleton.
    /// </summary>
    [Fact]
    public void LegacySceneGraph_IsRefusedAtTheRead_LaterCutTwo()
    {
        var options = SkeletonOptions(ExplicitSkeleton);
        var resolver = OneFile(ExplicitSkeleton, Skeleton(false));

        var refusal = Assert.Throws<NotSupportedException>(() =>
            ReadWith(NifKf2004Fixtures.BuildSceneGraph(), resolver, path: "meshes/test/legacy.nif", options: options));

        Assert.StartsWith(NifModelProbe.LaterCutTwoCategory, refusal.Message, StringComparison.Ordinal);
        Assert.Single(ReadWith(NifKf2004Fixtures.Build(controllerType: NifKf2004Fixtures.TransformControllerType),
            resolver, path: KfPath, options: options).Document.Animations);
    }

    /// <summary>
    ///     Cut 2, the review fix: a <c>.kf</c> of the key identity whose blocks use a type outside the eight the reader
    ///     measured (here an NiFloatData nothing references, the shape of the Oblivion <c>.kf</c> the review measured)
    ///     is refused at the read with the later-cut(2) reason naming that type, after the roots said "stream" and
    ///     before any block is bound, where the probe declines it too. Control: the same file without that block reads
    ///     its one clip with the same skeleton.
    /// </summary>
    [Fact]
    public void LegacyKfWithAnUnmeasuredBlockType_IsRefusedAtTheRead_LaterCutTwo()
    {
        var options = SkeletonOptions(ExplicitSkeleton);
        var resolver = OneFile(ExplicitSkeleton, Skeleton(false));
        var kf = NifKf2004Fixtures.Build(controllerType: NifKf2004Fixtures.TransformControllerType,
            extraBlockType: "NiFloatData");

        var refusal = Assert.Throws<NotSupportedException>(() => ReadWith(kf, resolver, path: KfPath, options: options));

        Assert.StartsWith(NifModelProbe.LaterCutTwoCategory, refusal.Message, StringComparison.Ordinal);
        Assert.Contains("; NiFloatData is not among them", refusal.Message, StringComparison.Ordinal);
        Assert.Equal(ModelProbeKind.Unsupported, Probe(kf).Kind);
        Assert.Single(ReadWith(NifKf2004Fixtures.Build(controllerType: NifKf2004Fixtures.TransformControllerType),
            resolver, path: KfPath, options: options).Document.Animations);
    }

    /// <summary>
    ///     The legacy record is the legacy stream's alone: a 20.2.0.7 <c>.kf</c> read through the same path carries no
    ///     <c>stringPalette</c> member in its clip extras and its string indices are the header table's, so the extras of
    ///     every other version are unchanged by the carry.
    /// </summary>
    [Fact]
    public void ModernKf_CarriesNoLegacyRecord()
    {
        var options = SkeletonOptions(ExplicitSkeleton);
        var result = ReadWith(Kf(false, 34, PelvisName), OneFile(ExplicitSkeleton, Skeleton(false)), path: KfPath,
            options: options);

        var extras = JsonNode.Parse(Assert.Single(result.Document.Animations).ExtrasJson!)!.AsObject()
            [NifModelAnimationExtras.Key]!.AsObject();

        Assert.False(extras.ContainsKey("stringPalette"));
        Assert.Equal(0, (int)extras["nameIndex"]!);
    }

    /// <summary>The app options naming an explicit skeleton.</summary>
    private static Dictionary<string, string> SkeletonOptions(string path)
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [BethesdaModelRegistration.SkeletonOption] = path
        };
    }
}
