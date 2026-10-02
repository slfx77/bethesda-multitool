using System.Numerics;
using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Modeling.Nif;
using BethesdaMultitool.Tests.Helpers;
using Slfx77.Multitool.Core.Models;
using Slfx77.Multitool.Core.Models.Sources;
using Xunit;
using static BethesdaMultitool.Tests.Core.Modeling.Nif.NifModelTestSupport;

namespace BethesdaMultitool.Tests.Core.Modeling.Nif;

/// <summary>
///     Slice 7, the skin palette: NiSkinInstance becomes a <see cref="SceneSkin" /> whose joints are the bone occurrences
///     in bone order, whose skeleton root is the resolved occurrence, whose inverse binds are each bone's NiSkinData Skin
///     Transform composed S R^T T exactly as stored, and whose overall Skin Transform stays native with a diagnostic.
///     Corrupt palettes (a repeated bone, an ambiguous instanced bone) throw; a bone outside the scene leaves the skin
///     untyped, as BMT's renderer tolerates it. Every produced
///     document passes Shared's structure validation.
/// </summary>
public class NifModelSkinBindTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Skin_BecomesAPalette_WithJointsInBoneOrder_TheSkeletonRoot_AndJointRoles(bool bigEndian)
    {
        var result = Read(new NifSkinFixture { BigEndian = bigEndian }.Build());
        var document = result.Document;

        Assert.Equal(["Scene Root", "Bip01", "Bone A", "Bone B", "Body"], document.Nodes.Select(n => n.Name));
        var skin = Assert.Single(document.Skins);
        Assert.Equal([1, 2, 3], skin.JointNodeIndices);
        Assert.Equal(0, skin.SkeletonRootNodeIndex);
        Assert.Equal(SceneSkinBindMode.InverseBind, skin.BindMode);
        Assert.Equal("Body", skin.Name);
        Assert.Equal(3, skin.InverseBindMatrices.Count);

        var body = document.Nodes[4];
        Assert.Equal(0, body.SkinIndex);
        Assert.Equal(0, body.MeshIndex);
        Assert.Equal(SceneNodeRole.Joint, document.Nodes[1].Role);
        Assert.Equal(SceneNodeRole.Joint, document.Nodes[2].Role);
        Assert.Equal(SceneNodeRole.Joint, document.Nodes[3].Role);

        // Control: nodes that are not bones keep their roles.
        Assert.Equal(SceneNodeRole.Transform, document.Nodes[0].Role);
        Assert.Equal(SceneNodeRole.Transform, body.Role);
        Assert.Equal(nameof(SceneNodeRole.Joint), (string)BlockPayload(document, 2)["node"]!["role"]!);
        Assert.Equal([2], BlockPayload(document, 2)["node"]!["jointOccurrences"]!.AsArray().Select(v => (int)v!));
        Assert.Null(BlockPayload(document, 4)["node"]!["jointOccurrences"]);

        Assert.Equal(ModelSourceCoverageKind.Typed, result.Coverage.GetClassification("block:6").Kind);
        Assert.Equal(ModelSourceCoverageKind.Typed, result.Coverage.GetClassification("block:7").Kind);
        var row = Assert.Single(Rows(document, NifModelSkinReader.SkinKind));
        Assert.Equal(new SceneElementRef(SceneElementKind.Skin, 0), row.Target);
        var payload = JsonNode.Parse(row.PayloadJson)!;
        Assert.Equal([1, 2, 3], payload["jointBlocks"]!.AsArray().Select(v => (int)v!));
        Assert.Equal(0, (int)payload["skeletonRoot"]!["occurrence"]!);
        Assert.Equal(nameof(SceneSkinBindMode.InverseBind), (string)payload["bindMode"]!);
        SceneValidation.ValidateStructure(document);
    }

    /// <summary>
    ///     The inverse binds are S R^T T of each bone's Skin Transform, element for element, with no orthonormalization.
    ///     Controls: the untransposed composition S R T differs in the sign of the quarter turn's off-diagonal elements,
    ///     and the TRS rule applied to diag(1, 1, 1 + ulp) (orthonormal within 1e-5, so it becomes a quaternion) gives
    ///     M33 = 1, not the stored 1 + ulp.
    /// </summary>
    [Fact]
    public void InverseBinds_AreSRtT_OfEachBone_ExactlyAsStored()
    {
        var skin = Read(new NifSkinFixture().Build()).Document.Skins[0];

        Assert.Equal(new Matrix4x4(1f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, -1f, 1f),
            skin.InverseBindMatrices[0]);
        var turned = skin.InverseBindMatrices[1];
        Assert.Equal(new Matrix4x4(0f, 2f, 0f, 0f, -2f, 0f, 0f, 0f, 0f, 0f, 2f, 0f, 1f, 2f, 3f, 1f), turned);
        var r = NifSkinFixture.QuarterTurnAboutZ;
        var untransposed = new Matrix4x4(2f * r[0], 2f * r[1], 2f * r[2], 0f, 2f * r[3], 2f * r[4], 2f * r[5], 0f,
            2f * r[6], 2f * r[7], 2f * r[8], 0f, 1f, 2f, 3f, 1f);
        Assert.NotEqual(untransposed, turned);

        var nearly = skin.InverseBindMatrices[2];
        Assert.Equal(MathF.BitIncrement(1f), nearly.M33);
        Assert.Equal(new Vector3(0.5f, 0f, 0f), nearly.Translation);
        var orthonormalized = NifModelTransform.Resolve(new Vector3(0.5f, 0f, 0f), NifSkinFixture.NearlyIdentity, 1f);
        Assert.Equal(NifModelTransformKind.Trs, orthonormalized.Kind);
        Assert.Equal(1f, orthonormalized.Matrix.M33);
        Assert.NotEqual(orthonormalized.Matrix, nearly);
    }

    /// <summary>
    ///     A non-identity overall Skin Transform (the armor case, a translation of (0, -0.80, -0.31)) is kept in native
    ///     state and reported, and the inverse binds are the per-bone transforms alone. Control: folding it in would change
    ///     the binds, which stay bit-identical to the identity-overall fixture's; that fixture reports nothing.
    /// </summary>
    [Fact]
    public void OverallTransform_IsNative_Reported_AndNotFolded()
    {
        var translated = Read(new NifSkinFixture { OverallTranslation = (0f, -0.8f, -0.31f) }.Build()).Document;
        var identity = Read(new NifSkinFixture().Build()).Document;

        Assert.Contains(translated.Diagnostics, d => d.Code == NifModelSkinReader.OverallTransformDiagnostic);
        Assert.DoesNotContain(identity.Diagnostics, d => d.Code == NifModelSkinReader.OverallTransformDiagnostic);
        Assert.Equal(identity.Skins[0].InverseBindMatrices, translated.Skins[0].InverseBindMatrices);

        var overall = JsonNode.Parse(Assert.Single(Rows(translated, NifModelSkinReader.SkinKind)).PayloadJson)!
            ["overallTransform"]!;
        Assert.False((bool)overall["identity"]!);
        Assert.False((bool)overall["foldedIntoInverseBinds"]!);
        Assert.Equal([0f, -0.8f, -0.31f], overall["translation"]!.AsArray().Select(v => (float)v!));
        SceneValidation.ValidateStructure(translated);
    }

    /// <summary>A bone listed twice would repeat a joint, which Shared rejects: corrupt input. Control: distinct bones read.</summary>
    [Fact]
    public void RepeatedBone_Throws()
    {
        var error = Assert.Throws<InvalidDataException>(() => Read(new NifSkinFixture { Bones = [1, 2, 2] }.Build()));

        Assert.Contains("repeats", error.Message);
        Assert.Single(Read(new NifSkinFixture { Bones = [1, 2, 3] }.Build()).Document.Skins);
    }

    /// <summary>
    ///     A bone that is not placed in the scene graph cannot become a joint, so the skin is not typed: no palette, the
    ///     skin blocks NativeOnly with the unplaced-bone reason, the geometry placed unskinned (BMT's renderer tolerates
    ///     such a bone instead of failing). Control: the same bone placed under the root types the skin and resolves to
    ///     its occurrence.
    /// </summary>
    [Fact]
    public void BoneOutsideTheScene_LeavesTheSkinUntyped()
    {
        static NifSkinFixture Fixture(int[] rootChildren)
        {
            return new NifSkinFixture
            {
                Bones = [1, 2, 8],
                RootChildren = rootChildren,
                ExtraBlocks = builder => AddNode(builder, builder.AddString("Loose"), [])
            };
        }

        var unplaced = Read(Fixture([1, 4]).Build());
        Assert.Empty(unplaced.Document.Skins);
        Assert.All(unplaced.Document.Nodes, node => Assert.Null(node.SkinIndex));
        foreach (var identity in new[] { "block:6", "block:7" })
        {
            var row = unplaced.Coverage.GetClassification(identity);
            Assert.Equal(ModelSourceCoverageKind.NativeOnly, row.Kind);
            Assert.Equal(NifModelCoverage.SkinUnplacedBoneReason, row.Reason);
        }

        SceneValidation.ValidateStructure(unplaced.Document);

        var placed = Read(Fixture([1, 4, 8]).Build()).Document;
        Assert.Equal("Loose", placed.Nodes[5].Name);
        Assert.Equal([1, 2, 5], placed.Skins[0].JointNodeIndices);
        SceneValidation.ValidateStructure(placed);
    }

    /// <summary>
    ///     Bone B is instanced under Bip01 and under Other. With Bip01 as the skeleton root it resolves to the occurrence
    ///     under Bip01. Control: with the scene root as skeleton root both occurrences qualify, which is ambiguous and
    ///     throws.
    /// </summary>
    [Fact]
    public void InstancedBone_ResolvesToTheOccurrenceUnderTheSkeletonRoot()
    {
        static NifSkinFixture Fixture(int skeletonRoot)
        {
            return new NifSkinFixture
            {
                SkeletonRoot = skeletonRoot,
                RootChildren = [1, 4, 8],
                ExtraBlocks = builder => AddNode(builder, builder.AddString("Other"), [3])
            };
        }

        var document = Read(Fixture(1).Build()).Document;

        Assert.Equal(["Scene Root", "Bip01", "Bone A", "Bone B", "Body", "Other", "Bone B"],
            document.Nodes.Select(n => n.Name));
        var skin = Assert.Single(document.Skins);
        Assert.Equal([1, 2, 3], skin.JointNodeIndices);
        Assert.Equal(1, skin.SkeletonRootNodeIndex);
        Assert.Equal(SceneNodeRole.Transform, document.Nodes[6].Role);
        SceneValidation.ValidateStructure(document);

        var error = Assert.Throws<InvalidDataException>(() => Read(Fixture(0).Build()));
        Assert.Contains("ambiguous", error.Message);
    }

    /// <summary>
    ///     Big-endian PC-layout skin data decodes to the same palette, binds and influences bit for bit. Control: a
    ///     NiSkinData body laid out in the other byte order no longer decodes exactly, and a typed skin refuses it.
    /// </summary>
    [Fact]
    public void BigEndian_PcLayoutSkin_DecodesIdentically()
    {
        var little = Read(new NifSkinFixture().Build()).Document;
        var big = Read(new NifSkinFixture { BigEndian = true }.Build()).Document;

        Assert.Equal(little.Skins[0].JointNodeIndices, big.Skins[0].JointNodeIndices);
        Assert.Equal(little.Skins[0].InverseBindMatrices, big.Skins[0].InverseBindMatrices);
        var littleInfluences = PrimitiveOf(little, "Body").SkinInfluences!;
        var bigInfluences = PrimitiveOf(big, "Body").SkinInfluences!;
        Assert.Equal(littleInfluences.InfluencesPerVertex, bigInfluences.InfluencesPerVertex);
        Assert.Equal(littleInfluences.JointIndices, bigInfluences.JointIndices);
        Assert.Equal(littleInfluences.Weights.Select(BitConverter.SingleToUInt32Bits),
            bigInfluences.Weights.Select(BitConverter.SingleToUInt32Bits));

        Assert.Throws<InvalidDataException>(() =>
            Read(new NifSkinFixture { BigEndian = true, SkinDataInTheWrongByteOrder = true }.Build()));
        Assert.Throws<InvalidDataException>(() =>
            Read(new NifSkinFixture { SkinDataInTheWrongByteOrder = true }.Build()));
    }

    /// <summary>
    ///     A skinned shape whose packed block declares no channel is packed geometry of an unknown layout: its skin
    ///     blocks take the packed geometry's reason, no skin is typed and the bones keep their Transform role. Control:
    ///     the inline form types both skin blocks (the known packed layouts are typed in NifModelPackedGeometryTests).
    /// </summary>
    [Fact]
    public void PackedGeometrySkin_OfUnknownLayout_StaysNativeOnly_WithThePackedReason()
    {
        var packed = Read(new NifSkinFixture
        {
            AdditionalData = NifSkinFixture.PartitionBlock,
            ExtraBlocks = builder => builder.AddBlock("BSPackedAdditionalGeometryData",
                w => NifTestBlockLayouts.EmptyAdditionalGeometryData(w, 4))
        }.Build());

        Assert.Empty(packed.Document.Skins);
        Assert.Null(packed.Document.Nodes[4].SkinIndex);
        Assert.Equal(SceneNodeRole.Transform, packed.Document.Nodes[1].Role);
        foreach (var identity in new[] { "block:4", "block:5", "block:6", "block:7" })
        {
            var row = packed.Coverage.GetClassification(identity);
            Assert.Equal(ModelSourceCoverageKind.NativeOnly, row.Kind);
            Assert.Equal(NifModelCoverage.PackedLayoutUnknownReason, row.Reason);
        }

        SceneValidation.ValidateStructure(packed.Document);

        var inline = Read(new NifSkinFixture().Build());
        Assert.Equal(ModelSourceCoverageKind.Typed, inline.Coverage.GetClassification("block:6").Kind);
        Assert.Equal(ModelSourceCoverageKind.Typed, inline.Coverage.GetClassification("block:7").Kind);
    }
}
