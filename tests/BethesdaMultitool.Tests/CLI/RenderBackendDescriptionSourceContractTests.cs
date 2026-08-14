using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.CLI;

public sealed class RenderBackendDescriptionSourceContractTests
{
    [Fact]
    public void GpuOptionsDescribeTheBackendThatTheSelectorActuallyConstructs()
    {
        foreach (var command in new[] { "RenderCommand.cs", "RenderNpcCommand.cs" })
        {
            var source = SourceContract.ReadSource(
                "src", "BethesdaMultitool", "CLI", "Commands", "Render", command);
            Assert.Contains("Force GPU rendering (D3D12)", source, StringComparison.Ordinal);
            Assert.DoesNotContain("Vulkan/D3D11", source, StringComparison.Ordinal);
        }

        var selector = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "CLI", "Rendering", "SpriteRenderBackendSelector.cs");
        Assert.Contains("new GpuSpriteRenderer12(device)", selector, StringComparison.Ordinal);
    }
}
