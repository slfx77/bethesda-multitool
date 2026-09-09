using BethesdaMultitool.Core.Formats.Nif.Rendering;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Materials;

internal static class OblivionHairLayerTestData
{
    internal const string Diffuse = "textures/characters/hair/grey.dds";
    internal const string Layer = "textures/characters/hair/grey_hl.dds";

    internal static RenderableSubmesh Create()
    {
        return new RenderableSubmesh
        {
            ShapeName = "Synthetic Hair:0",
            SourceBlockIndex = 2,
            LegacyMaterialName = "Hair",
            Positions = [0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f],
            Triangles = [0, 1, 2],
            Normals = [0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f],
            UVs = [0f, 0f, 1f, 0f, 0f, 1f],
            VertexColors = [2, 255, 255, 255, 75, 255, 255, 255, 130, 255, 255, 255],
            DiffuseTexturePath = Diffuse,
            HasAuthoredOblivionHairLayerInputs = true,
            UsesClassicHairMaterial = true,
            HasAlphaBlend = true,
            HasAlphaTest = true,
            AlphaTestThreshold = 0,
            TintColor = (192f / 255f, 192f / 255f, 192f / 255f),
            MaterialAlpha = 1f,
            MaterialDiffuse = (1f, 1f, 1f),
            MaterialGlossiness = 10f,
            SpecularColor = (0.9f, 0.9f, 0.9f)
        };
    }
}