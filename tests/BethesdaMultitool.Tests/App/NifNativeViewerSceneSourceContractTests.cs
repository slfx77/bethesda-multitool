using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.App;

public sealed class NifNativeViewerSceneSourceContractTests
{
    private static string BrowserServiceSource() => SourceContract.ReadSource(
        "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering",
        "NifBrowserService.cs");

    [Fact]
    public void RawPreviewBuildsNativeSceneBeforeCompatibilityGlb()
    {
        var source = BrowserServiceSource();
        var compatibility = SourceContract.Extract(
            source,
            "internal NifBrowserGlbBuildResult BuildGlbWithDiagnostics(",
            "internal NifBrowserViewerSceneBuildResult BuildViewerSceneWithDiagnostics(");
        var native = SourceContract.Extract(
            source,
            "internal NifBrowserViewerSceneBuildResult BuildViewerSceneWithDiagnostics(",
            "internal byte[] ExportViewerSceneToGlb(");

        SourceContract.AssertOrder(
            compatibility,
            "BuildViewerSceneWithDiagnostics(nifData, sourceLabel)",
            "ExportViewerSceneToGlb(build.Scene)");
        SourceContract.AssertOrder(
            native,
            "ParseAndConvert(nifData)",
            "DetectViewerGame(nif)",
            "NifGeometryExtractor.Extract(",
            "return Finish(scene, data, nif)");
        Assert.Contains("BethesdaViewerSceneGlbAdapter.FromGlbScene(", native, StringComparison.Ordinal);
        Assert.Contains("BethesdaViewerScenePurpose.RawNif", native, StringComparison.Ordinal);
        Assert.Contains("game: detectedGame", native, StringComparison.Ordinal);
        Assert.Contains("textureSourcePaths: TexturePaths", native, StringComparison.Ordinal);
    }

    [Fact]
    public void ModernStreamVersionsSelectExplicitRendererGame()
    {
        var detection = SourceContract.Extract(
            BrowserServiceSource(),
            "private static BethesdaGame DetectViewerGame(",
            "internal byte[]? RenderPng(");

        SourceContract.AssertOrder(
            detection,
            ">= 170 => BethesdaGame.Starfield",
            ">= 155 => BethesdaGame.Fallout76",
            ">= 130 => BethesdaGame.Fallout4",
            "_ => BethesdaGame.Unknown");
    }

    [Fact]
    public void MeshViewerWorkflowCarriesSceneBesideTemporaryGlb()
    {
        var workflow = SourceContract.ReadAppSource("NifConverterWorkflowService.cs");
        var load = SourceContract.Extract(
            workflow,
            "internal static Task<NifViewerModelLoadResult> LoadModelAsync(",
            "internal static async Task<NifBrowserGlbBuildResult?> BuildGlbAsync(");

        SourceContract.AssertOrder(
            load,
            "BuildViewerSceneWithDiagnostics(nifData, item.DisplayName)",
            "service.ExportViewerSceneToGlb(build.Scene)",
            "build.Scene,",
            "build.ExternalGeometry.IncompleteWarningMessage");
        Assert.Contains("BethesdaViewerScene? Scene", workflow, StringComparison.Ordinal);
        Assert.Contains("temporary WebView compatibility", workflow, StringComparison.Ordinal);
    }
}
