using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Modeling.Nif;
using BethesdaMultitool.Tests.Helpers;
using Slfx77.Multitool.Core.Models;
using Xunit;
using static BethesdaMultitool.Tests.Core.Modeling.Nif.NifModelAnimationReaderTestSupport;
using static BethesdaMultitool.Tests.Core.Modeling.Nif.NifModelAnimationTargetTestSupport;
using static BethesdaMultitool.Tests.Core.Modeling.Nif.NifModelAnimationTestSupport;

namespace BethesdaMultitool.Tests.Core.Modeling.Nif;

/// <summary>
///     Cut-1b slice 8, the <c>bmt.nif.animation.clip</c> native-state row (<see cref="NifModelAnimationNativeState" />;
///     plan section 2.2): one row per clip, targeting Animation k, located at the sequence block, carrying the raw name
///     bytes, the clock bits, the accumulation root, the text-key and anim-note refs, every controlled block's raw
///     strings, the per-track map, the NativeOnly tracks and the events' raw bytes; the <c>(controllers)</c> clip's row
///     at its pseudo-element. Every test carries a control that fails.
/// </summary>
public sealed class NifModelAnimationNativeStateTests
{
    private const int SequenceBlock = 3;
    private const int FirstAnimation = 3;

    /// <summary>
    ///     The sequence clip's row round-trips its fields exactly: target Animation 3, the sequence's byte range, the name
    ///     bytes 'Idl' + 0xE9 (text and raw hex), the weight, cycle, start and stop bits, the accumulation root, manager
    ///     and text-key refs, the controlled blocks' raw strings, the three transform tracks with their states, the
    ///     native NiAlphaController block (a property controlled block with no Property Type, slice 14), and both text
    ///     keys as stored. Control: one changed byte (the text key 'b' to
    ///     'c') changes the payload, and only in that label.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SequenceRow_RoundTripsTheStoredFields(bool bigEndian)
    {
        var (state, graph) = ReadGraph(SequenceFixture(bigEndian, "b"));
        var result = NifModelAnimationReader.ReadNif(state, graph, TestContext.Current.CancellationToken);

        var rows = NifModelAnimationNativeState.Build(state, result, FirstAnimation,
            TestContext.Current.CancellationToken);

        var row = Assert.Single(rows);
        Assert.Equal(NifModelAnimationNativeState.Kind, row.Kind);
        Assert.Equal("bmt.nif.animation.clip", row.Kind);
        Assert.Equal(NifModelAnimationNativeState.PayloadVersion, row.Version);
        Assert.Equal(new SceneElementRef(SceneElementKind.Animation, FirstAnimation), row.Target);
        Assert.False(row.HasRawContent);
        Assert.NotNull(row.SourceLocation);
        Assert.Equal("block:3", row.SourceLocation!.ElementIdentity);
        Assert.Equal((long?)state.Blocks[SequenceBlock].Offset, row.SourceLocation.ByteOffset);
        Assert.Equal((long?)state.Blocks[SequenceBlock].Size, row.SourceLocation.ByteLength);

        var payload = JsonNode.Parse(row.PayloadJson)!.AsObject();
        Assert.Equal(1, payload["version"]!.GetValue<int>());
        Assert.Equal("sequence", payload["source"]!.GetValue<string>());
        Assert.Equal(SequenceBlock, payload["block"]!.GetValue<int>());
        Assert.Equal("Idlé", payload["name"]!["text"]!.GetValue<string>());
        Assert.Equal("49646ce9", payload["name"]!["rawHex"]!.GetValue<string>());
        Assert.Equal("Root", payload["accumRootName"]!["text"]!.GetValue<string>());
        var clip = payload["clip"]!;
        Assert.Equal(Bits(0.75f), clip["weightBits"]!.GetValue<uint>());
        Assert.Equal(2, clip["cycleType"]!.GetValue<int>());
        Assert.Equal(Bits(0f), clip["startTimeBits"]!.GetValue<uint>());
        Assert.Equal(Bits(1.5f), clip["stopTimeBits"]!.GetValue<uint>());
        Assert.Equal(Bits(1f), clip["frequencyBits"]!.GetValue<uint>());
        Assert.Equal(2, clip["managerRef"]!.GetValue<int>());
        Assert.Equal(5, clip["textKeysRef"]!.GetValue<int>());
        Assert.Equal(0, clip["animNoteRefs"]!.AsArray().Count);
        Assert.True(clip["animNotesArrayForm"]!.GetValue<bool>());
        var strings = payload["controlledBlockStrings"]!.AsArray();
        Assert.Equal(2, strings.Count);
        Assert.Equal("Bone", strings[0]!["nodeName"]!["text"]!.GetValue<string>());
        Assert.Equal("CtlId", strings[0]!["controllerId"]!["text"]!.GetValue<string>());
        Assert.Equal("InterpId", strings[0]!["interpolatorId"]!["text"]!.GetValue<string>());
        Assert.Null(strings[0]!["propertyType"]);
        Assert.Equal("NiAlphaController", strings[1]!["controllerType"]!["text"]!.GetValue<string>());

        var tracks = payload["tracks"]!.AsArray();
        Assert.Equal(3, tracks.Count);
        Assert.Equal(new[] { "translation", "rotation", "scale" },
            tracks.Select(static t => t!["property"]!.GetValue<string>()));
        Assert.Equal(new[] { "Keyed", "Constant", "Constant" },
            tracks.Select(static t => t!["state"]!.GetValue<string>()));
        Assert.Equal("LINEAR (1)", tracks[0]!["keyType"]!.GetValue<string>());
        Assert.Equal(2, tracks[0]!["keyCount"]!.GetValue<int>());
        Assert.Equal("none", tracks[0]!["conversion"]!.GetValue<string>());
        Assert.Contains("X, Y, Z, W", tracks[1]!["conversion"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.All(tracks, static t => Assert.False(t!["hasClock"]!.GetValue<bool>()));
        Assert.Equal(0, payload["morphTracks"]!.AsArray().Count);

        Assert.Equal(0, payload["propertyTracks"]!.AsArray().Count);
        var native = payload["nativeTracks"]!.AsArray();
        var alpha = Assert.Single(native, static n => n!["block"]!.GetValue<int>() == 6)!;
        Assert.Equal(1, alpha["controlledBlock"]!.GetValue<int>());
        Assert.Equal(NifModelAnimationReasons.PropertyTypeMissingCode, alpha["code"]!.GetValue<string>());
        Assert.Equal(NifModelAnimationReasons.PropertyTypeMissing, alpha["reason"]!.GetValue<string>());
        Assert.Contains(native, static n => n!["block"]!.GetValue<int>() == SequenceBlock &&
                                           n!["code"]!.GetValue<string>() == NifModelAnimationReasons.SequenceNativeFieldsCode);

        var events = payload["events"]!.AsArray();
        Assert.Equal(2, events.Count);
        Assert.Equal(Bits(2f), events[0]!["timeBits"]!.GetValue<uint>());
        Assert.Equal("b", events[0]!["label"]!["text"]!.GetValue<string>());
        Assert.False(events[0]!["isNull"]!.GetValue<bool>());
        Assert.Equal(Bits(0.5f), events[1]!["timeBits"]!.GetValue<uint>());
        Assert.Equal("a", events[1]!["label"]!["text"]!.GetValue<string>());
        Assert.True(events[0]!["labelIndex"]!.GetValue<int>() >= 0);

        var (controlState, controlGraph) = ReadGraph(SequenceFixture(bigEndian, "c"));
        var control = NifModelAnimationReader.ReadNif(controlState, controlGraph,
            TestContext.Current.CancellationToken);
        var controlRow = Assert.Single(NifModelAnimationNativeState.Build(controlState, control, FirstAnimation,
            TestContext.Current.CancellationToken));
        Assert.NotEqual(row.PayloadJson, controlRow.PayloadJson);
        Assert.Equal(controlRow.PayloadJson,
            row.PayloadJson.Replace("\"text\":\"b\"", "\"text\":\"c\"", StringComparison.Ordinal));
    }

    /// <summary>
    ///     The <c>(controllers)</c> clip's row targets its animation index, is located at the pseudo-element with no byte
    ///     range, names its source, lists its typed controller with kind 'transform' and its three clocked tracks, and
    ///     carries no sequence strings or events. Control: the sequence row of the previous test is located at a block
    ///     with a byte range, so the two forms are distinguishable.
    /// </summary>
    [Fact]
    public void ControllersRow_LocatesAtThePseudoElement()
    {
        var (state, graph) = ReadGraph(ControllerFixture());
        var result = NifModelAnimationReader.ReadNif(state, graph, TestContext.Current.CancellationToken);

        var rows = NifModelAnimationNativeState.Build(state, result, 0, TestContext.Current.CancellationToken);

        var row = Assert.Single(rows);
        Assert.Equal(new SceneElementRef(SceneElementKind.Animation, 0), row.Target);
        Assert.Equal(NifModelAnimationReader.ControllersClipName, row.SourceLocation!.ElementIdentity);
        Assert.Null(row.SourceLocation.ByteOffset);
        Assert.Null(row.SourceLocation.ByteLength);
        var payload = JsonNode.Parse(row.PayloadJson)!.AsObject();
        Assert.Equal(NifModelAnimationReader.ControllersClipName, payload["source"]!.GetValue<string>());
        Assert.Null(payload["block"]);
        Assert.Equal(NifModelAnimationReader.ControllersClipName, payload["name"]!["text"]!.GetValue<string>());
        Assert.Null(payload["controlledBlockStrings"]);
        Assert.Null(payload["events"]);
        var controller = Assert.Single(payload["clip"]!["controllers"]!.AsArray())!;
        Assert.Equal(2, controller["block"]!.GetValue<int>());
        Assert.Equal(NifModelAnimationExtras.TransformKind, controller["kind"]!.GetValue<string>());
        var tracks = payload["tracks"]!.AsArray();
        Assert.Equal(3, tracks.Count);
        Assert.All(tracks, static t => Assert.True(t!["hasClock"]!.GetValue<bool>()));
        Assert.All(tracks, static t => Assert.Equal(2, t!["controller"]!.GetValue<int>()));
        Assert.Empty(payload["nativeTracks"]!.AsArray());

        var (sequenceState, sequenceGraph) = ReadGraph(SequenceFixture(false, "b"));
        var sequence = NifModelAnimationReader.ReadNif(sequenceState, sequenceGraph,
            TestContext.Current.CancellationToken);
        var sequenceRow = Assert.Single(NifModelAnimationNativeState.Build(sequenceState, sequence, 0,
            TestContext.Current.CancellationToken));
        Assert.NotNull(sequenceRow.SourceLocation!.ByteOffset);
        Assert.NotEqual(row.SourceLocation.ElementIdentity, sequenceRow.SourceLocation.ElementIdentity);
    }

    /// <summary>
    ///     Root (node 0, manager 2) with child Bone (node 1); manager 2 lists sequence 3, named 'Idl' + 0xE9, weight
    ///     0.75, CLAMP over [0, 1.5], accumulation root 'Root', text keys 5 (2.0 then 0.5 with the given first label and
    ///     'a'), one transform controlled block (interpolator 4 on Bone, priority 26, 'CtlId', 'InterpId') and one
    ///     NiAlphaController block (interpolator 6, a float interpolator); 7 the translation keys.
    /// </summary>
    private static byte[] SequenceFixture(bool bigEndian, string firstLabel)
    {
        var builder = new NifTestFileBuilder(bigEndian, Bs);
        var root = builder.AddString("Root");
        var bone = builder.AddString("Bone");
        var name = builder.AddRawString([0x49, 0x64, 0x6C, 0xE9]);
        var transformType = builder.AddString(TransformController);
        var alphaType = builder.AddString("NiAlphaController");
        var controllerId = builder.AddString("CtlId");
        var interpolatorId = builder.AddString("InterpId");
        var first = builder.AddString(firstLabel);
        var a = builder.AddString("a");
        Add(builder, 0, "NiNode", Node(root, [1], 2));
        Add(builder, 1, "NiNode", Node(bone, []));
        Add(builder, 2, "NiControllerManager", Manager(0, [3], -1));
        Add(builder, 3, "NiControllerSequence", Sequence(name,
        [
            Controlled(4, bone, transformType, controllerId: controllerId, interpolatorId: interpolatorId),
            Controlled(6, bone, alphaType)
        ], 5, 2, root, 0.75f, 2, 0f, 1.5f));
        Add(builder, 4, "NiTransformInterpolator", TransformInterpolator(7));
        Add(builder, 5, "NiTextKeyExtraData", TextKeys((2f, first), (0.5f, a)));
        Add(builder, 6, "NiFloatInterpolator", NifModelAnimationMorphTestSupport.FloatInterpolator(-1));
        Add(builder, 7, "NiTransformData", TransformData(NoRotation, (0f, 0f, 0f, 0f), (1f, 1f, 2f, 3f)));
        return builder.Build();
    }

    /// <summary>Root (node 0) with child Spin (node 1) driven by a free-running CLAMP NiTransformController (block 2).</summary>
    private static byte[] ControllerFixture()
    {
        var builder = new NifTestFileBuilder(false, Bs);
        var root = builder.AddString("Root");
        var spin = builder.AddString("Spin");
        Add(builder, 0, "NiNode", Node(root, [1]));
        Add(builder, 1, "NiNode", Node(spin, [], 2));
        Add(builder, 2, "NiTransformController", TransformControllerBlock(1, 3, Active | ClampCycle));
        Add(builder, 3, "NiTransformInterpolator", TransformInterpolator(4));
        Add(builder, 4, "NiTransformData", TransformData(NoRotation, (0f, 0f, 0f, 0f), (1f, 10f, 0f, 0f)));
        return builder.Build();
    }
}
