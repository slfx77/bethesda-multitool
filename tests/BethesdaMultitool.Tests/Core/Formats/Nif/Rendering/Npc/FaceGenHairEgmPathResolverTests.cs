using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Assets;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Npc;

public sealed class FaceGenHairEgmPathResolverTests
{
    private const string HairNif = @"meshes\Characters\Hair\Style03.NIF";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OblivionUsesTheSingleSiblingEgm(bool useHatGeometry)
    {
        var path = FaceGenHairEgmPathResolver.Build(
            BethesdaGame.Oblivion,
            HairNif,
            useHatGeometry);

        Assert.Equal(@"meshes\Characters\Hair\Style03.egm", path);
    }

    [Theory]
    [InlineData(false, @"meshes\Characters\Hair\Style03nohat.egm")]
    [InlineData(true, @"meshes\Characters\Hair\Style03hat.egm")]
    public void OtherFamiliesRetainTheExistingGeometrySuffix(
        bool useHatGeometry,
        string expected)
    {
        var path = FaceGenHairEgmPathResolver.Build(
            BethesdaGame.FalloutNewVegas,
            HairNif,
            useHatGeometry);

        Assert.Equal(expected, path);
    }

    [Fact]
    public void CpuAndNativeAssemblersUseTheSharedResolver()
    {
        var cpu = SourceContract.ReadSource(
            ["src", "BethesdaMultitool", "CLI", "Rendering", "Npc", "NpcHeadPartAttacher.cs"]);
        var native = SourceContract.ReadSource(
            ["src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Npc",
                "Assembly", "NpcExportHeadAssembler.cs"]);

        Assert.Contains("FaceGenHairEgmPathResolver.Build(", cpu, StringComparison.Ordinal);
        Assert.Contains("FaceGenHairEgmPathResolver.Build(", native, StringComparison.Ordinal);
        Assert.DoesNotContain("hairBaseName + egmSuffix", cpu + native, StringComparison.Ordinal);
    }
}
