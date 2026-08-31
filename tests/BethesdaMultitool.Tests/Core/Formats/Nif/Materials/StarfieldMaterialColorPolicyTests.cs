using System.Numerics;
using System.Text;
using BethesdaMultitool.Core.Formats.Bsa.Ba2;
using BethesdaMultitool.Core.Formats.Nif.Materials;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Materials;

/// <summary>
///     Pins the CE2 material components that decide whether Starfield mesh vertex colour is visual
///     tint data. These are intentionally tested separately from texture-slot resolution: a
///     <c>ColorChannelTypeComponent</c> belongs to a layer blender, while base-surface tint is enabled
///     by <c>ParamBool</c> slot 0 on the layer's material object.
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
public sealed class StarfieldMaterialColorPolicyTests
{
    private const string MaterialPath = @"materials\test\color.mat";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResolveBaseColorPolicy_DecodesObjtAndDiffComponents(bool useDiffChunks)
    {
        var db = StarfieldMaterialDatabase.Parse(BuildDatabase(useDiffChunks));

        Assert.NotNull(db);
        Assert.Equal(db!.ComponentTableCount, db.ComponentChunkCount);
        var policy = db.ResolveBaseColorPolicy(MaterialPath);
        Assert.True(policy.IsResolved);
        Assert.True(policy.UsesVertexColorAsTint);
        Assert.Equal(StarfieldMaterialColorOverrideMode.Multiply, policy.OverrideMode);
        Assert.Equal(new Vector4(0.25f, 0.5f, 0.75f, 1f), policy.Color);
        Assert.Equal(0xFFBF8040u, policy.ColorRgba);
        Assert.Equal(
            StarfieldMaterialColorChannel.Blue,
            Assert.IsType<StarfieldMaterialColorChannel>(db.ResolveBlenderColorChannel(MaterialPath)));
    }

    /// <summary>
    ///     ParamBool slot 0 is overloaded by owner type. On the file-backed root it means TwoSided;
    ///     only the layer MaterialID target uses it as "vertex colour is tint". Reading the root flag
    ///     would therefore tint every two-sided material.
    /// </summary>
    [Fact]
    public void ResolveBaseColorPolicy_DoesNotTreatRootTwoSidedAsVertexTint()
    {
        var db = StarfieldMaterialDatabase.Parse(BuildDatabase(
            useDiffChunks: true,
            includeLayerColorComponents: false,
            rootParamBool: true));

        Assert.NotNull(db);
        var policy = db!.ResolveBaseColorPolicy(MaterialPath);
        Assert.True(policy.IsResolved);
        Assert.False(policy.UsesVertexColorAsTint);
        Assert.Equal(StarfieldMaterialColorOverrideMode.Lerp, policy.OverrideMode);
        Assert.Equal(0x00FFFFFFu, policy.ColorRgba);

        // A blender's channel selector is also independent of base-surface tint enablement.
        Assert.Equal(
            StarfieldMaterialColorChannel.Blue,
            Assert.IsType<StarfieldMaterialColorChannel>(db.ResolveBlenderColorChannel(MaterialPath)));
        Assert.True(db.ResolveRootTwoSided(MaterialPath) == true);
    }

    [Fact]
    public void ResolveRootTwoSided_PreservesOrderedParamBoolAndShaderModelSetters()
    {
        var shaderOnly = StarfieldMaterialDatabase.Parse(BuildDatabase(
            useDiffChunks: true,
            rootShaderModel: "TwoSided1Layer"));
        var shaderThenClear = StarfieldMaterialDatabase.Parse(BuildDatabase(
            useDiffChunks: true,
            rootParamBool: false,
            rootShaderModel: "TwoSided1Layer"));
        var clearThenShader = StarfieldMaterialDatabase.Parse(BuildDatabase(
            useDiffChunks: true,
            rootParamBool: false,
            rootShaderModel: "TwoSided1Layer",
            rootParamBoolBeforeShaderModel: true));
        var layerTintOnly = StarfieldMaterialDatabase.Parse(BuildDatabase(
            useDiffChunks: true,
            includeLayerColorComponents: true));
        var inheritedLateShaderSetter = StarfieldMaterialDatabase.Parse(BuildDatabase(
            useDiffChunks: true,
            rootParamBool: true,
            baseRootParamBool: true,
            baseRootShaderModel: "BaseMaterial",
            baseRootParamBoolBeforeShaderModel: true));

        Assert.NotNull(shaderOnly);
        Assert.NotNull(shaderThenClear);
        Assert.NotNull(clearThenShader);
        Assert.NotNull(layerTintOnly);
        Assert.NotNull(inheritedLateShaderSetter);
        Assert.True(shaderOnly!.ResolveRootTwoSided(MaterialPath) == true);
        Assert.True(shaderThenClear!.ResolveRootTwoSided(MaterialPath) == false);
        Assert.True(clearThenShader!.ResolveRootTwoSided(MaterialPath) == true);
        Assert.True(layerTintOnly!.ResolveRootTwoSided(MaterialPath) == false);
        // copyBaseObject replaces the derived ParamBool at its inherited position without moving
        // the later inherited ShaderModel setter; nearest-local-value resolution would return true.
        Assert.True(inheritedLateShaderSetter!.ResolveRootTwoSided(MaterialPath) == false);
        Assert.Null(shaderOnly.ResolveRootTwoSided(@"materials\test\missing.mat"));
    }

    [Fact]
    public void ResolveBaseColorPolicy_InheritsLayerMaterialComponents()
    {
        var db = StarfieldMaterialDatabase.Parse(BuildDatabase(
            useDiffChunks: true,
            policyOnBaseMaterial: true));

        Assert.NotNull(db);
        var policy = db!.ResolveBaseColorPolicy(MaterialPath);
        Assert.True(policy.IsResolved);
        Assert.True(policy.UsesVertexColorAsTint);
        Assert.Equal(StarfieldMaterialColorOverrideMode.Multiply, policy.OverrideMode);
        Assert.Equal(0xFFBF8040u, policy.ColorRgba);
    }

    [Fact]
    public void TryResolveConstantLerp_ExpandsRgbAndPreservesLinearWeight()
    {
        var policy = new StarfieldMaterialColorPolicy(
            true,
            false,
            StarfieldMaterialColorOverrideMode.Lerp,
            new Vector4(0.25f, 0.5f, 0.75f, 0.4f));

        Assert.True(policy.TryResolveConstantLerp(out var linearTint));
        Assert.Equal(0.05140095f, linearTint.X, 6);
        Assert.Equal(0.21378447f, linearTint.Y, 6);
        Assert.Equal(0.52255344f, linearTint.Z, 6);
        Assert.Equal(0.4f, linearTint.W);

        var state = policy.ResolveRenderState();
        Assert.Equal(StarfieldMaterialColorRenderMode.ConstantLerp, state.Mode);
        Assert.Equal(linearTint, state.LinearTint);
    }

    [Fact]
    public void TryResolveConstantLerp_LeavesVertexLerpAndInvalidPoliciesFailClosed()
    {
        StarfieldMaterialColorPolicy[] unsupported =
        [
            new(true, true, StarfieldMaterialColorOverrideMode.Lerp, new Vector4(0.2f)),
            new(false, false, StarfieldMaterialColorOverrideMode.Lerp, new Vector4(0.2f)),
            new(true, false, StarfieldMaterialColorOverrideMode.Multiply, new Vector4(0.2f)),
            new(true, false, StarfieldMaterialColorOverrideMode.Lerp,
                new Vector4(float.NaN, 0.2f, 0.3f, 0.4f)),
            // CE2's inherited default white/0 Lerp is an exact no-op and should not consume the
            // persistent state or a shader union lane.
            new(true, false, StarfieldMaterialColorOverrideMode.Lerp,
                new Vector4(1f, 1f, 1f, 0f)),
            new(true, false, StarfieldMaterialColorOverrideMode.Lerp,
                new Vector4(0.1f, 0.2f, 0.3f, 1.01f))
        ];

        foreach (var policy in unsupported)
        {
            Assert.False(policy.TryResolveConstantLerp(out var linearTint));
            Assert.Equal(Vector4.Zero, linearTint);
            Assert.Equal(default(StarfieldMaterialColorRenderState), policy.ResolveRenderState());
        }
    }

    [Fact]
    public void ResolveBaseColorPolicy_ReportsAnUnresolvedPath()
    {
        var db = StarfieldMaterialDatabase.Parse(BuildDatabase(useDiffChunks: false));

        Assert.NotNull(db);
        Assert.False(db!.ResolveBaseColorPolicy(@"materials\test\missing.mat").IsResolved);
        Assert.Null(db.ResolveBlenderColorChannel(@"materials\test\missing.mat"));
    }

    [Fact]
    public void NifTextureResolver_ExposesEffectiveBaseColorPolicy()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"BethesdaMultitool-StarfieldColorPolicy-{Guid.NewGuid():N}");
        var materials = Path.Combine(root, "materials");
        Directory.CreateDirectory(materials);
        try
        {
            File.WriteAllBytes(Path.Combine(materials, "materialsbeta.cdb"), BuildDatabase(
                useDiffChunks: true,
                rootParamBool: true));
            using var resolver = new NifTextureResolver(root);

            var policy = resolver.ResolveStarfieldBaseColorPolicy(MaterialPath);

            Assert.True(policy.IsResolved);
            Assert.True(policy.UsesVertexColorAsTint);
            Assert.Equal(StarfieldMaterialColorOverrideMode.Multiply, policy.OverrideMode);
            Assert.Equal(0xFFBF8040u, policy.ColorRgba);
            Assert.True(resolver.ResolveStarfieldRootTwoSided(MaterialPath) == true);
            Assert.False(resolver.ResolveStarfieldBaseColorPolicy(@"textures\not-a-material.dds").IsResolved);
            Assert.Null(resolver.ResolveStarfieldRootTwoSided(@"textures\not-a-material.dds"));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void SharedGeometryExtractor_OrsOnlyResolvedPositiveRootTwoSidedState()
    {
        var source = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering",
            "NifGeometryExtractor.cs");
        var block = SourceContract.Extract(
            source,
            "if (materialPath is not null && textureResolver is not null &&",
            "if (materialPath is not null && textureResolver?.TryGetMaterial(materialPath) is { } bgsm)");

        SourceContract.AssertOrder(
            block,
            "ResolveStarfieldBaseColorPolicy(materialPath)",
            "ResolveStarfieldAlphaPolicy(materialPath)",
            "isDoubleSided |= textureResolver.ResolveStarfieldRootTwoSided(materialPath) == true;");
    }

    [Fact]
    public void NifTextureResolver_CdbCacheIdentityTracksEveryOrderedCandidateAndLooseFileMetadata()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"BethesdaMultitool-StarfieldCdbIdentity-{Guid.NewGuid():N}");
        var firstRoot = Path.Combine(root, "first");
        var secondRoot = Path.Combine(root, "second");
        var firstMaterials = Path.Combine(firstRoot, "materials");
        var secondMaterials = Path.Combine(secondRoot, "materials");
        Directory.CreateDirectory(firstMaterials);
        Directory.CreateDirectory(secondMaterials);
        var firstCdb = Path.Combine(firstMaterials, "materialsbeta.cdb");
        var secondCdb = Path.Combine(secondMaterials, "materialsbeta.cdb");
        File.WriteAllBytes(firstCdb, new byte[7]);
        File.WriteAllBytes(secondCdb, new byte[11]);

        try
        {
            string firstIdentity;
            using (var resolver = new NifTextureResolver(firstRoot, secondRoot))
            {
                firstIdentity = Assert.IsType<string>(resolver.StarfieldMaterialDatabaseCacheIdentity);
                Assert.Contains("sourceIndex=0", firstIdentity, StringComparison.Ordinal);
                Assert.Contains("sourceIndex=1", firstIdentity, StringComparison.Ordinal);
                Assert.Contains(Path.GetFullPath(firstCdb), firstIdentity, StringComparison.OrdinalIgnoreCase);
                Assert.Contains(Path.GetFullPath(secondCdb), firstIdentity, StringComparison.OrdinalIgnoreCase);
            }

            using (var resolver = new NifTextureResolver(secondRoot, firstRoot))
            {
                var reversedIdentity = Assert.IsType<string>(resolver.StarfieldMaterialDatabaseCacheIdentity);
                Assert.Contains(Path.GetFullPath(secondCdb), reversedIdentity, StringComparison.OrdinalIgnoreCase);
                Assert.Contains(Path.GetFullPath(firstCdb), reversedIdentity, StringComparison.OrdinalIgnoreCase);
                Assert.NotEqual(firstIdentity, reversedIdentity);
            }

            File.WriteAllBytes(firstCdb, new byte[8]);
            string changedFirstIdentity;
            using (var resolver = new NifTextureResolver(firstRoot, secondRoot))
            {
                changedFirstIdentity = Assert.IsType<string>(resolver.StarfieldMaterialDatabaseCacheIdentity);
                Assert.NotEqual(firstIdentity, changedFirstIdentity);
            }

            File.WriteAllBytes(secondCdb, new byte[12]);
            using (var resolver = new NifTextureResolver(firstRoot, secondRoot))
            {
                Assert.NotEqual(changedFirstIdentity, resolver.StarfieldMaterialDatabaseCacheIdentity);
            }

            File.Delete(firstCdb);
            using (var resolver = new NifTextureResolver(firstRoot, secondRoot))
            {
                var secondOnlyIdentity = Assert.IsType<string>(resolver.StarfieldMaterialDatabaseCacheIdentity);
                Assert.Contains(Path.GetFullPath(secondCdb), secondOnlyIdentity, StringComparison.OrdinalIgnoreCase);
            }

            File.Delete(secondCdb);
            using var noCdbResolver = new NifTextureResolver(firstRoot, secondRoot);
            Assert.Null(noCdbResolver.StarfieldMaterialDatabaseCacheIdentity);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>
    ///     Optional retail gate. It proves the simple component decoders walk the installed 1.44M-entry
    ///     component stream rather than merely accepting our synthetic encoding. Counts are bounded,
    ///     not pinned to one patch's database, so a normal Starfield update does not create fixture
    ///     churn.
    /// </summary>
    [Fact]
    [Trait("Category", BucketBTestGuard.Category)]
    public void RetailDatabase_DecodesVertexTintAndBlenderChannelComponents()
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

        var db = StarfieldMaterialDatabase.Parse(extractor.ExtractFile(entry!));
        Assert.NotNull(db);
        Assert.Equal(db!.ComponentTableCount, db.ComponentChunkCount);
        Assert.InRange(db.MaterialVertexColorPolicyObjectCount, 1, 10_000);
        Assert.InRange(db.BlenderColorChannelObjectCount, 1, 10_000);
    }

    private static byte[] BuildDatabase(
        bool useDiffChunks,
        bool includeLayerColorComponents = true,
        bool? rootParamBool = null,
        bool policyOnBaseMaterial = false,
        string? rootShaderModel = null,
        bool rootParamBoolBeforeShaderModel = false,
        bool? baseRootParamBool = null,
        string? baseRootShaderModel = null,
        bool baseRootParamBoolBeforeShaderModel = false)
    {
        var strings = new[]
        {
            "BSComponentDB2::DBFileIndex::ObjectInfo",
            "BSComponentDB2::DBFileIndex::ComponentInfo",
            "BSMaterial::Internal::CompiledDB",
            "BSComponentDB2::DBFileIndex",
            "BSMaterial::LayerID",
            "BSMaterial::BlenderID",
            "BSMaterial::MaterialID",
            "BSMaterial::Color",
            "BSMaterial::MaterialOverrideColorTypeComponent",
            "BSMaterial::ParamBool",
            "BSMaterial::ShaderModelComponent",
            "BSMaterial::ColorChannelTypeComponent"
        };

        var offsets = new Dictionary<string, uint>();
        var strt = new List<byte>();
        foreach (var value in strings)
        {
            offsets[value] = (uint)strt.Count;
            strt.AddRange(Encoding.ASCII.GetBytes(value));
            strt.Add(0);
        }

        const uint rootId = 1;
        const uint layerId = 2;
        const uint materialId = 3;
        const uint blenderId = 4;
        const uint baseMaterialId = 5;
        const uint layeredMaterialsRootId = 6;
        const uint layersRootId = 7;
        const uint materialsRootId = 8;
        const uint blendersRootId = 9;
        const uint baseRootId = 10;
        var hasBaseRoot = baseRootParamBool is not null || baseRootShaderModel is not null;
        var resource = StarfieldMaterialDatabase.ComputeResourceId(MaterialPath);
        var layeredMaterialsRoot = StarfieldMaterialDatabase.ComputeResourceId(
            @"materials\layered\root\layeredmaterials.mat");
        var layersRoot = StarfieldMaterialDatabase.ComputeResourceId(
            @"materials\layered\root\layers.mat");
        var materialsRoot = StarfieldMaterialDatabase.ComputeResourceId(
            @"materials\layered\root\materials.mat");
        var blendersRoot = StarfieldMaterialDatabase.ComputeResourceId(
            @"materials\layered\root\blenders.mat");

        var chunks = new List<byte[]>();
        chunks.Add(Chunk("CLAS", Concat(
            U32(offsets["BSComponentDB2::DBFileIndex::ObjectInfo"]), U32(1), U16(0), U16(4))));

        var objects = new List<byte>();
        objects.AddRange(U32(offsets["BSComponentDB2::DBFileIndex::ObjectInfo"]));
        objects.AddRange(U32((policyOnBaseMaterial ? 9u : 8u) + (hasBaseRoot ? 1u : 0u)));
        objects.AddRange(ObjectRecord(
            resource.File,
            resource.Ext,
            resource.Dir,
            rootId,
            hasBaseRoot ? baseRootId : layeredMaterialsRootId));
        objects.AddRange(ObjectRecord(0, 0, 0, layerId, layersRootId));
        objects.AddRange(ObjectRecord(
            0,
            0,
            0,
            materialId,
            policyOnBaseMaterial ? baseMaterialId : materialsRootId));
        objects.AddRange(ObjectRecord(0, 0, 0, blenderId, blendersRootId));
        if (policyOnBaseMaterial)
        {
            objects.AddRange(ObjectRecord(0, 0, 0, baseMaterialId, materialsRootId));
        }
        if (hasBaseRoot)
        {
            objects.AddRange(ObjectRecord(0, 0, 0, baseRootId, layeredMaterialsRootId));
        }
        objects.AddRange(ObjectRecord(
            layeredMaterialsRoot.File,
            layeredMaterialsRoot.Ext,
            layeredMaterialsRoot.Dir,
            layeredMaterialsRootId));
        objects.AddRange(ObjectRecord(layersRoot.File, layersRoot.Ext, layersRoot.Dir, layersRootId));
        objects.AddRange(ObjectRecord(materialsRoot.File, materialsRoot.Ext, materialsRoot.Dir, materialsRootId));
        objects.AddRange(ObjectRecord(blendersRoot.File, blendersRoot.Ext, blendersRoot.Dir, blendersRootId));

        chunks.Add(Chunk("LIST", [.. objects]));

        // Retail carries two top-level OBJT records before ComponentInfo. They describe the
        // compiled database and its file index, not material components, so neither may advance
        // the positional component cursor or inflate ComponentChunkCount.
        chunks.Add(Chunk("OBJT", Concat(
            U32(offsets["BSMaterial::Internal::CompiledDB"]), Str("1.16.244.0"))));
        chunks.Add(Chunk("OBJT", Concat(
            U32(offsets["BSComponentDB2::DBFileIndex"]), [0])));

        var components = new List<(uint Owner, uint Slot, string ClassName, byte[] Packed, byte[] Diff)>
        {
            (rootId, 0, "BSMaterial::LayerID", U32(layerId), Concat(U16(0), U16(0), U32(layerId))),
            (rootId, 0, "BSMaterial::BlenderID", U32(blenderId), Concat(U16(0), U16(0), U32(blenderId))),
            (layerId, 0, "BSMaterial::MaterialID", U32(materialId), Concat(U16(0), U16(0), U32(materialId)))
        };

        void AddParamBool(uint owner, bool? setting)
        {
            if (setting is not { } value)
            {
                return;
            }

            var encoded = value ? (byte)1 : (byte)0;
            components.Add((owner, 0, "BSMaterial::ParamBool", [encoded], Concat(U16(0), [encoded])));
        }

        void AddShaderModel(uint owner, string? shaderModel)
        {
            if (shaderModel is null)
            {
                return;
            }

            components.Add((
                owner,
                0,
                "BSMaterial::ShaderModelComponent",
                Str(shaderModel),
                Concat(U16(0), Str(shaderModel))));
        }

        void AddTwoSidedSetters(uint owner, bool? paramBool, string? shaderModel, bool paramBoolFirst)
        {
            if (paramBoolFirst)
            {
                AddParamBool(owner, paramBool);
                AddShaderModel(owner, shaderModel);
            }
            else
            {
                AddShaderModel(owner, shaderModel);
                AddParamBool(owner, paramBool);
            }
        }

        if (hasBaseRoot)
        {
            AddTwoSidedSetters(
                baseRootId,
                baseRootParamBool,
                baseRootShaderModel,
                baseRootParamBoolBeforeShaderModel);
        }

        AddTwoSidedSetters(rootId, rootParamBool, rootShaderModel, rootParamBoolBeforeShaderModel);

        if (includeLayerColorComponents)
        {
            var policyOwner = policyOnBaseMaterial ? baseMaterialId : materialId;
            components.Add((policyOwner, 0, "BSMaterial::Color",
                Concat(F32(0.25f), F32(0.5f), F32(0.75f), F32(1f)),
                Concat(
                    U16(0),
                    U16(0), F32(0.25f),
                    U16(1), F32(0.5f),
                    U16(2), F32(0.75f),
                    U16(3), F32(1f),
                    U16(0xFFFF), U16(0xFFFF))));
            components.Add((policyOwner, 0, "BSMaterial::MaterialOverrideColorTypeComponent",
                Str("Multiply"), Concat(U16(0), Str("Multiply"))));
            components.Add((policyOwner, 0, "BSMaterial::ParamBool", [1], Concat(U16(0), [1])));
        }

        components.Add((blenderId, 0, "BSMaterial::ColorChannelTypeComponent",
            Str("Blue"), Concat(U16(0), Str("Blue"))));

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
        file.AddRange(U32((uint)strt.Count));
        file.AddRange(strt);
        foreach (var chunk in chunks)
        {
            file.AddRange(chunk);
        }

        return [.. file];
    }

    private static byte[] ObjectRecord(uint file, uint ext, uint dir, uint dbId, uint baseId = 0)
    {
        return Concat(U32(file), U32(ext), U32(dir), U32(dbId), U32(baseId), [1]);
    }

    private static byte[] Chunk(string tag, byte[] body)
    {
        return Concat(Encoding.ASCII.GetBytes(tag), U32((uint)body.Length), body);
    }

    private static byte[] Str(string value)
    {
        return Concat(U16((ushort)value.Length), Encoding.ASCII.GetBytes(value));
    }

    private static byte[] F32(float value)
    {
        return BitConverter.GetBytes(value);
    }

    private static byte[] U32(uint value)
    {
        return BitConverter.GetBytes(value);
    }

    private static byte[] U16(ushort value)
    {
        return BitConverter.GetBytes(value);
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var result = new List<byte>();
        foreach (var part in parts)
        {
            result.AddRange(part);
        }

        return [.. result];
    }
}
