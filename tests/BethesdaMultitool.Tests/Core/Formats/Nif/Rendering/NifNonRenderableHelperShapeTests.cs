using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

/// <summary>
///     Covers <see cref="NifBlockParsers.IsNonRenderableHelperShape" />: a BSShaderProperty-era
///     (FO3/FNV+, BSVersion ≥ 34) geometry shape with NO texture-source property — neither a
///     <c>*ShaderProperty</c> nor a legacy <c>NiTexturingProperty</c> — is a non-visual helper the game
///     never draws (furniture-marker / boundary / collision-viz placeholders, or shader-less leftovers),
///     so the render extractor drops it instead of baking an untextured white blob. Older NIFs are left
///     untouched. The FO3/FNV shader names (<c>BSShaderPPLightingProperty</c>) interleave the word, so the
///     match must be "Shader" + "Property" suffix, NOT a "ShaderProperty" substring.
/// </summary>
public sealed class NifNonRenderableHelperShapeTests
{
    private static NifInfo NifWith(uint bsVersion, params string[] propertyBlockTypes)
    {
        var nif = new NifInfo { BsVersion = bsVersion };
        // Block 0 reserved as the shape itself; property blocks follow at indices 1..N.
        nif.Blocks.Add(new BlockInfo { TypeName = "NiTriStrips" });
        foreach (var t in propertyBlockTypes)
        {
            nif.Blocks.Add(new BlockInfo { TypeName = t });
        }

        return nif;
    }

    private static IReadOnlyList<int> Refs(int count) => Enumerable.Range(1, count).ToList();

    [Fact]
    public void ZeroProperties_OnBsShaderEra_IsHelper()
    {
        // LoungeChair_Tops MarkerSource / ChairBoundary strips: zero properties → no shader path at all.
        Assert.True(NifBlockParsers.IsNonRenderableHelperShape(NifWith(34), null));
        Assert.True(NifBlockParsers.IsNonRenderableHelperShape(NifWith(34), Refs(0)));
    }

    [Fact]
    public void OnlyMaterialProperty_OnBsShaderEra_IsHelper()
    {
        // NV_McCarran-WallRubble:2 — only a NiMaterialProperty, no shader / texture set → renders white.
        var nif = NifWith(34, "NiMaterialProperty");
        Assert.True(NifBlockParsers.IsNonRenderableHelperShape(nif, Refs(1)));
    }

    [Theory]
    [InlineData("BSShaderPPLightingProperty")] // FO3/FNV — the substring-bug guard
    [InlineData("BSShaderNoLightingProperty")] // FO3/FNV emissive
    [InlineData("BSLightingShaderProperty")]   // Skyrim/FO4
    [InlineData("BSEffectShaderProperty")]     // effects
    [InlineData("WaterShaderProperty")]        // placeable water
    [InlineData("SkyShaderProperty")]          // sky layers
    public void ShaderProperty_OnBsShaderEra_IsRenderable(string shaderType)
    {
        var nif = NifWith(34, shaderType);
        Assert.False(NifBlockParsers.IsNonRenderableHelperShape(nif, Refs(1)));
    }

    [Fact]
    public void LegacyTexturingProperty_IsRenderable()
    {
        // effects\ambient\fxvulturesNV.nif renders via a NiTexturingProperty base map, no BSShader.
        var nif = NifWith(34, "NiTexturingProperty");
        Assert.False(NifBlockParsers.IsNonRenderableHelperShape(nif, Refs(1)));
    }

    [Fact]
    public void ShaderAmongOtherProperties_IsRenderable()
    {
        // Real Shape01: NiMaterialProperty + BSShaderPPLightingProperty — a shader anywhere keeps it.
        var nif = NifWith(34, "NiMaterialProperty", "BSShaderPPLightingProperty");
        Assert.False(NifBlockParsers.IsNonRenderableHelperShape(nif, Refs(2)));
    }

    [Fact]
    public void PreBsShaderEra_NeverHelper()
    {
        // Oblivion (BSVersion 11) / Morrowind use property inheritance + NiTexturingProperty; the filter
        // must not touch them, even a shape that carries only a NiMaterialProperty (or none).
        Assert.False(NifBlockParsers.IsNonRenderableHelperShape(NifWith(11, "NiMaterialProperty"), Refs(1)));
        Assert.False(NifBlockParsers.IsNonRenderableHelperShape(NifWith(11), null));
        Assert.False(NifBlockParsers.IsNonRenderableHelperShape(NifWith(0), Refs(0)));
    }
}
