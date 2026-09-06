using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Water;

/// <summary>
///     D3D12 upload, shader ABI, and WinUI host integration are unavailable in the headless TFM.
///     Scalar/texture behavior is covered separately by OblivionWaterDisplacementCompositionTests.
/// </summary>
public sealed class OblivionWaterDisplacementSourceContractTests
{
    [Fact]
    public void EverySceneLoadRebindsAfterSetGameAndUsesANoMipDiagnosticTexture()
    {
        var cells = SourceContract.ReadAppSource("WorldView3DControl.Cells.cs");
        Assert.Equal(2, SourceContract.CountOccurrences(cells,
            "_water?.SetGame(_data.Game);\n        BindOblivionDisplacementDiagnostic();"));

        var host = SourceContract.ReadAppSource("WorldView3DControl.WaterDisplacement.cs");
        SourceContract.AssertOrder(host,
            "_water.SetOblivionDisplacementTexture(null, 0f, 0f);",
            "_data?.Game != BethesdaGame.Oblivion",
            "Core.EnvironmentVariables.Viewer.OblivionWaterDisplacementProbe",
            "mode == OblivionWaterDisplacementComposition.ProbeMode.Disabled",
            "OblivionWaterDisplacementComposition.GenerateProbeTexture(mode)",
            "GetOrCreateSyntheticBindlessIndex(",
            "generateMips: false",
            "_water.SetOblivionDisplacementTexture(\n            index,");
        Assert.Contains("diagnostic:oblivion-water-displacement:rgba8-nomips:", host, StringComparison.Ordinal);
        Assert.Contains("WATERDISPLACE simulation unavailable.", host, StringComparison.Ordinal);
    }

    [Fact]
    public void CpuAndShaderAppendTheSameFourLaneRegisterAfterStarfield()
    {
        var renderer = ReadRenderer();
        var layout = SourceContract.Extract(renderer, "private struct WaterFrameUniforms", "\n    }\n}");
        SourceContract.AssertOrder(layout,
            "public StarfieldWaterFrameUniforms Starfield;",
            "public uint OblivionDisplacementIndex;",
            "public float OblivionDisplacementRadius;",
            "public float OblivionDisplacementBlendAmount;",
            "public uint OblivionDisplacementRoute;");
        Assert.Contains("private const uint OblivionDisplacementUniformByteSize = 16;", renderer,
            StringComparison.Ordinal);
        Assert.Contains("StarfieldWaterUniformByteSize + OblivionDisplacementUniformByteSize;", renderer,
            StringComparison.Ordinal);
        SourceContract.AssertOrder(renderer,
            "var uniformSize = Marshal.SizeOf<WaterFrameUniforms>();",
            "if (uniformSize != (int)WaterFrameUniformsByteSize)",
            "throw new InvalidOperationException");

        var shader = SourceContract.ReadShaderSource("water_common.hlsli");
        SourceContract.AssertOrder(shader,
            "float4 uStarfieldUnderwaterColor;",
            "uint4 uOblivionDisplacement;",
            "\n};");
    }

    [Fact]
    public void UploadRetainsZeroBlendBindingsAndFNVExplicitlyDisablesItsSource()
    {
        var renderer = ReadRenderer();
        var upload = SourceContract.Extract(renderer,
            "*(WaterFrameUniforms*)perFrameAlloc.CpuPtr", "UploadInstances(instanceCount);");
        Assert.Contains("OblivionDisplacementRouteEnabled ? _oblivionDisplacementBindlessIndex : NoNormalMap", upload,
            StringComparison.Ordinal);
        Assert.Contains("OblivionDisplacementRouteEnabled ? _oblivionDisplacementRadius : 0f", upload,
            StringComparison.Ordinal);
        Assert.Contains("OblivionDisplacementRouteEnabled ? _oblivionDisplacementBlendAmount : 0f", upload,
            StringComparison.Ordinal);
        Assert.Contains("OblivionDisplacementRoute = OblivionDisplacementRouteEnabled ? 1u : 0u", upload,
            StringComparison.Ordinal);
        Assert.DoesNotContain("OblivionDisplacementHasContribution", upload, StringComparison.Ordinal);

        var fnvUpload = SourceContract.Extract(renderer,
            "*(WaterFrameUniforms*)perFrame.CpuPtr", "_frameMaterialCb[(material.WaterFormId, water001)]");
        Assert.Contains("OblivionDisplacementIndex = NoNormalMap", fnvUpload, StringComparison.Ordinal);
        Assert.DoesNotContain("OblivionDisplacementRoute = 1", fnvUpload, StringComparison.Ordinal);
    }

    [Fact]
    public void ShaderBlendsDecodedLocalNormalAfterGlobalDistanceFadeThenNormalizesOnce()
    {
        var shader = SourceContract.ReadShaderSource("water_oblivion.frag.hlsl");
        var composition = SourceContract.Extract(shader,
            "pert.xy *= oblivionDistanceAtten;", "float ndotv =");
        SourceContract.AssertOrder(composition,
            "uint displacementIndex = uOblivionDisplacement.x;",
            "float displacementRadius = asfloat(uOblivionDisplacement.y);",
            "float displacementAmount = asfloat(uOblivionDisplacement.z);",
            "uOblivionDisplacement.w != 0u",
            "displacementIndex != 0xFFFFFFFFu",
            "displacementAmount > 0.0",
            "(input.vWorldPos.xy - uCamPosTime.xy) * (1.0 / 1024.0)",
            "2.0 * displacementDistance / displacementRadius",
            "(1.0 - displacementRamp) * displacementAmount",
            ".Sample(gOblivionWaterSampler, displacementUv).xyz * 2.0 - 1.0",
            "pert = lerp(pert, displacementNormal, displacementWeight);",
            "float3 N = normalize(pert);");
        Assert.Contains("saturate(max(\n            0.1,", composition, StringComparison.Ordinal);
        Assert.Equal(1, SourceContract.CountOccurrences(composition, "normalize("));
    }

    [Fact]
    public void CaptureDistinguishesDiagnosticBindingRouteAndContribution()
    {
        var capture = SourceContract.ReadAppSource("WorldView3DControl.SceneCapture.cs");
        Assert.Contains("fields[\"waterOblivionDisplacementDiagnosticSource\"] = _water?.OblivionDisplacementDiagnosticSource;",
            capture, StringComparison.Ordinal);
        Assert.Contains("fields[\"waterOblivionDisplacementSourceBound\"] = _water?.OblivionDisplacementSourceBound ?? false;",
            capture, StringComparison.Ordinal);
        Assert.Contains("fields[\"waterOblivionDisplacementRouteEnabled\"] = _water?.OblivionDisplacementRouteEnabled ?? false;",
            capture, StringComparison.Ordinal);
        Assert.Contains("fields[\"waterOblivionDisplacementHasContribution\"] = _water?.OblivionDisplacementHasContribution ?? false;",
            capture, StringComparison.Ordinal);
    }

    private static string ReadRenderer() => SourceContract.ReadSource(
        "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12", "WaterRenderer12.cs");
}
