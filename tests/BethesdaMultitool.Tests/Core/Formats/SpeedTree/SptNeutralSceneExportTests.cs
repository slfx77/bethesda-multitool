using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Export;
using BethesdaMultitool.Core.Formats.SpeedTree;
using BethesdaMultitool.Tests.Helpers;
using BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Export;
using SharpGLTF.Schema2;
using Slfx77.Multitool.Core.Models;
using Slfx77.Multitool.Media.Models;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.SpeedTree;

/// <summary>
///     Pins the SpeedTree neutral route and the vegetation behaviours it must decline rather than approximate.
/// </summary>
/// <remarks>
///     SpeedTree has no adapter of its own by design: the geometry builder already yields a renderable model and
///     the NIF export path already turns that into the scene the NIF adapter consumes. These cases pin that the
///     composition holds and that the behaviours a neutral scene cannot hold are refused with a reason.
/// </remarks>
[Collection(SequentialIntegrationGroup.Name)]
public sealed class SptNeutralSceneExportTests
{
    private static readonly string[] RetailTreeNames =
    [
        "euonymusbush01", "oasiselm01", "oasiselm02", "oasistreetop01", "pine01",
        "sugarmaple01", "sycamore01", "wastelandshrub01", "wastelandundergrowth01", "whiteoak01"
    ];

    private static NifTextureResolver Resolver() => new(static _ => null);

    /// <summary>A minimal scene carrying one submesh whose vegetation state the caller sets.</summary>
    private static GlbScene VegetationScene(Action<RenderableSubmesh> mutate)
    {
        var scene = new GlbScene();
        var node = scene.AddNode("Tree", GlbScene.RootNodeIndex, Matrix4x4.Identity, Matrix4x4.Identity,
            GlbNodeKind.Attachment, "Tree");
        var submesh = new RenderableSubmesh
        {
            ShapeName = "Tree",
            Positions = [0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f],
            Triangles = [0, 1, 2],
            Normals = [0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f],
            UVs = [0f, 0f, 1f, 0f, 0f, 1f]
        };
        mutate(submesh);
        scene.MeshParts.Add(new GlbMeshPart { Name = "Tree", NodeIndex = node, Submesh = submesh });
        return scene;
    }

    private static string? Decline(GlbScene scene)
    {
        using var resolver = Resolver();
        Assert.False(NifNeutralSceneAdapter.TryAdapt(scene, resolver, "tree", out var document, out var reason,
            TestContext.Current.CancellationToken));
        Assert.Null(document);
        return reason;
    }

    [Fact]
    public void BillboardOrientation_Declines()
    {
        Assert.Equal("SpeedTree billboard orientation is a per-frame runtime behavior and retains the native writer.",
            Decline(VegetationScene(submesh => submesh.IsBillboard = true)));
    }

    [Fact]
    public void LeafBillboard_Declines()
    {
        Assert.Equal("SpeedTree billboard orientation is a per-frame runtime behavior and retains the native writer.",
            Decline(VegetationScene(submesh => submesh.IsLeafBillboard = true)));
    }

    [Fact]
    public void WindRigSpeeds_Decline()
    {
        Assert.Equal("SpeedTree wind-rig speeds drive an unbaked vertex animation and retain the native writer.",
            Decline(VegetationScene(submesh =>
            {
                submesh.IsSpeedTreeBranch = true;
                submesh.SpeedTreeWindSpeeds = new Vector2(2f, 3f);
            })));
    }

    [Fact]
    public void FarLodFallback_Declines()
    {
        var scene = new GlbScene();
        var node = scene.AddNode("Tree", GlbScene.RootNodeIndex, Matrix4x4.Identity, Matrix4x4.Identity,
            GlbNodeKind.Attachment, "Tree");
        scene.MeshParts.Add(new GlbMeshPart
        {
            Name = "Tree",
            NodeIndex = node,
            Submesh = new RenderableSubmesh
            {
                ShapeName = "Tree",
                Positions = [0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f],
                Triangles = [0, 1, 2],
                Normals = [0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f],
                UVs = [0f, 0f, 1f, 0f, 0f, 1f],
                IsFarLodFallback = true
            }
        });

        Assert.Equal("A far-LOD fallback shape retains the native writer.", Decline(scene));
    }

    [Fact]
    public void AStillBranchWithDefaultWind_IsCarriedRatherThanDeclined()
    {
        // The decline policy must be narrow. A branch that is not billboarded, carries the default wind
        // speeds and has no LOD set holds nothing a neutral scene cannot express, so refusing it would be
        // over-declining rather than caution.
        using var resolver = Resolver();
        var scene = VegetationScene(submesh => submesh.IsSpeedTreeBranch = true);

        Assert.True(NifNeutralSceneAdapter.TryAdapt(scene, resolver, "tree", out var document, out var reason,
            TestContext.Current.CancellationToken), reason);
        Assert.NotNull(document);
        Assert.Single(document.Meshes);
    }

    [Trait("Category", BucketBTestGuard.Category)]
    [Fact]
    public void RealExtractedTrees_RouteThroughTheNeutralPathOrDeclineWithAReason()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var trees = FindExtractedTrees();
        Assert.SkipWhen(trees is null, "Extracted SpeedTree fixtures not present; set BETHESDA_TEST_DATA_ROOT " +
            "to a repository containing TestOutput/fnv_spt/trees or to that directory.");

        var textures = RealAssetPaths.SampleDirectory("Unpacked_Builds/PC_Final_Unpacked/Data");
        Assert.SkipWhen(textures is null, RealAssetPaths.SkipMessage("FNV extracted texture data"));
        var files = RetailTreeNames.Select(name => Path.Combine(trees, name + ".spt")).ToArray();

        using var resolver = new NifTextureResolver(textures);
        var carried = 0;
        var declined = 0;
        var reasons = new SortedDictionary<string, int>(StringComparer.Ordinal);

        foreach (var path in files)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            Assert.True(File.Exists(path), $"Named SpeedTree fixture missing: {path}");
            Assert.InRange(new FileInfo(path).Length, 1, 1024 * 1024);
            var model = SptFile.Parse(File.ReadAllBytes(path));
            var name = Path.GetFileNameWithoutExtension(path);
            var renderable = SptGeometryBuilder.Build(model, 1);
            var source = Assert.IsType<GlbScene>(NifExportSceneBuilder.BuildRenderableModel(renderable, name));
            var missingTextures = renderable.Submeshes
                .SelectMany(static part => new[] { part.DiffuseTexturePath, part.NormalMapTexturePath })
                .OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(texture => resolver.GetTexture(texture) is null).Order(StringComparer.Ordinal).ToArray();
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"{name}: missing authored texture paths [{string.Join(", ", missingTextures)}]");
            var completeTextureFixture = name is "wastelandshrub01" or "wastelandundergrowth01";
            if (completeTextureFixture)
            {
                Assert.True(missingTextures.Length == 0,
                    $"{name}: complete-texture fixture gap; missing authored texture paths " +
                    $"[{string.Join(", ", missingTextures)}]");
            }

            var built = SptNeutralSceneExport.TryBuild(model, seed: 1, resolver,
                Path.GetFileNameWithoutExtension(path), out var document, out var reason,
                TestContext.Current.CancellationToken);

            if (built)
            {
                Assert.NotNull(document);
                Assert.Null(reason);
                Assert.NotEmpty(document.Images);
                Assert.Contains(document.Materials, static material => material.Texture is not null);
                Assert.Contains(document.Materials, static material => material.NormalTexture is not null);
                if (completeTextureFixture)
                {
                    Assert.Contains(document.Materials, static material => material.Name == "spt:leaves" &&
                        material.AlphaMode == SceneAlphaMode.Mask && material.Texture is not null);
                    Assert.Contains(document.Materials, static material => material.Name == "spt:bark" &&
                        material.Texture is not null && material.NormalTexture is not null);
                }
                var nativeBytes = GlbWriter.WriteToBytes(source, resolver);
                var native = ModelRoot.ParseGLB(nativeBytes);
                var encoded = GltfExporter.Encode(SceneGltfBuilder.Build(document, GltfExportIntent.Interchange,
                    TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
                NifCorpusExportAssertions.Equivalent(native, ModelRoot.ParseGLB(encoded), source, resolver);
                TestContext.Current.TestOutputHelper?.WriteLine(
                    $"{name}: compared {document.Meshes.Sum(static mesh => mesh.Primitives.Sum(static part => part.Indices.Count / 3))} " +
                    $"static triangles, {document.Images.Count} images; legacy degenerate drops 0");
                if (completeTextureFixture)
                    WriteCompleteTextureFixture(name, nativeBytes, encoded);
                carried++;
            }
            else
            {
                Assert.Null(document);
                Assert.False(string.IsNullOrWhiteSpace(reason), $"{path}: declined without a reason.");
                reasons[reason!] = reasons.GetValueOrDefault(reason!) + 1;
                declined++;
            }
        }

        Assert.True(carried + declined > 0, "No extracted tree reached the neutral route.");

        // Every decline must be a reason this route knows about. Accepting any reason at all would let
        // the gate keep passing while the route quietly stopped carrying anything for a new cause.
        string[] known =
        [
            "Maps beyond diffuse retain the native material writer.",
            "SpeedTree billboard orientation is a per-frame runtime behavior and retains the native writer.",
            "SpeedTree wind-rig speeds drive an unbaked vertex animation and retain the native writer.",
            "SpeedTree level-of-detail selection is a draw-time choice and retains the native writer.",
            "A far-LOD fallback shape retains the native writer."
        ];
        foreach (var entry in reasons)
        {
            Assert.Contains(entry.Key, known);
        }

        // This is the existing default static export, with crossed leaf cards and default wind.
        // Actual texture/channel and geometry comparison above is required before recording admission.
        // Unresolved dev-era leaf paths remain explicit; this is not live billboard/wind/LOD parity.
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"trees {files.Length}, carried {carried}, declined {declined}");
        foreach (var entry in reasons)
        {
            TestContext.Current.TestOutputHelper?.WriteLine($"  {entry.Value}x {entry.Key}");
        }
        Assert.Equal(files.Length, carried);
        Assert.Equal(0, declined);
    }

    /// <summary>Optionally retains only the two complete-texture exports for independent validator and render checks.</summary>
    private static void WriteCompleteTextureFixture(string name, byte[] nativeBytes, byte[] sharedBytes)
    {
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"{name}: complete authored textures; native {nativeBytes.Length} bytes, shared {sharedBytes.Length} bytes");
        var output = Environment.GetEnvironmentVariable("BMT_NEUTRAL_CORPUS_OUTPUT");
        if (string.IsNullOrWhiteSpace(output)) return;
        var directory = Path.Combine(output, "speedtree");
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, name + ".native.glb"), nativeBytes);
        File.WriteAllBytes(Path.Combine(directory, name + ".shared.glb"), sharedBytes);
    }

    /// <summary>Finds existing private extracted fixtures without copying them into this checkout.</summary>
    private static string? FindExtractedTrees()
    {
        var external = Environment.GetEnvironmentVariable(RealAssetPaths.RootVariable);
        if (!string.IsNullOrWhiteSpace(external))
        {
            var nested = Path.Combine(external, "TestOutput", "fnv_spt", "trees");
            if (Directory.Exists(nested)) return nested;
            if (Directory.Exists(external) && Directory.EnumerateFiles(external, "*.spt").Any()) return external;
        }
        var local = Path.Combine(SourceContract.RepoRoot, "TestOutput", "fnv_spt", "trees");
        return Directory.Exists(local) ? local : null;
    }
}
