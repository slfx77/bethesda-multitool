using System.Numerics;
using System.Security.Cryptography;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Export;
using BethesdaMultitool.Core.Formats.Nif.Rendering.FaceGen;
using BethesdaMultitool.Tests.Core.Formats.FaceGen.Egm;
using BethesdaMultitool.Tests.Core.Formats.FaceGen.Tri;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Npc;

/// <summary>Discriminates exact head routing from source-order selection and pre-skin from post-skin morphing.</summary>
public sealed class NifPreSkinMorphTargetTests
{
    private static readonly float[] Symmetric = [1, 0];
    private static readonly float[] Asymmetric = [0];
    private static readonly Matrix4x4 BonePose = new(
        0, 1, 0, 0, -1, 0, 0, 0, 0, 0, 1, 0, 10, 20, 30, 1);

    /// <summary>The second exact topology match alone receives EGM before a noncommuting bone transform.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RendererTargetsVerifiedSecondShapeBeforeSkinning(bool dualQuaternion)
    {
        var bytes = NifMorphTargetFixture.Create();
        var nif = Assert.IsType<NifInfo>(NifParser.Parse(bytes));
        var target = Find(bytes);
        var deltas = Deltas();
        var before = deltas.ToArray();
        var sourceHash = SHA256.HashData(bytes);
        var bones = new Dictionary<string, Matrix4x4> { ["MorphBone"] = BonePose };
        var legacy = Assert.IsType<NifRenderableModel>(NifGeometryExtractor.Extract(bytes, nif,
            externalBoneTransforms: bones, useDualQuaternionSkinning: dualQuaternion, preSkinMorphDeltas: deltas));
        var result = Assert.IsType<NifRenderableModel>(NifGeometryExtractor.Extract(bytes, nif,
            externalBoneTransforms: bones, useDualQuaternionSkinning: dualQuaternion,
            preSkinMorphDeltas: deltas, preSkinMorphTarget: target));
        Assert.Equal(2, result.Submeshes.Count);
        Near(new Vector3(10, 31, 30), First(legacy, NifMorphTargetFixture.AttachmentShape));
        Near(new Vector3(10, 21, 30), First(legacy, NifMorphTargetFixture.HeadShape));
        Near(new Vector3(10, 30, 30), First(result, NifMorphTargetFixture.AttachmentShape));
        Near(new Vector3(10, 22, 30), First(result, NifMorphTargetFixture.HeadShape));
        Assert.True(Vector3.Distance(new Vector3(11, 21, 30), First(result, NifMorphTargetFixture.HeadShape)) > 1);
        Assert.Equal(15, deltas.Length);
        Assert.Equal(4, deltas[9]);
        Assert.Equal(5, deltas[13]);
        Assert.Equal(before, deltas);
        Assert.Equal(sourceHash, SHA256.HashData(bytes));
    }

    /// <summary>Export retains both skins and local coordinates while routing the same complete array to the proven owner.</summary>
    [Fact]
    public void ExportTargetsVerifiedShapeAndKeepsFullDomain()
    {
        var bytes = NifMorphTargetFixture.Create();
        var nif = Assert.IsType<NifInfo>(NifParser.Parse(bytes));
        var target = Find(bytes);
        var deltas = Deltas();
        var before = deltas.ToArray();
        var legacy = NifExportExtractor.Extract(bytes, nif, preSkinMorphDeltas: deltas);
        var result = NifExportExtractor.Extract(bytes, nif, preSkinMorphDeltas: deltas, preSkinMorphTarget: target);
        Assert.Equal(2, result.MeshParts.Count);
        Assert.All(result.MeshParts, part => Assert.NotNull(part.Skin));
        Assert.Equal(11, ExportFirst(legacy, NifMorphTargetFixture.AttachmentShape).X);
        Assert.Equal(1, ExportFirst(legacy, NifMorphTargetFixture.HeadShape).X);
        Assert.Equal(10, ExportFirst(result, NifMorphTargetFixture.AttachmentShape).X);
        Assert.Equal(2, ExportFirst(result, NifMorphTargetFixture.HeadShape).X);
        Assert.Equal(before, deltas);
        Assert.Equal(5, target.FullVertexCount);
        Assert.Equal(3, target.BaseVertexCount);
        Assert.Equal(NifMorphTargetFixture.HeadShape, target.ShapeBlockIndex);
        Assert.Equal(NifMorphTargetFixture.HeadData, target.DataBlockIndex);
    }

    /// <summary>Excluding the verified head cannot redirect its displacements to the remaining attachment.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FilteredTargetNeverFallsThroughToAnotherShape(bool export)
    {
        var bytes = NifMorphTargetFixture.Create();
        var nif = Assert.IsType<NifInfo>(NifParser.Parse(bytes));
        var target = Find(bytes);
        if (export)
        {
            var scene = NifExportExtractor.Extract(bytes, nif, filterShapeName: "Attachment",
                preSkinMorphDeltas: Deltas(), preSkinMorphTarget: target);
            Assert.Single(scene.MeshParts);
            Assert.Equal(10, ExportFirst(scene, NifMorphTargetFixture.AttachmentShape).X);
        }
        else
        {
            var model = Assert.IsType<NifRenderableModel>(NifGeometryExtractor.Extract(bytes, nif,
                filterShapeName: "Attachment", preSkinMorphDeltas: Deltas(), preSkinMorphTarget: target));
            Assert.Single(model.Submeshes);
            Assert.Equal(10, First(model, NifMorphTargetFixture.AttachmentShape).X);
        }
    }

    /// <summary>Two matching topology owners remain ambiguous rather than silently selecting the earliest shape.</summary>
    [Fact]
    public void AmbiguousShapeProofReturnsNull()
    {
        var bytes = NifMorphTargetFixture.Create(ambiguous: true);
        Assert.Null(NifPreSkinMorphTarget.Find(bytes, "synthetic.nif", TriFixture.Create().Bytes, "selected.tri", 5));
    }

    /// <summary>A quad-bearing explicit TRI is inspectable but cannot establish this triangle-only routing proof.</summary>
    [Fact]
    public void UnsupportedTopologyRetainsNoProof()
    {
        Assert.Null(NifPreSkinMorphTarget.Find(NifMorphTargetFixture.Create(), "synthetic.nif",
            TriFixture.Create(quad: true).Bytes, "selected.tri", 5));
    }

    /// <summary>The statistical suffix is required even though the displayed geometry has only three vertices.</summary>
    [Fact]
    public void WrongFullDomainCannotEstablishProof()
    {
        Assert.Throws<InvalidDataException>(() => NifPreSkinMorphTarget.Find(NifMorphTargetFixture.Create(),
            "synthetic.nif", TriFixture.Create().Bytes, "selected.tri", 3));
    }

    /// <summary>Changed source bytes and truncated delta domains fail before either extraction path can mutate geometry.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StaleSourceAndTruncatedDomainAreRejected(bool export)
    {
        var bytes = NifMorphTargetFixture.Create();
        var nif = Assert.IsType<NifInfo>(NifParser.Parse(bytes));
        var target = Find(bytes);
        var changed = bytes.ToArray();
        changed[^1] ^= 1;
        if (export)
        {
            Assert.Throws<InvalidDataException>(() => NifExportExtractor.Extract(changed, nif,
                preSkinMorphDeltas: Deltas(), preSkinMorphTarget: target));
            Assert.Throws<InvalidDataException>(() => NifExportExtractor.Extract(bytes, nif,
                preSkinMorphDeltas: Deltas()[..9], preSkinMorphTarget: target));
        }
        else
        {
            Assert.Throws<InvalidDataException>(() => NifGeometryExtractor.Extract(changed, nif,
                preSkinMorphDeltas: Deltas(), preSkinMorphTarget: target));
            Assert.Throws<InvalidDataException>(() => NifGeometryExtractor.Extract(bytes, nif,
                preSkinMorphDeltas: Deltas()[..9], preSkinMorphTarget: target));
        }
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), target.NifSourceHash);
    }

    /// <summary>Builds the existing parser's exact nonzero V+K displacement array, preserving production coefficient policy.</summary>
    private static float[] Deltas()
    {
        var egm = Assert.IsType<EgmParser>(EgmParser.Parse(EgmFixture.Create()));
        return Assert.IsType<float[]>(FaceGenMeshMorpher.ComputeAccumulatedDeltas(egm,
            Symmetric, Asymmetric, egm.VertexCount));
    }

    /// <summary>Uses the explicit source TRI and exact topology to obtain the second shape's proof.</summary>
    private static NifPreSkinMorphTarget Find(byte[] bytes) =>
        Assert.IsType<NifPreSkinMorphTarget>(NifPreSkinMorphTarget.Find(bytes, "synthetic.nif",
            TriFixture.Create().Bytes, "selected.tri", 5));

    /// <summary>Reads the first vertex of the exact source owner from renderer output.</summary>
    private static Vector3 First(NifRenderableModel model, int shapeIndex)
    {
        var part = Assert.Single(model.Submeshes, part => part.SourceBlockIndex == shapeIndex);
        return new Vector3(part.Positions[0], part.Positions[1], part.Positions[2]);
    }

    /// <summary>Reads the first source-local vertex of the exact owner from export output.</summary>
    private static Vector3 ExportFirst(NifExportExtractor.ExtractedScene scene, int shapeIndex)
    {
        var part = Assert.Single(scene.MeshParts, part => part.Submesh.SourceBlockIndex == shapeIndex).Submesh;
        return new Vector3(part.Positions[0], part.Positions[1], part.Positions[2]);
    }

    /// <summary>Allows only normal floating-point skinning roundoff around independently known coordinates.</summary>
    private static void Near(Vector3 expected, Vector3 actual) =>
        Assert.InRange(Vector3.Distance(expected, actual), 0, 0.00001f);
}
