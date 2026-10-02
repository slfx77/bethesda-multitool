using BethesdaMultitool.Core.Formats.Nif.Schema;
using BethesdaMultitool.Core.Modeling.Nif;
using BethesdaMultitool.Tests.Helpers;
using Slfx77.Multitool.Core.Models.Sources;
using Xunit;
using static BethesdaMultitool.Tests.Core.Modeling.Nif.NifModelAnimationReaderTestSupport;
using static BethesdaMultitool.Tests.Core.Modeling.Nif.NifModelAnimationTargetTestSupport;

namespace BethesdaMultitool.Tests.Core.Modeling.Nif;

/// <summary>
///     Cut-1b slice 8, the coverage classifier (<see cref="NifModelAnimationCoverage" />; plan section 2.1): every table
///     row family through a synthetic file carrying that block type, Typed winning over NativeOnly on a shared data block
///     whatever the order, the particle reach, and the table's completeness over the census controller and interpolator
///     type names. Every test carries a control that fails.
/// </summary>
public sealed class NifModelAnimationCoverageTests
{
    /// <summary>
    ///     The embedded controller types of the FNV and FO3 corpus census (65,764 distinct files): the
    ///     <c>controllersByType</c> keys of <c>TestOutput/cut1b-prep-20260925/corpus-receipt-all.json</c>, the needs doc
    ///     <c>TestOutput/cut1b-prep-20260925/animation-vocabulary-needs.md</c> sections 3.2 and 3.5, and
    ///     <c>docs/formats/nif-animation-engine-behavior-20260925.md</c> (RE-22's controller census); the <c>seq:</c>
    ///     entries name the same types plus BSTreadTransfController, which appears only as a controlled block's
    ///     Controller Type string.
    /// </summary>
    private static readonly string[] CensusControllerTypes =
    [
        "BSFrustumFOVController", "BSMaterialEmittanceMultController", "BSPSysMultiTargetEmitterCtlr",
        "BSRefractionFirePeriodController", "BSRefractionStrengthController", "BSTreadTransfController",
        "NiAlphaController", "NiBSBoneLODController", "NiControllerManager", "NiFloatExtraDataController",
        "NiGeomMorpherController", "NiLightColorController", "NiLightDimmerController", "NiMaterialColorController",
        "NiMultiTargetTransformController", "NiPSysEmitterCtlr", "NiPSysEmitterDeclinationCtlr",
        "NiPSysEmitterDeclinationVarCtlr", "NiPSysEmitterInitialRadiusCtlr", "NiPSysEmitterLifeSpanCtlr",
        "NiPSysEmitterPlanarAngleCtlr", "NiPSysEmitterPlanarAngleVarCtlr", "NiPSysEmitterSpeedCtlr",
        "NiPSysGravityStrengthCtlr", "NiPSysInitialRotAngleCtlr", "NiPSysInitialRotSpeedCtlr",
        "NiPSysInitialRotSpeedVarCtlr", "NiPSysModifierActiveCtlr", "NiPSysResetOnLoopCtlr", "NiPSysUpdateCtlr",
        "NiTextureTransformController", "NiTransformController", "NiVisController", "bhkBlendController"
    ];

    /// <summary>
    ///     The interpolator types of the same census (<c>interpolatorsByType</c> in <c>corpus-receipt-all.json</c> and
    ///     <c>crosscut.json</c>, the needs doc sections 3.1 and 4).
    /// </summary>
    private static readonly string[] CensusInterpolatorTypes =
    [
        "BSRotAccumTransfInterpolator", "BSTreadTransfInterpolator", "NiBSplineCompFloatInterpolator",
        "NiBSplineCompPoint3Interpolator", "NiBSplineCompTransformInterpolator", "NiBSplineTransformInterpolator",
        "NiBlendBoolInterpolator", "NiBlendFloatInterpolator", "NiBlendPoint3Interpolator", "NiBoolInterpolator",
        "NiBoolTimelineInterpolator", "NiFloatInterpolator", "NiLookAtInterpolator", "NiPathInterpolator",
        "NiPoint3Interpolator", "NiTransformInterpolator"
    ];

    /// <summary>
    ///     One test per table row family: a file carrying the block gets the row's disposition and code, through the
    ///     reader (which decides the transform controller, since slice 14 the property and visibility controllers, the
    ///     manager, the multi-target controller and the blend interpolators; an unreferenced controller with no
    ///     interpolator or data gets 'no interpolator') and the classifier (every other row, including the property and
    ///     visibility interpolators and data no clip reaches). The camera, light, Havok and particle controllers get
    ///     their own rows although they are NiFloatInterpController, NiPoint3InterpController and NiTimeController
    ///     subclasses (the category-order fix). Control: BSTreeNode is not an animation block and gets no row.
    /// </summary>
    [Theory]
    [InlineData("NiAlphaController", NifModelAnimationReasons.NoInterpolatorCode)]
    [InlineData("NiMaterialColorController", NifModelAnimationReasons.NoInterpolatorCode)]
    [InlineData("BSMaterialEmittanceMultController", NifModelAnimationReasons.NoInterpolatorCode)]
    [InlineData("NiTextureTransformController", NifModelAnimationReasons.NoInterpolatorCode)]
    [InlineData("NiUVController", NifModelAnimationReasons.NoInterpolatorCode)]
    [InlineData("NiFloatInterpolator", NifModelAnimationCoverage.PropertyUnreachedCode)]
    [InlineData("NiPoint3Interpolator", NifModelAnimationCoverage.PropertyUnreachedCode)]
    [InlineData("NiBSplineCompFloatInterpolator", NifModelAnimationCoverage.PropertyUnreachedCode)]
    [InlineData("NiBSplineCompPoint3Interpolator", NifModelAnimationCoverage.PropertyUnreachedCode)]
    [InlineData("NiFloatData", NifModelAnimationCoverage.PropertyUnreachedCode)]
    [InlineData("NiPosData", NifModelAnimationCoverage.PropertyUnreachedCode)]
    [InlineData("NiColorData", NifModelAnimationCoverage.PropertyUnreachedCode)]
    [InlineData("NiUVData", NifModelAnimationCoverage.PropertyUnreachedCode)]
    [InlineData("NiVisController", NifModelAnimationReasons.NoInterpolatorCode)]
    [InlineData("NiBoolInterpolator", NifModelAnimationCoverage.VisibilityUnreachedCode)]
    [InlineData("NiBoolTimelineInterpolator", NifModelAnimationCoverage.VisibilityUnreachedCode)]
    [InlineData("NiBoolData", NifModelAnimationCoverage.VisibilityUnreachedCode)]
    [InlineData("NiVisData", NifModelAnimationCoverage.VisibilityUnreachedCode)]
    [InlineData("BSFrustumFOVController", NifModelAnimationCoverage.CameraLightCode)]
    [InlineData("NiLightColorController", NifModelAnimationCoverage.CameraLightCode)]
    [InlineData("NiLightDimmerController", NifModelAnimationCoverage.CameraLightCode)]
    [InlineData("BSRefractionStrengthController", NifModelAnimationCoverage.RefractionCode)]
    [InlineData("BSRefractionFirePeriodController", NifModelAnimationCoverage.RefractionCode)]
    [InlineData("NiFloatExtraDataController", NifModelAnimationCoverage.ExtraDataControllerCode)]
    [InlineData("NiBSBoneLODController", NifModelAnimationCoverage.BoneLodCode)]
    [InlineData("bhkBlendController", NifModelAnimationCoverage.HavokCode)]
    [InlineData("NiPSysUpdateCtlr", NifModelAnimationCoverage.ParticlesCode)]
    [InlineData("NiPSysEmitterCtlr", NifModelAnimationCoverage.ParticlesCode)]
    [InlineData("BSPSysMultiTargetEmitterCtlr", NifModelAnimationCoverage.ParticlesCode)]
    [InlineData("NiPSysModifierActiveCtlr", NifModelAnimationCoverage.ParticlesCode)]
    [InlineData("BSAnimNotes", NifModelAnimationCoverage.AnimNotesCode)]
    [InlineData("BSAnimNote", NifModelAnimationCoverage.AnimNotesCode)]
    [InlineData("NiTextKeyExtraData", NifModelAnimationCoverage.TextKeysOutsideClipCode)]
    [InlineData("NiControllerSequence", NifModelAnimationCoverage.SequenceUnlistedCode)]
    [InlineData("NiControllerManager", NifModelAnimationReasons.ManagerNoClipCode)]
    [InlineData("NiDefaultAVObjectPalette", NifModelAnimationCoverage.PaletteNoBindingCode)]
    [InlineData("NiMultiTargetTransformController", NifModelAnimationReasons.MultiTargetBindingCode)]
    [InlineData("BSTreadTransfInterpolator", NifModelAnimationReasons.TreadTransformCode)]
    [InlineData("BSRotAccumTransfInterpolator", NifModelAnimationReasons.RotationAccumulationCode)]
    [InlineData("NiPathInterpolator", NifModelAnimationReasons.PathLookAtCode)]
    [InlineData("NiLookAtInterpolator", NifModelAnimationReasons.PathLookAtCode)]
    [InlineData("NiBlendFloatInterpolator", NifModelAnimationReasons.BlendStateCode)]
    [InlineData("NiBlendBoolInterpolator", NifModelAnimationReasons.BlendStateCode)]
    [InlineData("NiBlendPoint3Interpolator", NifModelAnimationReasons.BlendStateCode)]
    [InlineData("NiBlendTransformInterpolator", NifModelAnimationReasons.BlendStateCode)]
    [InlineData("NiTransformInterpolator", NifModelAnimationCoverage.CurveUnreachedCode)]
    [InlineData("NiBSplineCompTransformInterpolator", NifModelAnimationCoverage.CurveUnreachedCode)]
    [InlineData("NiTransformData", NifModelAnimationCoverage.CurveUnreachedCode)]
    [InlineData("NiBSplineData", NifModelAnimationCoverage.CurveUnreachedCode)]
    [InlineData("NiBSplineBasisData", NifModelAnimationCoverage.CurveUnreachedCode)]
    [InlineData("NiTransformController", NifModelAnimationReasons.NoInterpolatorCode)]
    [InlineData("NiStringPalette", NifModelAnimationCoverage.StringPaletteCode)]
    public void ClassifyFile_GivesEveryRowFamilyItsCode(string type, string expectedCode)
    {
        var (state, graph) = ReadGraph(CarrierFixture(type));

        var result = NifModelAnimationReader.ReadNif(state, graph, TestContext.Current.CancellationToken);
        var classifications = NifModelAnimationCoverage.ClassifyFile(state, result,
            TestContext.Current.CancellationToken);

        var carrier = Assert.Single(classifications, static c => c.Block == 1);
        Assert.Equal(ModelSourceCoverageKind.NativeOnly, carrier.Kind);
        Assert.Equal(expectedCode, carrier.Code);
        Assert.False(string.IsNullOrWhiteSpace(carrier.Reason));
        Assert.DoesNotContain(classifications, static c => c.Block == 0);
        Assert.Null(NifModelAnimationCoverage.Classify(state.Schema, 0, "BSTreeNode", []));
    }

    /// <summary>
    ///     Every row the plan wrote as pending a Shared form names the settled state: property and visibility map since
    ///     slice 14, so their table rows say only 'not reached by any clip', never 'not mapped yet' or 'no Shared form';
    ///     the Euler and Squad rotations map since slices 13 and 16b, so their remaining reasons are engine guards that
    ///     cite RE-20 or RE-24 and say neither. Control: the unknown-key-type refusal still says 'no Shared form', so the
    ///     assertion discriminates.
    /// </summary>
    [Fact]
    public void PendingRows_NameTheSettledState()
    {
        foreach (var reason in new[]
                 {
                     NifModelAnimationCoverage.PropertyUnreachedReason, NifModelAnimationCoverage.VisibilityUnreachedReason
                 })
        {
            Assert.Contains("not reached by any clip", reason, StringComparison.Ordinal);
            Assert.DoesNotContain("not mapped yet", reason, StringComparison.Ordinal);
            Assert.DoesNotContain("no Shared form", reason, StringComparison.Ordinal);
        }

        foreach (var block in new[]
                 {
                     NifModelCurveBlock.EulerRecordCount, NifModelCurveBlock.EulerAxisKeyType,
                     NifModelCurveBlock.EulerSampledBeforeFirstKey
                 })
        {
            Assert.Contains("RE-20", NifModelAnimationReasons.Reason(block), StringComparison.Ordinal);
            Assert.DoesNotContain("not mapped yet", NifModelAnimationReasons.Reason(block), StringComparison.Ordinal);
            Assert.DoesNotContain("no Shared form", NifModelAnimationReasons.Reason(block), StringComparison.Ordinal);
        }

        foreach (var block in new[] { NifModelCurveBlock.SquadZeroLengthSpan, NifModelCurveBlock.SquadPolicyPs3 })
        {
            Assert.Contains("RE-24", NifModelAnimationReasons.Reason(block), StringComparison.Ordinal);
            Assert.DoesNotContain("not mapped yet", NifModelAnimationReasons.Reason(block), StringComparison.Ordinal);
        }

        Assert.Contains("no Shared form", NifModelAnimationReasons.Reason(NifModelCurveBlock.UnknownKeyType),
            StringComparison.Ordinal);
    }

    /// <summary>
    ///     Typed wins on a shared data block whatever the order the decisions were made in: an NiTransformData (block 5)
    ///     fed by a typed sequence and by an inactive free-running controller is Typed through the file, and the pure
    ///     classifier gives Typed for both orders. Control: a first-wins fold over the same decisions gives NativeOnly
    ///     for one order, so the rule is not order dependent by accident.
    /// </summary>
    [Fact]
    public void TypedWins_OverNativeOnly_WhateverTheOrder()
    {
        var (state, graph) = ReadGraph(SharedDataFixture());

        var result = NifModelAnimationReader.ReadNif(state, graph, TestContext.Current.CancellationToken);
        var classifications = NifModelAnimationCoverage.ClassifyFile(state, result,
            TestContext.Current.CancellationToken);

        var data = Assert.Single(classifications, static c => c.Block == 5);
        Assert.True(data.IsTyped);
        Assert.Equal(NifModelAnimationReasons.TypedCode, data.Code);
        Assert.True(Assert.Single(classifications, static c => c.Block == 4).IsTyped);
        var controller = Assert.Single(classifications, static c => c.Block == 6);
        Assert.Equal(NifModelAnimationReasons.Code(NifModelCurveBlock.InactiveController), controller.Code);
        var decisions = DecisionsFor(result, 5);
        Assert.Contains(decisions, static d => d.IsTyped);
        Assert.Contains(decisions, static d => !d.IsTyped);

        var typed = decisions.First(static d => d.IsTyped);
        var native = decisions.First(static d => !d.IsTyped);
        var forward = NifModelAnimationCoverage.Classify(state.Schema, 5, "NiTransformData", [native, typed]);
        var backward = NifModelAnimationCoverage.Classify(state.Schema, 5, "NiTransformData", [typed, native]);
        Assert.NotNull(forward);
        Assert.NotNull(backward);
        Assert.True(forward.Value.IsTyped);
        Assert.True(backward.Value.IsTyped);

        NifModelAnimationDisposition[] nativeFirst = [native, typed];
        var firstWins = nativeFirst[0].Disposition;
        Assert.False(firstWins.IsTyped);
        Assert.NotEqual(firstWins.IsTyped, forward.Value.IsTyped);
    }

    /// <summary>
    ///     An interpolator and its data reached only from a particle controller get the particles row; the same blocks
    ///     also reached from an NiAlphaController get that controller's reader decision instead (control: its target is
    ///     not a property block), because they are no longer reached only from particles.
    /// </summary>
    [Fact]
    public void ParticleReach_MarksBlocksReachedOnlyFromParticleControllers()
    {
        var (state, graph) = ReadGraph(ParticleFixture(false));
        var result = NifModelAnimationReader.ReadNif(state, graph, TestContext.Current.CancellationToken);
        var classifications = NifModelAnimationCoverage.ClassifyFile(state, result,
            TestContext.Current.CancellationToken);
        Assert.Equal(new[] { 2, 3 }, NifModelAnimationCoverage.ParticleReach(state, TestContext.Current.CancellationToken)
            .OrderBy(static b => b));
        foreach (var block in new[] { 1, 2, 3 })
        {
            Assert.Equal(NifModelAnimationCoverage.ParticlesCode,
                Assert.Single(classifications, c => c.Block == block).Code);
        }

        var (controlState, controlGraph) = ReadGraph(ParticleFixture(true));
        var control = NifModelAnimationReader.ReadNif(controlState, controlGraph,
            TestContext.Current.CancellationToken);
        var controlRows = NifModelAnimationCoverage.ClassifyFile(controlState, control,
            TestContext.Current.CancellationToken);
        Assert.Empty(NifModelAnimationCoverage.ParticleReach(controlState, TestContext.Current.CancellationToken));
        foreach (var block in new[] { 2, 3 })
        {
            Assert.Equal(NifModelAnimationReasons.ControllerTargetNotPropertyCode,
                Assert.Single(controlRows, c => c.Block == block).Code);
        }

        Assert.Equal(NifModelAnimationCoverage.ParticlesCode, Assert.Single(controlRows, static c => c.Block == 1).Code);
    }

    /// <summary>
    ///     Every controller and interpolator type of the corpus census has a table row with a non-empty code, so a type
    ///     the census knows can never fall outside the classification. Control: a node type gets no row, so a census
    ///     entry without a row would fail this check.
    /// </summary>
    [Fact]
    public void TableCompleteness_EveryCensusTypeHasARow()
    {
        var schema = NifSchema.LoadEmbedded();
        Assert.Equal(34, CensusControllerTypes.Length);
        Assert.Equal(16, CensusInterpolatorTypes.Length);
        foreach (var type in CensusControllerTypes.Concat(CensusInterpolatorTypes))
        {
            var row = NifModelAnimationCoverage.TableRow(schema, type);
            Assert.True(row.HasValue, $"{type} has no table row.");
            Assert.False(string.IsNullOrWhiteSpace(row!.Value.Code), $"{type} has no code.");
            Assert.False(string.IsNullOrWhiteSpace(row.Value.Reason), $"{type} has no reason.");
            Assert.True(NifModelAnimationCoverage.IsAnimationType(schema, type), $"{type} is not an animation type.");
        }

        Assert.Null(NifModelAnimationCoverage.TableRow(schema, "BSTreeNode"));
        Assert.Null(NifModelAnimationCoverage.TableRow(schema, "NiNode"));
        Assert.False(NifModelAnimationCoverage.IsAnimationType(schema, "NiTriShape"));
    }

    /// <summary>Root (node 0) and one block of the given type with a minimal body the reader does not need to decode.</summary>
    private static byte[] CarrierFixture(string type)
    {
        var builder = new NifTestFileBuilder(false, Bs);
        var root = builder.AddString("Root");
        var name = builder.AddString("Carrier");
        var schema = NifSchema.LoadEmbedded();
        Add(builder, 0, "NiNode", Node(root, []));
        Action<NifTestBlockWriter> body = type switch
        {
            "NiControllerSequence" => Sequence(name, []),
            "NiControllerManager" => Manager(-1, [], -1),
            "NiMultiTargetTransformController" => MultiTarget(0, Active | ClampCycle),
            "NiDefaultAVObjectPalette" => static w => NifTestBlockLayouts.DefaultAvObjectPalette(w, 0, []),
            "NiTextKeyExtraData" => TextKeys(),
            "NiStringPalette" => static w => w.SizedString("").U32(0),
            "BSAnimNotes" => static w => w.U16(0),
            "NiUVController" => static w =>
            {
                TimeController(w, -1, Active | ClampCycle, 1f, 0f, 0f, 1f, -1);
                w.U16(0).Ref(-1);
            },
            "NiTransformInterpolator" => TransformInterpolator(-1),
            "BSRotAccumTransfInterpolator" => TransformInterpolator(-1),
            "NiBlendFloatInterpolator" or "NiBlendBoolInterpolator" or "NiBlendPoint3Interpolator" or
                "NiBlendTransformInterpolator" => NifModelAnimationMorphTestSupport.BlendFloatInterpolator(),
            _ when schema.Inherits(type, "NiTimeController") => w =>
            {
                TimeController(w, -1, Active | ClampCycle, 1f, 0f, 0f, 1f, -1);
                w.Ref(-1).StringIndex(-1).Ref(-1);
            },
            _ => static w => w.U32(0).U32(0).U32(0).U32(0).U32(0).U32(0).U32(0).U32(0)
        };
        Add(builder, 1, type, body);
        return builder.Build();
    }

    /// <summary>
    ///     Root (node 0, manager 2) with child Bone (node 1, free-running INACTIVE NiTransformController 6); sequence 3
    ///     'Idle' drives Bone through interpolator 4 over NiTransformData 5; controller 6 targets Bone through the same
    ///     interpolator 4, so blocks 4 and 5 receive a Typed decision (the sequence) and a NativeOnly one (the inactive
    ///     controller).
    /// </summary>
    private static byte[] SharedDataFixture()
    {
        var builder = new NifTestFileBuilder(false, Bs);
        var root = builder.AddString("Root");
        var bone = builder.AddString("Bone");
        var idle = builder.AddString("Idle");
        var type = builder.AddString(TransformController);
        Add(builder, 0, "NiNode", Node(root, [1], 2));
        Add(builder, 1, "NiNode", Node(bone, [], 6));
        Add(builder, 2, "NiControllerManager", Manager(0, [3], -1));
        Add(builder, 3, "NiControllerSequence", Sequence(idle, [Controlled(4, bone, type)], manager: 2));
        Add(builder, 4, "NiTransformInterpolator", TransformInterpolator(5));
        Add(builder, 5, "NiTransformData", TransformData(NoRotation, (0f, 0f, 0f, 0f), (1f, 1f, 2f, 3f)));
        Add(builder, 6, "NiTransformController", TransformControllerBlock(1, 4, ClampCycle));
        return builder.Build();
    }

    /// <summary>
    ///     Root (node 0); an NiPSysEmitterCtlr (block 1) whose Interpolator is NiFloatInterpolator 2 over NiFloatData 3;
    ///     with <paramref name="alsoFromAlpha" /> an NiAlphaController (block 4) drives the same interpolator.
    /// </summary>
    private static byte[] ParticleFixture(bool alsoFromAlpha)
    {
        var builder = new NifTestFileBuilder(false, Bs);
        var root = builder.AddString("Root");
        Add(builder, 0, "NiNode", Node(root, []));
        Add(builder, 1, "NiPSysEmitterCtlr", static w =>
        {
            TimeController(w, -1, Active | ClampCycle, 1f, 0f, 0f, 1f, -1);
            w.Ref(2).StringIndex(-1).Ref(-1);
        });
        Add(builder, 2, "NiFloatInterpolator", NifModelAnimationMorphTestSupport.FloatInterpolator(3));
        Add(builder, 3, "NiFloatData",
            NifModelAnimationMorphTestSupport.FloatData(NifModelAnimationMorphTestSupport.Linear, [0f, 0f], [1f, 1f]));
        if (alsoFromAlpha)
        {
            Add(builder, 4, "NiAlphaController", static w =>
            {
                TimeController(w, -1, Active | ClampCycle, 1f, 0f, 0f, 1f, -1);
                w.Ref(2);
            });
        }

        return builder.Build();
    }
}
