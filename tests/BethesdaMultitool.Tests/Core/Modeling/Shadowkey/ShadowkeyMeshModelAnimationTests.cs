using System.Numerics;
using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Formats.Travels.Shadowkey;
using BethesdaMultitool.Core.Modeling.Shadowkey;
using BethesdaMultitool.Tests.Helpers;
using Slfx77.Multitool.Core.Models;
using Slfx77.Multitool.Core.Models.Export;
using Xunit;
using static BethesdaMultitool.Tests.Core.Modeling.Shadowkey.ShadowkeyModelTestSupport;

namespace BethesdaMultitool.Tests.Core.Modeling.Shadowkey;

/// <summary>
///     The skin alternatives and the animation of the mesh document (cut-2 plan sections 3.4 and 3.5, decisions D4 and
///     D5; slices 5 and 6) on the golden animated record, against the Python oracle's digests of the same bytes, each
///     with its control; and the GLB writer's admission of a multi-skin animated record (cut-2 review finding 1).
/// </summary>
public class ShadowkeyMeshModelAnimationTests : IDisposable
{
    private const string GoldenTargets = "63b9a56c725080025ee4cc578df0834700bd8bf22a4851dde1ed49b0ce2af3a6";
    private const string GoldenSkin0Original = "3a62011382ad86dcb00597b7c9782c544a31e012df40fcff5f0be0ed791cf971";
    private const string GoldenSkin0Rgba = "5a4ca5826622c05dc168abaedcb98a15d05a79bb3331c05f781b7b2f6bb50c90";
    private const string GoldenSkin1Rgba = "09fa9502036d7ece2c86a1c63db412f72910a466238e3e7d668f579093b7c91c";
    private const string Seq00Times = "5a1dfa10657e0a21707cf91228b779a860426ba61f928eebffa3038bda0dbd68";
    private const string Seq00Weights = "af5570f5a1810b7af78caf4bc70a660f0df51e42baf91d4de5b2328de0e83dfc";
    private const string Seq01Times = "b0e5290705ba071c3bab4c841b5c7bbf6b4ea26def76d7063d094d3cc7e6caa7";
    private const string Seq01Weights = "80b8fd6d60fa85fd14a38b5295cb92abd80dfec5ca406c9f969609a79d36809d";

    private readonly string _directory = Directory.CreateTempSubdirectory("bmt-cut2-shadowkey-").FullName;

    /// <inheritdoc />
    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static ModelDocument Golden()
    {
        return ReadMesh(ShadowkeyTestBuilder.GoldenAnimated()).Document;
    }

    [Fact]
    public void Skins_AreOneExclusiveGroup_OfSceneRoots_WithSkinZeroOn()
    {
        var document = Golden();

        SceneValidation.ValidateStructure(document);
        Assert.Equal(2, document.Nodes.Count);
        Assert.Equal(new[] { 0, 1 }, Assert.Single(document.Scenes).RootNodeIndices);
        Assert.All(document.Nodes, static n => Assert.Empty(n.Children));
        Assert.Equal(("175_arrow.skin00", 0), (document.Nodes[0].Name, document.Nodes[0].MeshIndex!.Value));
        Assert.Equal(("175_arrow.skin01", 1), (document.Nodes[1].Name, document.Nodes[1].MeshIndex!.Value));
        Assert.Equal(new[] { "skin00", "skin01" }, document.LayerSets.Select(static l => l.Id));
        Assert.Equal(new[] { true, false }, document.LayerSets.Select(static l => l.DefaultOn));
        Assert.All(document.LayerSets, static l => Assert.Equal(ShadowkeyMeshModelLayers.ExclusiveGroup, l.ExclusiveGroup));
        Assert.Equal(new[] { 0 }, document.LayerSets[0].Members);
        Assert.Equal(new[] { 1 }, document.LayerSets[1].Members);
        Assert.Equal(0, document.Meshes[0].Primitives[0].MaterialIndex);
        Assert.Equal(1, document.Meshes[1].Primitives[0].MaterialIndex);
        Assert.Equal(GoldenSkin0Original, document.Images[0].Source!.Original!.Sha256);
        Assert.Equal(GoldenSkin0Rgba, Digest(StandardPng(document.Images[0]).Rgba()));
        Assert.Equal(GoldenSkin1Rgba, Digest(StandardPng(document.Images[1]).Rgba()));
        Assert.Contains(document.Diagnostics, static d => d.Code == ShadowkeyModelDiagnostics.SkinSelection);
    }

    [Fact]
    public void TwoDefaultOnSkins_FailValidation_ThatIsTheControl()
    {
        var document = Golden();
        var broken = new ModelDocument(document.SourceFormat, document.Name, document.Scenes, document.Nodes,
            document.Meshes, document.Materials, document.Images, document.Samplers, document.Animations,
            units: document.Units, sourceBasis: document.SourceBasis,
            layerSets: document.LayerSets.Select(static l =>
                new SceneLayerSet(l.Id, l.Label, l.Members, true, l.SourceKind, l.ExclusiveGroup)));

        Assert.Throws<InvalidDataException>(() => SceneValidation.ValidateStructure(broken));
    }

    [Fact]
    public void FramesAfterTheFirst_AreAbsoluteTargets_SharedByTheSkins()
    {
        var document = Golden();
        var first = document.Meshes[0].Primitives[0];
        var second = document.Meshes[1].Primitives[0];

        var target = Assert.Single(first.MorphTargets);
        Assert.Equal("frame 001", target.Name);
        Assert.Empty(target.PositionDeltas);
        Assert.Same(target, Assert.Single(second.MorphTargets));
        Assert.Equal(GoldenTargets, Float32Digest(Targets(first)));
        // UV 4 belongs to vertex 0, so its target position is vertex 0's frame-1 position (3, -7, 11).
        Assert.Equal(new Vector3(3, -7, 11), target.AbsolutePositions![4]);
        // Control: the relative form (frame 1 minus frame 0) is a different array.
        var relative = target.AbsolutePositions.Select((p, i) => p - first.Vertices[i].Position)
            .SelectMany(static p => new[] { p.X, p.Y, p.Z });
        Assert.NotEqual(GoldenTargets, Float32Digest(relative));
    }

    [Fact]
    public void EverySequence_IsAStepClip_WithADerivedFinalKey_AndTheOraclesDigests()
    {
        var document = Golden();

        Assert.Equal(new[] { "seq00", "seq01" }, document.Animations.Select(static a => a.Name));
        var seq00 = document.Animations[0];
        var seq01 = document.Animations[1];
        // One track per clip, on the default skin's node (review finding 1, option (a)); skin01 holds frame 0.
        Assert.All(document.Animations,
            static a => Assert.Equal(new[] { 0 }, a.MorphTracks.Select(static t => t.NodeIndex)));
        Assert.All(document.Animations.SelectMany(static a => a.MorphTracks), static t =>
        {
            Assert.Equal(SceneInterpolation.Step, t.Interpolation);
            Assert.Equal(1, t.TargetCount);
        });
        Assert.Equal(Seq00Times, Float32Digest(seq00.MorphTracks[0].Times));
        Assert.Equal(Seq00Weights, Float32Digest(seq00.MorphTracks[0].Weights));
        Assert.Equal(Seq01Times, Float32Digest(seq01.MorphTracks[0].Times));
        Assert.Equal(Seq01Weights, Float32Digest(seq01.MorphTracks[0].Weights));
        // (1, 2, 3): frame 1 at 0 s and, the derived final key, at 1/3 s; no authored duration.
        Assert.Equal(new[] { 0f, 1f / 3f }, seq01.MorphTracks[0].Times);
        Assert.Equal(new[] { 1f, 1f }, seq01.MorphTracks[0].Weights);
        Assert.Null(seq01.DurationSeconds);
        // Control: without the derived key the clip would end at its first key, 0 s.
        Assert.NotEqual(0f, seq01.MorphTracks[0].Times[^1]);
        Assert.Equal(new[] { 0f, 0f }, seq00.MorphTracks[0].Weights);
    }

    [Fact]
    public void TheTiming_IsTheRateAsFramesPerSecond_Assumed_WithTheRawRate()
    {
        var clip = Golden().Animations[0];

        Assert.Equal((double?)10, clip.Timing!.FramesPerSecond);
        Assert.Equal((double?)10, clip.Timing.RawRate);
        Assert.Equal(ShadowkeyMeshModelAnimation.RawRateUnit, clip.Timing.RawRateUnit);
        Assert.Equal(SceneValueProvenance.Assumed, clip.Timing.Provenance);
        var extras = JsonNode.Parse(clip.ExtrasJson!)!;
        Assert.Equal((0, 1, 10),
            (extras["start"]!.GetValue<int>(), extras["end"]!.GetValue<int>(), extras["rate"]!.GetValue<int>()));
    }

    [Fact]
    public void ALongerSequence_KeysEveryFrameOneHot_AndHoldsTheLast()
    {
        var clip = ShadowkeyMeshModelAnimation.Create(0, new ShadowkeySequence(1, 4, 2), 5, [0]);

        var track = Assert.Single(clip.MorphTracks);
        Assert.Equal(4, track.TargetCount);
        Assert.Equal(new[] { 0f, 0.5f, 1f, 1.5f }, track.Times);
        Assert.Equal(new float[]
        {
            1, 0, 0, 0,
            0, 1, 0, 0,
            0, 0, 1, 0,
            0, 0, 1, 0
        }, track.Weights);
        // Control: Linear would blend between the whole keyframes the file stores.
        Assert.NotEqual(SceneInterpolation.Linear, track.Interpolation);
    }

    [Fact]
    public void AOneFrameRecord_HasNoClip_AndTheHumanoidTableAndRateOneAreFlagged()
    {
        var still = ReadMesh(ShadowkeyTestBuilder.GoldenStatic()).Document;
        Assert.Empty(still.Animations);

        var positions = Enumerable.Range(0, 144).SelectMany(static f =>
            ShadowkeyTestBuilder.GoldenFrame0.Select(v => (short)(v + f))).ToArray();
        var humanoid = new ShadowkeyTestBuilder.Record
        {
            Frames = 144, Positions = positions, Uvs = ShadowkeyTestBuilder.GoldenUvs,
            Faces = ShadowkeyTestBuilder.GoldenFaces, Texels = ShadowkeyTestBuilder.GoldenSkin1,
            Sequences = [.. ShadowkeyMeshModelAnimation.HumanoidTable.Select(static s => ((int)s.Start, (int)s.EndExclusive, (int)s.Rate))]
        }.Build();
        var slow = new ShadowkeyTestBuilder.Record
        {
            Frames = 144, Positions = positions, Uvs = ShadowkeyTestBuilder.GoldenUvs,
            Faces = ShadowkeyTestBuilder.GoldenFaces, Texels = ShadowkeyTestBuilder.GoldenSkin1,
            Sequences = [(0, 100, 10), (100, 144, 1)]
        }.Build();

        var humanoidDocument = ReadMesh(humanoid).Document;
        var slowDocument = ReadMesh(slow).Document;

        Assert.Equal(11, humanoidDocument.Animations.Count);
        Assert.Contains(humanoidDocument.Diagnostics, static d => d.Code == ShadowkeyModelDiagnostics.ActorAssembly);
        Assert.DoesNotContain(humanoidDocument.Diagnostics, static d => d.Code == ShadowkeyModelDiagnostics.RateSuspect);
        Assert.Contains(slowDocument.Diagnostics, static d => d.Code == ShadowkeyModelDiagnostics.RateSuspect);
        Assert.DoesNotContain(slowDocument.Diagnostics, static d => d.Code == ShadowkeyModelDiagnostics.ActorAssembly);
        Assert.Contains(slowDocument.Diagnostics, static d => d.Code == ShadowkeyModelDiagnostics.StepInterpolation);
    }

    /// <summary>
    ///     Hop B's shape on the golden multi-skin animated record (cut-2 review finding 1): the GLB writer converts it with
    ///     the default layers and with every alternative, and Blender carries both clips natively. Shared now permits
    ///     reported omissions of unselected exclusive morph tracks. Saved GLB channels retain the selected source keys
    ///     and original durations; a selection with no surviving tracks retains original clip metadata only.
    /// </summary>
    [Fact]
    public async Task AMultiSkinAnimatedRecord_PreservesSelectedPlayback_AndReportsExclusiveTrackOmissions()
    {
        var document = Golden();

        var (rows, written, refusal) = await WriteGlbAsync(document, _directory, "default");
        Assert.Null(refusal);
        Assert.Equal(ModelItemOutcome.Converted, written!.Outcome);
        Assert.DoesNotContain(rows, static r => r.ReasonCode == "draw.morph-target-suppressed");
        AssertGlbMorphPlayback(document, Path.Combine(_directory, "default.glb"), 0);

        var (_, allWritten, allRefusal) = await WriteGlbAsync(document, _directory, "all",
            new ModelLayerSelection(ModelLayerSelectionMode.All));
        Assert.Null(allRefusal);
        Assert.Equal(ModelItemOutcome.Converted, allWritten!.Outcome);
        AssertGlbMorphPlayback(document, Path.Combine(_directory, "all.glb"), 0, 1);

        var clips = BlenderRows(document)
            .Where(static r => r.Target.Kind == SceneElementKind.Animation && r.FeatureId == "clip").ToList();
        Assert.Equal(2, clips.Count);
        Assert.All(clips, static r => Assert.Equal(ModelFidelityOutcome.Converted, r.Outcome));

        var everyDocument = WithTracksOnEverySkin(document);
        var (everyRows, everyWritten, everyRefusal) = await WriteGlbAsync(everyDocument, _directory,
            "every-skin");
        Assert.Null(everyRefusal);
        Assert.Equal(ModelItemOutcome.Converted, everyWritten!.Outcome);
        AssertExclusiveMorphOmissions(everyDocument, everyRows, 1);
        AssertExclusiveMorphOmissions(everyDocument, everyWritten.Fidelity.Rows, 1);
        var everyGraph = AssertGlbMorphPlayback(everyDocument, Path.Combine(_directory, "every-skin.glb"), 0);
        Assert.Null(everyGraph.LogicalNodes[1].Mesh);

        var (everyAllRows, everyAllWritten, everyAllRefusal) = await WriteGlbAsync(everyDocument, _directory,
            "every-skin-all", new ModelLayerSelection(ModelLayerSelectionMode.All));
        Assert.Null(everyAllRefusal);
        Assert.Equal(ModelItemOutcome.Converted, everyAllWritten!.Outcome);
        AssertExclusiveMorphOmissions(everyDocument, everyAllRows);
        AssertGlbMorphPlayback(everyDocument, Path.Combine(_directory, "every-skin-all.glb"), 0, 1);

        var (explicitRows, explicitWritten, explicitRefusal) = await WriteGlbAsync(document, _directory, "explicit",
            new ModelLayerSelection(ModelLayerSelectionMode.Explicit, ["skin01"]));
        Assert.Null(explicitRefusal);
        Assert.Equal(ModelItemOutcome.Converted, explicitWritten!.Outcome);
        AssertExclusiveMorphOmissions(document, explicitRows, 0);
        var explicitGraph = AssertGlbMorphPlayback(document, Path.Combine(_directory, "explicit.glb"), 1);
        Assert.Null(explicitGraph.LogicalNodes[0].Mesh);
        Assert.NotNull(explicitGraph.LogicalNodes[1].Mesh);
        var metadata = explicitGraph.Extras!["multitoolAnimationMetadata"]!["clips"]!.AsArray();
        Assert.Equal(document.Animations.Count, metadata.Count);
        for (var index = 0; index < document.Animations.Count; index++)
        {
            var source = document.Animations[index];
            Assert.Equal(index, metadata[index]!["sourceAnimation"]!.GetValue<int>());
            Assert.Null(metadata[index]!["outputAnimation"]);
            Assert.Equal(source.Name, metadata[index]!["name"]!.GetValue<string>());
            Assert.Equal(source.Timing!.RawRate, metadata[index]!["timing"]!["rawRate"]!.GetValue<double>());
        }

        // Without an exclusive group, dropping the hidden skin's track is still unsupported.
        var nonexclusive = new ModelDocument(everyDocument.SourceFormat, everyDocument.Name, everyDocument.Scenes,
            everyDocument.Nodes, everyDocument.Meshes, everyDocument.Materials, everyDocument.Images,
            everyDocument.Samplers, everyDocument.Animations, units: everyDocument.Units,
            sourceBasis: everyDocument.SourceBasis, layerSets: everyDocument.LayerSets.Select(layer =>
                new SceneLayerSet(layer.Id, layer.Label, layer.Members, layer.DefaultOn, layer.SourceKind)));
        var (rejectedRows, rejectedWritten, rejectedRefusal) = await WriteGlbAsync(nonexclusive, _directory, "nonexclusive");
        Assert.Null(rejectedWritten);
        Assert.NotNull(rejectedRefusal);
        Assert.Contains(rejectedRows, static row => row.ReasonCode == "draw.morph-target-suppressed");
    }
}
