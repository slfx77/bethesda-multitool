using System.Numerics;
using System.Security.Cryptography;
using BethesdaMultitool.Core.Formats.Dds;
using BethesdaMultitool.Core.Formats.Nif;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Export;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;
using BethesdaMultitool.Tests.Helpers;
using ImageMagick;
using SharpGLTF.Schema2;
using Slfx77.Multitool.Core.Models;
using Slfx77.Multitool.Media.Models;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Export;

/// <summary>Requires textured rigid and skinned retail fixtures to pass both complete export paths.</summary>
[Trait("Category", BucketBTestGuard.Category)]
[Collection(SequentialIntegrationGroup.Name)]
public sealed class NifNeutralSceneRetailCorpusTests
{
    private const string DataDirectory = "Unpacked_Builds/PC_Final_Unpacked/Data";
    private const string NeonGlowPixels = "94169817AF052EA4C978804086FFAC74186C8EFFC221D2EC5CE0ECAEBE24031F";
    private static readonly (string Path, string Hash)[] NeonTextures =
    [
        ("textures/architecture/goodsprings/NV_ProspectorSaloon-Neon_g.dds", "C08BC2B90B92E361A2DA477CF063DE7EB869927BC63953F129EA9DAED6B0AEE4"),
        ("textures/architecture/goodsprings/NV_ProspectorSaloon-Neon_n.dds", "2C677C55F8CB4F58044A81875C1FFD63A256DC54E0F6016D91F8882638377EC7"),
        ("textures/architecture/goodsprings/NV_ProspectorSaloon-Neon.dds", "A8475FF2C38B5C469541AB3F649B9921FD33EF5C55A5775B997064FCEDD621FA"),
        ("textures/architecture/strip/NV_Neon-Lights.dds", "64E6CB40CC5CB8A3D7D1F7A36D4B0F9A1AEC67EEDD47CD4C8996EFB110BA9155"),
        ("textures/architecture/strip/NV_TheTops-Sign04.dds", "B81FB01264A24745E8741F5AB4FB5CFE7C388BE5D28179AC1F9EED37C3E179C9"),
        ("textures/architecture/urban/metalworkquad02_n.dds", "E8EBBDBD8305E0154034A5B6CFE2DEEF5926E6ECC02BC9C03565B9DEA02512B6"),
        ("textures/architecture/urban/metalworkquad02.dds", "22F6FDFE528A5A31A87A2ACCAAF7D90E002EC9F22E852EB112349FB50FF3CC64")
    ];

    /// <summary>Checks actual texture pixels, geometry corners, source skins and encoded material behavior.</summary>
    /// <param name="relativePath">The deterministic model below the external FNV data directory.</param>
    /// <param name="expectedHash">The exact private fixture content identity.</param>
    /// <param name="requiresSkin">Whether this fixture must exercise a nonempty skin palette.</param>
    /// <param name="requiresEmission">Whether this fixture must retain the complete neon scene and its glow channel.</param>
    [Theory]
    [InlineData("meshes/clutter/junk/tincan01.nif",
        "E928406FDE567A4C933A0F9CB5344B7F5E9F70E66B76A2C65B45FC4E62D79BF1", false, false)]
    [InlineData("meshes/characters/_male/upperbody.nif",
        "65064044AAA4B2EE40D720921E633E57E25E4B9118E01B3F90F94CE83B73AC1A", true, false)]
    [InlineData("meshes/architecture/goodsprings/NV_ProspectorSaloon-Neon_Lights.NIF",
        "2DA30703F96B9C59F9135CA692F88D32B2DB94B4438E356805A4E59071A8BDED", false, true)]
    public void TexturedRetailFixture_PreservesGeometryMaterialsAndCompleteSkin(
        string relativePath, string expectedHash, bool requiresSkin, bool requiresEmission)
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var dataDirectory = RealAssetPaths.SampleDirectory(DataDirectory);
        Assert.SkipWhen(dataDirectory is null, RealAssetPaths.SkipMessage(DataDirectory));
        var path = Path.Combine(dataDirectory, relativePath.ToLowerInvariant());
        Assert.True(File.Exists(path), $"The named retail fixture is missing: {path}");
        Assert.InRange(new FileInfo(path).Length, 1, 4 * 1024 * 1024);
        var token = TestContext.Current.CancellationToken;
        token.ThrowIfCancellationRequested();
        var data = File.ReadAllBytes(path);
        Assert.Equal(expectedHash, Convert.ToHexString(SHA256.HashData(data)));
        var nif = Assert.IsType<NifInfo>(NifParser.Parse(data));
        var source = Assert.IsType<GlbScene>(NifExportSceneBuilder.Build(data, nif, relativePath));
        var original = new NifCorpusSourceSnapshot(source);
        var maximumInfluences = source.MeshParts.Where(static part => part.Skin is not null)
            .SelectMany(static part => part.Skin!.PerVertexInfluences)
            .Select(static influences => influences.Count(static influence => influence.Weight > 0))
            .DefaultIfEmpty(0).Max();
        Assert.InRange(maximumInfluences, requiresSkin ? 1 : 0, requiresSkin ? 4 : 0);
        // The legacy writer truncates above four influences. These named parity fixtures must not
        // acquire that limitation silently; complete source-to-neutral influence checks remain separate.
        using var resolver = new NifTextureResolver(dataDirectory);
        var textureSnapshots = requiresEmission ? SnapshotNeonTextures(source, dataDirectory, resolver) : null;
        Assert.True(NifNeutralSceneAdapter.TryAdapt(source, resolver, relativePath, out var neutral,
            out var reason, token), $"{relativePath}: {reason}");
        original.AssertUnchanged(source);
        Assert.NotEmpty(neutral.Images);
        Assert.Contains(neutral.Materials, static material => material.Texture is not null);
        Assert.Contains(neutral.Materials, static material => material.NormalTexture is not null);
        Assert.Equal(requiresSkin, neutral.Skins.Count > 0);
        NifNeutralSceneCorpusTests.AssertOriginalNodesAndSkinPlacements(source, neutral, resolver);
        if (requiresEmission)
        {
            AssertNeonPlacementsAndMaterials(source, neutral, resolver);
            AssertNeonTexturesUnchanged(textureSnapshots!, resolver);
        }

        var nativeBytes = GlbWriter.WriteToBytes(source, resolver);
        var sharedBytes = GltfExporter.Encode(SceneGltfBuilder.Build(neutral, GltfExportIntent.Interchange, token), token);
        var native = ModelRoot.ParseGLB(nativeBytes);
        var shared = ModelRoot.ParseGLB(sharedBytes);
        if (requiresEmission)
        {
            AssertNeonEncodedEmission(native);
            AssertNeonEncodedEmission(shared);
            AssertNeonTexturesUnchanged(textureSnapshots!, resolver);
            NifCorpusExportAssertions.Equivalent(native, shared, source, resolver);
        }
        else
            NifCorpusExportAssertions.Equivalent(native, shared);
        Assert.Equal(expectedHash, Convert.ToHexString(SHA256.HashData(data)));
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"{relativePath}: {neutral.Meshes.Count} meshes, {neutral.Skins.Count} skins, {neutral.Images.Count} images, " +
            $"maximum {maximumInfluences} positive influences; " +
            $"native {nativeBytes.Length} bytes, shared {sharedBytes.Length} bytes; SHA-256 {expectedHash}");

        var output = Environment.GetEnvironmentVariable("BMT_NEUTRAL_CORPUS_OUTPUT");
        if (string.IsNullOrWhiteSpace(output)) return;
        Directory.CreateDirectory(output);
        var stem = Path.GetFileNameWithoutExtension(relativePath);
        File.WriteAllBytes(Path.Combine(output, stem + ".native.glb"), nativeBytes);
        File.WriteAllBytes(Path.Combine(output, stem + ".shared.glb"), sharedBytes);
    }

    /// <summary>Pins every authored texture file and snapshots its cached base-level object, buffer and pixel content.</summary>
    /// <param name="source">The complete authored model whose texture references must match the pinned set.</param>
    /// <param name="dataDirectory">The external retail data directory.</param>
    /// <param name="resolver">The same resolver supplied to both export paths.</param>
    /// <returns>Every resolved source image, original base-level buffer and pixel hash before export.</returns>
    private static Dictionary<string, (DecodedTexture Texture, byte[] Buffer, string Hash)> SnapshotNeonTextures(
        GlbScene source, string dataDirectory, NifTextureResolver resolver)
    {
        var paths = source.MeshParts.SelectMany(static part => new[]
            { part.Submesh.DiffuseTexturePath, part.Submesh.NormalMapTexturePath, part.Submesh.ShaderMetadata?.GlowMapPath })
            .Where(static path => path is not null).Select(static path => path!.Replace('\\', '/'))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        Assert.Equal(NeonTextures.Select(static texture => texture.Path).Order(StringComparer.Ordinal), paths);
        var snapshots = new Dictionary<string, (DecodedTexture Texture, byte[] Buffer, string Hash)>(StringComparer.Ordinal);
        foreach (var (path, hash) in NeonTextures)
        {
            var physicalPath = NifTexturePathUtility.Normalize(path).Replace('\\', Path.DirectorySeparatorChar);
            var file = Path.Combine(dataDirectory, physicalPath);
            Assert.True(File.Exists(file), $"The named retail texture is missing: {file}");
            Assert.InRange(new FileInfo(file).Length, 1, 4 * 1024 * 1024);
            Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))));
            var texture = resolver.GetTexture(path);
            Assert.NotNull(texture);
            snapshots.Add(path, (texture, texture.Pixels, Convert.ToHexString(SHA256.HashData(texture.Pixels))));
        }
        return snapshots;
    }

    /// <summary>Requires the export boundary to leave every resolved source texture and its pixels unchanged.</summary>
    /// <param name="snapshots">The original source texture objects, buffers and pixel hashes.</param>
    /// <param name="resolver">The resolver whose cached source images must remain unchanged.</param>
    private static void AssertNeonTexturesUnchanged(
        Dictionary<string, (DecodedTexture Texture, byte[] Buffer, string Hash)> snapshots, NifTextureResolver resolver)
    {
        Assert.Equal(7, snapshots.Count);
        foreach (var (path, saved) in snapshots)
        {
            var texture = resolver.GetTexture(path);
            Assert.Same(saved.Texture, texture);
            Assert.Same(saved.Buffer, texture!.Pixels);
            Assert.Equal(saved.Hash, Convert.ToHexString(SHA256.HashData(texture.Pixels)));
        }
    }

    /// <summary>Checks every original draw placement, including all unlit controls, without selecting only the emissive part.</summary>
    /// <param name="source">The complete source hierarchy and draw occurrences.</param>
    /// <param name="neutral">The full immutable snapshot being accepted.</param>
    /// <param name="resolver">The source material resolver used by the writer's drawable predicate.</param>
    private static void AssertNeonPlacementsAndMaterials(GlbScene source, ModelDocument neutral, NifTextureResolver resolver)
    {
        Assert.Equal(19, source.Nodes.Count);
        Assert.Equal(11, source.MeshParts.Count);
        Assert.Equal(2455, source.MeshParts.Sum(static part => part.Submesh.VertexCount));
        Assert.Equal(1895, source.MeshParts.Sum(static part => part.Submesh.TriangleCount));
        Assert.Equal(5, source.MeshParts.Count(static part => part.Submesh.IsEmissive));
        Assert.Equal(11, neutral.Meshes.Count);
        Assert.Equal(5, neutral.Materials.Count);
        Assert.Equal(0, NifCorpusLegacyTriangles.RepeatedPositions(source, resolver));
        Assert.Equal(0, NifCorpusLegacyTriangles.RepeatedPositions(neutral));
        var emitting = Assert.Single(neutral.Materials, static material => material.EmissiveTexture is not null);
        Assert.False(emitting.Unlit);
        Assert.Equal(Vector3.One, emitting.EmissiveFactor);
        Assert.Equal(1f, emitting.EmissiveStrength);
        AssertNeonGlowPixels(neutral.Images[emitting.EmissiveTexture!.Value.ImageIndex].CopyContent());
        foreach (var part in source.MeshParts)
        {
            Assert.Null(part.Skin);
            Assert.NotNull(part.NodeIndex);
            var meshIndex = neutral.Nodes[part.NodeIndex.Value].MeshIndex;
            Assert.NotNull(meshIndex);
            var primitive = Assert.Single(neutral.Meshes[meshIndex.Value].Primitives);
            Assert.Equal(part.Name, primitive.Name);
            Assert.Equal(part.Submesh.VertexCount, primitive.Vertices.Count);
            Assert.Equal(part.Submesh.Triangles.Length, primitive.Indices.Count);
            Assert.NotNull(primitive.MaterialIndex);
            var material = neutral.Materials[primitive.MaterialIndex.Value];
            Assert.Equal(part.Submesh.IsEmissive, material.Unlit);
            Assert.Equal(part.Submesh.SourceBlockIndex == 91, ReferenceEquals(emitting, material));
        }
    }

    /// <summary>Checks the independently measured glow pixels and the encoded default-strength term on the complete GLB.</summary>
    /// <param name="model">The complete native or shared GLB parsed after encoding.</param>
    private static void AssertNeonEncodedEmission(ModelRoot model)
    {
        Assert.Equal(11, model.LogicalMeshes.Count);
        Assert.Equal(5, model.LogicalMaterials.Count);
        Assert.Equal(2, model.LogicalMaterials.Count(static material => material.Unlit));
        Assert.DoesNotContain("KHR_materials_emissive_strength", model.ExtensionsUsed);
        var material = Assert.Single(model.LogicalMaterials, static candidate => candidate.FindChannel("Emissive")?.Texture is not null);
        Assert.False(material.Unlit);
        var emissive = material.FindChannel("Emissive")!.Value;
        Assert.Equal(Vector3.One, new Vector3(emissive.Color.X, emissive.Color.Y, emissive.Color.Z));
        Assert.Equal(1f, emissive.GetFactor("EmissiveStrength"));
        AssertNeonGlowPixels(emissive.Texture!.PrimaryImage.Content.Content.ToArray());
    }

    /// <summary>Preserves the existing BC1 endpoint expansion and rounded-third policy, independently decoded from raw blocks in Python.</summary>
    /// <param name="png">The image bytes retained by the neutral document or encoded emissive channel.</param>
    private static void AssertNeonGlowPixels(byte[] png)
    {
        // Pillow's BC1 thirds differ by at most one RGB unit. This exact digest pins BMT's existing
        // floor RGB565 expansion / rounded-third decode; emission transport must not change it.
        using var image = new MagickImage(png);
        Assert.Equal(512u, image.Width);
        Assert.Equal(128u, image.Height);
        using var pixelView = image.GetPixels();
        var rgba = Assert.IsType<byte[]>(pixelView.ToByteArray(PixelMapping.RGBA));
        Assert.Equal(NeonGlowPixels, Convert.ToHexString(SHA256.HashData(rgba)));
    }
}
