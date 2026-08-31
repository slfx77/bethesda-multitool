using System.Text;
using BethesdaMultitool.Core.Formats.Bsa.Ba2;
using BethesdaMultitool.Core.Formats.Nif.Materials;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Materials;

[Collection(SequentialIntegrationGroup.Name)]
public sealed class StarfieldMaterialAlphaPolicyTests(ITestOutputHelper output)
{
    private const string MaterialPath = @"materials\test\cutout.mat";

    [Fact]
    public void ResolveAlphaPolicy_InheritsPackedBaseAndPartialDiffOverride()
    {
        var db = Assert.IsType<StarfieldMaterialDatabase>(
            StarfieldMaterialDatabase.Parse(BuildDatabase()));

        var policy = db.ResolveAlphaPolicy(MaterialPath);

        Assert.True(policy.IsResolved);
        Assert.True(policy.HasOpacity);
        Assert.Equal(0.5f, policy.AlphaTestThreshold);
        Assert.Equal(0, policy.OpacitySourceLayer);
        Assert.Equal(StarfieldMaterialAlphaBlenderMode.Linear, policy.BlenderMode);
        Assert.False(policy.UsesDetailBlendMask);
        Assert.False(policy.UsesVertexColor);
        Assert.Equal(0u, policy.OpacityUvStreamId);
        Assert.True(policy.OpacityUvUsesIdentityUv0);
        Assert.False(policy.HasMalformedSettings);
        Assert.False(policy.UsesDitheredTransparency);
        Assert.False(policy.OpacityLayerUsesFlipbook);
        Assert.Equal(@"Data\Textures\Test\fence_opacity.dds", policy.OpacitySlot.TexturePath);

        Assert.True(policy.TryResolveStaticCutout(out var state));
        Assert.Equal(StarfieldMaterialAlphaRenderMode.Layer0OpacityCutout, state.Mode);
        Assert.Equal(0.5f, state.AlphaTestThreshold);
    }

    [Theory]
    [InlineData(true, false, false, false, 0u)]
    [InlineData(false, true, false, false, 0u)]
    [InlineData(false, false, true, false, 0u)]
    [InlineData(false, false, false, true, 0u)]
    [InlineData(false, false, false, false, 42u)]
    public void ResolveStaticCutout_RejectsAdditionalCoverageInputs(
        bool useDetailMask,
        bool useVertexColor,
        bool dithered,
        bool flipbook,
        uint uvStream)
    {
        var policy = new StarfieldMaterialAlphaPolicy(
            true,
            true,
            0.4f,
            0,
            StarfieldMaterialAlphaBlenderMode.Linear,
            useDetailMask,
            useVertexColor,
            StarfieldMaterialColorChannel.Red,
            uvStream,
            uvStream == 0,
            false,
            0f,
            0.05f,
            0.5f,
            0f,
            dithered,
            flipbook,
            new StarfieldMaterialSlot(@"textures\test\opacity.dds", null));

        Assert.False(policy.TryResolveStaticCutout(out _));
    }

    [Theory]
    [InlineData(AlphaUvFixture.Identity, true, false, 6u)]
    [InlineData(AlphaUvFixture.Scaled, false, false, 6u)]
    [InlineData(AlphaUvFixture.MissingTarget, false, false, 99u)]
    [InlineData(AlphaUvFixture.WrongTypeTarget, false, false, 6u)]
    [InlineData(AlphaUvFixture.MalformedWrapper, false, true, 0u)]
    [InlineData(AlphaUvFixture.TrailingData, false, true, 0u)]
    public void ResolveAlphaPolicy_DecodesNestedUvStreamDiffAndAdmitsOnlyIdentityUv0(
        AlphaUvFixture fixture,
        bool expectedSupported,
        bool expectedMalformed,
        uint expectedStreamId)
    {
        var db = Assert.IsType<StarfieldMaterialDatabase>(
            StarfieldMaterialDatabase.Parse(BuildDatabase(alphaUvFixture: fixture)));

        var policy = db.ResolveAlphaPolicy(MaterialPath);

        Assert.Equal(expectedStreamId, policy.OpacityUvStreamId);
        Assert.Equal(expectedSupported, policy.OpacityUvUsesIdentityUv0);
        Assert.Equal(expectedMalformed, policy.HasMalformedSettings);
        if (fixture is AlphaUvFixture.Identity or
            AlphaUvFixture.Scaled or
            AlphaUvFixture.MissingTarget or
            AlphaUvFixture.WrongTypeTarget)
        {
            // Fields on both sides of the nested UVStreamID prove that both inner terminators were
            // consumed without ending the AlphaBlenderSettings or root AlphaSettings DIFF early.
            Assert.Equal(0.125f, policy.HeightBlendThreshold);
            Assert.Equal(0.25f, policy.Contrast);
            Assert.Equal(0.5f, policy.AlphaTestThreshold);
        }

        Assert.Equal(expectedSupported, policy.TryResolveStaticCutout(out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResolveAlphaPolicy_RejectsFlipbookOnSelectedOpacityLayer(
        bool diffWritesAnotherFieldFirst)
    {
        var db = Assert.IsType<StarfieldMaterialDatabase>(
            StarfieldMaterialDatabase.Parse(BuildDatabase(
                layerIsFlipbook: true,
                flipbookDiffHasLeadingField: diffWritesAnotherFieldFirst)));

        var policy = db.ResolveAlphaPolicy(MaterialPath);

        Assert.True(policy.OpacityLayerUsesFlipbook);
        Assert.False(policy.TryResolveStaticCutout(out _));
    }

    [Fact]
    [Trait("Category", BucketBTestGuard.Category)]
    public void RetailDatabase_CensusesAlphaSettingsWithoutConflatingBlendOrTint()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var archivePath = RealAssetPaths.SteamGameFile(
            "Starfield", @"Data\Starfield - Materials.ba2");
        Assert.SkipUnless(
            archivePath is not null,
            RealAssetPaths.SkipMessage("Starfield materials archive"));

        using var extractor = new Ba2Extractor(archivePath!);
        var entry = extractor.Archive.FindFile(@"materials\materialsbeta.cdb");
        Assert.NotNull(entry);
        var db = Assert.IsType<StarfieldMaterialDatabase>(
            StarfieldMaterialDatabase.Parse(extractor.ExtractFile(entry!)));

        var census = db.BuildAlphaCensus();
        output.WriteLine("{0}", census);

        Assert.Equal(db.ComponentTableCount, db.ComponentChunkCount);
        Assert.InRange(census.ComponentObjectCount, 1, 100_000);
        Assert.InRange(census.ResourceMaterialsWithOpacity, 1, census.ResourceMaterialCount);
        Assert.InRange(census.SupportedStaticCutouts, 1, census.ResourceMaterialsWithOpacity);
    }

    private static byte[] BuildDatabase(
        bool layerIsFlipbook = false,
        bool flipbookDiffHasLeadingField = false,
        AlphaUvFixture alphaUvFixture = AlphaUvFixture.None)
    {
        var strings = new[]
        {
            "BSComponentDB2::DBFileIndex::ObjectInfo",
            "BSComponentDB2::DBFileIndex::ComponentInfo",
            "BSMaterial::Internal::CompiledDB",
            "BSComponentDB2::DBFileIndex",
            "BSMaterial::LayerID",
            "BSMaterial::MaterialID",
            "BSMaterial::TextureSetID",
            "BSMaterial::MRTextureFile",
            "BSMaterial::AlphaSettingsComponent",
            "BSMaterial::FlipbookComponent",
            "BSMaterial::Scale"
        };
        var offsets = new Dictionary<string, uint>();
        var strt = new List<byte>();
        foreach (var value in strings)
        {
            offsets[value] = (uint)strt.Count;
            strt.AddRange(Encoding.ASCII.GetBytes(value));
            strt.Add(0);
        }

        const uint root = 1;
        const uint baseRoot = 2;
        const uint layer = 3;
        const uint material = 4;
        const uint textureSet = 5;
        const uint uvStream = 6;
        const uint uvStreamRoot = 7;
        const uint layeredMaterialsRoot = 8;
        const uint layersRoot = 9;
        const uint materialsRoot = 10;
        const uint textureSetsRoot = 11;
        var declaresUvStream = alphaUvFixture is AlphaUvFixture.Identity or
            AlphaUvFixture.Scaled or AlphaUvFixture.WrongTypeTarget;
        var uvRootResource = StarfieldMaterialDatabase.ComputeResourceId(
            alphaUvFixture == AlphaUvFixture.WrongTypeTarget
                ? @"materials\layered\root\materials.mat"
                : @"materials\layered\root\uvstreams.mat");
        var layeredMaterialsRootResource = StarfieldMaterialDatabase.ComputeResourceId(
            @"materials\layered\root\layeredmaterials.mat");
        var layersRootResource = StarfieldMaterialDatabase.ComputeResourceId(
            @"materials\layered\root\layers.mat");
        var materialsRootResource = StarfieldMaterialDatabase.ComputeResourceId(
            @"materials\layered\root\materials.mat");
        var textureSetsRootResource = StarfieldMaterialDatabase.ComputeResourceId(
            @"materials\layered\root\texturesets.mat");
        var resource = StarfieldMaterialDatabase.ComputeResourceId(MaterialPath);
        var chunks = new List<byte[]>
        {
            Chunk("CLAS", Concat(
                U32(offsets["BSComponentDB2::DBFileIndex::ObjectInfo"]), U32(1), U16(0), U16(4)))
        };

        var objects = new List<byte>();
        objects.AddRange(U32(offsets["BSComponentDB2::DBFileIndex::ObjectInfo"]));
        objects.AddRange(U32(declaresUvStream ? 11u : 9u));
        objects.AddRange(ObjectRecord(resource.File, resource.Ext, resource.Dir, root, baseRoot));
        objects.AddRange(ObjectRecord(0, 0, 0, baseRoot, layeredMaterialsRoot));
        objects.AddRange(ObjectRecord(0, 0, 0, layer, layersRoot));
        objects.AddRange(ObjectRecord(0, 0, 0, material, materialsRoot));
        objects.AddRange(ObjectRecord(0, 0, 0, textureSet, textureSetsRoot));
        if (declaresUvStream)
        {
            objects.AddRange(ObjectRecord(0, 0, 0, uvStream, uvStreamRoot));
            objects.AddRange(ObjectRecord(
                uvRootResource.File,
                uvRootResource.Ext,
                uvRootResource.Dir,
                uvStreamRoot));
        }
        objects.AddRange(ObjectRecord(
            layeredMaterialsRootResource.File,
            layeredMaterialsRootResource.Ext,
            layeredMaterialsRootResource.Dir,
            layeredMaterialsRoot));
        objects.AddRange(ObjectRecord(
            layersRootResource.File,
            layersRootResource.Ext,
            layersRootResource.Dir,
            layersRoot));
        objects.AddRange(ObjectRecord(
            materialsRootResource.File,
            materialsRootResource.Ext,
            materialsRootResource.Dir,
            materialsRoot));
        objects.AddRange(ObjectRecord(
            textureSetsRootResource.File,
            textureSetsRootResource.Ext,
            textureSetsRootResource.Dir,
            textureSetsRoot));
        chunks.Add(Chunk("LIST", [.. objects]));
        chunks.Add(Chunk("OBJT", Concat(
            U32(offsets["BSMaterial::Internal::CompiledDB"]), Str("1.16.244.0"))));
        chunks.Add(Chunk("OBJT", Concat(U32(offsets["BSComponentDB2::DBFileIndex"]), [0])));

        var alphaDiff = alphaUvFixture switch
        {
            AlphaUvFixture.None => Concat(U16(1), F32(0.5f), U16(0xFFFF)),
            AlphaUvFixture.TrailingData => Concat(
                U16(1), F32(0.5f), U16(0xFFFF), [0x7F]),
            AlphaUvFixture.MalformedWrapper => Concat(
                U16(3),
                U16(4), U16(0), U32(uvStream), U16(0xFFFF),
                U16(0xFFFF), U16(0xFFFF)),
            _ => Concat(
                U16(3),
                U16(5), F32(0.125f),
                U16(4), U16(0), U16(0),
                U32(alphaUvFixture == AlphaUvFixture.MissingTarget ? 99u : uvStream),
                U16(0xFFFF), U16(0xFFFF),
                U16(8), F32(0.25f),
                U16(0xFFFF),
                U16(1), F32(0.5f),
                U16(0xFFFF))
        };

        var components = new List<(uint Owner, uint Slot, string ClassName, string Tag, byte[] Data)>
        {
            (root, 0, "BSMaterial::LayerID", "DIFF", Concat(U16(0), U16(0), U32(layer))),
            (layer, 0, "BSMaterial::MaterialID", "DIFF", Concat(U16(0), U16(0), U32(material))),
            (material, 0, "BSMaterial::TextureSetID", "DIFF", Concat(U16(0), U16(0), U32(textureSet))),
            (textureSet, 2, "BSMaterial::MRTextureFile", "DIFF",
                Concat(U16(0), Str(@"Data\Textures\Test\fence_opacity.dds"), U16(0xFFFF))),
            (baseRoot, 0, "BSMaterial::AlphaSettingsComponent", "OBJT", Concat(
                [1], F32(0.25f), Str("MATERIAL_LAYER_0"),
                Str("Linear"), [0], [0], Str("Red"), U32(0),
                F32(0f), F32(0.05f), F32(0.5f), F32(0f), [0])),
            // The default is a one-field DIFF; UV fixtures exercise the three nested compound levels.
            (root, 0, "BSMaterial::AlphaSettingsComponent", "DIFF", alphaDiff)
        };
        if (declaresUvStream)
        {
            components.Add((
                uvStream,
                0,
                "BSMaterial::Scale",
                "OBJT",
                Concat(F32(alphaUvFixture == AlphaUvFixture.Scaled ? 2f : 1f), F32(1f))));
        }
        if (layerIsFlipbook)
        {
            components.Add(flipbookDiffHasLeadingField
                ? (material, 0u, "BSMaterial::FlipbookComponent", "DIFF",
                    Concat(U16(1), U32(4), U16(0), [1], U16(4), [1], U16(0xFFFF)))
                : (material, 0u, "BSMaterial::FlipbookComponent", "OBJT",
                    Concat([1], U32(4), U32(4), F32(24f), [1])));
        }

        var table = new List<byte>();
        table.AddRange(U32(offsets["BSComponentDB2::DBFileIndex::ComponentInfo"]));
        table.AddRange(U32((uint)components.Count));
        foreach (var component in components)
        {
            table.AddRange(U32(component.Owner));
            table.AddRange(U32(component.Slot));
        }

        chunks.Add(Chunk("LIST", [.. table]));
        foreach (var component in components)
        {
            chunks.Add(Chunk(component.Tag, Concat(U32(offsets[component.ClassName]), component.Data)));
        }

        var file = new List<byte>();
        file.AddRange(Encoding.ASCII.GetBytes("BETH"));
        file.AddRange(U32(8));
        file.AddRange(U32(4));
        file.AddRange(U32((uint)chunks.Count + 2));
        file.AddRange(Encoding.ASCII.GetBytes("STRT"));
        file.AddRange(U32((uint)strt.Count));
        file.AddRange(strt);
        foreach (var chunk in chunks) file.AddRange(chunk);
        return [.. file];
    }

    private static byte[] ObjectRecord(uint file, uint ext, uint dir, uint id, uint baseId = 0) =>
        Concat(U32(file), U32(ext), U32(dir), U32(id), U32(baseId), [1]);

    private static byte[] Chunk(string tag, byte[] body) =>
        Concat(Encoding.ASCII.GetBytes(tag), U32((uint)body.Length), body);

    private static byte[] Str(string value) =>
        Concat(U16((ushort)value.Length), Encoding.ASCII.GetBytes(value));

    private static byte[] F32(float value) => BitConverter.GetBytes(value);
    private static byte[] U32(uint value) => BitConverter.GetBytes(value);
    private static byte[] U16(ushort value) => BitConverter.GetBytes(value);

    private static byte[] Concat(params byte[][] parts)
    {
        var result = new List<byte>();
        foreach (var part in parts) result.AddRange(part);
        return [.. result];
    }

    public enum AlphaUvFixture
    {
        None,
        Identity,
        Scaled,
        MissingTarget,
        WrongTypeTarget,
        MalformedWrapper,
        TrailingData
    }
}
