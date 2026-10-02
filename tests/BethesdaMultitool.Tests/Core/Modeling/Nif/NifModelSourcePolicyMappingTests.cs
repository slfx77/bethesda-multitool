using System.Text;
using BethesdaMultitool.Core.Formats.Nif.Decoding;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;
using BethesdaMultitool.Core.Modeling.Nif;
using Slfx77.Multitool.Core.Models;
using Xunit;
using static BethesdaMultitool.Tests.Core.Modeling.Nif.NifModelAnimationTestSupport;

namespace BethesdaMultitool.Tests.Core.Modeling.Nif;

/// <summary>
///     SA7 source policies (<see cref="NifModelSourcePolicyMapping" />): the controlled block's Priority, the sequence's
///     Weight and Accum Root Name, provenance Authored, and nothing invented for an absent declaration. The policies are
///     attached to a document that Shared's public <see cref="ScenePoseEvaluator" /> validates.
/// </summary>
public sealed class NifModelSourcePolicyMappingTests
{
    [Fact]
    public void TrackPriority_IsTheControlledBlockByte()
    {
        var policy = Assert.IsType<SceneAnimationTrackSourcePolicy>(NifModelSourcePolicyMapping.MapTrack(Block(26)));

        Assert.Equal(26, policy.Priority);
        Assert.Equal(SceneValueProvenance.Authored, policy.Provenance);
        Assert.Equal(NifModelSourcePolicyMapping.PriorityEvidence, policy.Evidence);

        // Control: a stream without a Priority byte yields no policy rather than an invented priority 0.
        Assert.Null(NifModelSourcePolicyMapping.MapTrack(Block(null)));
    }

    /// <summary>
    ///     The weight is the stored Float32 and the accumulation root is the Latin-1 text of the stored bytes, a byte at
    ///     or above 0x80 included and trailing space kept.
    /// </summary>
    [Fact]
    public void SequencePolicy_KeepsTheWeightAndTheLatin1AccumRoot()
    {
        var strings = Strings("Idle", "Bip01 Réf ");

        var policy = NifModelSourcePolicyMapping.MapSequence(Sequence(Bits(0.75f), 1), strings, 0);

        Assert.Equal(0.75f, policy.Weight);
        Assert.Equal(SceneValueProvenance.Authored, policy.Provenance);
        var root = Assert.IsType<SceneAnimationAccumulationRoot>(policy.AccumulationRoot);
        Assert.Equal("Bip01 Réf ", root.SourceName);
        Assert.Equal(0, root.NodeIndex);
        Assert.Equal(SceneValueProvenance.Authored, root.Provenance);

        // Control: a sequence that names no root gets none, not the first string or an empty name.
        var noRoot = NifModelSourcePolicyMapping.MapSequence(Sequence(Bits(1f), NifAnimationStrings.NoString), strings);
        Assert.Null(noRoot.AccumulationRoot);
        Assert.Throws<ArgumentException>(() =>
            NifModelSourcePolicyMapping.MapSequence(Sequence(Bits(1f), NifAnimationStrings.NoString), strings, 0));
    }

    [Fact]
    public void MalformedDeclarations_Throw()
    {
        var strings = Strings("Idle");

        Assert.Throws<InvalidDataException>(() =>
            NifModelSourcePolicyMapping.MapSequence(Sequence(0x7FC00000, NifAnimationStrings.NoString), strings));
        Assert.Throws<InvalidDataException>(() =>
            NifModelSourcePolicyMapping.MapSequence(Sequence(Bits(1f), 5), strings));

        // Control: a finite weight and an in-range index map.
        Assert.Equal(1f, NifModelSourcePolicyMapping.MapSequence(Sequence(Bits(1f), 0), strings).Weight);
    }

    /// <summary>The typed policies travel on Shared's clip and track and pass its document validation.</summary>
    [Fact]
    public void Policies_AttachToASharedClip()
    {
        var strings = Strings("Bip01");
        var clipPolicy = NifModelSourcePolicyMapping.MapSequence(Sequence(Bits(1f), 0), strings, 0);
        var trackPolicy = NifModelSourcePolicyMapping.MapTrack(Block(3));
        var track = new SceneTransformTrack(0, SceneTransformProperty.Translation, [0f], [1f, 2f, 3f],
            sourcePolicy: trackPolicy);
        var document = Document(IdentityRest, track);
        var clip = new SceneAnimation("clip", [], transformTracks: [track], sourcePolicy: clipPolicy);
        var withPolicy = new ModelDocument(document.SourceFormat, document.Name, document.Scenes, document.Nodes,
            document.Meshes, animations: [clip]);

        var evaluator = new ScenePoseEvaluator(withPolicy, TestContext.Current.CancellationToken);

        Assert.Same(clipPolicy, evaluator.Document.Animations[0].SourcePolicy);
        Assert.Equal(3, evaluator.Document.Animations[0].TransformTracks[0].SourcePolicy!.Priority);

        // Control: a root bound past the document's nodes is refused by the same validation.
        var unbound = new SceneAnimation("clip", [], transformTracks: [track],
            sourcePolicy: NifModelSourcePolicyMapping.MapSequence(Sequence(Bits(1f), 0), strings, 7));
        Assert.Throws<InvalidDataException>(() => new ScenePoseEvaluator(
            new ModelDocument(document.SourceFormat, document.Name, document.Scenes, document.Nodes, document.Meshes,
                animations: [unbound]),
            TestContext.Current.CancellationToken));
    }

    /// <summary>A controlled block with the given Priority byte.</summary>
    private static NifControlledBlockView Block(byte? priority)
    {
        return new NifControlledBlockView(0, 1, 2, priority, 0, -1, -1, -1, -1);
    }

    /// <summary>A sequence with the given weight bits and Accum Root Name index.</summary>
    private static NifControllerSequenceView Sequence(uint weightBits, int accumRootNameIndex)
    {
        return new NifControllerSequenceView(0, 0, [], weightBits, -1, 2, Bits(1f), Bits(0f), Bits(1f), -1,
            accumRootNameIndex, null, null, true);
    }

    /// <summary>A raw string table whose entries are the Latin-1 bytes of the given texts.</summary>
    private static NifHeaderStringTable Strings(params string[] texts)
    {
        return new NifHeaderStringTable(0, 64, texts.Select(static text => Encoding.Latin1.GetBytes(text)).ToArray());
    }
}
