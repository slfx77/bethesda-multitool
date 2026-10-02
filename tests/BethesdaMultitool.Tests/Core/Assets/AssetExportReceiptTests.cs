using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Assets;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Export;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Scene;
using SharpGLTF.Schema2;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Assets;

public sealed class AssetExportReceiptTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GlbRetainsCompositionAndExportReadsAcrossWarmTextureCache(bool textures)
    {
        var root = Path.Combine(Path.GetTempPath(), "BMT-AssetExport-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "meshes"));
        Directory.CreateDirectory(Path.Combine(root, "textures"));
        try
        {
            byte[] compositionBytes = [1, 3, 5, 7];
            var textureBytes = PixelDds();
            File.WriteAllBytes(Path.Combine(root, "meshes", "composition.bin"), compositionBytes);
            File.WriteAllBytes(Path.Combine(root, "textures", "fixture.dds"), textureBytes);
            using var composition = new AssetSelectionSession(AssetSourcePlan.FromPaths([root]));
            var read = composition.Read("meshes/composition.bin", bytes => bytes);
            Assert.NotNull(read.Value);
            using var resolver = new NifTextureResolver(AssetSourcePlan.FromPaths(textures ? [root] : []));
            var scene = new GlbScene { AssetReadReceipts = [read.Receipt] };
            var uses = new AssetUseGraphBuilder();
            uses.Add(AssetRecordOwner.Unavailable("ARMA", 1), "equipment", "ModelPath", "meshes/composition.bin");
            uses.Add(AssetRecordOwner.Unavailable("ARMA", 2), "equipment", "ModelPath", "meshes/composition.bin");
            scene.AssetUses = uses.Build();
            scene.MeshParts.Add(new GlbMeshPart
            {
                Name = "Triangle",
                NodeIndex = GlbScene.RootNodeIndex,
                Submesh = new RenderableSubmesh
                {
                    ShapeName = "Triangle",
                    SourceNifPath = "meshes/composition.bin",
                    SourceBlockIndex = 7,
                    Positions = [0, 0, 0, 1, 0, 0, 0, 1, 0],
                    Normals = [0, 0, 1, 0, 0, 1, 0, 0, 1],
                    UVs = [0, 0, 1, 0, 0, 1],
                    Triangles = [0, 1, 2],
                    DiffuseTexturePath = "textures/fixture.dds"
                }
            });

            string? firstTextureReceipt = null;
            for (var pass = 0; pass < 2; pass++)
            {
                using var stream = new MemoryStream(GlbWriter.WriteToBytes(scene, resolver), false);
                var model = ModelRoot.ReadGLB(stream);
                Assert.Single(model.LogicalMeshes);
                var extras = Assert.IsType<JsonObject>(model.Extras);
                Assert.Equal("composition-and-export-reads", extras["BMT_asset_scope"]!.GetValue<string>());
                Assert.False(extras["BMT_engine_priority_verified"]!.GetValue<bool>());
                var receipts = Assert.IsType<JsonArray>(extras["BMT_asset_reads"]);
                var graph = Assert.IsType<JsonObject>(extras["BMT_asset_uses"]);
                var nodes = Assert.IsType<JsonArray>(graph["Nodes"]);
                Assert.Equal(2, nodes.Count(n => n!["Owner"]?["Signature"]?.GetValue<string>() == "ARMA"));
                Assert.Contains(nodes, n => n!["NifBlockIndex"]?.GetValue<int>() == 7);
                var selected = receipts.Select(node => Assert.IsType<JsonObject>(node))
                    .Where(node => node["Status"]!.GetValue<string>() == "Selected").ToArray();
                Assert.Equal(textures ? 2 : 1, selected.Length);
                AssertReceipt(selected.Single(node => node["RequestedPath"]!.GetValue<string>()
                    .EndsWith("composition.bin", StringComparison.Ordinal)), compositionBytes, root);
                if (textures)
                {
                    var texture = selected.Single(node => node["RequestedPath"]!.GetValue<string>()
                        .EndsWith("fixture.dds", StringComparison.Ordinal));
                    AssertReceipt(texture, textureBytes, root);
                    if (pass == 0) firstTextureReceipt = texture.ToJsonString();
                    else Assert.Equal(firstTextureReceipt, texture.ToJsonString());
                }
                Assert.Equal(textures ? 1 : 0, model.LogicalImages.Count);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Authored_alias_keeps_actual_fallback_receipts_for_ordinary_and_generated_pixels(bool generated)
    {
        var root = Path.Combine(Path.GetTempPath(), "BMT-AssetAlias-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "textures"));
        try
        {
            File.WriteAllBytes(Path.Combine(root, "textures", "head.ddx"), PixelDds());
            using var resolver = new NifTextureResolver(AssetSourcePlan.FromPaths([root]));
            using var scope = resolver.AssetSelection!.CaptureReads();
            var pixels = Assert.IsType<BethesdaMultitool.Core.Formats.Dds.DecodedTexture>(resolver.GetTexture("textures/head.dds"));
            var builder = new AssetUseGraphBuilder();
            var owner = builder.Add(AssetRecordOwner.Unavailable("RACE", 1), "head", "MaleHeadTexturePath", "textures/head.dds");
            if (generated)
            {
                resolver.InjectTexture("textures/generated.dds", pixels,
                    new("fixture composition", ["textures/head.dds"], ObservedInputs: [new("textures/head.dds", [.. pixels.AssetReadReceipts])]));
                builder.Add(null, "head-slot", "DiffuseTexturePath", "textures/generated.dds");
                Assert.Same(pixels, resolver.GetTexture("textures/generated.dds"));
                // A later component can observe different bytes at the same authored path.
                // The first generated texture must retain its actual earlier inputs.
                var replacement = PixelDds();
                replacement[128] = 201;
                var path = Path.Combine(root, "textures", "head.ddx");
                File.WriteAllBytes(path, replacement);
                File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(5));
                Assert.NotSame(pixels, resolver.GetTexture("textures/head.dds"));
            }
            else Assert.Same(pixels, resolver.GetTexture("textures/head.dds"));
            var graph = SceneAssetUses.WithGeneratedInputs(builder.Build(), resolver);
            graph = SceneAssetUses.WithResolvedTextureReads(graph, resolver, scope.Receipts).Bind(scope.Receipts);
            var binding = Assert.Single(graph.Bindings, b => b.UseId == owner);
            Assert.Equal("component-read-scope", binding.Basis);
            var actual = Assert.Single(binding.Reads, r => r.Status == AssetSelectionStatus.Selected);
            Assert.EndsWith("head.ddx", actual.RequestedPath, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(PixelDds())), actual.PayloadSha256);
            Assert.Contains(binding.Reads, r => r.Status == AssetSelectionStatus.Missing && r.RequestedPath.EndsWith("head.dds", StringComparison.OrdinalIgnoreCase));
            var unobserved = SceneAssetUses.WithResolvedTextureReads(builder.Build(), resolver, []).Bind([]);
            Assert.Equal("NotObserved", Assert.Single(unobserved.Bindings, b => b.UseId == owner).Status);
        }
        finally { Directory.Delete(root, true); }
    }

    private static void AssertReceipt(JsonObject receipt, byte[] bytes, string root)
    {
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), receipt["PayloadSha256"]!.GetValue<string>());
        var selected = Assert.IsType<JsonObject>(receipt["Selected"]);
        Assert.Equal(Path.GetFullPath(Path.Combine(root,
            selected["VirtualPath"]!.GetValue<string>().Replace('\\', Path.DirectorySeparatorChar))),
            selected["SourcePath"]!.GetValue<string>());
        Assert.Equal((long)bytes.Length, selected["Size"]!.GetValue<long>());
        Assert.Equal("extracted-bytes-sha256", receipt["PayloadHashScope"]!.GetValue<string>());
        Assert.False(receipt["EnginePriorityVerified"]!.GetValue<bool>());
    }

    private static byte[] PixelDds()
    {
        var bytes = new byte[132];
        "DDS "u8.CopyTo(bytes);
        Word(4, 124); Word(8, 0x100F); Word(12, 1); Word(16, 1); Word(20, 4); Word(28, 1);
        Word(76, 32); Word(80, 0x41); Word(88, 32);
        Word(92, 0xFF); Word(96, 0xFF00); Word(100, 0xFF0000); Word(104, 0xFF000000); Word(108, 0x1000);
        bytes[128] = 31; bytes[129] = 63; bytes[130] = 95; bytes[131] = 255;
        return bytes;
        void Word(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), value);
    }
}
