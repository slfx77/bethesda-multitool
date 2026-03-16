using System.Numerics;
using FalloutXbox360Utils.Core.Formats.Dds;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Export;
using SharpGLTF.Runtime;
using SharpGLTF.Schema2;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Nif.Rendering;

public sealed class NpcExportHelperTests
{
    [Fact]
    public void BuildFileName_UsesSanitizedEditorIdWhenAvailable()
    {
        var invalidChar = Path.GetInvalidFileNameChars()[0];
        var appearance = new NpcAppearance
        {
            NpcFormId = 0x1234u,
            EditorId = $" Craig{invalidChar}Boone "
        };

        var fileName = NpcExportFileNaming.BuildFileName(appearance);

        Assert.Equal("Craig_Boone_00001234.glb", fileName);
    }

    [Fact]
    public void BuildFileName_FallsBackToFormIdWhenSanitizedEditorIdIsEmpty()
    {
        var invalidChar = Path.GetInvalidFileNameChars()[0];
        var appearance = new NpcAppearance
        {
            NpcFormId = 0x92BD2u,
            EditorId = new string(invalidChar, 3)
        };

        var fileName = NpcExportFileNaming.BuildFileName(appearance);

        Assert.Equal("00092BD2.glb", fileName);
    }

    [Fact]
    public void FlipGreenChannel_FlipsOnlyGreenAndLeavesInputUntouched()
    {
        var source = new byte[]
        {
            10, 20, 30, 40,
            50, 60, 70, 80
        };

        var flipped = NpcGlbTextureEncoder.FlipGreenChannel(source);

        Assert.Equal(
            new byte[]
            {
                10, 235, 30, 40,
                50, 195, 70, 80
            },
            flipped);
        Assert.Equal(
            new byte[]
            {
                10, 20, 30, 40,
                50, 60, 70, 80
            },
            source);
    }

    [Fact]
    public void EncodePng_WritesPngSignature()
    {
        var texture = DecodedTexture.FromBaseLevel(
            [
                255, 0, 0, 255,
                0, 255, 0, 255,
                0, 0, 255, 255,
                255, 255, 255, 255
            ],
            2,
            2,
            generateMipChain: false);

        var png = NpcGlbTextureEncoder.EncodePng(texture, flipGreenChannel: true);

        Assert.Equal(
            new byte[] { 0x89, 0x50, 0x4E, 0x47 },
            png.Take(4).ToArray());
    }

    [Fact]
    public void EstimateGlossStrength_UsesNormalMapAlphaChannel()
    {
        var texture = DecodedTexture.FromBaseLevel(
            [
                128, 128, 255, 0,
                128, 128, 255, 255
            ],
            2,
            1,
            generateMipChain: false);

        var glossStrength = NpcGlbMaterialTuning.EstimateGlossStrength(texture);

        Assert.Equal(0.5f, glossStrength, 3);
    }

    [Fact]
    public void Derive_EyeEnvmapMaterialProducesLowRoughness()
    {
        var submesh = new RenderableSubmesh
        {
            Positions = [0f, 0f, 0f],
            Triangles = [],
            MaterialGlossiness = 10f,
            IsEyeEnvmap = true,
            EnvMapScale = 1f
        };

        var profile = NpcGlbMaterialTuning.Derive(submesh, normalTexture: null);

        Assert.True(profile.RoughnessFactor <= 0.18f);
        Assert.True(profile.SpecularFactor >= 0.9f);
    }

    [Fact]
    public void Derive_DefaultMaterialGlossinessKeepsClothingMatte()
    {
        var submesh = new RenderableSubmesh
        {
            Positions = [0f, 0f, 0f],
            Triangles = [],
            MaterialGlossiness = 10f
        };
        var normalTexture = DecodedTexture.FromBaseLevel(
            [
                128, 128, 255, 255,
                128, 128, 255, 255
            ],
            2,
            1,
            generateMipChain: false);

        var profile = NpcGlbMaterialTuning.Derive(submesh, normalTexture);

        Assert.True(profile.RoughnessFactor >= 0.9f, $"Expected matte roughness, found {profile.RoughnessFactor}");
        Assert.True(profile.SpecularFactor <= 0.3f, $"Expected conservative non-eye specular factor, found {profile.SpecularFactor}");
    }

    [Fact]
    public void Derive_GlossyEnvironmentHintsReduceRoughness()
    {
        var submesh = new RenderableSubmesh
        {
            Positions = [0f, 0f, 0f],
            Triangles = [],
            MaterialGlossiness = 80f,
            EnvMapScale = 0.75f,
            ShaderMetadata = new NifShaderTextureMetadata
            {
                ShaderFlags = 1u << 7,
                TextureSlots =
                [
                    null,
                    @"textures\characters\boone_n.dds",
                    null,
                    null,
                    @"textures\cubemaps\chrome_e.dds",
                    @"textures\cubemaps\chrome_m.dds"
                ]
            }
        };
        var normalTexture = DecodedTexture.FromBaseLevel(
            [
                128, 128, 255, 255,
                128, 128, 255, 255
            ],
            2,
            1,
            generateMipChain: false);

        var profile = NpcGlbMaterialTuning.Derive(submesh, normalTexture);

        Assert.True(profile.RoughnessFactor < 0.45f, $"Expected tuned roughness under 0.45, found {profile.RoughnessFactor}");
        Assert.True(profile.SpecularFactor > 0.45f, $"Expected elevated envmap specular factor, found {profile.SpecularFactor}");
    }

    [Fact]
    public void HasEnvironmentHints_DoesNotTreatDefaultEnvMapScaleAsReflectiveWithoutShaderFlag()
    {
        var submesh = new RenderableSubmesh
        {
            Positions = [0f, 0f, 0f],
            Triangles = [],
            EnvMapScale = 1f,
            ShaderMetadata = new NifShaderTextureMetadata()
        };

        Assert.False(NpcGlbMaterialTuning.HasEnvironmentHints(submesh));
    }

    [Fact]
    public void DeriveSpecularPath_RewritesNormalSuffixToSpecularSuffix()
    {
        Assert.Equal(
            @"textures\characters\boone_s.dds",
            NpcGlbNormalMapPacker.DeriveSpecularPath(@"textures\characters\boone_n.dds"));
        Assert.Equal(
            @"textures\characters\boone_s.ddx",
            NpcGlbNormalMapPacker.DeriveSpecularPath(@"textures\characters\boone_n.ddx"));
        Assert.Null(NpcGlbNormalMapPacker.DeriveSpecularPath(@"textures\characters\boone.dds"));
    }

    [Fact]
    public void MergeNormalAndSpecular_CopiesSpecularIntoAlpha()
    {
        var normalTexture = DecodedTexture.FromBaseLevel(
            [
                64, 128, 255, 255,
                192, 64, 200, 255
            ],
            2,
            1,
            generateMipChain: false);
        var specularTexture = DecodedTexture.FromBaseLevel(
            [
                10, 1, 2, 3,
                240, 4, 5, 6
            ],
            2,
            1,
            generateMipChain: false);

        var merged = NpcGlbNormalMapPacker.MergeNormalAndSpecular(normalTexture, specularTexture);

        Assert.Equal(
            new byte[]
            {
                64, 128, 255, 10,
                192, 64, 200, 240
            },
            merged.Pixels);
    }

    [Fact]
    public void BuildMetallicRoughnessTexture_UsesGlossAlphaAndEnvironmentMask()
    {
        var normalTexture = DecodedTexture.FromBaseLevel(
            [
                128, 128, 255, 255,
                128, 128, 255, 0
            ],
            2,
            1,
            generateMipChain: false);
        var environmentMask = DecodedTexture.FromBaseLevel(
            [
                255, 255, 255, 255,
                0, 0, 0, 255
            ],
            2,
            1,
            generateMipChain: false);

        var packed = NpcGlbMaterialTexturePacker.BuildMetallicRoughnessTexture(
            normalTexture,
            hasGlossAlpha: true,
            environmentMask,
            hasEnvironmentMapping: true);

        Assert.NotNull(packed);
        Assert.True(packed!.Pixels[1] < packed.Pixels[5], "Masked glossy texel should be less rough than the matte texel.");
    }

    [Fact]
    public void BuildSpecularFactorTexture_UsesEnvironmentMaskToGateSpecular()
    {
        var normalTexture = DecodedTexture.FromBaseLevel(
            [
                128, 128, 255, 255,
                128, 128, 255, 255
            ],
            2,
            1,
            generateMipChain: false);
        var environmentMask = DecodedTexture.FromBaseLevel(
            [
                255, 255, 255, 255,
                0, 0, 0, 255
            ],
            2,
            1,
            generateMipChain: false);

        var packed = NpcGlbMaterialTexturePacker.BuildSpecularFactorTexture(
            normalTexture,
            hasGlossAlpha: true,
            environmentMask,
            hasEnvironmentMapping: true);

        Assert.NotNull(packed);
        Assert.Equal(255, packed!.Pixels[3]);
        Assert.Equal(0, packed.Pixels[7]);
    }

    [Fact]
    public void BuildOcclusionTexture_UsesHeightLuminance()
    {
        var heightTexture = DecodedTexture.FromBaseLevel(
            [
                0, 0, 0, 255,
                255, 255, 255, 255
            ],
            2,
            1,
            generateMipChain: false);

        var occlusion = NpcGlbMaterialTexturePacker.BuildOcclusionTexture(heightTexture);

        Assert.NotNull(occlusion);
        Assert.Equal(0, occlusion!.Pixels[0]);
        Assert.Equal(255, occlusion.Pixels[4]);
    }

    [Fact]
    public void PrepareAlpha_InvertsLessFunctionForCutout()
    {
        var submesh = new RenderableSubmesh
        {
            Positions = [0f, 0f, 0f],
            Triangles = [],
            HasAlphaTest = true,
            AlphaTestFunction = 1,
            AlphaTestThreshold = 64
        };
        var diffuseTexture = DecodedTexture.FromBaseLevel(
            [
                255, 255, 255, 100
            ],
            1,
            1,
            generateMipChain: false);

        var prepared = NpcGlbAlphaTexturePacker.Prepare(submesh, diffuseTexture);

        Assert.Equal(NifAlphaRenderMode.Cutout, prepared.RenderMode);
        Assert.True(prepared.HasTextureTransform);
        Assert.Equal(155, prepared.Texture!.Pixels[3]);
        Assert.Equal(192, prepared.AlphaThreshold);
    }

    [Fact]
    public void PrepareAlpha_PrefersCutoutForBinaryBlendTextures()
    {
        var submesh = new RenderableSubmesh
        {
            Positions = [0f, 0f, 0f],
            Triangles = [],
            HasAlphaBlend = true,
            HasAlphaTest = true,
            AlphaTestThreshold = 128,
            AlphaTestFunction = 4,
            SrcBlendMode = 6,
            DstBlendMode = 7
        };
        var diffuseTexture = DecodedTexture.FromBaseLevel(
            [
                255, 255, 255, 0,
                255, 255, 255, 255
            ],
            2,
            1,
            generateMipChain: false);

        var prepared = NpcGlbAlphaTexturePacker.Prepare(submesh, diffuseTexture);

        Assert.Equal(NifAlphaRenderMode.Cutout, prepared.RenderMode);
        Assert.False(prepared.HasTextureTransform);
    }

    [Fact]
    public void BuildBaseColor_TintedSubmeshUsesNeutralFactor()
    {
        var submesh = new RenderableSubmesh
        {
            Positions = [0f, 0f, 0f],
            Triangles = [],
            MaterialAlpha = 0.75f,
            TintColor = (0.25f, 0.35f, 0.45f)
        };

        var baseColor = NpcGlbTintColorEncoder.BuildBaseColor(submesh, hasDiffuseTexture: true);

        Assert.Equal(new Vector4(1f, 1f, 1f, 0.75f), baseColor);
    }

    [Fact]
    public void BuildBaseColor_TintedSubmeshWithoutTextureUsesBakedTint()
    {
        var submesh = new RenderableSubmesh
        {
            Positions = [0f, 0f, 0f],
            Triangles = [],
            TintColor = (0.30f, 0.25f, 0.20f)
        };

        var baseColor = NpcGlbTintColorEncoder.BuildBaseColor(submesh, hasDiffuseTexture: false);

        Assert.Equal(0.60f, baseColor.X, 2);
        Assert.Equal(0.50f, baseColor.Y, 2);
        Assert.Equal(0.40f, baseColor.Z, 2);
        Assert.Equal(1f, baseColor.W, 3);
    }

    [Fact]
    public void BuildVertexColor_TintedSubmeshPreservesRawVertexColors()
    {
        var submesh = new RenderableSubmesh
        {
            Positions = [0f, 0f, 0f],
            Triangles = [],
            UseVertexColors = true,
            VertexColors =
            [
                255, 128, 64, 192
            ],
            TintColor = (0.30f, 0.25f, 0.20f)
        };

        var color = NpcGlbTintColorEncoder.BuildVertexColor(submesh, 0);

        Assert.Equal(1f, color.X, 3);
        Assert.Equal(128f / 255f, color.Y, 3);
        Assert.Equal(64f / 255f, color.Z, 3);
        Assert.Equal(192f / 255f, color.W, 3);
    }

    [Fact]
    public void BuildVertexColor_EmissiveWithoutVertexColorFlag_NeutralizesRgbButPreservesAlpha()
    {
        var submesh = new RenderableSubmesh
        {
            Positions = [0f, 0f, 0f],
            Triangles = [],
            IsEmissive = true,
            UseVertexColors = false,
            VertexColors =
            [
                255, 240, 0, 96
            ]
        };

        var color = NpcGlbTintColorEncoder.BuildVertexColor(submesh, 0);

        Assert.Equal(1f, color.X, 3);
        Assert.Equal(1f, color.Y, 3);
        Assert.Equal(1f, color.Z, 3);
        Assert.Equal(96f / 255f, color.W, 3);
    }

    [Fact]
    public void BakeDiffuseTexture_TintedSubmeshAppliesDoubleTintFactor()
    {
        var submesh = new RenderableSubmesh
        {
            Positions = [0f, 0f, 0f],
            Triangles = [],
            TintColor = (0.30f, 0.25f, 0.20f)
        };
        var diffuseTexture = DecodedTexture.FromBaseLevel(
            [
                200, 100, 50, 255
            ],
            1,
            1,
            generateMipChain: false);

        var tinted = NpcGlbTintColorEncoder.BakeDiffuseTexture(submesh, diffuseTexture);

        Assert.NotNull(tinted);
        Assert.Equal(120, tinted!.Pixels[0]);
        Assert.Equal(50, tinted.Pixels[1]);
        Assert.Equal(20, tinted.Pixels[2]);
        Assert.Equal(255, tinted.Pixels[3]);
    }

    [Fact]
    public void ShouldExportGlowAsEmissive_HairHighlightTexture_ReturnsFalse()
    {
        var submesh = new RenderableSubmesh
        {
            Positions = [0f, 0f, 0f],
            Triangles = [],
            ShapeName = "HairBun",
            DiffuseTexturePath = @"textures\characters\hair\HairBun.dds",
            TintColor = (0.25f, 0.10f, 0.05f)
        };
        var shaderMetadata = new NifShaderTextureMetadata
        {
            TextureSlots =
            [
                submesh.DiffuseTexturePath,
                @"textures\characters\hair\HairBun_n.dds",
                @"textures\characters\hair\HairBun_hl.dds"
            ]
        };

        Assert.False(NpcGlbMaterialChannelDecider.ShouldExportGlowAsEmissive(submesh, shaderMetadata));
    }

    [Fact]
    public void ShouldExportGlowAsEmissive_NonHairGlowTexture_ReturnsTrue()
    {
        var submesh = new RenderableSubmesh
        {
            Positions = [0f, 0f, 0f],
            Triangles = [],
            ShapeName = "NeonSign",
            DiffuseTexturePath = @"textures\signs\neon.dds"
        };
        var shaderMetadata = new NifShaderTextureMetadata
        {
            TextureSlots =
            [
                submesh.DiffuseTexturePath,
                null,
                @"textures\signs\neon_g.dds"
            ]
        };

        Assert.True(NpcGlbMaterialChannelDecider.ShouldExportGlowAsEmissive(submesh, shaderMetadata));
    }

    [Fact]
    public void Write_TwoSkinnedPartsUsingSharedSkeleton_ReloadsWithSeparateSkins()
    {
        var scene = new NpcExportScene();
        var pelvisNode = scene.AddNode(
            "Bip01 Pelvis",
            scene.RootNodeIndex,
            Matrix4x4.Identity,
            Matrix4x4.Identity,
            NpcExportNodeKind.Skeleton,
            "Bip01 Pelvis");
        var spineNode = scene.AddNode(
            "Bip01 Spine",
            pelvisNode,
            Matrix4x4.Identity,
            Matrix4x4.Identity,
            NpcExportNodeKind.Skeleton,
            "Bip01 Spine");

        scene.MeshParts.Add(CreateSkinnedMeshPart("BodyA", pelvisNode, spineNode, 0f));
        scene.MeshParts.Add(CreateSkinnedMeshPart("BodyB", pelvisNode, spineNode, 2f));

        using var textureResolver = new NifTextureResolver();
        using var outputDir = new TemporaryDirectory();
        var outputPath = Path.Combine(outputDir.Path, "shared-skeleton.glb");

        NpcGlbWriter.Write(scene, textureResolver, outputPath);

        var model = ModelRoot.Load(outputPath);

        Assert.Equal(2, model.LogicalMeshes.Count);
        Assert.Equal(2, model.LogicalSkins.Count);
        Assert.True(model.LogicalNodes.Count >= 3);
    }

    [Fact]
    public void GltfCoordinateAdapter_MapsBethesdaAxesToGlbAxes()
    {
        var convertedForward = GltfCoordinateAdapter.ConvertPosition(Vector3.UnitY);
        var convertedUp = GltfCoordinateAdapter.ConvertPosition(Vector3.UnitZ);
        var convertedTranslation = GltfCoordinateAdapter.ConvertMatrix(Matrix4x4.CreateTranslation(1f, 2f, 3f));

        AssertVectorEquals(new Vector3(0f, 0f, -1f), convertedForward);
        AssertVectorEquals(new Vector3(0f, 1f, 0f), convertedUp);
        AssertVectorEquals(new Vector3(1f, 3f, -2f), Vector3.Transform(Vector3.Zero, convertedTranslation));
    }

    [Fact]
    public void Write_RigidMesh_ConvertsBethesdaAxesForGlb()
    {
        var scene = new NpcExportScene();
        var nodeIndex = scene.AddNode(
            "Rigid",
            scene.RootNodeIndex,
            Matrix4x4.Identity,
            Matrix4x4.Identity,
            NpcExportNodeKind.Attachment);
        scene.MeshParts.Add(new NpcExportMeshPart
        {
            Name = "Rigid",
            NodeIndex = nodeIndex,
            Submesh = new RenderableSubmesh
            {
                ShapeName = "Rigid",
                Positions =
                [
                    0f, 0f, 0f,
                    0f, 1f, 0f,
                    0f, 0f, 1f
                ],
                Triangles = [(ushort)0, 1, 2],
                Normals =
                [
                    1f, 0f, 0f,
                    1f, 0f, 0f,
                    1f, 0f, 0f
                ]
            }
        });

        using var textureResolver = new NifTextureResolver();
        using var outputDir = new TemporaryDirectory();
        var outputPath = Path.Combine(outputDir.Path, "rigid-axis.glb");

        NpcGlbWriter.Write(scene, textureResolver, outputPath);

        var model = ModelRoot.Load(outputPath);
        var primitive = model.LogicalMeshes.Single().Primitives.Single();
        var positions = primitive.GetVertexAccessor("POSITION").AsVector3Array().ToArray();

        Assert.Contains(positions, position => IsNearlyEqual(position, Vector3.Zero));
        Assert.Contains(positions, position => IsNearlyEqual(position, new Vector3(0f, 0f, -1f)));
        Assert.Contains(positions, position => IsNearlyEqual(position, new Vector3(0f, 1f, 0f)));
    }

    private static NpcExportMeshPart CreateSkinnedMeshPart(
        string name,
        int pelvisNode,
        int spineNode,
        float xOffset)
    {
        return new NpcExportMeshPart
        {
            Name = name,
            Submesh = new RenderableSubmesh
            {
                ShapeName = name,
                Positions =
                [
                    xOffset + 0f, 0f, 0f,
                    xOffset + 1f, 0f, 0f,
                    xOffset + 0f, 1f, 0f
                ],
                Triangles = [(ushort)0, 1, 2],
                Normals =
                [
                    0f, 0f, 1f,
                    0f, 0f, 1f,
                    0f, 0f, 1f
                ],
                UVs =
                [
                    0f, 0f,
                    1f, 0f,
                    0f, 1f
                ]
            },
            Skin = new NpcExportSkinBinding
            {
                JointNodeIndices = [pelvisNode, spineNode],
                InverseBindMatrices = [Matrix4x4.Identity, Matrix4x4.Identity],
                PerVertexInfluences =
                [
                    [ (BoneIdx: 0, Weight: 1f) ],
                    [ (BoneIdx: 1, Weight: 1f) ],
                    [ (BoneIdx: 1, Weight: 1f) ]
                ]
            }
        };
    }

    private static void AssertVectorEquals(Vector3 expected, Vector3 actual, float tolerance = 0.0001f)
        => Assert.True(
            IsNearlyEqual(actual, expected, tolerance),
            $"Expected {expected} but found {actual}");

    private static bool IsNearlyEqual(Vector3 actual, Vector3 expected, float tolerance = 0.0001f)
        => Vector3.Distance(actual, expected) <= tolerance;

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"NpcGlbExportTests_{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
