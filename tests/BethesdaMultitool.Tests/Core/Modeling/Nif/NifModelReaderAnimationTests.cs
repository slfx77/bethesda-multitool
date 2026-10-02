using BethesdaMultitool.Core.Modeling.Nif;
using BethesdaMultitool.Tests.Helpers;
using Slfx77.Multitool.Core.Models;
using Slfx77.Multitool.Core.Models.Sources;
using Xunit;
using static BethesdaMultitool.Tests.Core.Modeling.Nif.NifModelAnimationReaderTestSupport;
using static BethesdaMultitool.Tests.Core.Modeling.Nif.NifModelTestSupport;

namespace BethesdaMultitool.Tests.Core.Modeling.Nif;

/// <summary>
///     Cut-1b slice 10, the switch-over (owner ruling D1: no option switches it): <see cref="NifModelReader" /> runs the
///     animation stage on every <c>.nif</c>, so the document carries the clips, their <c>bmt.nif.animation.clip</c> rows
///     and the stage's diagnostic, and the animation blocks are classified by the stage (Typed, or its reasons) instead
///     of the retired 'later-cut(1b): animation'. Synthetic files written field by field, read through the real reader
///     contract, little- and big-endian; every test carries a control that fails.
/// </summary>
public sealed class NifModelReaderAnimationTests
{
    /// <summary>
    ///     0 NiNode Root [1] (controller 2); 1 NiNode Bone (controller 8); 2 NiControllerManager [3]; 3 NiControllerSequence
    ///     Idle driving Bone (4, 5) and the unbound name Ghost (6, 7); 8 an active, free-running NiTransformController on
    ///     Bone (9, 10). The document holds the Idle clip and the (controllers) clip, both on Bone's node; every animation
    ///     block is Typed except Ghost's two, which stay native with 'target not in the file'; one clip row per clip; the
    ///     stage's diagnostic names Ghost's reason. Control: no classification carries the pre-slice-10 'later-cut(1b)'
    ///     reason, which every one of these animation blocks had before the switch-over.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnimatedNif_YieldsClipsNativeRowsAndTypedCoverage(bool bigEndian)
    {
        var result = Read(AnimatedFixture(bigEndian));

        var document = result.Document;
        Assert.Equal(new[] { "Idle", NifModelAnimationReader.ControllersClipName },
            document.Animations.Select(static clip => clip.Name));
        var idle = document.Animations[0];
        Assert.NotNull(idle.Clock);
        Assert.Equal(3, idle.TransformTracks.Count);
        Assert.All(idle.TransformTracks, static track => Assert.Equal(1, track.NodeIndex));
        var translation = Assert.Single(idle.TransformTracks,
            static track => track.Property == SceneTransformProperty.Translation);
        Assert.Equal(new[] { 0f, 1f }, translation.Times);
        var controllers = document.Animations[1];
        Assert.Equal(3, controllers.TransformTracks.Count);
        Assert.All(controllers.TransformTracks, static track =>
        {
            Assert.Equal(1, track.NodeIndex);
            Assert.NotNull(track.Clock);
        });

        foreach (var block in new[] { 2, 3, 4, 5, 8, 9, 10 })
        {
            Assert.Equal(ModelSourceCoverageKind.Typed, result.Coverage.GetClassification($"block:{block}").Kind);
        }

        foreach (var block in new[] { 6, 7 })
        {
            var ghost = result.Coverage.GetClassification($"block:{block}");
            Assert.Equal(ModelSourceCoverageKind.NativeOnly, ghost.Kind);
            Assert.Equal(NifModelTargetNames.TargetNotInFileReason, ghost.Reason);
        }

        var clipRows = Rows(document, NifModelAnimationNativeState.Kind);
        Assert.Equal(
            new[] { new SceneElementRef(SceneElementKind.Animation, 0), new SceneElementRef(SceneElementKind.Animation, 1) },
            clipRows.Select(static row => row.Target));
        var diagnostic = Assert.Single(document.Diagnostics,
            static d => d.Code == NifModelAnimationDiagnostics.NativeBlocksDiagnostic);
        Assert.Contains(NifModelTargetNames.TargetNotInFileReason, DiagnosticText(diagnostic), StringComparison.Ordinal);
        SceneValidation.ValidateStructure(document);

        Assert.DoesNotContain(result.Coverage.Classifications,
            static c => c.Reason?.Contains("later-cut(1b)", StringComparison.Ordinal) == true);
    }

    /// <summary>
    ///     A static <c>.nif</c> gains no clip, no clip row and no animation diagnostic: the stage adds nothing where there
    ///     is no animation. Control: the animated fixture gains all three (see above).
    /// </summary>
    [Fact]
    public void StaticNif_GainsNoClipRowOrDiagnostic()
    {
        var builder = new NifTestFileBuilder(false, Bs);
        AddNode(builder, builder.AddString("Root"), []);

        var document = Read(builder.Build()).Document;

        Assert.Empty(document.Animations);
        Assert.Empty(Rows(document, NifModelAnimationNativeState.Kind));
        Assert.DoesNotContain(document.Diagnostics,
            static d => d.Code == NifModelAnimationDiagnostics.NativeBlocksDiagnostic);
        Assert.NotEmpty(Read(AnimatedFixture(false)).Document.Animations);
    }

    /// <summary>
    ///     The row budget (plan section 2.3) counts one row per clip and, for a <c>.kf</c>, the skeleton row: exactly
    ///     <see cref="ModelDocument.MaximumNativeStates" /> rows pass, one more clip or the skeleton row is not supported.
    ///     Control: the pre-slice-10 count (header and blocks alone) would have admitted both refused files.
    /// </summary>
    [Fact]
    public void RowBudget_CountsOneRowPerClip_AndTheSkeletonRow()
    {
        var blocks = ModelDocument.MaximumNativeStates - 3;

        NifModelReader.CheckRowBudget(blocks, 0, 0, 0, 0, 2, 0);
        var clip = Assert.Throws<NotSupportedException>(() => NifModelReader.CheckRowBudget(blocks, 0, 0, 0, 0, 3, 0));
        Assert.Throws<NotSupportedException>(() => NifModelReader.CheckRowBudget(blocks, 0, 0, 0, 0, 2, 1));

        Assert.Contains("3 clips", clip.Message, StringComparison.Ordinal);
        Assert.True(1L + blocks <= ModelDocument.MaximumNativeStates);
    }

    /// <summary>
    ///     The morph gate's two fixed answers: targets built without a geometry stage admit every occurrence (component
    ///     tests assemble their own documents), and a <c>.kf</c>'s skeleton-only targets admit none (its document has no
    ///     mesh). The reader-level control is <see cref="NifModelMorphTests" />: typed targets admit the channel, a vertex
    ///     count the geometry refused does not.
    /// </summary>
    [Fact]
    public void MorphGate_NoneAdmitsEveryOccurrence_SkeletonOnlyNone()
    {
        Assert.False(NifModelPropertyTargets.None.KnowsMeshes);
        Assert.True(NifModelPropertyTargets.None.AdmitsMorphChannels([0, 1], 2));
        Assert.True(NifModelPropertyTargets.SkeletonOnly.KnowsMeshes);
        Assert.False(NifModelPropertyTargets.SkeletonOnly.AdmitsMorphChannels([0, 1], 2));
    }

    /// <summary>The fixture of <see cref="AnimatedNif_YieldsClipsNativeRowsAndTypedCoverage" />.</summary>
    private static byte[] AnimatedFixture(bool bigEndian)
    {
        var builder = new NifTestFileBuilder(bigEndian, Bs);
        var root = builder.AddString("Root");
        var bone = builder.AddString("Bone");
        var idle = builder.AddString("Idle");
        var ghost = builder.AddString("Ghost");
        var type = builder.AddString(TransformController);
        Add(builder, 0, "NiNode", Node(root, [1], 2));
        Add(builder, 1, "NiNode", Node(bone, [], 8));
        Add(builder, 2, "NiControllerManager", Manager(0, [3], -1));
        Add(builder, 3, "NiControllerSequence",
            Sequence(idle, [Controlled(4, bone, type), Controlled(6, ghost, type)], manager: 2));
        Add(builder, 4, "NiTransformInterpolator", TransformInterpolator(5));
        Add(builder, 5, "NiTransformData", TransformData(NoRotation, (0f, 0f, 0f, 0f), (1f, 1f, 2f, 3f)));
        Add(builder, 6, "NiTransformInterpolator", TransformInterpolator(7));
        Add(builder, 7, "NiTransformData", TransformData(NoRotation, (0f, 0f, 0f, 0f), (1f, 4f, 5f, 6f)));
        Add(builder, 8, "NiTransformController", TransformControllerBlock(1, 9, Active | ClampCycle));
        Add(builder, 9, "NiTransformInterpolator", TransformInterpolator(10));
        Add(builder, 10, "NiTransformData", TransformData(NoRotation, (0f, 0f, 0f, 0f), (1f, 7f, 8f, 9f)));
        return builder.Build();
    }
}
