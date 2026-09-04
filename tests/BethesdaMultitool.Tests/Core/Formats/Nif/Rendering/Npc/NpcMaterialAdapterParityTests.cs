using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Assets;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Npc;

public sealed class NpcMaterialAdapterParityTests
{
    private const string FamilyDiffusePath = @"textures\characters\male\headhuman.dds";
    private const string EffectiveDiffusePath = @"facegen_egt\00000001.dds";

    [Fact]
    public void MorphedBaseHeadPreparationProducesEquivalentCpuAndExportMaterialInputs()
    {
        var cpuSubmesh = CreateBaseHeadTriangle();
        var exportSubmesh = CreateBaseHeadTriangle();

        NpcBaseHeadGeometryPolicy.PrepareForMaterial(
            [cpuSubmesh], positionsWereMorphed: true, deferTangentRebuildToMaterialResolver: true);
        NpcBaseHeadGeometryPolicy.PrepareForMaterial(
            [exportSubmesh], positionsWereMorphed: true, deferTangentRebuildToMaterialResolver: true);

        Assert.Equal<float>(cpuSubmesh.Normals!, exportSubmesh.Normals!);
        Assert.Equal<float>([0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f], cpuSubmesh.Normals!);
        Assert.Null(cpuSubmesh.Tangents);
        Assert.Null(exportSubmesh.Tangents);
        Assert.Null(cpuSubmesh.Bitangents);
        Assert.Null(exportSubmesh.Bitangents);

        var normalPath = FaceGenHeadShaderFamilyResolver.BuildSiblingPath(FamilyDiffusePath, "_n")!;
        using var textureResolver = new NifTextureResolver();
        textureResolver.InjectTexture(normalPath, TestTextures.Single(127, 127, 255, 255));

        FaceGenHeadShaderFamilyResolver.ApplyClassicSkin2000Material(
            [cpuSubmesh], textureResolver, FamilyDiffusePath, EffectiveDiffusePath);
        FaceGenHeadShaderFamilyResolver.ApplyClassicSkin2000Material(
            [exportSubmesh], textureResolver, FamilyDiffusePath, EffectiveDiffusePath);

        Assert.Equal(cpuSubmesh.DiffuseTexturePath, exportSubmesh.DiffuseTexturePath);
        Assert.Equal(cpuSubmesh.NormalMapTexturePath, exportSubmesh.NormalMapTexturePath);
        Assert.Equal<float>(cpuSubmesh.Tangents!, exportSubmesh.Tangents!);
        Assert.Equal<float>(cpuSubmesh.Bitangents!, exportSubmesh.Bitangents!);
        Assert.True(cpuSubmesh.IsFaceGen);
        Assert.True(exportSubmesh.IsFaceGen);
    }

    [Fact]
    public void GenericBaseHeadPreparationRebuildsAuthoredNormalMapTangentSpace()
    {
        var submesh = CreateBaseHeadTriangle();
        submesh.NormalMapTexturePath = @"textures\actors\character\facegendata\facetint\head_n.dds";

        NpcBaseHeadGeometryPolicy.PrepareForMaterial(
            [submesh],
            positionsWereMorphed: true,
            deferTangentRebuildToMaterialResolver: false);

        Assert.Equal(@"textures\actors\character\facegendata\facetint\head_n.dds",
            submesh.NormalMapTexturePath);
        Assert.NotNull(submesh.Tangents);
        Assert.NotNull(submesh.Bitangents);
        Assert.All(submesh.Tangents!, value => Assert.True(float.IsFinite(value)));
        Assert.All(submesh.Bitangents!, value => Assert.True(float.IsFinite(value)));
    }

    [Fact]
    public void HairPolicyRetainsSingleAndDoubleSidedExtractorShapes()
    {
        var singleSided = CreateBaseHeadTriangle();
        var doubleSided = CreateBaseHeadTriangle();
        doubleSided.IsDoubleSided = true;
        var model = new NifRenderableModel();
        model.Submeshes.Add(singleSided);
        model.Submeshes.Add(doubleSided);

        using var textureResolver = new NifTextureResolver();
        const string hairDiffusePath = @"textures\characters\hair\short.dds";
        var tint = (R: 0.25f, G: 0.5f, B: 0.75f);

        NpcHairSubmeshPolicy.Apply(
            model,
            BethesdaGame.Skyrim,
            tint,
            textureResolver,
            hairDiffusePath);

        Assert.Equal(2, model.Submeshes.Count);
        Assert.Same(singleSided, model.Submeshes[0]);
        Assert.Same(doubleSided, model.Submeshes[1]);
        Assert.All(model.Submeshes, submesh =>
        {
            Assert.Equal(tint, submesh.TintColor!.Value);
            Assert.Equal(hairDiffusePath, submesh.DiffuseTexturePath);
        });
    }

    [Fact]
    public void HairPolicyKeepsTintDisabledWhenActorHasNoHairColor()
    {
        var submesh = CreateBaseHeadTriangle();
        var model = new NifRenderableModel();
        model.Submeshes.Add(submesh);

        using var textureResolver = new NifTextureResolver();
        NpcHairSubmeshPolicy.Apply(
            model,
            BethesdaGame.Skyrim,
            tint: null,
            textureResolver,
            diffuseTexturePath: null);

        Assert.Single(model.Submeshes);
        Assert.Null(submesh.TintColor);
    }

    [Fact]
    public void CpuAndExportAdaptersUseTheSameHeadAndHairPoliciesBeforeConsumption()
    {
        var cpuHead = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "CLI", "Rendering", "Npc", "NpcHeadBuilder.cs");
        var cpuParts = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "CLI", "Rendering", "Npc", "NpcHeadPartAttacher.cs");
        var exportHead = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Npc", "Assembly",
            "NpcExportHeadAssembler.cs");

        SourceContract.AssertOrder(
            cpuHead,
            "NpcBaseHeadGeometryPolicy.PrepareForMaterial(",
            "FaceGenHeadShaderFamilyResolver.ApplyClassicSkin2000Material(");
        SourceContract.AssertOrder(
            exportHead,
            "NpcBaseHeadGeometryPolicy.PrepareForMaterial(",
            "FaceGenHeadShaderFamilyResolver.ApplyClassicSkin2000Material(",
            "NpcExportSceneBuilder.AddSkinnedPart(");
        SourceContract.AssertOrder(
            cpuParts,
            "NpcHairSubmeshPolicy.Apply(",
            "foreach (var sub in hairModel.Submeshes)");
        SourceContract.AssertOrder(
            exportHead,
            "NpcHairSubmeshPolicy.Apply(",
            "NpcExportSceneBuilder.AddRigidModel(scene, npc.HairNifPath, hairModel);");
        Assert.DoesNotContain("hairModel.Submeshes.RemoveAll", cpuParts + exportHead,
            StringComparison.Ordinal);
    }

    private static RenderableSubmesh CreateBaseHeadTriangle()
    {
        return new RenderableSubmesh
        {
            Positions = [0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f],
            Triangles = [0, 1, 2],
            Normals = [0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f, 0f],
            UVs = [0f, 0f, 1f, 0f, 0f, 1f],
            Tangents = [0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f],
            Bitangents = [1f, 0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f],
            DiffuseTexturePath = FamilyDiffusePath
        };
    }
}
