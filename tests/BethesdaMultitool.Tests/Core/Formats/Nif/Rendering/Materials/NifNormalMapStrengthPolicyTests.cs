using System.Numerics;
using BethesdaMultitool.Core.Formats.Dds;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Export;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Materials;
using BethesdaMultitool.Tests.Helpers;
using SharpGLTF.Schema2;
using Slfx77.Multitool.Media.Models;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Materials;

public sealed class NifNormalMapStrengthPolicyTests
{
    [Fact]
    public void GenericDefaultUsesUnattenuatedRetailNormal()
    {
        Assert.Equal(1f, NifNormalMapStrengthPolicy.GenericDefault);
    }

    [Fact]
    public void SpriteAndNativeReferenceRenderersConsumeSharedDefault()
    {
        var spriteRenderer = SourceContract.ReadSource("src", "BethesdaMultitool", "Core", "Formats", "Nif",
            "Rendering", "Rasterization", "NifSpriteRenderer.cs");
        var referenceCache = SourceContract.ReadSource("src", "BethesdaMultitool", "Core", "Formats", "Nif",
            "Rendering", "D3D12", "ReferenceMeshCache12.cs");

        Assert.Contains(
            "BumpStrength { get; set; } = NifNormalMapStrengthPolicy.GenericDefault;",
            spriteRenderer,
            StringComparison.Ordinal);
        Assert.Contains(
            "NifNormalMapStrengthPolicy.GenericDefault,",
            referenceCache,
            StringComparison.Ordinal);
        Assert.DoesNotContain("ReferenceBumpStrength", referenceCache, StringComparison.Ordinal);
    }

    /// <summary>Both serialized export paths retain the unattenuated normal channel after material preparation.</summary>
    [Fact]
    public void NativeAndNeutralGltfExportsKeepUnattenuatedNormalScale()
    {
        const string name = "normal-strength";
        var normalTexture = DecodedTexture.FromBaseLevel([128, 128, 255, 255], 1, 1, false);
        using var resolver = new NifTextureResolver(_ => normalTexture);
        var submesh = new RenderableSubmesh
        {
            ShapeName = name,
            Positions = [0, 0, 0, 1, 0, 0, 0, 1, 0],
            Triangles = [0, 1, 2],
            Normals = [0, 0, 1, 0, 0, 1, 0, 0, 1],
            UVs = [0, 0, 1, 0, 0, 1],
            NormalMapTexturePath = "normal.dds"
        };
        var scene = new GlbScene();
        var node = scene.AddNode(name, GlbScene.RootNodeIndex, Matrix4x4.Identity,
            Matrix4x4.Identity, GlbNodeKind.Attachment, name);
        scene.MeshParts.Add(new GlbMeshPart { Name = name, Submesh = submesh, NodeIndex = node });
        Assert.True(NifNeutralSceneAdapter.TryAdapt(scene, resolver, name, out var document,
            out var reason, TestContext.Current.CancellationToken), reason);
        var sharedBytes = GltfExporter.Encode(
            SceneGltfBuilder.Build(document!, GltfExportIntent.Interchange, TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);

        foreach (var bytes in new[] { GlbWriter.WriteToBytes(scene, resolver), sharedBytes })
        {
            var model = ModelRoot.ParseGLB(bytes);
            var material = Assert.Single(model.LogicalMaterials);
            var normal = Assert.IsType<MaterialChannel>(material.FindChannel("Normal"));
            Assert.NotNull(normal.Texture);
            Assert.Equal(1f, normal.GetFactor("NormalScale"));
        }
    }
}
