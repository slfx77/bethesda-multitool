using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Profiling;

public sealed class NifHeadlessStarfieldSiblingArchiveSourceContractTests
{
    [Fact]
    public void HeadlessRetailVerifierUsesContentClassifiedSiblingMeshesOnlyForStarfield()
    {
        var source = SourceContract.ReadSource(
            "src", "BethesdaRendererProfiler", "NifHeadlessRenderer.cs");

        Assert.Contains("if (game == BethesdaGame.Starfield)", source, StringComparison.Ordinal);
        Assert.Contains("--game <FO76|Starfield|BethesdaGame>", source, StringComparison.Ordinal);
        Assert.Contains("game = BethesdaGame.Unknown;", source, StringComparison.Ordinal);
        Assert.Contains("return false;", source, StringComparison.Ordinal);
        Assert.Contains("BsaDiscovery.DiscoverInDirectory(archiveDirectory)", source,
            StringComparison.Ordinal);
        Assert.Contains(".MeshesBsaPaths", source, StringComparison.Ordinal);
        Assert.Contains("path, primaryMeshArchive, StringComparison.OrdinalIgnoreCase", source,
            StringComparison.Ordinal);
        Assert.Contains("siblingMeshArchives is { Length: > 0 } ? siblingMeshArchives : null", source,
            StringComparison.Ordinal);
        Assert.Contains("meshCache.TotalParseFailedModelPaths", source, StringComparison.Ordinal);
        Assert.Contains("meshCache.TotalMissingGeometryBlobs", source, StringComparison.Ordinal);
        Assert.Contains("meshCache.TotalDecodeFailedGeometryBlobs", source, StringComparison.Ordinal);
        Assert.Contains("if (!streamingSettled || !drew || finalStats.ReferenceMeshMissing != 0 ||", source,
            StringComparison.Ordinal);
        Assert.Contains("incomplete render rejected", source, StringComparison.Ordinal);
        Assert.Contains("externalMeshLoader: LoadExternalGeometry", source, StringComparison.Ordinal);
    }
}
