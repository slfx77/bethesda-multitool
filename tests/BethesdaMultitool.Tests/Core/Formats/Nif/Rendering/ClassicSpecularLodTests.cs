using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Materials;
using BethesdaMultitool.Core.Formats.SpeedTree;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

public sealed class ClassicSpecularLodTests
{
    [Fact]
    public void Profile_UsesRecoveredFnvDefaultsAndNoOtherGame()
    {
        foreach (var game in Enum.GetValues<BethesdaGame>())
        {
            var profile = ClassicSpecularLodProfile.ForGame(game);
            Assert.Equal(game == BethesdaGame.FalloutNewVegas, profile.Supported);
            Assert.Equal(game == BethesdaGame.FalloutNewVegas, profile.Enabled);
        }

        var fnv = ClassicSpecularLodProfile.ForGame(BethesdaGame.FalloutNewVegas);
        Assert.Equal(500f, fnv.StartFade);
        Assert.Equal(300f, fnv.FadeRange);
        Assert.Equal(800f, fnv.EndFade);
        Assert.Equal(1f, fnv.LodAdjust);
        Assert.Equal(new Vector4(500f, 800f, 1f, 1f), fnv.ShaderParameters(true));
        Assert.Equal(0f, fnv.ShaderParameters(false).W);
    }

    [Theory]
    [InlineData(499f, 1f)]
    [InlineData(500f, 1f)]
    [InlineData(650f, 0.5f)]
    [InlineData(799f, 1f / 300f)]
    [InlineData(800f, 0f)]
    [InlineData(900f, 0f)]
    public void Fade_UsesExactSphereSurfacePiecewiseDistance(float surfaceDistance, float expected)
    {
        var profile = ClassicSpecularLodProfile.ForGame(BethesdaGame.FalloutNewVegas);
        const float radius = 25f;

        var fade = ClassicSpecularLodFade.ComputeFromCameraDistance(
            in profile, surfaceDistance + radius, radius);

        Assert.Equal(expected, fade, 5);
    }

    [Fact]
    public void Fade_PreservesInsideSphereNegativeDistanceAndZeroRangeEndpoint()
    {
        var fnv = ClassicSpecularLodProfile.ForGame(BethesdaGame.FalloutNewVegas);
        Assert.Equal(1f, ClassicSpecularLodFade.ComputeFromCameraDistance(
            in fnv, 5f, 10f));

        var zeroRange = new ClassicSpecularLodProfile(true, 500f, 0f, 1f);
        Assert.Equal(1f, ClassicSpecularLodFade.ComputeFromCameraDistance(
            in zeroRange, 509f, 10f));
        Assert.Equal(0f, ClassicSpecularLodFade.ComputeFromCameraDistance(
            in zeroRange, 510f, 10f));
    }

    [Fact]
    public void Fade_AppliesLodAdjustAfterSurfaceDistance()
    {
        var profile = new ClassicSpecularLodProfile(true, 500f, 300f, 2f);

        Assert.Equal(0f, ClassicSpecularLodFade.ComputeFromCameraDistance(
            in profile, 410f, 10f));
    }

    [Fact]
    public void Fade_TransformsRadiusAndIsInvariantUnderRenderOriginRebasing()
    {
        var profile = ClassicSpecularLodProfile.ForGame(BethesdaGame.FalloutNewVegas);
        var absoluteWorld = Matrix4x4.CreateScale(2f) * Matrix4x4.CreateTranslation(1_000f, 2_000f, 30f);
        var localCenter = Vector3.UnitX;
        const float localRadius = 2f;
        var worldCenter = Vector3.Transform(localCenter, absoluteWorld);
        var absoluteCamera = worldCenter + new Vector3(654f, 0f, 0f);
        var absoluteFade = ClassicSpecularLodFade.Compute(
            in profile, localCenter, localRadius, absoluteWorld, absoluteCamera);

        var renderOrigin = new Vector3(960f, 1_984f, 0f);
        var relativeWorld = absoluteWorld;
        relativeWorld.Translation -= renderOrigin;
        var relativeFade = ClassicSpecularLodFade.Compute(
            in profile, localCenter, localRadius, relativeWorld, absoluteCamera - renderOrigin);

        Assert.Equal(0.5f, absoluteFade, 5);
        Assert.Equal(absoluteFade, relativeFade, 5);
    }

    [Fact]
    public void RendererAndShaders_PreserveAbiAndScopeFadeToDirectFnvSunSpecular()
    {
        var perDrawType = Assert.IsAssignableFrom<Type>(typeof(ReferenceRendererConstants12).GetNestedType(
            "PerDrawConstants", BindingFlags.NonPublic));
        var instanceDrawType = Assert.IsAssignableFrom<Type>(typeof(ReferenceRendererConstants12).GetNestedType(
            "InstanceDrawConstants", BindingFlags.NonPublic));
        Assert.Equal(256, Marshal.SizeOf(perDrawType));
        Assert.Equal(224, Marshal.OffsetOf(perDrawType, "UvScroll").ToInt32());
        Assert.Equal(256, Marshal.SizeOf(instanceDrawType));
        Assert.Equal(224, RecordFieldOffset(instanceDrawType, "SpecularLodBounds"));
        Assert.Equal(240, RecordFieldOffset(instanceDrawType, "SpecularLodParams"));

        var renderer = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
            "ReferenceRenderer12.cs");
        var rendererConstants = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
            "ReferenceRendererConstants12.cs");
        var topDown = SourceContract.ReadAppSource("WorldView3DControl.TopDown.cs");
        var directVertex = ReadEmbeddedShader("reference.vert.hlsl");
        var instancedVertex = ReadEmbeddedShader("reference_instanced.vert.hlsl");
        var pixel = ReadEmbeddedShader("reference.frag.hlsl");

        Assert.Contains("private const uint PerDrawByteSize = 256;", renderer, StringComparison.Ordinal);
        Assert.Contains("private const uint InstanceDrawByteSize = 256;", renderer, StringComparison.Ordinal);
        Assert.Contains("var instanceStride = (uint)Marshal.SizeOf<Matrix4x4>();", renderer,
            StringComparison.Ordinal);
        var tallGrassOffset = rendererConstants.IndexOf(
            "Vector4 TallGrassWind = default", StringComparison.Ordinal);
        var boundsOffset = rendererConstants.IndexOf(
            "Vector4 SpecularLodBounds = default", StringComparison.Ordinal);
        var paramsOffset = rendererConstants.IndexOf(
            "Vector4 SpecularLodParams = default", StringComparison.Ordinal);
        Assert.True(tallGrassOffset >= 0 && boundsOffset > tallGrassOffset && paramsOffset > boundsOffset);
        Assert.Contains("StructuredBuffer<float4x4> uInstanceWorlds", instancedVertex,
            StringComparison.Ordinal);
        Assert.Contains("float4 uSpecularLodBounds;", instancedVertex, StringComparison.Ordinal);
        Assert.Contains("float4 uSpecularLodParams;", instancedVertex, StringComparison.Ordinal);
        Assert.Contains("float specularLodFade = 1.0;", instancedVertex, StringComparison.Ordinal);
        Assert.Contains("#ifdef SHADOW_CARD_LIGHT_FACING", instancedVertex, StringComparison.Ordinal);
        Assert.Contains(
            "float scaleX = length(float3(world[0].x, world[1].x, world[2].x));",
            instancedVertex,
            StringComparison.Ordinal);
        Assert.Contains(
            "float scaleY = length(float3(world[0].y, world[1].y, world[2].y));",
            instancedVertex,
            StringComparison.Ordinal);
        Assert.Contains(
            "float scaleZ = length(float3(world[0].z, world[1].z, world[2].z));",
            instancedVertex,
            StringComparison.Ordinal);
        Assert.Contains(
            "SpecularLodBounds: new Vector4(sub.LocalBoundsCenter, sub.LocalBoundsRadius)",
            renderer,
            StringComparison.Ordinal);
        Assert.Contains(
            "SpecularLodParams: _classicSpecularLodProfile.ShaderParameters(",
            renderer,
            StringComparison.Ordinal);
        Assert.Contains("var specularLodFade = ClassicSpecularLodFade.Compute(", renderer,
            StringComparison.Ordinal);
        Assert.Contains("specularLodFade, 0f)", renderer, StringComparison.Ordinal);
        Assert.Contains("shadingCameraPosOverride: cylinder.Position", topDown,
            StringComparison.Ordinal);
        Assert.Contains("cameraPosition: cylinder.Position", topDown, StringComparison.Ordinal);

        foreach (var vertex in new[] { directVertex, instancedVertex })
        {
            Assert.Contains("vSpecularLodFade : TEXCOORD15", vertex, StringComparison.Ordinal);
        }

        Assert.Contains("o.vSpecularLodFade = uUvScroll.z;", directVertex, StringComparison.Ordinal);
        Assert.Equal(2, CountOccurrences(pixel, "vSpecularLodFade"));

        var directStart = pixel.IndexOf("// FNV sun specular", StringComparison.Ordinal);
        var environmentStart = pixel.IndexOf(
            "// FO3/FNV classic PP-lighting environment pass", StringComparison.Ordinal);
        Assert.True(directStart >= 0 && environmentStart > directStart);
        var directSunSpecular = pixel[directStart..environmentStart];
        Assert.Contains("specMask * specTerm * input.vSpecularLodFade", directSunSpecular,
            StringComparison.Ordinal);
        Assert.Contains("uSunColorLighting.rgb", directSunSpecular, StringComparison.Ordinal);
        Assert.DoesNotContain("input.vSpecular.rgb", directSunSpecular, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string source, string value)
    {
        return source.Split(value).Length - 1;
    }

    private static int RecordFieldOffset(Type type, string propertyName)
    {
        var field = Assert.Single(type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            candidate => candidate.Name.Contains($"<{propertyName}>", StringComparison.Ordinal));
        return Marshal.OffsetOf(type, field.Name).ToInt32();
    }

    private static string ReadEmbeddedShader(string name)
    {
        var assembly = typeof(SptGeometryOptions).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .Single(candidate => candidate.EndsWith(name, StringComparison.OrdinalIgnoreCase));
        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}