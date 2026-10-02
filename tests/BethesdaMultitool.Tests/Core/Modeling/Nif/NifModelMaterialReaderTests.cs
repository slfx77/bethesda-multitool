using System.Numerics;
using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Modeling.Nif;
using BethesdaMultitool.Tests.Helpers;
using Slfx77.Multitool.Core.Models;
using Slfx77.Multitool.Core.Models.Sources;
using Xunit;
using static BethesdaMultitool.Tests.Core.Modeling.Nif.NifMaterialFixtures;
using static BethesdaMultitool.Tests.Core.Modeling.Nif.NifModelTestSupport;

namespace BethesdaMultitool.Tests.Core.Modeling.Nif;

/// <summary>
///     Slice 5 through the real reader: the effective property set (nearest wins, per placement), every property row of
///     plan section 3, the material key, coverage of typed and untyped properties, and native state. Fixtures are
///     hand-laid from nif.xml; every document passes Shared's structure validation. Each test names its control.
/// </summary>
public class NifModelMaterialReaderTests
{
    private const int SrcAlpha = 6;
    private const int InvSrcAlpha = 7;
    private const int One = 0;

    /// <summary>
    ///     The alpha test reference is T/255 as a double with the raw T retained, so 8-bit alpha a8 passes GREATER exactly
    ///     when a8 &gt; T. Control: a T/256 reading admits a8 = T, which the engine rejects.
    /// </summary>
    [Fact]
    public void AlphaTest_ReferenceIsThresholdOver255_WithTheRawThreshold()
    {
        var bytes = Shape(34, [FirstExtraBlock],
            b => AddAlpha(b, NifTestBlockLayouts.AlphaFlags(false, SrcAlpha, InvSrcAlpha, true, 4), 128));
        var document = Read(bytes).Document;
        var test = MaterialOf(document).RenderState!.AlphaTest!;

        Assert.Equal(SceneCompareFunction.Greater, test.Compare);
        Assert.Equal(128 / 255.0, test.Reference);
        Assert.Equal(128UL, test.RawReference);
        Assert.True(test.Enabled);
        Assert.False(test.Evaluate(128 / 255.0));
        Assert.True(test.Evaluate(129 / 255.0));

        var byteQuarter = new SceneAlphaTest(SceneCompareFunction.Greater, 128 / 256.0, 128);
        Assert.NotEqual(byteQuarter.Reference, test.Reference);
        Assert.True(byteQuarter.Evaluate(128 / 255.0));
        SceneValidation.ValidateStructure(document);
    }

    /// <summary>
    ///     Test Func follows TestFunction (0 ALWAYS ... 7 NEVER), not the stencil order (0 NEVER ... 7 ALWAYS), and Alpha
    ///     Test (bit 9) clear disables the retained test.
    /// </summary>
    [Theory]
    [InlineData(0, SceneCompareFunction.Always)]
    [InlineData(3, SceneCompareFunction.LessEqual)]
    [InlineData(6, SceneCompareFunction.GreaterEqual)]
    [InlineData(7, SceneCompareFunction.Never)]
    public void AlphaTestFunction_FollowsTheTestFunctionEnum(int function, SceneCompareFunction expected)
    {
        var bytes = Shape(34, [FirstExtraBlock],
            b => AddAlpha(b, NifTestBlockLayouts.AlphaFlags(false, SrcAlpha, InvSrcAlpha, false, function), 10));

        var test = MaterialOf(Read(bytes).Document).RenderState!.AlphaTest!;

        Assert.Equal(expected, test.Compare);
        Assert.False(test.Enabled);
    }

    /// <summary>
    ///     Emit Mult stays separate from the emissive color, and Shared's summary derives color and strength from both.
    ///     Control: a pre-multiplied reading (the legacy renderer's) stores a different color and a different summary.
    /// </summary>
    [Fact]
    public void EmitMult_IsKeptSeparateFromTheEmissiveColor()
    {
        var bytes = Shape(34, [FirstExtraBlock], b => AddMaterial(b, 2.5f));
        var document = Read(bytes).Document;
        var material = MaterialOf(document);
        var source = material.Source!;

        Assert.Equal(new Vector3(0.2f, 0.4f, 0.6f), source.EmissiveColor);
        Assert.Equal(2.5f, source.EmissiveMultiplier);
        Assert.Equal(new Vector3(0.2f, 0.4f, 0.6f), material.EmissiveFactor);
        Assert.Equal(2.5f, material.EmissiveStrength);

        var premultiplied = new Vector3(0.2f, 0.4f, 0.6f) * 2.5f;
        Assert.NotEqual(premultiplied, source.EmissiveColor);
        var wrong = SceneMaterial.FromSource("control",
            new SceneMaterialSource(Vector4.One, false, SceneLightingModel.BlinnPhong, emissiveColor: premultiplied));
        Assert.NotEqual(material.EmissiveFactor, wrong.EmissiveFactor);
        Assert.NotEqual(material.EmissiveStrength, wrong.EmissiveStrength);
        SceneValidation.ValidateStructure(document);
    }

    /// <summary>
    ///     NiMaterialProperty field presence by BS: below 26 the diffuse color is the base color and ambient is declared;
    ///     from 26 the base color is white; Emit Mult exists above 21 only. A reader gating Emit Mult on BS &gt; 26 would
    ///     leave four bytes unread at BS 26, fail the exact-decode requirement and throw.
    /// </summary>
    [Theory]
    [InlineData(21u)]
    [InlineData(26u)]
    [InlineData(34u)]
    public void MaterialProperty_FieldPresenceFollowsTheBsVersion(uint bs)
    {
        var bytes = Shape(bs, [FirstExtraBlock], b => AddMaterial(b, 3f, 0.5f));
        var document = Read(bytes).Document;
        var source = MaterialOf(document).Source!;

        if (bs < 26)
        {
            Assert.Equal(new Vector4(0.4f, 0.5f, 0.6f, 0.5f), source.BaseColor);
            Assert.Equal(new Vector3(0.1f, 0.2f, 0.3f), source.AmbientColor);
        }
        else
        {
            Assert.Equal(new Vector4(1f, 1f, 1f, 0.5f), source.BaseColor);
            Assert.Null(source.AmbientColor);
        }

        float? multiplier = bs > 21 ? 3f : null;
        Assert.Equal(multiplier, source.EmissiveMultiplier);
        Assert.Equal(new Vector3(0.7f, 0.8f, 0.9f), source.SpecularColor);
        Assert.Equal(12.5f, source.Glossiness);
        Assert.Equal(SceneLightingModel.BlinnPhong, source.LightingModel);
        Assert.False(source.Unlit);
        SceneValidation.ValidateStructure(document);
    }

    /// <summary>
    ///     The nearest property of a slot wins: the shape's own alpha overrides the root's, which stays uninherited.
    ///     Control: without the shape's alpha, the root's is inherited and becomes Typed (the unlisted one is unreachable).
    /// </summary>
    [Fact]
    public void Inheritance_NearestPropertyWins()
    {
        var overridden = Shape(34, [4], b =>
        {
            AddAlpha(b, NifTestBlockLayouts.AlphaFlags(true, SrcAlpha, One), 10);
            AddAlpha(b, NifTestBlockLayouts.AlphaFlags(true, SrcAlpha, InvSrcAlpha), 200);
        }, rootProperties: [3]);
        var inherited = Shape(34, [], b =>
        {
            AddAlpha(b, NifTestBlockLayouts.AlphaFlags(true, SrcAlpha, One), 10);
            AddAlpha(b, NifTestBlockLayouts.AlphaFlags(true, SrcAlpha, InvSrcAlpha), 200);
        }, rootProperties: [3]);

        var near = Read(overridden);
        var far = Read(inherited);

        Assert.Equal(200UL, MaterialOf(near.Document).RenderState!.AlphaTest!.RawReference);
        Assert.Equal(10UL, MaterialOf(far.Document).RenderState!.AlphaTest!.RawReference);
        Assert.Equal(NifModelCoverage.UninheritedPropertyReason, near.Coverage.GetClassification("block:3").Reason);
        Assert.Equal(ModelSourceCoverageKind.Typed, near.Coverage.GetClassification("block:4").Kind);
        Assert.Equal(ModelSourceCoverageKind.Typed, far.Coverage.GetClassification("block:3").Kind);
        Assert.Equal(NifModelCoverage.UnreachableReason, far.Coverage.GetClassification("block:4").Reason);
        SceneValidation.ValidateStructure(near.Document);
        SceneValidation.ValidateStructure(far.Document);
    }

    /// <summary>
    ///     A property on a node gives each placement under it its own key: one shape instanced under a node with an alpha
    ///     and under one without becomes two materials and two meshes. Control: without that alpha both placements share
    ///     one material and one mesh.
    /// </summary>
    [Fact]
    public void NodeProperty_GivesEachPlacementItsOwnMaterial()
    {
        static byte[] Build(bool alphaOnA)
        {
            var builder = new NifTestFileBuilder(false, 34);
            AddNode(builder, builder.AddString("Root"), [1, 2]);
            AddNode(builder, builder.AddString("A"), [3], properties: alphaOnA ? new[] { 6 } : []);
            AddNode(builder, builder.AddString("B"), [3]);
            AddTriShape(builder, builder.AddString("Shape"), 4, properties: [5]);
            AddTriShapeData(builder, Quad(), [0, 1, 2, 1, 3, 2]);
            AddMaterial(builder);
            AddAlpha(builder, NifTestBlockLayouts.AlphaFlags(true, SrcAlpha, InvSrcAlpha), 0);
            return builder.Build();
        }

        var split = Read(Build(true)).Document;
        var shared = Read(Build(false)).Document;

        Assert.Equal(2, split.Materials.Count);
        Assert.Equal(2, split.Meshes.Count);
        var placements = split.Nodes.Where(n => n.Name == "Shape").ToList();
        Assert.Equal(2, placements.Count);
        Assert.NotEqual(placements[0].MeshIndex, placements[1].MeshIndex);
        var underA = split.Meshes[placements[0].MeshIndex!.Value].Primitives[0];
        var underB = split.Meshes[placements[1].MeshIndex!.Value].Primitives[0];
        Assert.NotNull(split.Materials[underA.MaterialIndex!.Value].RenderState?.Blend);
        Assert.Null(split.Materials[underB.MaterialIndex!.Value].RenderState?.Blend);

        Assert.Single(shared.Materials);
        Assert.Single(shared.Meshes);
        Assert.All(shared.Nodes.Where(n => n.Name == "Shape"), n => Assert.Equal(0, n.MeshIndex));
        SceneValidation.ValidateStructure(split);
        SceneValidation.ValidateStructure(shared);
    }

    /// <summary>Two shapes with the same effective properties share one material; control: a second property splits them.</summary>
    [Fact]
    public void IdenticalKeys_ShareOneMaterial()
    {
        static byte[] Build(bool extra)
        {
            var builder = new NifTestFileBuilder(false, 34);
            AddNode(builder, builder.AddString("Root"), [1, 2]);
            AddTriShape(builder, builder.AddString("First"), 3, properties: [4]);
            AddTriShape(builder, builder.AddString("Second"), 3, properties: extra ? new[] { 4, 5 } : new[] { 4 });
            AddTriShapeData(builder, Quad(), [0, 1, 2, 1, 3, 2]);
            AddMaterial(builder);
            AddAlpha(builder, NifTestBlockLayouts.AlphaFlags(true, SrcAlpha, InvSrcAlpha), 0);
            return builder.Build();
        }

        var same = Read(Build(false)).Document;
        var different = Read(Build(true)).Document;

        Assert.Single(same.Materials);
        Assert.Equal(PrimitiveOf(same, "First").MaterialIndex, PrimitiveOf(same, "Second").MaterialIndex);
        Assert.Equal(2, different.Materials.Count);
        Assert.NotEqual(PrimitiveOf(different, "First").MaterialIndex, PrimitiveOf(different, "Second").MaterialIndex);
    }

    /// <summary>
    ///     Stencil draw modes (DRAW_CCW_OR_BOTH read as counterclockwise, Assumed) and the test function decoded by its
    ///     declared width: bit 15 is set in every fixture, so a reader using nif.xml's mask 0xF000 would read 12 (undefined)
    ///     instead of GREATER. Both faces make Shared's summary double-sided; the others do not.
    /// </summary>
    [Theory]
    [InlineData(0, SceneStencilDrawMode.CounterClockwise, false)]
    [InlineData(1, SceneStencilDrawMode.CounterClockwise, false)]
    [InlineData(2, SceneStencilDrawMode.Clockwise, false)]
    [InlineData(3, SceneStencilDrawMode.Both, true)]
    public void Stencil_DrawModesAndTheTestFunction(int drawMode, SceneStencilDrawMode expected, bool doubleSided)
    {
        var flags = (ushort)(NifTestBlockLayouts.StencilFlags(true, 0, 1, 3, drawMode, 4) | 0x8000);
        var bytes = Shape(34, [FirstExtraBlock], b => b.AddBlock("NiStencilProperty",
            w => NifTestBlockLayouts.StencilProperty(w, flags, 7, 0xF0)));
        var document = Read(bytes).Document;
        var material = MaterialOf(document);
        var stencil = material.RenderState!.Stencil!;

        Assert.Equal(expected, stencil.DrawMode);
        var test = stencil.Test!;
        Assert.Equal(SceneCompareFunction.Greater, test.Compare);
        Assert.Equal(7u, test.Reference);
        Assert.Equal(0xF0u, test.ReadMask);
        Assert.Equal(0xF0u, test.WriteMask);
        Assert.Equal(SceneStencilOperation.Keep, test.FailOperation);
        Assert.Equal(SceneStencilOperation.Zero, test.DepthFailOperation);
        Assert.Equal(SceneStencilOperation.IncrementSaturate, test.PassOperation);
        Assert.True(test.Enabled);
        Assert.Equal(doubleSided, material.DoubleSided);
        SceneValidation.ValidateStructure(document);
    }

    /// <summary>
    ///     The stencil test function follows nif.xml's StencilTestFunc (0 NEVER .. 7 ALWAYS), which is the reverse of the
    ///     alpha TestFunction at 0 and 7; the two enums agree on 1-6, so only those two values discriminate. Control:
    ///     GREATER (4) reads the same under either enum.
    /// </summary>
    [Theory]
    [InlineData(0, SceneCompareFunction.Never)]
    [InlineData(7, SceneCompareFunction.Always)]
    [InlineData(4, SceneCompareFunction.Greater)]
    public void Stencil_TestFunctionFollowsStencilTestFunc(int function, SceneCompareFunction expected)
    {
        var flags = NifTestBlockLayouts.StencilFlags(true, 0, 1, 3, 3, function);
        var bytes = Shape(34, [FirstExtraBlock], b => b.AddBlock("NiStencilProperty",
            w => NifTestBlockLayouts.StencilProperty(w, flags, 7, 0xF0)));
        var document = Read(bytes).Document;

        Assert.Equal(expected, MaterialOf(document).RenderState!.Stencil!.Test!.Compare);
        SceneValidation.ValidateStructure(document);
    }

    /// <summary>
    ///     NiZBufferProperty: test, write and a function decoded by width (bits 2-4), unaffected by bit 5 which nif.xml's
    ///     mask 0x003C would fold in. It wins over the shader flags; control: without it the flags supply depth.
    /// </summary>
    [Fact]
    public void ZBuffer_WinsOverShaderFlags()
    {
        const ushort flags = 0x0001 | (3 << 2) | 0x0020; // test, no write, LESS_EQUAL, stray bit 5
        var withProperty = Shape(34, [3, 4], b =>
        {
            b.AddBlock("NiZBufferProperty", w => NifTestBlockLayouts.ZBufferProperty(w, flags));
            b.AddBlock("BSShaderPPLightingProperty", w => NifTestBlockLayouts.PerPixelLightingProperty(w, 34,
                0x8000_0000u, 0x1u, 1f, 3, -1));
        });
        var fromFlags = Shape(34, [3], b => b.AddBlock("BSShaderPPLightingProperty",
            w => NifTestBlockLayouts.PerPixelLightingProperty(w, 34, 0x8000_0000u, 0x1u, 1f, 3, -1)));

        var property = MaterialOf(Read(withProperty).Document).RenderState!.Depth!;
        var shader = MaterialOf(Read(fromFlags).Document).RenderState!.Depth!;

        Assert.True(property.Test);
        Assert.False(property.Write);
        Assert.Equal(SceneCompareFunction.LessEqual, property.Compare);
        Assert.True(shader.Test);
        Assert.True(shader.Write);
        Assert.Equal(SceneCompareFunction.LessEqual, shader.Compare);
    }

    /// <summary>
    ///     Without NiVertexColorProperty, vertex colors modulate ambient and diffuse and vertex alpha is opacity whatever
    ///     SF2 bit 5 and SF1 bit 3 say (Assumed, RE-11; BMT's renderer policy, and a bit set in 0 of 20,309 shaded FNV
    ///     files cannot gate colors); TallGrass alpha is a wind weight; with the property, its Source Vertex Mode and
    ///     Lighting Mode win. Control: flags clear and flags set give the same use, and only TallGrass differs.
    /// </summary>
    [Fact]
    public void VertexColorUse_FromThePropertyOrTheShaderFamily()
    {
        static byte[] Shader(uint sf1, uint sf2)
        {
            return Shape(34, [3], b => b.AddBlock("BSShaderPPLightingProperty",
                w => NifTestBlockLayouts.PerPixelLightingProperty(w, 34, sf1, sf2, 1f, 3, -1)));
        }

        var flagged = MaterialOf(Read(Shader(0x8u, 0x20u)).Document).RenderState!.VertexColorUse!;
        var clear = MaterialOf(Read(Shader(0u, 0u)).Document).RenderState!.VertexColorUse!;
        var grass = MaterialOf(Read(Shape(34, [3], b => b.AddBlock("TallGrassShaderProperty",
            w => NifTestBlockLayouts.TallGrassProperty(w, @"textures\landscape\grass.dds")))).Document)
            .RenderState!.VertexColorUse!;
        var property = MaterialOf(Read(Shape(34, [3], b => b.AddBlock("NiVertexColorProperty",
            w => NifTestBlockLayouts.VertexColorProperty(w, (ushort)((1 << 4) | (0 << 3)))))).Document)
            .RenderState!.VertexColorUse!;

        Assert.Equal(SceneVertexColorSource.AmbientDiffuse, flagged.Source);
        Assert.True(flagged.UseAlphaForOpacity);
        Assert.Equal(flagged.Source, clear.Source);
        Assert.Equal(flagged.Lighting, clear.Lighting);
        Assert.Equal(flagged.UseAlphaForOpacity, clear.UseAlphaForOpacity);
        Assert.Equal(SceneVertexColorSource.AmbientDiffuse, grass.Source);
        Assert.False(grass.UseAlphaForOpacity);
        Assert.Equal(SceneVertexColorSource.Emissive, property.Source);
        Assert.Equal(SceneVertexLightingMode.Emissive, property.Lighting);
        Assert.False(property.UseAlphaForOpacity);
        Assert.Equal(Vector4.One, property.NeutralScale);
    }

    /// <summary>
    ///     BSShaderPPLightingProperty binds texture-set slots 0-5 with their roles through one sampler from Texture Clamp
    ///     Mode (CLAMP_S_WRAP_T: U clamped, V repeated), the environment layer carrying the Env Map Scale, the normal map's
    ///     green declared down (Assumed), and a specular layer on the normal image only when SF1 bit 0 is set.
    ///     Control: SF1 bit 0 clear, no specular layer.
    /// </summary>
    [Fact]
    public void PerPixelLighting_TextureSetSlotsBecomeLayers()
    {
        static byte[] Build(uint sf1)
        {
            return Shape(34, [3], b =>
            {
                b.AddBlock("BSShaderPPLightingProperty",
                    w => NifTestBlockLayouts.PerPixelLightingProperty(w, 34, sf1, 1u, 0.5f, 1, 4));
                b.AddBlock("BSShaderTextureSet", w => NifTestBlockLayouts.TextureSet(w, @"textures\t\a.dds",
                    @"textures\t\a_n.dds", @"textures\t\a_g.dds", "", @"textures\t\cube.dds", "", ""));
            });
        }

        var result = Read(Build(0x1u));
        var document = result.Document;
        var material = MaterialOf(document);
        var roles = material.Layers.Select(l => l.Role).ToArray();

        Assert.Equal([SceneTextureLayerRole.BaseColor, SceneTextureLayerRole.Normal, SceneTextureLayerRole.Glow,
            SceneTextureLayerRole.Environment, SceneTextureLayerRole.Specular], roles);
        Assert.Single(document.Samplers);
        Assert.Equal(new SceneSampler(SceneTextureWrap.ClampToEdge, SceneTextureWrap.Repeat), document.Samplers[0]);
        Assert.Equal(new Vector4(0.5f), material.Layers[3].Constant);
        Assert.Equal(material.Layers[1].Binding, material.Layers[4].Binding);
        Assert.Equal(SceneTextureSwizzle.Identity, material.Layers[4].Swizzle);
        Assert.Equal(SceneNormalGreen.Down, material.NormalGreen!.Green);
        Assert.Equal(SceneValueProvenance.Assumed, material.NormalGreen.Provenance);
        Assert.Equal(@"textures\t\a.dds", ImageOf(document, material.Layers[0]).Name);
        Assert.Equal(ModelSourceCoverageKind.Typed, result.Coverage.GetClassification("block:4").Kind);
        SceneValidation.ValidateStructure(document);

        var control = MaterialOf(Read(Build(0u)).Document);
        Assert.DoesNotContain(control.Layers, l => l.Role == SceneTextureLayerRole.Specular);
    }

    /// <summary>
    ///     BSShaderNoLightingProperty is unlit, binds its File Name, and carries its falloff as a view-angle opacity from
    ///     BS 27 on. Control: at BS 26 the falloff fields do not exist and no ramp is declared.
    /// </summary>
    [Theory]
    [InlineData(26u)]
    [InlineData(34u)]
    public void NoLighting_IsUnlit_WithFalloffAboveBs26(uint bs)
    {
        var bytes = Shape(bs, [3], b => b.AddBlock("BSShaderNoLightingProperty",
            w => NifTestBlockLayouts.NoLightingProperty(w, bs, @"textures\fx\glow.dds", (0.9f, 0.2f, 1f, 0f))));
        var document = Read(bytes).Document;
        var material = MaterialOf(document);

        Assert.True(material.Source!.Unlit);
        Assert.Equal(SceneTextureLayerRole.BaseColor, Assert.Single(material.Layers).Role);
        var ramp = material.RenderState!.ViewAngleOpacity;
        if (bs > 26)
        {
            Assert.NotNull(ramp);
            Assert.Equal(0.9f, (float)ramp.StartCosine);
            Assert.Equal(0.2f, (float)ramp.StopCosine);
            Assert.Equal(1d, ramp.StartOpacity);
            Assert.Equal(0d, ramp.StopOpacity);
            Assert.True(ramp.AbsoluteDot);
            Assert.Equal(SceneValueProvenance.Assumed, ramp.Provenance);
        }
        else
        {
            Assert.Null(ramp);
        }

        SceneValidation.ValidateStructure(document);
    }

    /// <summary>
    ///     Water shading has no typed vocabulary: the shape keeps a material from its other properties, a diagnostic names
    ///     the shader, and the shader block is NativeOnly with the water reason. NiFogProperty is NativeOnly too.
    /// </summary>
    [Fact]
    public void WaterAndFog_AreNativeOnly_TheShapeKeepsAPlaceholderMaterial()
    {
        var bytes = Shape(34, [3, 4, 5], b =>
        {
            b.AddBlock("WaterShaderProperty", NifTestBlockLayouts.WaterShaderProperty);
            b.AddBlock("NiFogProperty", NifTestBlockLayouts.FogProperty);
            AddAlpha(b, NifTestBlockLayouts.AlphaFlags(true, SrcAlpha, InvSrcAlpha), 0);
        });
        var result = Read(bytes);
        var material = MaterialOf(result.Document);

        Assert.Empty(material.Layers);
        Assert.NotNull(material.RenderState!.Blend);
        Assert.Contains(result.Document.Diagnostics, d => d.Code == NifModelMaterialReader.WaterShadingDiagnostic);
        Assert.Equal(NifModelCoverage.WaterReason, result.Coverage.GetClassification("block:3").Reason);
        Assert.Equal(NifModelCoverage.NoVocabularyReason, result.Coverage.GetClassification("block:4").Reason);
        Assert.Equal(ModelSourceCoverageKind.Typed, result.Coverage.GetClassification("block:5").Kind);
        SceneValidation.ValidateStructure(result.Document);
    }

    /// <summary>
    ///     NiTexturingProperty: the apply mode drives the base layer (REPLACE replaces, MODULATE multiplies), the dark map
    ///     modulates, and a Max-method transform with scale 2 and translation (0.5, 0) about the origin composes to offset
    ///     (1, 0) (Assumed formula). The source textures become Typed. Control: no transform leaves the binding without one.
    ///     APPLY_HILIGHT (3) is defined by nif.xml but has no established equation: a product is typed as an assumption
    ///     and reported as unmapped, never as undefined.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(3)]
    public void Texturing_ApplyModeMapsAndTransform(int applyMode)
    {
        var bytes = Shape(34, [3], b =>
        {
            b.AddBlock("NiTexturingProperty", w => NifTestBlockLayouts.TexturingProperty(w, applyMode,
                m => NifTestBlockLayouts.TexDesc(m, 4, clamp: 0, filter: 1),
                m => NifTestBlockLayouts.TexDesc(m, 5, transform: (0.5f, 0f, 2f, 2f, 0f, 1u, 0f, 0f))));
            b.AddBlock("NiSourceTexture", w => NifTestBlockLayouts.SourceTexture(w, b.AddString(@"textures\fx\a.dds")));
            b.AddBlock("NiSourceTexture", w => NifTestBlockLayouts.SourceTexture(w, b.AddString(@"textures\fx\d.dds")));
        });
        var result = Read(bytes);
        var document = result.Document;
        var material = MaterialOf(document);

        Assert.Equal(2, material.Layers.Count);
        var baseLayer = material.Layers[0];
        Assert.Equal(SceneTextureLayerRole.BaseColor, baseLayer.Role);
        Assert.Equal(applyMode == 0 ? NifModelLayerAlgebra.Replacement : NifModelLayerAlgebra.Product, baseLayer.ColorOp);
        Assert.Null(baseLayer.Binding.Transform);
        Assert.Equal(new SceneSampler(SceneTextureWrap.ClampToEdge, SceneTextureWrap.ClampToEdge,
            SceneTextureFilter.Linear, SceneTextureFilter.Linear), document.Samplers[baseLayer.Binding.SamplerIndex]);
        var dark = material.Layers[1];
        Assert.Equal(SceneTextureLayerRole.Dark, dark.Role);
        Assert.Equal(NifModelLayerAlgebra.Product, dark.ColorOp);
        var transform = dark.Binding.Transform!.Value;
        Assert.Equal(new Vector2(1f, 0f), transform.Offset);
        Assert.Equal(new Vector2(2f, 2f), transform.Scale);
        Assert.Equal(0f, transform.Rotation);
        Assert.Equal(ModelSourceCoverageKind.Typed, result.Coverage.GetClassification("block:4").Kind);
        Assert.Equal(ModelSourceCoverageKind.Typed, result.Coverage.GetClassification("block:5").Kind);
        Assert.Equal(applyMode == 3,
            document.Diagnostics.Any(d => d.Code == NifModelMaterialReader.UnmappedValueDiagnostic));
        Assert.DoesNotContain(document.Diagnostics, d => d.Code == NifModelMaterialReader.UndefinedValueDiagnostic);
        SceneValidation.ValidateStructure(document);
    }

    /// <summary>
    ///     A geometry that stores no texture coordinates gets no texture layers: each set-0 layer is left out and reported,
    ///     and its texture is never resolved, rather than sampling one texel over the whole surface. Control: the same
    ///     shape with texture coordinates binds the texture-set layers.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GeometryWithoutUvSets_LeavesTextureLayersOut(bool withUvs)
    {
        var bytes = Shape(34, [3], b =>
        {
            b.AddBlock("BSShaderPPLightingProperty",
                w => NifTestBlockLayouts.PerPixelLightingProperty(w, 34, 0u, 1u, 0.5f, 1, 4));
            b.AddBlock("BSShaderTextureSet", w => NifTestBlockLayouts.TextureSet(w, @"textures\t\a.dds",
                @"textures\t\a_n.dds", "", "", "", "", ""));
        }, streams: withUvs ? Quad() : QuadWithoutUvs());
        var document = Read(bytes).Document;
        var material = MaterialOf(document);

        Assert.Equal(withUvs ? 2 : 0, material.Layers.Count);
        Assert.Equal(withUvs ? 2 : 0, document.Images.Count);
        Assert.Equal(!withUvs, document.Diagnostics.Any(d => d.Code == NifModelMaterialReader.UvSetDiagnostic));
        SceneValidation.ValidateStructure(document);
    }

    /// <summary>
    ///     A map on a texture-coordinate set the geometry does not store (BS 34 stores at most set 0) is left out and
    ///     reported, so Shared never sees an absent set. Control: the same map on set 0 is bound.
    /// </summary>
    [Theory]
    [InlineData(1, false)]
    [InlineData(0, true)]
    public void Texturing_AbsentUvSet_LeavesTheLayerOut(int uvSet, bool bound)
    {
        var bytes = Shape(34, [3], b =>
        {
            b.AddBlock("NiTexturingProperty", w => NifTestBlockLayouts.TexturingProperty(w, 2,
                m => NifTestBlockLayouts.TexDesc(m, 4, uvSet)));
            b.AddBlock("NiSourceTexture", w => NifTestBlockLayouts.SourceTexture(w, b.AddString(@"textures\a.dds")));
        });
        var document = Read(bytes).Document;
        var material = MaterialOf(document);

        Assert.Equal(bound ? 1 : 0, material.Layers.Count);
        Assert.Equal(!bound, document.Diagnostics.Any(d => d.Code == NifModelMaterialReader.UvSetDiagnostic));
        SceneValidation.ValidateStructure(document);
    }

    /// <summary>
    ///     An NiSourceTexture that embeds its pixels is a missing image (NiPixelData is later-cut 2) and stays NativeOnly;
    ///     control: the same block marked external is Typed.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmbeddedSourceTexture_IsAMissingImage(bool external)
    {
        var bytes = Shape(34, [3], b =>
        {
            b.AddBlock("NiTexturingProperty", w => NifTestBlockLayouts.TexturingProperty(w, 2,
                m => NifTestBlockLayouts.TexDesc(m, 4)));
            b.AddBlock("NiSourceTexture",
                w => NifTestBlockLayouts.SourceTexture(w, b.AddString(@"textures\a.dds"), external));
        });
        var result = Read(bytes);
        var image = Assert.Single(result.Document.Images);

        Assert.Null(image.Source!.Original);
        if (external)
        {
            Assert.Equal(ModelSourceCoverageKind.Typed, result.Coverage.GetClassification("block:4").Kind);
        }
        else
        {
            Assert.Equal("NiPixelData", image.Source.Container);
            Assert.Equal(NifModelCoverage.PixelDataReason, result.Coverage.GetClassification("block:4").Reason);
        }

        SceneValidation.ValidateStructure(result.Document);
    }

    /// <summary>Two same-slot properties on one object: the first is used and the duplicate reported (Assumed).</summary>
    [Fact]
    public void DuplicateSlotOnOneObject_FirstWins_AndIsReported()
    {
        var bytes = Shape(34, [3, 4], b =>
        {
            AddAlpha(b, NifTestBlockLayouts.AlphaFlags(false, SrcAlpha, InvSrcAlpha, true), 20);
            AddAlpha(b, NifTestBlockLayouts.AlphaFlags(false, SrcAlpha, InvSrcAlpha, true), 90);
        });
        var result = Read(bytes);

        Assert.Equal(20UL, MaterialOf(result.Document).RenderState!.AlphaTest!.RawReference);
        Assert.Contains(result.Document.Diagnostics,
            d => d.Code == NifModelMaterialReader.DuplicatePropertyDiagnostic);
        Assert.Equal(NifModelCoverage.UninheritedPropertyReason, result.Coverage.GetClassification("block:4").Reason);
    }

    /// <summary>
    ///     Native state: one bmt.nif.material row per material, targeting it, listing each effective property with its
    ///     owner and the assumptions; the property block's own row now targets the material it fed.
    /// </summary>
    [Fact]
    public void NativeState_HasOneMaterialRow_AndPropertyRowsTargetTheirMaterial()
    {
        var bytes = Shape(34, [4], b =>
        {
            AddMaterial(b);
            AddAlpha(b, NifTestBlockLayouts.AlphaFlags(true, SrcAlpha, InvSrcAlpha), 0);
        }, rootProperties: [3]);
        var document = Read(bytes).Document;

        var row = Assert.Single(Rows(document, NifModelMaterialReader.MaterialKind));
        Assert.Equal(new SceneElementRef(SceneElementKind.Material, 0), row.Target);
        var payload = JsonNode.Parse(row.PayloadJson)!.AsObject();
        var properties = payload["properties"]!.AsArray().Select(p => p!.AsObject()).ToList();
        Assert.Contains(properties, p => (string)p["slot"]! == "material" && (int)p["owner"]! == 0);
        Assert.Contains(properties, p => (string)p["slot"]! == "alpha" && (int)p["owner"]! == 1);
        Assert.NotEmpty(payload["assumptions"]!.AsArray());
        var materialBlock = Rows(document, NifModelNativeState.BlockKind)
            .Single(r => r.SourceLocation?.ElementIdentity == "block:3");
        Assert.Equal(new SceneElementRef(SceneElementKind.Material, 0), materialBlock.Target);
        SceneValidation.ValidateStructure(document);
    }

    /// <summary>A shape with no effective property has no material; control: one property gives it one.</summary>
    [Fact]
    public void NoEffectiveProperty_NoMaterial()
    {
        var bare = Read(Shape(34, [], _ => { })).Document;
        var alpha = Read(Shape(34, [3], b => AddAlpha(b, 0, 0))).Document;

        Assert.Null(PrimitiveOf(bare, "Shape").MaterialIndex);
        Assert.Empty(bare.Materials);
        Assert.Equal(0, PrimitiveOf(alpha, "Shape").MaterialIndex);
    }
}
