using System.Numerics;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Shaders;

public sealed class DoubleSidedNormalMapBasisSourceContractTests
{
    [Fact]
    public void BackFaceReversesNormalAndBitangentToPreserveHandedness()
    {
        var frontNormal = Vector3.UnitZ;
        var tangent = Vector3.UnitX;
        var frontBitangent = Vector3.Cross(frontNormal, tangent);
        var backNormal = -frontNormal;
        var backBitangent = -frontBitangent;

        Assert.Equal(Vector3.Cross(backNormal, tangent), backBitangent);
        Assert.Equal(-Vector3.UnitY, backBitangent);
    }

    [Fact]
    public void ReferenceSpriteAndCpuNormalMapPathsApplyTheSameBackFaceRule()
    {
        var reference = SourceContract.ReadShaderSource("reference.frag.hlsl");
        var classicSkin = SourceContract.ReadShaderSource("reference_classic_skin.frag.hlsl");
        var sprite = SourceContract.ReadShaderSource("skin.frag.hlsl");
        var cpu = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Rasterization",
            "NifScanlineRasterizer.cs");

        Assert.Equal(3, SourceContract.CountOccurrences(reference, "B = -B;"));
        Assert.Equal(3, SourceContract.CountOccurrences(reference, "bool flipTangentBasis"));
        SourceContract.AssertOrder(
            classicSkin,
            "geometricNormal = -geometricNormal;",
            "bitangent = -bitangent;",
            "mapNormal.y * bitangent");
        SourceContract.AssertOrder(
            sprite,
            "normal = -normal;",
            "B = -B;",
            "float3x3 TBN = float3x3(T, B, N);");
        SourceContract.AssertOrder(
            cpu,
            "nx = -nx;",
            "bx = -bx;",
            "var wnx = tx * mapNx + bx * mapNy + nx * mapNz;");
    }
}
