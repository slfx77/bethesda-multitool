using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.D3D12;

public sealed class ReferenceSubmeshDecoder12SourceContractTests
{
    [Fact]
    public void PlacedAndStandaloneViewerDecodeUseTheSameAuthoritativeMapper()
    {
        var placedDecoder = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
            "ReferenceMeshDecoder12.cs");
        var placedLoop = SourceContract.Extract(
            placedDecoder,
            "var submeshes = new List<DecodedSubmesh12>",
            "if (submeshes.Count == 0)");
        var viewerDecoder = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12", "Viewer",
            "BethesdaViewerSceneDecoder12.cs");

        Assert.Contains("ReferenceSubmeshDecoder12.Decode(", placedLoop, StringComparison.Ordinal);
        Assert.DoesNotContain("NifAlphaClassifier.Classify(", placedLoop, StringComparison.Ordinal);
        Assert.DoesNotContain("NifSpecularPolicy.IsEnabled(", placedLoop, StringComparison.Ordinal);
        Assert.DoesNotContain("new DecodedSubmesh12(", placedLoop, StringComparison.Ordinal);
        Assert.Contains("ReferenceSubmeshDecoder12.Decode(", viewerDecoder, StringComparison.Ordinal);
        Assert.Contains("scene.TextureSourcePaths.ToArray()", viewerDecoder, StringComparison.Ordinal);
        Assert.Contains("scene.GeneratedTextures", viewerDecoder, StringComparison.Ordinal);
        Assert.Contains("scene.AnimationClips.Select(SnapshotAnimationClip)", viewerDecoder,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CpuPayloadAndBridgeRemainAvailableToHeadlessTests()
    {
        var payload = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
            "ReferenceDecodedMesh12.cs");
        var mapper = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
            "ReferenceSubmeshDecoder12.cs");
        var viewer = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12", "Viewer",
            "BethesdaViewerSceneDecoder12.cs");

        Assert.DoesNotContain("#if WINDOWS_GUI", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("#if WINDOWS_GUI", mapper, StringComparison.Ordinal);
        Assert.DoesNotContain("#if WINDOWS_GUI", viewer, StringComparison.Ordinal);
    }
}
