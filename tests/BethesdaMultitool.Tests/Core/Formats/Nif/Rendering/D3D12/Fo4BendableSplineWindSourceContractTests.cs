using System.Runtime.InteropServices;
using BethesdaMultitool.Tests.Helpers;
using Xunit;
using static BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12.ReferenceRendererConstants12;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.D3D12;

public sealed class Fo4BendableSplineWindSourceContractTests
{
    [Fact]
    public void GeneratedSplineIdentityAndFlexibilityReachCachedTextureState()
    {
        var geometry = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Procedural",
            "BendableSplineGeometry.cs");
        var decoded = D3D12Source("ReferenceDecodedMesh12.cs");
        var cached = D3D12Source("CachedSubmesh12.cs");
        var cache = D3D12Source("ReferenceMeshCache12.cs");

        SourceContract.AssertOrder(
            geometry,
            "bool UsesWindShader,",
            "float WindFlexibility,",
            "float MaximumPackedWindWeight",
            "FindMaximumPackedWindWeight(vertices)",
            "Fo4BendableSplineWind.BoundsExpansion(maximumPackedWindWeight)");
        Assert.Contains("bool IsBendableSplineWind = false", decoded, StringComparison.Ordinal);
        Assert.Contains("float BendableSplineWindFlexibility = 0f", decoded, StringComparison.Ordinal);
        Assert.Contains("BendableSplineWindTextureFlag = 1u << 18", cached, StringComparison.Ordinal);
        Assert.Contains("public bool IsBendableSplineWind", cached, StringComparison.Ordinal);
        Assert.Contains("public float BendableSplineWindFlexibility", cached, StringComparison.Ordinal);
        Assert.Contains(
            "(IsBendableSplineWind ? (float)BendableSplineWindTextureFlag : 0f)",
            cached,
            StringComparison.Ordinal);
        SourceContract.AssertOrder(
            cache,
            "IsBendableSplineWind: generated.UsesWindShader",
            "BendableSplineWindFlexibility: generated.WindFlexibility",
            "IsBendableSplineWind = sub.IsBendableSplineWind",
            "BendableSplineWindFlexibility = sub.BendableSplineWindFlexibility");
    }

    [Fact]
    public void RendererReusesExistingConstantUnionAndRetainsDirectionTimeAndTurbulence()
    {
        Assert.Equal(256, Marshal.SizeOf<InstanceDrawConstants>());

        var renderer = D3D12Source("ReferenceRenderer12.cs");
        var constants = D3D12Source("ReferenceRendererConstants12.cs");
        var frame = SourceContract.ReadAppSource("WorldView3DControl.Frame.cs");
        var capture = SourceContract.ReadAppSource("WorldView3DControl.SceneCapture.cs");

        Assert.Contains("private Vector2 _windDirection = Vector2.UnitX;", renderer,
            StringComparison.Ordinal);
        Assert.Contains("private float _windTurbulence;", renderer, StringComparison.Ordinal);
        Assert.DoesNotContain("_ = direction;", renderer, StringComparison.Ordinal);
        Assert.Contains("_windDirection = Fo4BendableSplineWind.NormalizeDirection(direction);",
            renderer, StringComparison.Ordinal);
        Assert.Contains("_windTurbulence = float.IsFinite(turbulence)", renderer,
            StringComparison.Ordinal);
        SourceContract.AssertOrder(
            renderer,
            "var splineWind = sub.IsBendableSplineWind",
            "Vector4 wind;",
            "if (sub.IsBendableSplineWind)",
            "wind = splineWind.WindVector;",
            "Wind: wind",
            "TallGrassWind: sub.IsBendableSplineWind",
            "? splineWind.WindVectorEx");
        Assert.Contains("Constant union: SpeedTree rock/rustle, or FO4 spline", constants,
            StringComparison.Ordinal);
        Assert.Contains("Constant union: FNV GRASS2000 wind, or FO4 spline", constants,
            StringComparison.Ordinal);
        Assert.Contains("Fo4BendableSplineWind.ResolveWeather(", frame, StringComparison.Ordinal);
        Assert.Contains("splineWeatherWind.NormalizedTurbulence);", frame,
            StringComparison.Ordinal);
        Assert.Contains("Fo4BendableSplineWind.ResolveWeather(", capture,
            StringComparison.Ordinal);
        Assert.Contains("[\"directionSelection\"] = splineWeatherWind.DirectionSelection.ToString()",
            capture, StringComparison.Ordinal);
        Assert.Contains("[\"windVectorEx\"] = new[]", capture, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedMainAndShadowVertexShaderContainOnlyRecoveredSplineEquation()
    {
        var shader = SourceContract.ReadShaderSource("reference_instanced.vert.hlsl");
        var pipeline = D3D12Source("ReferencePipelineFactory12.cs");

        Assert.Contains("IsBendableSplineWindMaterial", shader, StringComparison.Ordinal);
        SourceContract.AssertOrder(
            shader,
            "void ApplyBendableSplineWind(",
            "float placementPhase = 0.001 *",
            "absolutePlacement.x + absolutePlacement.y + absolutePlacement.z",
            "placementPhase * sin((time * 5.0) * placementPhase) / (frequency * 10.0)",
            "(1.0 + flexibility * packedAlpha) * spatial",
            "40.0 * frequency + 50.0 * frequency * time",
            "0.002 * speedRange * speedRange * sin(phase) + 0.25 * minimumSpeed",
            "worldPosition.x += cos(theta) * amplitude",
            "worldPosition.y += sin(theta) * amplitude",
            "float3 absolutePlacement = relativePlacement + uCameraOrigin.xyz",
            "ApplyBendableSplineWind(worldPos, absolutePlacement, bendableSplineWindWeight)");
        Assert.Contains("abs(frequency) <= 1e-6", shader, StringComparison.Ordinal);
        Assert.DoesNotContain("#ifndef SHADOW_CARD_LIGHT_FACING", shader,
            StringComparison.Ordinal);
        SourceContract.AssertOrder(
            pipeline,
            "CompileEmbeddedShader(\"reference_instanced.vert.hlsl\", \"main\", \"vs_5_1\")",
            "CompileEmbeddedShader(\"reference_instanced.vert.hlsl\", \"main\", \"vs_5_1\",",
            "new ShaderMacro(\"SHADOW_CARD_LIGHT_FACING\", \"1\"))");
    }

    [Fact]
    public void VertexAlphaIsWindDataNotMainOrShadowCoverage()
    {
        var vertex = SourceContract.ReadShaderSource("reference_instanced.vert.hlsl");
        var fragment = SourceContract.ReadShaderSource("reference.frag.hlsl");
        var shadow = SourceContract.ReadShaderSource("shadow.frag.hlsl");

        Assert.Contains("if (isTallGrass || isBendableSplineWind)", vertex,
            StringComparison.Ordinal);
        Assert.Contains("bool bendableSplineWind = IsBendableSplineWindMaterial", fragment,
            StringComparison.Ordinal);
        SourceContract.AssertOrder(
            fragment,
            "if (bendableSplineWind)",
            "vertexCoverageAlpha = 1.0;");
        SourceContract.AssertOrder(
            shadow,
            "bool bendableSplineWind = (materialFlags & 262144u) != 0u;",
            ": treeAnimation || bendableSplineWind",
            "? saturate(alpha * input.vAlphaState.z)");
    }

    [Fact]
    public void DynamicSplineCannotEnterCurrentPersistentPacketOrModernStandardLane()
    {
        var packet = D3D12Source("StaticOpaquePacketPolicy.cs");
        var modern = D3D12Source("ModernStandardOpaqueShaderPolicy.cs");

        Assert.DoesNotContain("Fallout4", packet, StringComparison.Ordinal);
        Assert.Contains("StaticOpaquePacketGame.Fallout76 or StaticOpaquePacketGame.Starfield",
            packet, StringComparison.Ordinal);
        Assert.Contains("private const uint TreeAnimationFeatureMask = 1u << 17;", modern,
            StringComparison.Ordinal);
        Assert.Contains(
            "(facts.TextureFeatureMask & ~TreeAnimationFeatureMask) != ModernSpecularMapFeatureMask",
            modern,
            StringComparison.Ordinal);
    }

    private static string D3D12Source(string fileName)
    {
        return SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12", fileName);
    }
}