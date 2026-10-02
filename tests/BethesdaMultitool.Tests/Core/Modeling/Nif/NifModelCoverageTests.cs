using BethesdaMultitool.Core.Formats.Nif.Schema;
using BethesdaMultitool.Core.Modeling.Nif;
using BethesdaMultitool.Tests.Helpers;
using Slfx77.Multitool.Core.Models;
using Slfx77.Multitool.Core.Models.Sources;
using Xunit;
using static BethesdaMultitool.Tests.Core.Modeling.Nif.NifModelTestSupport;

namespace BethesdaMultitool.Tests.Core.Modeling.Nif;

/// <summary>
///     The source census is the header block table, every block classified exactly once. The expected census here is
///     written from the fixture's own block list, independently of the reader, and a control shows that dropping one
///     element is detected both by that comparison and by Shared's coverage contract.
/// </summary>
public class NifModelCoverageTests
{
    /// <summary>
    ///     0 NiNode root (properties [2], children [1, 3]); 1 NiNode child; 2 NiAlphaProperty on the root; 3 NiTriShape
    ///     child with no data link; 4 NiNode orphan; 5 NiAlphaProperty orphan. The root's alpha is reachable but no
    ///     drawable geometry inherits it (the only shape has no data), so it stays NativeOnly with that reason.
    /// </summary>
    private static readonly (string Identity, string Kind)[] IndependentCensus =
    [
        ("block:0", "NiNode"),
        ("block:1", "NiNode"),
        ("block:2", "NiAlphaProperty"),
        ("block:3", "NiTriShape"),
        ("block:4", "NiNode"),
        ("block:5", "NiAlphaProperty")
    ];

    private static byte[] Fixture(bool bigEndian, uint bs)
    {
        var builder = new NifTestFileBuilder(bigEndian, bs);
        AddNode(builder, builder.AddString("Root"), [1, 3], properties: [2]);
        AddNode(builder, builder.AddString("Child"), []);
        AddAlphaProperty(builder);
        AddTriShape(builder, builder.AddString("Shape"));
        AddNode(builder, builder.AddString("Orphan"), []);
        AddAlphaProperty(builder);
        return builder.Build();
    }

    [Theory]
    [InlineData(false, 14u)]
    [InlineData(false, 34u)]
    [InlineData(true, 21u)]
    [InlineData(true, 34u)]
    public void Coverage_IsTheHeaderBlockTable_ClassifiedExactlyOnce(bool bigEndian, uint bs)
    {
        var result = Read(Fixture(bigEndian, bs));
        var coverage = result.Coverage;

        Assert.True(CensusMatches(IndependentCensus, coverage.Elements));
        Assert.Equal(IndependentCensus.Select(e => e.Identity), coverage.Classifications.Select(c => c.ElementIdentity));
        Assert.Equal(
            $"NIF header block table: 6 blocks, 3 types (20.2.0.7/11/{bs}, {(bigEndian ? "BE" : "LE")})",
            coverage.CensusEvidence);

        AssertRow(coverage, "block:0", ModelSourceCoverageKind.Typed, null);
        AssertRow(coverage, "block:1", ModelSourceCoverageKind.Typed, null);
        AssertRow(coverage, "block:2", ModelSourceCoverageKind.NativeOnly, NifModelCoverage.UninheritedPropertyReason);
        AssertRow(coverage, "block:3", ModelSourceCoverageKind.NativeOnly, NifModelCoverage.EmptyGeometryReason);
        AssertRow(coverage, "block:4", ModelSourceCoverageKind.NativeOnly, NifModelCoverage.UnreachableReason);
        AssertRow(coverage, "block:5", ModelSourceCoverageKind.NativeOnly, NifModelCoverage.UnreachableReason);
        Assert.Equal(6, coverage.TotalCount);
        Assert.Equal(2, coverage.TypedCount);
        Assert.Equal(4, coverage.NativeOnlyCount);
        Assert.Equal(0, coverage.DroppedCount);

        // The geometry block is still placed (NativeOnly means it yields no drawable triangles, not that it vanished),
        // and every fixture layout decoded exactly.
        var document = result.Document;
        Assert.Equal(["Root", "Child", "Shape"], document.Nodes.Select(n => n.Name));
        Assert.Equal(SceneNodeRole.Transform, document.Nodes[2].Role);
        Assert.DoesNotContain(document.Diagnostics, d => d.Code == NifModelReader.DecodeIncompleteDiagnostic);
        SceneValidation.ValidateStructure(document);
    }

    /// <summary>
    ///     Control: a census with one block removed no longer matches the independent header list, and Shared refuses a
    ///     coverage built from it (the reader's row for the removed block is then foreign). Removing a classification
    ///     instead is refused as an omission.
    /// </summary>
    [Fact]
    public void Coverage_Control_RemovingOneCensusElement_IsDetected()
    {
        var coverage = Read(Fixture(false, 34)).Coverage;
        var reduced = coverage.Elements.Where(e => e.Identity != "block:4").ToList();

        Assert.True(CensusMatches(IndependentCensus, coverage.Elements));
        Assert.False(CensusMatches(IndependentCensus, reduced));
        Assert.Throws<ArgumentException>(() =>
            new ModelSourceCoverage(coverage.Source, coverage.CensusEvidence, reduced, coverage.Classifications));
        Assert.Throws<ArgumentException>(() =>
            new ModelSourceCoverage(coverage.Source, coverage.CensusEvidence, coverage.Elements,
                coverage.Classifications.Skip(1)));
    }

    /// <summary>A block referenced only through a Ref (a property) is reachable; the same type unreferenced is not.</summary>
    [Fact]
    public void Reachability_FollowsReferences_NotJustChildren()
    {
        var coverage = Read(Fixture(false, 34)).Coverage;

        Assert.NotEqual(NifModelCoverage.UnreachableReason, coverage.GetClassification("block:2").Reason);
        Assert.Equal(NifModelCoverage.UnreachableReason, coverage.GetClassification("block:5").Reason);
    }

    /// <summary>
    ///     The category table: each row pins a distinct reason, and the plan's node types are Typed (null), billboard,
    ///     switch and LOD nodes included since slice 8. The three geometry types are null here because the geometry reader
    ///     decides each placed block (Typed, or NativeOnly with its own reason); the data and skin rows name blocks that no
    ///     drawable placed geometry uses (the skin reader decides the others); a packed block no placed geometry
    ///     references is unused geometry data like them (a referenced one is decided by the geometry reader, slice 10).
    ///     No row names a later slice of cut 1a.
    /// </summary>
    [Theory]
    [InlineData("NiNode", null)]
    [InlineData("BSFadeNode", null)]
    [InlineData("BSOrderedNode", null)]
    [InlineData("BSDamageStage", null)]
    [InlineData("BSMasterParticleSystem", null)]
    [InlineData("NiBillboardNode", null)]
    [InlineData("NiSwitchNode", null)]
    [InlineData("NiLODNode", null)]
    [InlineData("BSTreeNode", NifModelCoverage.OtherNodeReason)]
    [InlineData("NiTriShape", null)]
    [InlineData("NiTriStrips", null)]
    [InlineData("BSSegmentedTriShape", null)]
    [InlineData("BSLODTriShape", NifModelCoverage.OtherGeometryReason)]
    [InlineData("NiTriShapeData", NifModelCoverage.UnusedGeometryDataReason)]
    [InlineData("NiTriStripsData", NifModelCoverage.UnusedGeometryDataReason)]
    [InlineData("BSPackedAdditionalGeometryData", NifModelCoverage.UnusedGeometryDataReason)]
    [InlineData("NiMorphData", NifModelCoverage.UnusedGeometryDataReason)]
    [InlineData("NiAdditionalGeometryData", NifModelCoverage.AdditionalGeometryReason)]
    [InlineData("NiSkinInstance", NifModelCoverage.UnusedSkinReason)]
    [InlineData("BSDismemberSkinInstance", NifModelCoverage.UnusedSkinReason)]
    [InlineData("NiSkinData", NifModelCoverage.UnusedSkinReason)]
    [InlineData("NiSkinPartition", NifModelCoverage.UnusedSkinReason)]
    [InlineData("NiRangeLODData", NifModelCoverage.LodDataReason)]
    [InlineData("NiScreenLODData", NifModelCoverage.LodDataReason)]
    [InlineData("NiAlphaProperty", NifModelCoverage.UninheritedPropertyReason)]
    [InlineData("BSShaderPPLightingProperty", NifModelCoverage.UninheritedPropertyReason)]
    [InlineData("BSShaderTextureSet", NifModelCoverage.UnboundTextureReason)]
    [InlineData("WaterShaderProperty", NifModelCoverage.WaterReason)]
    [InlineData("NiFogProperty", NifModelCoverage.NoVocabularyReason)]
    [InlineData("NiSourceTexture", NifModelCoverage.UnboundTextureReason)]
    [InlineData("NiPixelData", NifModelCoverage.PixelDataReason)]
    [InlineData("NiCamera", NifModelCoverage.CameraLightReason)]
    [InlineData("NiPointLight", NifModelCoverage.CameraLightReason)]
    [InlineData("NiParticleSystem", NifModelCoverage.ParticleNodeReason)]
    [InlineData("BSStripParticleSystem", NifModelCoverage.ParticleNodeReason)]
    [InlineData("NiPSysGravityModifier", NifModelCoverage.ParticleReason)]
    [InlineData("NiPSysData", NifModelCoverage.ParticleReason)]
    [InlineData("BSWindModifier", NifModelCoverage.ParticleReason)]
    [InlineData("bhkCollisionObject", NifModelCoverage.HavokReason)]
    [InlineData("bhkBlendController", NifModelCoverage.HavokReason)]
    [InlineData("hkPackedNiTriStripsData", NifModelCoverage.HavokReason)]
    [InlineData("BSMultiBound", NifModelCoverage.MultiBoundReason)]
    [InlineData("BSMultiBoundAABB", NifModelCoverage.MultiBoundReason)]
    [InlineData("NiTransformController", NifModelAnimationCoverage.ControllerUnreachedReason)]
    [InlineData("NiGeomMorpherController", NifModelAnimationCoverage.MorphUnreachedReason)]
    [InlineData("NiTransformInterpolator", NifModelAnimationCoverage.CurveUnreachedReason)]
    [InlineData("NiTransformData", NifModelAnimationCoverage.CurveUnreachedReason)]
    [InlineData("NiControllerSequence", NifModelAnimationCoverage.SequenceUnlistedReason)]
    [InlineData("NiDefaultAVObjectPalette", NifModelAnimationCoverage.PaletteNoBindingReason)]
    [InlineData("NiTextKeyExtraData", NifModelAnimationReasons.TextKeysOutsideClip)]
    [InlineData("NiAlphaController", NifModelAnimationCoverage.PropertyUnreachedReason)]
    [InlineData("NiVisController", NifModelAnimationCoverage.VisibilityUnreachedReason)]
    [InlineData("NiMultiTargetTransformController", NifModelAnimationReasons.MultiTargetBinding)]
    [InlineData("NiBlendTransformInterpolator", NifModelAnimationReasons.BlendState)]
    [InlineData("BSFrustumFOVController", NifModelCoverage.CameraLightReason)]
    [InlineData("BSFurnitureMarker", NifModelCoverage.MarkerReason)]
    [InlineData("BSXFlags", NifModelCoverage.ExtraDataReason)]
    [InlineData("NiStringExtraData", NifModelCoverage.ExtraDataReason)]
    [InlineData("BSBound", NifModelCoverage.ExtraDataReason)]
    [InlineData("NiSpecularProperty", NifModelCoverage.FallbackReason)]
    public void CategoryReason_FollowsThePlanTables(string type, string? expected)
    {
        Assert.Equal(expected, NifModelCoverage.CategoryReason(NifSchema.LoadEmbedded(), type));
    }

    /// <summary>
    ///     Cut-1b slice 10: an undecided animation block takes its plan 2.1 table row, never the retired
    ///     'later-cut(1b): animation' reason; an interpolator reached only from particle controllers takes the particle
    ///     row. Control: the same interpolator reached from anywhere else keeps the curve row.
    /// </summary>
    [Fact]
    public void Classify_UndecidedAnimationBlock_TakesTheTableRow()
    {
        var schema = NifSchema.LoadEmbedded();

        var curve = NifModelCoverage.Classify(schema, 3, "NiTransformInterpolator", false, true);
        var particle = NifModelCoverage.Classify(schema, 3, "NiFloatInterpolator", false, true,
            reachedOnlyFromParticles: true);
        var property = NifModelCoverage.Classify(schema, 3, "NiFloatInterpolator", false, true);

        Assert.Equal(ModelSourceCoverageKind.NativeOnly, curve.Kind);
        Assert.Equal(NifModelAnimationCoverage.CurveUnreachedReason, curve.Reason);
        Assert.Equal(NifModelCoverage.ParticleReason, particle.Reason);
        Assert.Equal(NifModelAnimationCoverage.PropertyUnreachedReason, property.Reason);
        Assert.DoesNotContain("later-cut(1b)", curve.Reason!, StringComparison.Ordinal);
    }

    private static void AssertRow(ModelSourceCoverage coverage, string identity, ModelSourceCoverageKind kind,
        string? reason)
    {
        var row = coverage.GetClassification(identity);
        Assert.Equal(kind, row.Kind);
        Assert.Equal(reason, row.Reason);
    }

    private static bool CensusMatches(IReadOnlyList<(string Identity, string Kind)> expected,
        IReadOnlyList<ModelSourceElement> actual)
    {
        return expected.Count == actual.Count &&
               expected.Zip(actual).All(pair => pair.First.Identity == pair.Second.Identity &&
                                                pair.First.Kind == pair.Second.Kind);
    }
}
