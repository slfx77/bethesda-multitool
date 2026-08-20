using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.App;

public sealed class RendererBackendLabelSourceContractTests
{
    [Fact]
    public void WorldViewerDoesNotClaimADeadD3D11Fallback()
    {
        var hud = SourceContract.ReadAppSource("WorldView3DControl.Hud.cs");

        Assert.Contains("D3D12 unavailable", hud, StringComparison.Ordinal);
        Assert.DoesNotContain(": \"D3D11\"", hud, StringComparison.Ordinal);
    }

    [Fact]
    public void MainProjectDoesNotShipTheUnusedD3D11Binding()
    {
        var project = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "BethesdaMultitool.csproj");
        var centralPackages = SourceContract.ReadSource("Directory.Packages.props");

        Assert.DoesNotContain("Vortice.Direct3D11", project, StringComparison.Ordinal);
        Assert.DoesNotContain("Vortice.Direct3D11", centralPackages, StringComparison.Ordinal);
        Assert.Contains("Vortice.Direct3D12", project, StringComparison.Ordinal);
    }
}