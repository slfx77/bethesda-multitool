using System.Numerics;
using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Modeling.Nif;
using BethesdaMultitool.Tests.Helpers;
using Slfx77.Multitool.Core.Models;
using Slfx77.Multitool.Core.Models.Sources;
using Xunit;
using static BethesdaMultitool.Tests.Core.Modeling.Nif.NifModelAnimationPropertyTestSupport;
using static BethesdaMultitool.Tests.Core.Modeling.Nif.NifModelAnimationReaderTestSupport;

namespace BethesdaMultitool.Tests.Core.Modeling.Nif;

/// <summary>
///     Cut-1b slice 14, property and visibility tracks (<see cref="NifModelPropertyTracks" />,
///     <see cref="NifModelAnimationReader" />; SA4, plan sections 1.6 and 2.1): one test per row of the brief's mapping
///     table through synthetic files carrying the controller, the assembled document through Shared's structural
///     validation, and the animated value published by Shared's public <see cref="ScenePoseEvaluator" />, the only
///     oracle for sampled values. Every test runs little- and big-endian where the format differs and carries a control
///     that fails.
/// </summary>
public sealed class NifModelAnimationReaderPropertyTests
{
    private const int MaterialBlock = 3;
    private const int ControllerBlock = 4;
    private const int InterpolatorBlock = 5;
    private const int DataBlock = 6;
    private const int TexturingBlock = 3;
    private const int TextureTransformBlock = 5;
    private const float RestAlpha = 0.75f;

    private static readonly (float Tu, float Tv, float Su, float Sv, float Rotation, uint Method, float Cu, float Cv)
        IdentityMax = (0f, 0f, 1f, 1f, 0f, 1u, 0f, 0f);

    /// <summary>
    ///     NiAlphaController on the shape's NiMaterialProperty, keying alpha from the rest 0.75 at 0 s to 0.25 at 1 s:
    ///     one MaterialAlpha track (width 1) on material 0 with the controller's clock, the controller, interpolator and
    ///     data Typed, the document valid, and the evaluator publishing 0.25 at 1 s. Control: the rest pose gives the rest alpha 0.75.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AlphaController_DrivesMaterialAlpha(bool bigEndian)
    {
        var fixture = ReadFixture(MaterialFixture(bigEndian, AlphaController,
            SingleInterpController(MaterialBlock, InterpolatorBlock, Active | ClampCycle),
            NifModelAnimationMorphTestSupport.FloatInterpolator(DataBlock),
            NifModelAnimationMorphTestSupport.FloatData(NifModelAnimationMorphTestSupport.Linear, [0f, RestAlpha],
                [1f, 0.25f])));

        var result = Read(fixture);

        var clip = Assert.Single(result.Clips);
        Assert.Equal(NifModelAnimationReader.ControllersClipName, clip.Name);
        var track = Assert.Single(clip.PropertyTracks);
        Assert.Equal(new ScenePropertyTarget(ScenePropertyKind.MaterialAlpha, 0), track.Target);
        Assert.Equal(1, track.Curve.ComponentCount);
        Assert.Equal(SceneAnimationChannelState.Keyed, track.Curve.State);
        Assert.NotNull(track.Clock);
        Assert.Equal(Bits(RestAlpha), Bits(fixture.Materials.Materials[0].BaseColor.W));
        foreach (var block in new[] { ControllerBlock, InterpolatorBlock, DataBlock })
        {
            Assert.True(result.Dispositions[block].IsTyped, $"block {block}");
        }

        var document = AssembleDocument(fixture, result.Clips);
        SceneValidation.ValidateStructure(document, TestContext.Current.CancellationToken);
        Assert.Equal(0.25f, SampleMaterial(document, 0, 1f, 0).BaseColor.W);
        Assert.Equal(RestAlpha, RestMaterial(document, 0).BaseColor.W);
        Assert.NotEqual(SampleMaterial(document, 0, 1f, 0).BaseColor.W, RestMaterial(document, 0).BaseColor.W);
    }

    /// <summary>
    ///     NiMaterialColorController on the NiMaterialProperty: Target Color 1 (DIFFUSE) gives MaterialBaseColor, 0
    ///     MaterialAmbientColor, 2 MaterialSpecularColor, 3 MaterialEmissiveColor, each a width-3 track on material 0
    ///     from the NiPoint3Interpolator's NiPosData keys, and the evaluator publishes the animated color at 1 s in the
    ///     member the kind names. Control: every other Target Color yields a different kind, and the rest pose gives the rest
    ///     color (none for ambient, which an NiMaterialProperty at BS 26 and above does not store).
    /// </summary>
    [Theory]
    [InlineData(false, (ushort)0, ScenePropertyKind.MaterialAmbientColor)]
    [InlineData(true, (ushort)0, ScenePropertyKind.MaterialAmbientColor)]
    [InlineData(false, (ushort)1, ScenePropertyKind.MaterialBaseColor)]
    [InlineData(true, (ushort)1, ScenePropertyKind.MaterialBaseColor)]
    [InlineData(false, (ushort)2, ScenePropertyKind.MaterialSpecularColor)]
    [InlineData(true, (ushort)2, ScenePropertyKind.MaterialSpecularColor)]
    [InlineData(false, (ushort)3, ScenePropertyKind.MaterialEmissiveColor)]
    [InlineData(true, (ushort)3, ScenePropertyKind.MaterialEmissiveColor)]
    public void MaterialColorController_DrivesTheColorTheTargetColorNames(bool bigEndian, ushort targetColor,
        ScenePropertyKind expected)
    {
        var rest = targetColor switch
        {
            0 => new Vector3(0.1f, 0.1f, 0.1f),
            1 => Vector3.One,
            2 => new Vector3(0.4f, 0.5f, 0.6f),
            _ => new Vector3(0.2f, 0.4f, 0.6f)
        };
        var animated = new Vector3(0.9f, 0.3f, 0.1f);
        var fixture = ReadFixture(MaterialFixture(bigEndian, MaterialColorController,
            MaterialColorControllerBlock(MaterialBlock, InterpolatorBlock, Active | ClampCycle, targetColor),
            Point3Interpolator(DataBlock),
            PosData(NifModelAnimationMorphTestSupport.Linear, [0f, rest.X, rest.Y, rest.Z],
                [1f, animated.X, animated.Y, animated.Z])));

        var result = Read(fixture);

        var track = Assert.Single(Assert.Single(result.Clips).PropertyTracks);
        Assert.Equal(new ScenePropertyTarget(expected, 0), track.Target);
        Assert.Equal(3, track.Curve.ComponentCount);
        foreach (var other in new ushort[] { 0, 1, 2, 3 })
        {
            if (other != targetColor)
            {
                Assert.True(NifModelPropertyController.TryMaterialColorKind(other, out var otherKind));
                Assert.NotEqual(expected, otherKind);
            }
        }

        var document = AssembleDocument(fixture, result.Clips);
        SceneValidation.ValidateStructure(document, TestContext.Current.CancellationToken);
        Assert.Equal(animated, Member(SampleMaterial(document, 0, 1f, 0), expected));
        var restPose = RestMaterial(document, 0);
        if (expected == ScenePropertyKind.MaterialAmbientColor)
        {
            // An NiMaterialProperty at BS 26 and above stores no Ambient Color, so the rest is unknown and Shared
            // publishes none; the first key is not a rest value.
            var source = fixture.Materials.Materials[0].Source;
            Assert.NotNull(source);
            Assert.Null(source.AmbientColor);
            Assert.Null(restPose.AmbientColor);
        }
        else
        {
            Assert.Equal(rest, Member(restPose, expected));
        }
    }

    /// <summary>
    ///     BSMaterialEmittanceMultController on the NiMaterialProperty: one MaterialEmissiveStrength track (width 1) from
    ///     the rest Emit Mult 2.5 to 5 at 1 s; the evaluator publishes 5 at 1 s. Control: the rest pose gives
    ///     the rest 2.5.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmittanceMultController_DrivesMaterialEmissiveStrength(bool bigEndian)
    {
        var fixture = ReadFixture(MaterialFixture(bigEndian, EmittanceMultController,
            SingleInterpController(MaterialBlock, InterpolatorBlock, Active | ClampCycle),
            NifModelAnimationMorphTestSupport.FloatInterpolator(DataBlock),
            NifModelAnimationMorphTestSupport.FloatData(NifModelAnimationMorphTestSupport.Linear, [0f, 2.5f], [1f, 5f])));

        var result = Read(fixture);

        var track = Assert.Single(Assert.Single(result.Clips).PropertyTracks);
        Assert.Equal(new ScenePropertyTarget(ScenePropertyKind.MaterialEmissiveStrength, 0), track.Target);
        Assert.Equal(2.5f, fixture.Materials.Materials[0].EmissiveStrength);
        var document = AssembleDocument(fixture, result.Clips);
        SceneValidation.ValidateStructure(document, TestContext.Current.CancellationToken);
        Assert.Equal(5f, SampleMaterial(document, 0, 1f, 0).EmissiveStrength);
        Assert.Equal(2.5f, RestMaterial(document, 0).EmissiveStrength);
    }

    /// <summary>
    ///     NiTextureTransformController on the NiTexturingProperty's Base map (slot 0, Max method at identity rest):
    ///     Operation 0 gives LayerOffsetU, 1 LayerOffsetV, 2 LayerRotation, 3 LayerScaleU, 4 LayerScaleV, each a width-1
    ///     track on material 0, layer 0 (the Base map's ordinal), and the evaluator publishes the animated member at 1 s.
    ///     Control: every other Operation yields a different kind; the rest pose gives the rest member.
    /// </summary>
    [Theory]
    [InlineData(false, 0u, ScenePropertyKind.LayerOffsetU)]
    [InlineData(true, 0u, ScenePropertyKind.LayerOffsetU)]
    [InlineData(false, 1u, ScenePropertyKind.LayerOffsetV)]
    [InlineData(true, 1u, ScenePropertyKind.LayerOffsetV)]
    [InlineData(false, 2u, ScenePropertyKind.LayerRotation)]
    [InlineData(true, 2u, ScenePropertyKind.LayerRotation)]
    [InlineData(false, 3u, ScenePropertyKind.LayerScaleU)]
    [InlineData(true, 3u, ScenePropertyKind.LayerScaleU)]
    [InlineData(false, 4u, ScenePropertyKind.LayerScaleV)]
    [InlineData(true, 4u, ScenePropertyKind.LayerScaleV)]
    public void TextureTransformController_DrivesTheLayerMemberTheOperationNames(bool bigEndian, uint operation,
        ScenePropertyKind expected)
    {
        var rest = operation is 3 or 4 ? 1f : 0f;
        const float animated = 0.5f;
        var fixture = ReadFixture(LayerFixture(bigEndian, IdentityMax, 0, operation, [0f, rest], [1f, animated]));

        var result = Read(fixture);

        var track = Assert.Single(Assert.Single(result.Clips).PropertyTracks);
        Assert.Equal(new ScenePropertyTarget(expected, 0, 0), track.Target);
        Assert.Equal(1, track.Curve.ComponentCount);
        Assert.Equal(SceneTextureLayerRole.BaseColor, fixture.Materials.Materials[0].Layers[0].Role);
        Assert.True(result.Dispositions[TextureTransformBlock].IsTyped);
        foreach (var other in new uint[] { 0, 1, 2, 3, 4 })
        {
            if (other != operation)
            {
                Assert.True(NifModelPropertyController.TryOperationKind(other, out var otherKind));
                Assert.NotEqual(expected, otherKind);
            }
        }

        var document = AssembleDocument(fixture, result.Clips);
        SceneValidation.ValidateStructure(document, TestContext.Current.CancellationToken);
        Assert.Equal(animated, Member(SampleLayer(document, 0, 1f, 0, 0), expected));
        Assert.Equal(rest, Member(RestLayer(document, 0, 0), expected));
    }

    /// <summary>
    ///     The member gate (<see cref="NifModelTextureTransformMember" />): a Base map whose rest transform rotates by
    ///     0.3 rad under the Max method cannot animate TRANSLATE_U as one Shared member (the offset would also move in V),
    ///     so the controller stays native with the gate's reason and no clip. Control: the identity rest types the track.
    /// </summary>
    [Fact]
    public void TextureTransformController_RestRotation_BlocksTheTranslateMember()
    {
        var rotated = ReadFixture(LayerFixture(false, (0f, 0f, 1f, 1f, 0.3f, 1u, 0f, 0f), 0, 0, [0f, 0f], [1f, 0.5f]));
        var result = Read(rotated);

        Assert.Empty(result.Clips);
        Assert.Equal(ModelSourceCoverageKind.NativeOnly, result.Dispositions[TextureTransformBlock].Kind);
        Assert.Contains(DecisionsFor(result, TextureTransformBlock),
            static d => d.Code == NifModelAnimationReasons.TextureTransformMemberCode);
        Assert.Contains("rest rotation", result.Dispositions[TextureTransformBlock].Reason, StringComparison.Ordinal);

        var identity = Read(ReadFixture(LayerFixture(false, IdentityMax, 0, 0, [0f, 0f], [1f, 0.5f])));
        Assert.Single(Assert.Single(identity.Clips).PropertyTracks);
    }

    /// <summary>
    ///     NiUVController on the shape (Texture Set 0) whose NiUVData keys the U offset (0 to 0.25) and the V scale (1 to
    ///     2) and leaves the other two groups empty: two tracks on material 0, layer 0 (LayerOffsetU and LayerScaleV),
    ///     both with the controller's clock, and the evaluator publishes both members at 1 s. Control: the rest pose gives the rest members, and an empty group yields no track.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UvController_DrivesTheNonemptyGroups(bool bigEndian)
    {
        var fixture = ReadFixture(UvFixture(bigEndian, [[0f, 0f], [1f, 0.25f]], [], [], [[0f, 1f], [1f, 2f]]));

        var result = Read(fixture);

        var clip = Assert.Single(result.Clips);
        Assert.Equal(2, clip.PropertyTracks.Count);
        Assert.Equal(new[] { ScenePropertyKind.LayerOffsetU, ScenePropertyKind.LayerScaleV },
            clip.PropertyTracks.Select(static track => track.Target.Kind));
        Assert.All(clip.PropertyTracks, static track =>
        {
            Assert.Equal(0, track.Target.Index);
            Assert.Equal(0, track.Target.LayerIndex);
            Assert.NotNull(track.Clock);
        });
        Assert.True(result.Dispositions[5].IsTyped);
        Assert.True(result.Dispositions[6].IsTyped);

        var document = AssembleDocument(fixture, result.Clips);
        SceneValidation.ValidateStructure(document, TestContext.Current.CancellationToken);
        var animated = SampleLayer(document, 0, 1f, 0, 0);
        Assert.Equal(0.25f, animated.Offset.X);
        Assert.Equal(2f, animated.Scale.Y);
        Assert.Equal(0f, animated.Offset.Y);
        Assert.Equal(1f, animated.Scale.X);
        var rest = RestLayer(document, 0, 0);
        Assert.Equal(0f, rest.Offset.X);
        Assert.Equal(1f, rest.Scale.Y);
    }

    /// <summary>
    ///     NiVisController on node Lid with CONST NiBoolData keys 1 at 0 s and 0 at 0.5 s: one NodeVisibility track
    ///     (width 1, Step) on node 1, the evaluator hiding the node at 0.75 s. Control: the rest pose gives
    ///     the rest (visible).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void VisController_DrivesNodeVisibility(bool bigEndian)
    {
        var fixture = ReadFixture(VisibilityFixture(bigEndian, (0f, 1), (0.5f, 0)));

        var result = Read(fixture);

        var track = Assert.Single(Assert.Single(result.Clips).PropertyTracks);
        Assert.Equal(new ScenePropertyTarget(ScenePropertyKind.NodeVisibility, 1), track.Target);
        Assert.Equal(SceneInterpolation.Step, track.Curve.Interpolation);
        Assert.Equal(new[] { 1f, 0f }, track.Curve.Values);
        foreach (var block in new[] { 2, 3, 4 })
        {
            Assert.True(result.Dispositions[block].IsTyped, $"block {block}");
        }

        var document = AssembleDocument(fixture, result.Clips);
        SceneValidation.ValidateStructure(document, TestContext.Current.CancellationToken);
        Assert.False(SampleVisible(document, 0, 0.75f, 1));
        Assert.True(RestVisible(document, 1));
        Assert.True(SampleVisible(document, 0, 0.75f, 0));
    }

    /// <summary>
    ///     An NiVisController whose NiBoolInterpolator holds a static false over an empty NiBoolData yields one Constant
    ///     NodeVisibility track that is Step (Shared requires step visibility even for a held value; retail FO3 and FNV
    ///     files carry it) and a valid document hiding the node. Control: a static true keeps the node visible.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StaticVisController_IsAConstantStepTrack(bool bigEndian)
    {
        var hidden = ReadFixture(StaticVisibilityFixture(bigEndian, 0));
        var result = Read(hidden);

        var track = Assert.Single(Assert.Single(result.Clips).PropertyTracks);
        Assert.Equal(new ScenePropertyTarget(ScenePropertyKind.NodeVisibility, 1), track.Target);
        Assert.Equal(SceneAnimationChannelState.Constant, track.Curve.State);
        Assert.Equal(SceneInterpolation.Step, track.Curve.Interpolation);
        var document = AssembleDocument(hidden, result.Clips);
        SceneValidation.ValidateStructure(document, TestContext.Current.CancellationToken);
        Assert.False(SampleVisible(document, 0, 0.5f, 1));

        var shown = ReadFixture(StaticVisibilityFixture(bigEndian, 1));
        var shownDocument = AssembleDocument(shown, Read(shown).Clips);
        SceneValidation.ValidateStructure(shownDocument, TestContext.Current.CancellationToken);
        Assert.True(SampleVisible(shownDocument, 0, 0.5f, 1));
    }

    /// <summary>
    ///     A shared NiMaterialProperty feeding two materials (ShapeA also carries an NiAlphaProperty, so the two shapes get
    ///     two materials from one property block) yields two MaterialAlpha tracks, one per fed material, and the document
    ///     validates. Control: the cut-1a first-only map names one material, and targets built from it yield one track.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SharedMaterialProperty_DrivesEveryFedMaterial(bool bigEndian)
    {
        var fixture = ReadFixture(SharedMaterialFixture(bigEndian));
        const int property = 5;
        Assert.Equal(2, fixture.Materials.Materials.Count);
        Assert.Equal(new[] { 0, 1 }, fixture.Materials.MaterialsByFedBlock[property]);
        Assert.Equal(0, fixture.Materials.MaterialByFedBlock[property]);

        var result = Read(fixture);

        var clip = Assert.Single(result.Clips);
        Assert.Equal(new[] { 0, 1 }, clip.PropertyTracks.Select(static track => track.Target.Index));
        Assert.All(clip.PropertyTracks, static track => Assert.Equal(ScenePropertyKind.MaterialAlpha, track.Target.Kind));
        var document = AssembleDocument(fixture, result.Clips);
        SceneValidation.ValidateStructure(document, TestContext.Current.CancellationToken);
        Assert.Equal(0.25f, SampleMaterial(document, 0, 1f, 0).BaseColor.W);
        Assert.Equal(0.25f, SampleMaterial(document, 0, 1f, 1).BaseColor.W);

        var firstOnly = new NifModelMaterialResult(fixture.Materials.Materials, fixture.Materials.Samplers,
            fixture.Materials.NativeRows, fixture.Materials.Dispositions, fixture.Materials.MaterialByFedBlock,
            fixture.Materials.Diagnostics,
            fixture.Materials.MaterialByFedBlock.ToDictionary(static pair => pair.Key,
                static pair => (IReadOnlyList<int>)new[] { pair.Value }),
            fixture.Materials.LayerOrigins);
        var control = NifModelAnimationReader.ReadNif(fixture.State, fixture.Graph,
            NifModelPropertyTargets.FromResults(firstOnly, fixture.Geometry), fixture.Platform,
            TestContext.Current.CancellationToken);
        Assert.Single(Assert.Single(control.Clips).PropertyTracks);
    }

    /// <summary>
    ///     A texture slot the document's material has no layer for (slot 1, DARK, on a property with only a Base map)
    ///     stays native 'texture slot not a document layer' with no clip; a visibility key that is not 0 or 1 stays native
    ///     'visibility value not exactly 0 or 1' with no clip. Controls: slot 0 and a binary key type each track.
    /// </summary>
    [Fact]
    public void SlotNotALayer_AndNonBinaryVisibility_AreBlocked()
    {
        var dark = Read(ReadFixture(LayerFixture(false, IdentityMax, 1, 0, [0f, 0f], [1f, 0.5f])));
        Assert.Empty(dark.Clips);
        Assert.Equal(NifModelAnimationReasons.TextureSlotNotLayer, dark.Dispositions[TextureTransformBlock].Reason);
        Assert.Contains(DecisionsFor(dark, 6), static d => d.Code == NifModelAnimationReasons.TextureSlotNotLayerCode);
        Assert.Single(Assert.Single(Read(ReadFixture(LayerFixture(false, IdentityMax, 0, 0, [0f, 0f], [1f, 0.5f]))).Clips)
            .PropertyTracks);

        var nonBinary = Read(ReadFixture(VisibilityFixture(false, (0f, 1), (0.5f, 2))));
        Assert.Empty(nonBinary.Clips);
        Assert.Equal(NifModelAnimationReasons.VisibilityNotBinary, nonBinary.Dispositions[2].Reason);
        Assert.Contains(DecisionsFor(nonBinary, 4), static d => d.Code == NifModelAnimationReasons.VisibilityNotBinaryCode);
        Assert.Single(Assert.Single(Read(ReadFixture(VisibilityFixture(false, (0f, 1), (0.5f, 0)))).Clips).PropertyTracks);
    }

    /// <summary>
    ///     A sequence controlled block binds by Node Name ('Shape'), Property Type ('NiMaterialProperty' on the shape's own
    ///     property list) and Controller Type plus Controller ID (NiAlphaController, NULL): the clip 'Fade' gets one
    ///     MaterialAlpha track with no track clock and the block's priority, through the stored controller ref and,
    ///     without one, through the property's controller chain; the manager-controlled NiAlphaController stays native
    ///     (RE-22) and its NiBlendFloatInterpolator as blend state. Controls: the Property Type 'NiAlphaProperty', which
    ///     the shape does not carry, does not bind ('property type names no property'), and the ID 'SELF_ILLUM' on an
    ///     NiAlphaController is not interpretable.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SequenceBlock_BindsByNodeName_PropertyType_AndControllerId(bool bigEndian)
    {
        foreach (var storedControllerRef in new[] { true, false })
        {
            var fixture = ReadFixture(SequenceFixture(bigEndian, "NiMaterialProperty", null, storedControllerRef));
            var result = Read(fixture);

            var clip = Assert.Single(result.Clips);
            Assert.Equal("Fade", clip.Name);
            var track = Assert.Single(clip.PropertyTracks);
            Assert.Equal(new ScenePropertyTarget(ScenePropertyKind.MaterialAlpha, 0), track.Target);
            Assert.Null(track.Clock);
            Assert.Equal(26, track.SourcePolicy!.Priority);
            Assert.True(result.Dispositions[7].IsTyped);
            Assert.True(result.Dispositions[9].IsTyped);
            Assert.Equal(NifModelAnimationReasons.ManagerControlled, result.Dispositions[6].Reason);
            Assert.Equal(NifModelAnimationReasons.BlendState, result.Dispositions[8].Reason);
            var entry = Assert.Single(PropertyEntries(clip))!;
            Assert.Equal(0, entry["controlledBlock"]!.GetValue<int>());
            Assert.Equal(6, entry["controllerBlock"]!.GetValue<int>());
            var document = AssembleDocument(fixture, result.Clips);
            SceneValidation.ValidateStructure(document, TestContext.Current.CancellationToken);
            Assert.Equal(0.25f, SampleMaterial(document, 0, 1f, 0).BaseColor.W);
        }

        var wrongType = Read(ReadFixture(SequenceFixture(bigEndian, "NiAlphaProperty", null, true)));
        var wrongClip = Assert.Single(wrongType.Clips);
        Assert.Empty(wrongClip.PropertyTracks);
        Assert.Equal(NifModelAnimationReasons.PropertyNotOnTarget, wrongType.Dispositions[7].Reason);

        var wrongId = Read(ReadFixture(SequenceFixture(bigEndian, "NiMaterialProperty", "SELF_ILLUM", true)));
        Assert.Empty(Assert.Single(wrongId.Clips).PropertyTracks);
        Assert.Equal(NifModelAnimationReasons.ControllerIdUninterpretable, wrongId.Dispositions[7].Reason);
    }

    /// <summary>
    ///     A sequence NiMaterialColorController block binds its kind from the Controller ID ('SELF_ILLUM' gives
    ///     MaterialEmissiveColor) and the bound controller's Target Color must agree. Control: the ID 'SPEC' against a
    ///     controller storing Target Color 3 disagrees and stays native.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SequenceMaterialColorBlock_BindsTheKindFromTheControllerId(bool bigEndian)
    {
        var result = Read(ReadFixture(SequenceColorFixture(bigEndian, "SELF_ILLUM")));

        var track = Assert.Single(Assert.Single(result.Clips).PropertyTracks);
        Assert.Equal(new ScenePropertyTarget(ScenePropertyKind.MaterialEmissiveColor, 0), track.Target);
        Assert.Equal(3, track.Curve.ComponentCount);

        var disagreeing = Read(ReadFixture(SequenceColorFixture(bigEndian, "SPEC")));
        Assert.Empty(Assert.Single(disagreeing.Clips).PropertyTracks);
        Assert.Equal(NifModelAnimationReasons.ControllerIdDisagrees, disagreeing.Dispositions[7].Reason);
    }

    /// <summary>
    ///     RE-21 step 7: two controlled blocks of one sequence on the same exact property target (NiAlphaController on
    ///     the shape's NiMaterialProperty, twice) keep both native 'repeated property target' while the sequence stays a
    ///     clip; the free-running twin (two embedded NiAlphaControllers on one property) keeps both native too. Control:
    ///     one block yields the track.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepeatedPropertyTarget_KeepsTheSequencesPropertyTracksNative(bool bigEndian)
    {
        var repeated = Read(ReadFixture(SequenceFixture(bigEndian, "NiMaterialProperty", null, true, repeat: true)));

        var clip = Assert.Single(repeated.Clips);
        Assert.Empty(clip.PropertyTracks);
        Assert.NotNull(clip.Clock);
        foreach (var block in new[] { 7, 9, 10 })
        {
            Assert.Equal(NifModelAnimationReasons.RepeatedPropertyTarget, repeated.Dispositions[block].Reason);
            Assert.Contains(DecisionsFor(repeated, block),
                static d => d.Code == NifModelAnimationReasons.RepeatedPropertyTargetCode);
        }

        var single = Read(ReadFixture(SequenceFixture(bigEndian, "NiMaterialProperty", null, true)));
        Assert.Single(Assert.Single(single.Clips).PropertyTracks);

        var embedded = Read(ReadFixture(RepeatedEmbeddedFixture(bigEndian)));
        Assert.Empty(embedded.Clips);
        Assert.Equal(NifModelAnimationReasons.RepeatedEmbeddedPropertyTarget, embedded.Dispositions[4].Reason);
        Assert.Equal(NifModelAnimationReasons.RepeatedEmbeddedPropertyTarget, embedded.Dispositions[7].Reason);
    }

    /// <summary>
    ///     The clip extras carry the propertyTracks map exactly: for the Base-map TRANSLATE_U controller the entry names
    ///     controller 5, interpolator 6, kind LayerOffsetU, material 0, layer 0, property block 3 and key type LINEAR
    ///     (1), and the native-state row repeats it with the track's state and width. Control: the SCALE_V controller
    ///     changes the extras, and only in the kind.
    /// </summary>
    [Fact]
    public void Extras_CarryThePropertyTracksMap()
    {
        var fixture = ReadFixture(LayerFixture(false, IdentityMax, 0, 0, [0f, 0f], [1f, 0.5f]));
        var result = Read(fixture);
        var clip = Assert.Single(result.Clips);

        var entry = Assert.Single(PropertyEntries(clip))!;
        Assert.Equal(TextureTransformBlock, entry["controller"]!.GetValue<int>());
        Assert.Equal(6, entry["interpolator"]!.GetValue<int>());
        Assert.Equal("LayerOffsetU", entry["kind"]!.GetValue<string>());
        Assert.Equal(0, entry["index"]!.GetValue<int>());
        Assert.Equal(0, entry["layerIndex"]!.GetValue<int>());
        Assert.Null(entry["node"]);
        Assert.Equal(TexturingBlock, entry["propertyBlock"]!.GetValue<int>());
        Assert.Equal(TextureTransformBlock, entry["controllerBlock"]!.GetValue<int>());
        Assert.Equal(1u, entry["keyType"]!.GetValue<uint>());
        var typedController = Assert.Single(Extras(clip)["controllers"]!.AsArray())!;
        Assert.Equal(NifModelAnimationExtras.PropertyKind, typedController["kind"]!.GetValue<string>());

        var row = Assert.Single(NifModelAnimationNativeState.Build(fixture.State, result, 0,
            TestContext.Current.CancellationToken));
        var payload = JsonNode.Parse(row.PayloadJson)!.AsObject();
        var rowEntry = Assert.Single(payload["propertyTracks"]!.AsArray())!;
        Assert.Equal("LayerOffsetU", rowEntry["kind"]!.GetValue<string>());
        Assert.Equal("Keyed", rowEntry["state"]!.GetValue<string>());
        Assert.Equal(1, rowEntry["width"]!.GetValue<int>());
        Assert.Equal(2, rowEntry["keyCount"]!.GetValue<int>());
        Assert.True(rowEntry["hasClock"]!.GetValue<bool>());

        var other = Assert.Single(Read(ReadFixture(LayerFixture(false, IdentityMax, 0, 4, [0f, 1f], [1f, 0.5f]))).Clips);
        Assert.NotEqual(clip.ExtrasJson, other.ExtrasJson);
        Assert.Equal(other.ExtrasJson,
            clip.ExtrasJson!.Replace("\"LayerOffsetU\"", "\"LayerScaleV\"", StringComparison.Ordinal));
    }

    /// <summary>
    ///     An NiBSplineCompPoint3Interpolator under a SELF_ILLUM NiMaterialColorController: a width-3 B-spline curve over
    ///     four compact controls decoded as offset + s / 32767 * half range, which the evaluator publishes as the first
    ///     control at the start and the last at the stop (a clamped open-uniform cubic interpolates its end controls).
    ///     Control: an interval that ends before it starts is refused.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Point3Bspline_DrivesAColorTrack(bool bigEndian)
    {
        var fixture = ReadFixture(BsplineColorFixture(bigEndian, 0f, 1f));

        var result = Read(fixture);

        var track = Assert.Single(Assert.Single(result.Clips).PropertyTracks);
        Assert.Equal(new ScenePropertyTarget(ScenePropertyKind.MaterialEmissiveColor, 0), track.Target);
        Assert.Equal(SceneInterpolation.BSpline, track.Curve.Interpolation);
        Assert.NotNull(track.Curve.Spline);
        Assert.Equal(3, track.Curve.Spline!.ComponentCount);
        Assert.Equal(4, track.Curve.Spline.ControlPointCount);
        var document = AssembleDocument(fixture, result.Clips);
        SceneValidation.ValidateStructure(document, TestContext.Current.CancellationToken);
        var first = new Vector3(0.5f + 0f, 0.5f + 0.25f, 0.5f + 0.5f);
        var last = new Vector3(0.5f - 0.5f, 0.5f, 0.5f + 0.25f);
        AssertNear(first, SampleMaterial(document, 0, 0f, 0).EmissiveColor);
        AssertNear(last, SampleMaterial(document, 0, 1f, 0).EmissiveColor);

        var reversed = Read(ReadFixture(BsplineColorFixture(bigEndian, 1f, 0f)));
        Assert.Empty(reversed.Clips);
        Assert.Equal(NifModelAnimationReasons.Reason(NifModelCurveBlock.BsplineInvalidInterval),
            reversed.Dispositions[ControllerBlock].Reason);
    }

    private static void AssertNear(Vector3 expected, Vector3 actual)
    {
        Assert.True((expected - actual).Length() < 1e-4f, $"expected {expected}, got {actual}");
    }

    private static uint Bits(float value)
    {
        return BitConverter.SingleToUInt32Bits(value);
    }

    /// <summary>The material pose member a color kind drives.</summary>
    private static Vector3 Member(SceneMaterialPose pose, ScenePropertyKind kind)
    {
        return kind switch
        {
            ScenePropertyKind.MaterialAmbientColor => pose.AmbientColor!.Value,
            ScenePropertyKind.MaterialBaseColor => new Vector3(pose.BaseColor.X, pose.BaseColor.Y, pose.BaseColor.Z),
            ScenePropertyKind.MaterialSpecularColor => pose.SpecularColor,
            _ => pose.EmissiveColor
        };
    }

    /// <summary>The layer transform member a layer kind drives.</summary>
    private static float Member(SceneTextureTransform transform, ScenePropertyKind kind)
    {
        return kind switch
        {
            ScenePropertyKind.LayerOffsetU => transform.Offset.X,
            ScenePropertyKind.LayerOffsetV => transform.Offset.Y,
            ScenePropertyKind.LayerScaleU => transform.Scale.X,
            ScenePropertyKind.LayerScaleV => transform.Scale.Y,
            _ => transform.Rotation
        };
    }

    /// <summary>
    ///     Root (node 0) over the quad Shape (node 1, data 2) carrying NiMaterialProperty 3 (controller 4); 4 the given
    ///     controller over interpolator 5, whose data is 6.
    /// </summary>
    private static byte[] MaterialFixture(bool bigEndian, string controllerType,
        Action<NifTestBlockWriter> controller, Action<NifTestBlockWriter> interpolator, Action<NifTestBlockWriter> data)
    {
        var builder = new NifTestFileBuilder(bigEndian, Bs);
        var root = builder.AddString("Root");
        var shape = builder.AddString("Shape");
        Add(builder, 0, "NiNode", Node(root, [1]));
        AddQuad(builder, 1, shape, [MaterialBlock]);
        Add(builder, MaterialBlock, "NiMaterialProperty", MaterialProperty(RestAlpha, ControllerBlock));
        Add(builder, ControllerBlock, controllerType, controller);
        Add(builder, InterpolatorBlock, InterpolatorTypeOf(controllerType), interpolator);
        Add(builder, DataBlock, DataTypeOf(controllerType), data);
        return builder.Build();
    }

    private static string InterpolatorTypeOf(string controllerType)
    {
        return controllerType == MaterialColorController ? "NiPoint3Interpolator" : "NiFloatInterpolator";
    }

    private static string DataTypeOf(string controllerType)
    {
        return controllerType == MaterialColorController ? "NiPosData" : "NiFloatData";
    }

    /// <summary>
    ///     Root (node 0) over the quad Shape (node 1, data 2) carrying NiTexturingProperty 3 (Base map from NiSourceTexture
    ///     4 with the given rest transform, controller 5); 5 an NiTextureTransformController on the given slot and
    ///     operation over NiFloatInterpolator 6 and NiFloatData 7 (LINEAR keys).
    /// </summary>
    private static byte[] LayerFixture(bool bigEndian,
        (float Tu, float Tv, float Su, float Sv, float Rotation, uint Method, float Cu, float Cv) transform,
        uint slot, uint operation, params float[][] keys)
    {
        var builder = new NifTestFileBuilder(bigEndian, Bs);
        var root = builder.AddString("Root");
        var shape = builder.AddString("Shape");
        var file = builder.AddString(@"textures\fx\a.dds");
        Add(builder, 0, "NiNode", Node(root, [1]));
        AddQuad(builder, 1, shape, [TexturingBlock]);
        Add(builder, TexturingBlock, "NiTexturingProperty", TexturingProperty(4, transform, TextureTransformBlock));
        Add(builder, 4, "NiSourceTexture", SourceTexture(file));
        Add(builder, TextureTransformBlock, TextureTransformController,
            TextureTransformControllerBlock(TexturingBlock, 6, Active | ClampCycle, slot, operation));
        Add(builder, 6, "NiFloatInterpolator", NifModelAnimationMorphTestSupport.FloatInterpolator(7));
        Add(builder, 7, "NiFloatData", NifModelAnimationMorphTestSupport.FloatData(NifModelAnimationMorphTestSupport.Linear, keys));
        return builder.Build();
    }

    /// <summary>
    ///     Root (node 0) over the quad Shape (node 1, data 2, controller 5) carrying NiTexturingProperty 3 (Base map from
    ///     NiSourceTexture 4 at the identity Max rest); 5 an NiUVController (Texture Set 0) over NiUVData 6.
    /// </summary>
    private static byte[] UvFixture(bool bigEndian, float[][] uOffset, float[][] vOffset, float[][] uScale,
        float[][] vScale)
    {
        var builder = new NifTestFileBuilder(bigEndian, Bs);
        var root = builder.AddString("Root");
        var shape = builder.AddString("Shape");
        var file = builder.AddString(@"textures\fx\a.dds");
        Add(builder, 0, "NiNode", Node(root, [1]));
        AddQuad(builder, 1, shape, [3], 5);
        Add(builder, 3, "NiTexturingProperty", TexturingProperty(4, IdentityMax));
        Add(builder, 4, "NiSourceTexture", SourceTexture(file));
        Add(builder, 5, UvController, UvControllerBlock(1, 6, Active | ClampCycle));
        Add(builder, 6, "NiUVData", UvData(uOffset, vOffset, uScale, vScale));
        return builder.Build();
    }

    /// <summary>Root (node 0) over Lid (node 1, controller 2); 2 an NiVisController over NiBoolInterpolator 3 and NiBoolData 4 (CONST keys).</summary>
    /// <summary>As <see cref="VisibilityFixture" /> with the interpolator's static value and an empty NiBoolData.</summary>
    private static byte[] StaticVisibilityFixture(bool bigEndian, byte value)
    {
        var builder = new NifTestFileBuilder(bigEndian, Bs);
        var root = builder.AddString("Root");
        var lid = builder.AddString("Lid");
        Add(builder, 0, "NiNode", Node(root, [1]));
        Add(builder, 1, "NiNode", Node(lid, [], 2));
        Add(builder, 2, VisController, SingleInterpController(1, 3, Active | ClampCycle));
        Add(builder, 3, "NiBoolInterpolator", BoolInterpolator(4, value));
        Add(builder, 4, "NiBoolData", BoolData(NifModelAnimationMorphTestSupport.Constant));
        return builder.Build();
    }

    private static byte[] VisibilityFixture(bool bigEndian, params (float Time, byte Value)[] keys)
    {
        var builder = new NifTestFileBuilder(bigEndian, Bs);
        var root = builder.AddString("Root");
        var lid = builder.AddString("Lid");
        Add(builder, 0, "NiNode", Node(root, [1]));
        Add(builder, 1, "NiNode", Node(lid, [], 2));
        Add(builder, 2, VisController, SingleInterpController(1, 3, Active | ClampCycle));
        Add(builder, 3, "NiBoolInterpolator", BoolInterpolator(4));
        Add(builder, 4, "NiBoolData", BoolData(NifModelAnimationMorphTestSupport.Constant, keys));
        return builder.Build();
    }

    /// <summary>
    ///     Root (node 0) over ShapeA (node 1, data 2, properties 5 and 6) and ShapeB (node 3, data 4, property 5): the
    ///     shared NiMaterialProperty 5 (controller 7) and NiAlphaProperty 6 give two materials; 7 an NiAlphaController over
    ///     NiFloatInterpolator 8 and NiFloatData 9.
    /// </summary>
    private static byte[] SharedMaterialFixture(bool bigEndian)
    {
        var builder = new NifTestFileBuilder(bigEndian, Bs);
        var root = builder.AddString("Root");
        var shapeA = builder.AddString("ShapeA");
        var shapeB = builder.AddString("ShapeB");
        Add(builder, 0, "NiNode", Node(root, [1, 3]));
        AddQuad(builder, 1, shapeA, [5, 6]);
        AddQuad(builder, 3, shapeB, [5]);
        Add(builder, 5, "NiMaterialProperty", MaterialProperty(RestAlpha, 7));
        Add(builder, 6, "NiAlphaProperty", static w => NifTestBlockLayouts.AlphaProperty(w, 0x00ED, 128));
        Add(builder, 7, AlphaController, SingleInterpController(5, 8, Active | ClampCycle));
        Add(builder, 8, "NiFloatInterpolator", NifModelAnimationMorphTestSupport.FloatInterpolator(9));
        Add(builder, 9, "NiFloatData",
            NifModelAnimationMorphTestSupport.FloatData(NifModelAnimationMorphTestSupport.Linear, [0f, RestAlpha], [1f, 0.25f]));
        return builder.Build();
    }

    /// <summary>
    ///     Root (node 0, manager 3) over the quad Shape (node 1, data 2) carrying NiMaterialProperty 4 (controller 6);
    ///     manager 3 lists sequence 5 'Fade' with one NiAlphaController controlled block on 'Shape' (interpolator 7,
    ///     controller 6 or none, the given Property Type and Controller ID, priority 26), or two such blocks
    ///     (interpolator 10 as well) when repeated; 6 the manager-controlled NiAlphaController over NiBlendFloatInterpolator
    ///     8; 7 an NiFloatInterpolator over NiFloatData 9 (alpha 0.75 to 0.25); 10 another over 9.
    /// </summary>
    private static byte[] SequenceFixture(bool bigEndian, string propertyType, string? controllerId,
        bool storedControllerRef, bool repeat = false)
    {
        var builder = new NifTestFileBuilder(bigEndian, Bs);
        var root = builder.AddString("Root");
        var shape = builder.AddString("Shape");
        var fade = builder.AddString("Fade");
        var type = builder.AddString(AlphaController);
        var property = builder.AddString(propertyType);
        var id = controllerId is null ? -1 : builder.AddString(controllerId);
        var controller = storedControllerRef ? 6 : -1;
        var blocks = repeat
            ? new[]
            {
                Controlled(7, shape, type, 26, controller, property, id),
                Controlled(10, shape, type, 26, controller, property, id)
            }
            : new[] { Controlled(7, shape, type, 26, controller, property, id) };
        Add(builder, 0, "NiNode", Node(root, [1], 3));
        AddQuad(builder, 1, shape, [4]);
        Add(builder, 3, "NiControllerManager", Manager(0, [5], -1));
        Add(builder, 4, "NiMaterialProperty", MaterialProperty(RestAlpha, 6));
        Add(builder, 5, "NiControllerSequence", Sequence(fade, blocks, manager: 3));
        Add(builder, 6, AlphaController, SingleInterpController(4, 8, ManagerControlled | Active | ClampCycle));
        Add(builder, 7, "NiFloatInterpolator", NifModelAnimationMorphTestSupport.FloatInterpolator(9));
        Add(builder, 8, "NiBlendFloatInterpolator", NifModelAnimationMorphTestSupport.BlendFloatInterpolator());
        Add(builder, 9, "NiFloatData",
            NifModelAnimationMorphTestSupport.FloatData(NifModelAnimationMorphTestSupport.Linear, [0f, RestAlpha], [1f, 0.25f]));
        if (repeat)
        {
            Add(builder, 10, "NiFloatInterpolator", NifModelAnimationMorphTestSupport.FloatInterpolator(9));
        }

        return builder.Build();
    }

    /// <summary>
    ///     As <see cref="SequenceFixture" /> with an NiMaterialColorController: the sequence block names the given
    ///     Controller ID; 6 the manager-controlled controller storing Target Color 3 over NiBlendPoint3Interpolator 8; 7
    ///     an NiPoint3Interpolator over NiPosData 9.
    /// </summary>
    private static byte[] SequenceColorFixture(bool bigEndian, string controllerId)
    {
        var builder = new NifTestFileBuilder(bigEndian, Bs);
        var root = builder.AddString("Root");
        var shape = builder.AddString("Shape");
        var glow = builder.AddString("Glow");
        var type = builder.AddString(MaterialColorController);
        var property = builder.AddString("NiMaterialProperty");
        var id = builder.AddString(controllerId);
        Add(builder, 0, "NiNode", Node(root, [1], 3));
        AddQuad(builder, 1, shape, [4]);
        Add(builder, 3, "NiControllerManager", Manager(0, [5], -1));
        Add(builder, 4, "NiMaterialProperty", MaterialProperty(RestAlpha, 6));
        Add(builder, 5, "NiControllerSequence",
            Sequence(glow, [Controlled(7, shape, type, 26, 6, property, id)], manager: 3));
        Add(builder, 6, MaterialColorController,
            MaterialColorControllerBlock(4, 8, ManagerControlled | Active | ClampCycle, 3));
        Add(builder, 7, "NiPoint3Interpolator", Point3Interpolator(9));
        Add(builder, 8, "NiBlendPoint3Interpolator",
            static w => w.U8(1).U8(0).F32(0f).U32(InvalidFloatBits).U32(InvalidFloatBits).U32(InvalidFloatBits));
        Add(builder, 9, "NiPosData",
            PosData(NifModelAnimationMorphTestSupport.Linear, [0f, 0.2f, 0.4f, 0.6f], [1f, 1f, 0f, 0f]));
        return builder.Build();
    }

    /// <summary>
    ///     Root (node 0) over the quad Shape (node 1, data 2) carrying NiMaterialProperty 3 whose controller chain holds
    ///     two free-running NiAlphaControllers (4 then 7), each over its own NiFloatInterpolator (5, 8) and NiFloatData (6,
    ///     9).
    /// </summary>
    private static byte[] RepeatedEmbeddedFixture(bool bigEndian)
    {
        var builder = new NifTestFileBuilder(bigEndian, Bs);
        var root = builder.AddString("Root");
        var shape = builder.AddString("Shape");
        Add(builder, 0, "NiNode", Node(root, [1]));
        AddQuad(builder, 1, shape, [3]);
        Add(builder, 3, "NiMaterialProperty", MaterialProperty(RestAlpha, 4));
        Add(builder, 4, AlphaController, SingleInterpController(3, 5, Active | ClampCycle, next: 7));
        Add(builder, 5, "NiFloatInterpolator", NifModelAnimationMorphTestSupport.FloatInterpolator(6));
        Add(builder, 6, "NiFloatData",
            NifModelAnimationMorphTestSupport.FloatData(NifModelAnimationMorphTestSupport.Linear, [0f, RestAlpha], [1f, 0.25f]));
        Add(builder, 7, AlphaController, SingleInterpController(3, 8, Active | ClampCycle));
        Add(builder, 8, "NiFloatInterpolator", NifModelAnimationMorphTestSupport.FloatInterpolator(9));
        Add(builder, 9, "NiFloatData",
            NifModelAnimationMorphTestSupport.FloatData(NifModelAnimationMorphTestSupport.Linear, [0f, RestAlpha], [1f, 0.5f]));
        return builder.Build();
    }

    /// <summary>
    ///     As <see cref="MaterialFixture" /> with a SELF_ILLUM NiMaterialColorController over an
    ///     NiBSplineCompPoint3Interpolator (5) over NiBSplineData 6 (twelve compact controls, offset 0.5, half range 1)
    ///     and NiBSplineBasisData 7 (four control points), on the given interval.
    /// </summary>
    private static byte[] BsplineColorFixture(bool bigEndian, float start, float stop)
    {
        var builder = new NifTestFileBuilder(bigEndian, Bs);
        var root = builder.AddString("Root");
        var shape = builder.AddString("Shape");
        Add(builder, 0, "NiNode", Node(root, [1]));
        AddQuad(builder, 1, shape, [MaterialBlock]);
        Add(builder, MaterialBlock, "NiMaterialProperty", MaterialProperty(RestAlpha, ControllerBlock));
        Add(builder, ControllerBlock, MaterialColorController,
            MaterialColorControllerBlock(MaterialBlock, InterpolatorBlock, Active | ClampCycle, 3, start, stop));
        Add(builder, InterpolatorBlock, "NiBSplineCompPoint3Interpolator",
            CompPoint3Bspline(start, stop, DataBlock, 7, 0, 0.5f, 1f));
        Add(builder, DataBlock, "NiBSplineData", NifModelAnimationMorphTestSupport.BsplineData(
            0, 8192, 16384, 8192, 8192, 8192, 0, 0, 0, -16384, 0, 8192));
        Add(builder, 7, "NiBSplineBasisData", NifModelAnimationMorphTestSupport.BsplineBasis(4));
        return builder.Build();
    }
}
