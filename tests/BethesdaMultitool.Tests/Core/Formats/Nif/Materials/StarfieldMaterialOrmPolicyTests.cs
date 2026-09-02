using System.Numerics;
using System.Text;
using BethesdaMultitool.Core.Formats.Nif.Materials;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Materials;

public sealed class StarfieldMaterialOrmPolicyTests
{
    private const string MaterialPath = @"materials\test\orm.mat";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResolveOrmPolicy_DecodesStaticLayerUvAndScalarSlots(bool useDiffChunks)
    {
        var db = Assert.IsType<StarfieldMaterialDatabase>(
            StarfieldMaterialDatabase.Parse(BuildDatabase(useDiffChunks)));

        var policy = db.ResolveOrmPolicy(MaterialPath);

        Assert.Equal(db.ComponentTableCount, db.ComponentChunkCount);
        Assert.True(policy.IsResolved);
        Assert.True(policy.HasOnlyLayer0);
        Assert.False(policy.HasBlenders);
        Assert.False(policy.LayerUsesFlipbook);
        Assert.False(policy.HasMalformedStaticComponents);
        Assert.Equal(StarfieldMaterialShaderRoute.Deferred, policy.ShaderRoute);
        Assert.Equal(Vector2.One, policy.UvScale);
        Assert.Equal(Vector2.Zero, policy.UvOffset);
        Assert.Equal(StarfieldMaterialTextureAddressMode.Wrap, policy.TextureAddressMode);
        Assert.Equal(StarfieldMaterialUvChannel.One, policy.UvChannel);
        Assert.Equal(@"Data\Textures\Test\surface_rough.dds", policy.RoughnessSlot.TexturePath);
        Assert.Equal(0xFF000040u, policy.MetalnessSlot.ReplacementRgba);
        Assert.Equal(@"Data\Textures\Test\surface_ao.dds", policy.AmbientOcclusionSlot.TexturePath);
        Assert.True(policy.TryResolveStaticLayer0Orm(out var state));
        Assert.Equal(policy.RoughnessSlot, state.RoughnessSlot);
        Assert.Equal(policy.MetalnessSlot, state.MetalnessSlot);
        Assert.Equal(policy.AmbientOcclusionSlot, state.AmbientOcclusionSlot);
    }

    [Theory]
    [InlineData("extra-layer")]
    [InlineData("blender")]
    [InlineData("flipbook")]
    [InlineData("scaled")]
    [InlineData("clamped")]
    [InlineData("uv-two")]
    [InlineData("water")]
    [InlineData("hair")]
    [InlineData("missing-uv-target")]
    [InlineData("wrong-uv-target-type")]
    [InlineData("wrong-layer-target-type")]
    [InlineData("wrong-material-target-type")]
    [InlineData("wrong-texture-set-target-type")]
    [InlineData("malformed-texture")]
    [InlineData("malformed-replacement")]
    public void ResolveOrmPolicy_RejectsNonStaticOrNonRepresentableInputs(string unsupported)
    {
        var db = Assert.IsType<StarfieldMaterialDatabase>(
            StarfieldMaterialDatabase.Parse(BuildDatabase(
                useDiffChunks: true,
                unsupported: unsupported)));

        var policy = db.ResolveOrmPolicy(MaterialPath);

        Assert.True(policy.IsResolved);
        Assert.False(policy.TryResolveStaticLayer0Orm(out _));
    }

    [Fact]
    public void ResolveOrmPolicy_MissingPathFailsClosed()
    {
        var db = Assert.IsType<StarfieldMaterialDatabase>(
            StarfieldMaterialDatabase.Parse(BuildDatabase(useDiffChunks: false)));

        Assert.False(db.ResolveOrmPolicy(@"materials\test\missing.mat").IsResolved);
    }

    [Fact]
    public void ResolveShaderRoute_AdmitsOnlyDefinedEffectiveRoute_NotWaterShaderModelName()
    {
        var water = Assert.IsType<StarfieldMaterialDatabase>(
            StarfieldMaterialDatabase.Parse(BuildDatabase(
                useDiffChunks: true,
                shaderRoute: "Water",
                shaderModel: "Water1Layer")));
        var modelOnly = Assert.IsType<StarfieldMaterialDatabase>(
            StarfieldMaterialDatabase.Parse(BuildDatabase(
                useDiffChunks: true,
                shaderRoute: "Deferred",
                shaderModel: "Water1Layer")));
        var malformedRoute = Assert.IsType<StarfieldMaterialDatabase>(
            StarfieldMaterialDatabase.Parse(BuildDatabase(
                useDiffChunks: true,
                shaderRoute: "Water1Layer",
                shaderModel: "Water1Layer")));

        Assert.Equal(StarfieldMaterialShaderRoute.Water, water.ResolveShaderRoute(MaterialPath));
        Assert.Equal(StarfieldMaterialShaderRoute.Deferred, modelOnly.ResolveShaderRoute(MaterialPath));
        Assert.Null(malformedRoute.ResolveShaderRoute(MaterialPath));
        Assert.Null(water.ResolveShaderRoute(@"materials\test\missing.mat"));
    }

    [Theory]
    [InlineData("dangling", true)]
    [InlineData("zero", true)]
    [InlineData("has-data-false", false)]
    [InlineData("valid-wrong-numeric", false)]
    public void ResolveOrmPolicy_WideObjectInfoUsesPersistentParentWithReferencePrecedence(
        string objectInfoCase,
        bool expectedResolved)
    {
        var db = Assert.IsType<StarfieldMaterialDatabase>(
            StarfieldMaterialDatabase.Parse(BuildDatabase(
                useDiffChunks: false,
                objectInfoCase: objectInfoCase)));

        var policy = db.ResolveOrmPolicy(MaterialPath);

        Assert.Equal(expectedResolved, policy.IsResolved);
        Assert.Equal(expectedResolved, policy.TryResolveStaticLayer0Orm(out _));
    }

    [Fact]
    public void ResolveOrmPolicy_PartialReplacementDiffInheritsUnauthoredRedChannel()
    {
        var db = Assert.IsType<StarfieldMaterialDatabase>(
            StarfieldMaterialDatabase.Parse(BuildDatabase(
                useDiffChunks: true,
                inheritanceCase: "partial-replacement")));

        var policy = db.ResolveOrmPolicy(MaterialPath);

        Assert.True(policy.TryResolveStaticLayer0Orm(out _));
        Assert.Equal(0xFFBF8040u, policy.MetalnessSlot.ReplacementRgba);
    }

    [Fact]
    public void ResolveOrmPolicy_DisabledReplacementClearsInheritedEnabledReplacement()
    {
        var db = Assert.IsType<StarfieldMaterialDatabase>(
            StarfieldMaterialDatabase.Parse(BuildDatabase(
                useDiffChunks: true,
                inheritanceCase: "disabled-replacement")));

        var policy = db.ResolveOrmPolicy(MaterialPath);

        Assert.True(policy.TryResolveStaticLayer0Orm(out _));
        Assert.False(policy.MetalnessSlot.IsResolved);
    }

    [Fact]
    public void ResolveOrmPolicy_EmptyMrTexturePathClearsInheritedImage()
    {
        var db = Assert.IsType<StarfieldMaterialDatabase>(
            StarfieldMaterialDatabase.Parse(BuildDatabase(
                useDiffChunks: true,
                inheritanceCase: "empty-texture")));

        var policy = db.ResolveOrmPolicy(MaterialPath);

        Assert.True(policy.TryResolveStaticLayer0Orm(out _));
        Assert.False(policy.RoughnessSlot.IsResolved);
    }

    [Theory]
    [InlineData("layer", false)]
    [InlineData("material", false)]
    [InlineData("texture-set", false)]
    [InlineData("blender", true)]
    public void ResolveOrmPolicy_ExplicitZeroLinkStopsInheritance(
        string clearCase,
        bool expectedSupported)
    {
        var db = Assert.IsType<StarfieldMaterialDatabase>(
            StarfieldMaterialDatabase.Parse(BuildDatabase(
                useDiffChunks: true,
                clearCase: clearCase)));

        var policy = db.ResolveOrmPolicy(MaterialPath);

        Assert.Equal(expectedSupported, policy.TryResolveStaticLayer0Orm(out _));
        if (clearCase == "blender")
        {
            Assert.True(policy.IsResolved);
            Assert.False(policy.HasBlenders);
        }
        else
        {
            Assert.False(policy.IsResolved);
        }
    }

    internal static byte[] BuildDatabase(
        bool useDiffChunks,
        string? unsupported = null,
        string? objectInfoCase = null,
        string? inheritanceCase = null,
        string? clearCase = null,
        string shaderRoute = "Deferred",
        string shaderModel = "BaseMaterial",
        StarfieldEffectSettingsFixture? effectSettings = null,
        StarfieldEffectOpacityFixture? effectOpacity = null)
    {
        var classNames = new[]
        {
            "BSComponentDB2::DBFileIndex::ObjectInfo",
            "BSComponentDB2::DBFileIndex::ComponentInfo",
            "BSMaterial::Internal::CompiledDB",
            "BSComponentDB2::DBFileIndex",
            "BSMaterial::LayerID",
            "BSMaterial::BlenderID",
            "BSMaterial::MaterialID",
            "BSMaterial::TextureSetID",
            "BSMaterial::UVStreamID",
            "BSMaterial::Scale",
            "BSMaterial::Offset",
            "BSMaterial::TextureAddressModeComponent",
            "BSMaterial::Channel",
            "BSMaterial::ShaderRouteComponent",
            "BSMaterial::ShaderModelComponent",
            "BSMaterial::MRTextureFile",
            "BSMaterial::TextureReplacement",
            "BSMaterial::FlipbookComponent",
            "BSMaterial::EffectSettingsComponent",
            "BSMaterial::OpacityComponent"
        };
        var offsets = new Dictionary<string, uint>();
        var strings = new List<byte>();
        foreach (var className in classNames)
        {
            offsets[className] = (uint)strings.Count;
            strings.AddRange(Encoding.ASCII.GetBytes(className));
            strings.Add(0);
        }

        const uint rootId = 1;
        const uint layerId = 2;
        const uint materialId = 3;
        const uint textureSetId = 4;
        const uint uvStreamId = 5;
        const uint blenderId = 6;
        const uint uvStreamRootId = 7;
        const uint layeredMaterialsRootId = 8;
        const uint blendersRootId = 9;
        const uint layersRootId = 10;
        const uint materialsRootId = 11;
        const uint textureSetsRootId = 12;
        const uint persistentMaterialBaseId = 13;
        const uint baseTextureSetId = 13;
        const uint clearBaseObjectId = 13;
        var resource = StarfieldMaterialDatabase.ComputeResourceId(MaterialPath);
        var uvRootResource = StarfieldMaterialDatabase.ComputeResourceId(
            unsupported == "wrong-uv-target-type"
                ? @"materials\layered\root\materials.mat"
                : @"materials\layered\root\uvstreams.mat");
        var layeredMaterialsRoot = StarfieldMaterialDatabase.ComputeResourceId(
            @"materials\layered\root\layeredmaterials.mat");
        var blendersRoot = StarfieldMaterialDatabase.ComputeResourceId(
            @"materials\layered\root\blenders.mat");
        var layersRoot = StarfieldMaterialDatabase.ComputeResourceId(
            @"materials\layered\root\layers.mat");
        var materialsRoot = StarfieldMaterialDatabase.ComputeResourceId(
            @"materials\layered\root\materials.mat");
        var textureSetsRoot = StarfieldMaterialDatabase.ComputeResourceId(
            @"materials\layered\root\texturesets.mat");
        var persistentMaterialBase = StarfieldMaterialDatabase.ComputeResourceId(
            @"materials\test\persistent-base.mat");
        var wideObjectInfo = objectInfoCase is not null;
        var chunks = new List<byte[]>
        {
            Chunk("CLAS", Concat(
                U32(offsets["BSComponentDB2::DBFileIndex::ObjectInfo"]), U32(1), U16(0),
                U16(wideObjectInfo ? (ushort)5 : (ushort)4)))
        };

        var objects = new List<byte>();
        objects.AddRange(U32(offsets["BSComponentDB2::DBFileIndex::ObjectInfo"]));
        objects.AddRange(U32(
            wideObjectInfo || inheritanceCase is not null || clearCase is not null ? 13u : 12u));
        var rootNumericBase = objectInfoCase switch
        {
            "dangling" => 0x00F00000u,
            "zero" or "has-data-false" => 0u,
            "valid-wrong-numeric" => materialsRootId,
            _ => layeredMaterialsRootId
        };
        objects.AddRange(ObjectRecordForFixture(
            wideObjectInfo,
            resource.File,
            resource.Ext,
            resource.Dir,
            rootId,
            clearCase is "layer" or "blender" ? clearBaseObjectId : rootNumericBase,
            persistentMaterialBase,
            objectInfoCase != "has-data-false"));
        objects.AddRange(ObjectRecordForFixture(wideObjectInfo,
            0, 0, 0, layerId,
            clearCase == "material"
                ? clearBaseObjectId
                : unsupported == "wrong-layer-target-type" ? materialsRootId : layersRootId));
        objects.AddRange(ObjectRecordForFixture(wideObjectInfo,
            0, 0, 0, materialId,
            clearCase == "texture-set"
                ? clearBaseObjectId
                : unsupported == "wrong-material-target-type" ? layersRootId : materialsRootId));
        objects.AddRange(ObjectRecordForFixture(wideObjectInfo,
            0, 0, 0, textureSetId,
            unsupported == "wrong-texture-set-target-type"
                ? materialsRootId
                : inheritanceCase is not null ? baseTextureSetId : textureSetsRootId));
        objects.AddRange(ObjectRecordForFixture(wideObjectInfo, 0, 0, 0, uvStreamId, uvStreamRootId));
        objects.AddRange(ObjectRecordForFixture(wideObjectInfo, 0, 0, 0, blenderId, blendersRootId));
        objects.AddRange(ObjectRecordForFixture(wideObjectInfo,
            uvRootResource.File,
            uvRootResource.Ext,
            uvRootResource.Dir,
            uvStreamRootId));
        objects.AddRange(ObjectRecordForFixture(wideObjectInfo,
            layeredMaterialsRoot.File,
            layeredMaterialsRoot.Ext,
            layeredMaterialsRoot.Dir,
            layeredMaterialsRootId));
        objects.AddRange(ObjectRecordForFixture(wideObjectInfo,
            blendersRoot.File,
            blendersRoot.Ext,
            blendersRoot.Dir,
            blendersRootId));
        objects.AddRange(ObjectRecordForFixture(wideObjectInfo,
            layersRoot.File,
            layersRoot.Ext,
            layersRoot.Dir,
            layersRootId));
        objects.AddRange(ObjectRecordForFixture(wideObjectInfo,
            materialsRoot.File,
            materialsRoot.Ext,
            materialsRoot.Dir,
            materialsRootId));
        objects.AddRange(ObjectRecordForFixture(wideObjectInfo,
            textureSetsRoot.File,
            textureSetsRoot.Ext,
            textureSetsRoot.Dir,
            textureSetsRootId));
        if (wideObjectInfo)
        {
            objects.AddRange(ObjectRecordForFixture(
                true,
                persistentMaterialBase.File,
                persistentMaterialBase.Ext,
                persistentMaterialBase.Dir,
                persistentMaterialBaseId,
                layeredMaterialsRootId));
        }
        else if (inheritanceCase is not null)
        {
            objects.AddRange(ObjectRecord(
                0, 0, 0, baseTextureSetId, textureSetsRootId));
        }
        else if (clearCase is not null)
        {
            var clearTypeRoot = clearCase switch
            {
                "layer" or "blender" => layeredMaterialsRootId,
                "material" => layersRootId,
                "texture-set" => materialsRootId,
                _ => 0u
            };
            objects.AddRange(ObjectRecord(0, 0, 0, clearBaseObjectId, clearTypeRoot));
        }
        chunks.Add(Chunk("LIST", [.. objects]));
        chunks.Add(Chunk("OBJT", Concat(
            U32(offsets["BSMaterial::Internal::CompiledDB"]), Str("1.16.244.0"))));
        chunks.Add(Chunk("OBJT", Concat(
            U32(offsets["BSComponentDB2::DBFileIndex"]), [0])));

        var components = new List<Component>
        {
            Id(layerId, 0, "BSMaterial::UVStreamID",
                unsupported == "missing-uv-target" ? 99u : uvStreamId),
            Float2(uvStreamId, "BSMaterial::Scale", unsupported == "scaled" ? 2f : 1f, 1f),
            Float2(uvStreamId, "BSMaterial::Offset", 0f, 0f),
            StringValue(uvStreamId, "BSMaterial::TextureAddressModeComponent",
                unsupported == "clamped" ? "Clamp" : "Wrap"),
            StringValue(uvStreamId, "BSMaterial::Channel", unsupported == "uv-two" ? "Two" : "One"),
            StringValue(rootId, "BSMaterial::ShaderRouteComponent",
                unsupported == "water" ? "Water" : shaderRoute),
            StringValue(rootId, "BSMaterial::ShaderModelComponent",
                unsupported == "hair" ? "Hair1Layer" : shaderModel),
            StringValue(textureSetId, "BSMaterial::MRTextureFile",
                @"Data\Textures\Test\surface_ao.dds", 5)
        };
        if (clearCase == "layer")
        {
            components.Add(Id(clearBaseObjectId, 0, "BSMaterial::LayerID", layerId));
            components.Add(Id(rootId, 0, "BSMaterial::LayerID", 0));
        }
        else
        {
            components.Add(Id(rootId, 0, "BSMaterial::LayerID", layerId));
        }

        if (clearCase == "material")
        {
            components.Add(Id(clearBaseObjectId, 0, "BSMaterial::MaterialID", materialId));
            components.Add(Id(layerId, 0, "BSMaterial::MaterialID", 0));
        }
        else
        {
            components.Add(Id(layerId, 0, "BSMaterial::MaterialID", materialId));
        }

        if (clearCase == "texture-set")
        {
            components.Add(Id(clearBaseObjectId, 0, "BSMaterial::TextureSetID", textureSetId));
            components.Add(Id(materialId, 0, "BSMaterial::TextureSetID", 0));
        }
        else
        {
            components.Add(Id(materialId, 0, "BSMaterial::TextureSetID", textureSetId));
        }

        if (clearCase == "blender")
        {
            components.Add(Id(clearBaseObjectId, 0, "BSMaterial::BlenderID", blenderId));
            components.Add(Id(rootId, 0, "BSMaterial::BlenderID", 0));
        }
        if (inheritanceCase == "empty-texture")
        {
            components.Add(StringValue(
                baseTextureSetId,
                "BSMaterial::MRTextureFile",
                @"Data\Textures\Test\surface_rough.dds",
                3));
            components.Add(StringValue(textureSetId, "BSMaterial::MRTextureFile", string.Empty, 3));
        }
        else
        {
            components.Add(unsupported == "malformed-texture"
                ? MalformedTexture(textureSetId, 3)
                : StringValue(textureSetId, "BSMaterial::MRTextureFile",
                    @"Data\Textures\Test\surface_rough.dds", 3));
        }

        if (inheritanceCase is "partial-replacement" or "disabled-replacement")
        {
            components.Add(Replacement(baseTextureSetId, 4, 0.25f, 0f, 0f, 1f));
            components.Add(inheritanceCase == "partial-replacement"
                ? PartialReplacement(textureSetId, 4, 0.5f, 0.75f, 1f)
                : DisabledReplacement(textureSetId, 4));
        }
        else
        {
            components.Add(unsupported == "malformed-replacement"
                ? MalformedReplacement(textureSetId, 4)
                : Replacement(textureSetId, 4, 0.25f, 0f, 0f, 1f));
        }
        if (unsupported == "extra-layer")
        {
            components.Add(Id(rootId, 1, "BSMaterial::LayerID", layerId));
        }

        if (unsupported == "blender")
        {
            components.Add(Id(rootId, 0, "BSMaterial::BlenderID", blenderId));
        }

        if (unsupported == "flipbook")
        {
            components.Add(new Component(
                materialId, 0, "BSMaterial::FlipbookComponent", [1], Concat(U16(0), [1])));
        }

        if (effectSettings is { } effect)
        {
            components.Add(EffectSettings(rootId, effect));
            if (effect.OpacityTexturePath is { } opacityTexturePath)
            {
                components.Add(StringValue(
                    textureSetId,
                    "BSMaterial::MRTextureFile",
                    opacityTexturePath,
                    2));
            }
        }

        if (effectOpacity is { } opacity)
        {
            components.Add(EffectOpacity(rootId, opacity));
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
            chunks.Add(Chunk(useDiffChunks ? "DIFF" : "OBJT", Concat(
                U32(offsets[component.ClassName]),
                useDiffChunks ? component.Diff : component.Packed)));
        }

        var file = new List<byte>();
        file.AddRange(Encoding.ASCII.GetBytes("BETH"));
        file.AddRange(U32(8));
        file.AddRange(U32(4));
        file.AddRange(U32((uint)chunks.Count + 2));
        file.AddRange(Encoding.ASCII.GetBytes("STRT"));
        file.AddRange(U32((uint)strings.Count));
        file.AddRange(strings);
        foreach (var chunk in chunks) file.AddRange(chunk);
        return [.. file];
    }

    private static Component Id(uint owner, uint slot, string className, uint value) =>
        new(owner, slot, className, U32(value), Concat(U16(0), U16(0), U32(value)));

    private static Component Float2(uint owner, string className, float x, float y) =>
        new(owner, 0, className, Concat(F32(x), F32(y)), Concat(
            U16(0), U16(0), F32(x), U16(1), F32(y), U16(0xFFFF), U16(0xFFFF)));

    private static Component StringValue(
        uint owner, string className, string value, uint slot = 0) =>
        new(owner, slot, className, Str(value), Concat(U16(0), Str(value), U16(0xFFFF)));

    private static Component Replacement(
        uint owner, uint slot, float r, float g, float b, float a) =>
        new(owner, slot, "BSMaterial::TextureReplacement",
            Concat([1], F32(r), F32(g), F32(b), F32(a)),
            Concat(
                U16(0), [1], U16(1), U16(0),
                U16(0), F32(r), U16(1), F32(g), U16(2), F32(b), U16(3), F32(a),
                U16(0xFFFF), U16(0xFFFF), U16(0xFFFF)));

    private static Component PartialReplacement(uint owner, uint slot, float g, float b, float a) =>
        new(owner, slot, "BSMaterial::TextureReplacement",
            Concat([1], F32(0f), F32(g), F32(b), F32(a)),
            Concat(
                U16(1), U16(0),
                U16(1), F32(g), U16(2), F32(b), U16(3), F32(a),
                U16(0xFFFF), U16(0xFFFF), U16(0xFFFF)));

    private static Component DisabledReplacement(uint owner, uint slot) =>
        new(owner, slot, "BSMaterial::TextureReplacement",
            Concat([0], F32(0f), F32(0f), F32(0f), F32(1f)),
            Concat(U16(0), [0], U16(0xFFFF)));

    private static Component MalformedTexture(uint owner, uint slot) =>
        new(owner, slot, "BSMaterial::MRTextureFile",
            Concat(U16(8), [1]),
            Concat(U16(0), U16(8), [1], U16(0xFFFF)));

    private static Component MalformedReplacement(uint owner, uint slot) =>
        new(owner, slot, "BSMaterial::TextureReplacement",
            [1],
            Concat(U16(1), U16(0), U16(0), F32(0.5f)));

    private static Component EffectSettings(uint owner, StarfieldEffectSettingsFixture effect) =>
        new(owner, 0, "BSMaterial::EffectSettingsComponent",
            Concat(
                [0], [0],
                F32(0f), F32(0f), F32(0f), F32(0f),
                [effect.UsesVertexColor ? (byte)1 : (byte)0],
                [0], F32(0.5f), [0], [0], F32(2f), [0], [0], [0], [0],
                [effect.IsGlass ? (byte)1 : (byte)0],
                [effect.HasFrosting ? (byte)1 : (byte)0],
                F32(0.98f), F32(0f), F32(effect.MaterialOverallAlpha),
                [1], [0], Str(effect.BlendingMode), [0],
                F32(0f), F32(1f), F32(0f),
                F32(1f), F32(1f), F32(1f), F32(1f),
                [0], [0], [0], [0], U16(0)),
            Concat(
                U16(6), [effect.UsesVertexColor ? (byte)1 : (byte)0],
                U16(16), [effect.IsGlass ? (byte)1 : (byte)0],
                U16(17), [effect.HasFrosting ? (byte)1 : (byte)0],
                U16(20), F32(effect.MaterialOverallAlpha),
                U16(23), Str(effect.BlendingMode),
                U16(0xFFFF)));

    private static Component EffectOpacity(uint owner, StarfieldEffectOpacityFixture opacity) =>
        new(owner, 0, "BSMaterial::OpacityComponent",
            Concat(
                Str($"MATERIAL_LAYER_{opacity.SourceLayer}"),
                [opacity.SecondLayerActive ? (byte)1 : (byte)0],
                Str("MATERIAL_LAYER_1"), Str("BLEND_LAYER_0"), Str("Lerp"),
                [opacity.ThirdLayerActive ? (byte)1 : (byte)0],
                Str("MATERIAL_LAYER_2"), Str("BLEND_LAYER_1"), Str("Lerp"),
                F32(1f)),
            Concat(
                U16(0), Str($"MATERIAL_LAYER_{opacity.SourceLayer}"),
                U16(1), [opacity.SecondLayerActive ? (byte)1 : (byte)0],
                U16(5), [opacity.ThirdLayerActive ? (byte)1 : (byte)0],
                U16(0xFFFF)));

    private static byte[] ObjectRecord(
        uint file,
        uint ext,
        uint dir,
        uint dbId,
        uint baseId = 0) =>
        Concat(U32(file), U32(ext), U32(dir), U32(dbId), U32(baseId), [1]);

    private static byte[] ObjectRecordForFixture(
        bool wide,
        uint file,
        uint ext,
        uint dir,
        uint dbId,
        uint baseId = 0,
        (uint Dir, uint File, uint Ext) parent = default,
        bool hasData = true) =>
        wide
            ? Concat(
                U32(file), U32(ext), U32(dir), U32(dbId), U32(baseId),
                U32(parent.File), U32(parent.Ext), U32(parent.Dir),
                [hasData ? (byte)1 : (byte)0])
            : ObjectRecord(file, ext, dir, dbId, baseId);

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

    private readonly record struct Component(
        uint Owner,
        uint Slot,
        string ClassName,
        byte[] Packed,
        byte[] Diff);
}

internal readonly record struct StarfieldEffectSettingsFixture(
    bool IsGlass,
    bool HasFrosting,
    bool UsesVertexColor,
    float MaterialOverallAlpha,
    string BlendingMode,
    string? OpacityTexturePath = null);

internal readonly record struct StarfieldEffectOpacityFixture(
    int SourceLayer,
    bool SecondLayerActive,
    bool ThirdLayerActive);
