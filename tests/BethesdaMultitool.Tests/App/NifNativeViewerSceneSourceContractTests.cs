using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.App;

public sealed class NifNativeViewerSceneSourceContractTests
{
    private static string BrowserServiceSource()
    {
        return SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering",
            "NifBrowserService.cs");
    }

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
            "BuildViewerSceneWithDiagnostics(",
            "item.DisplayName,",
            "item.FullPath);",
            "service.ExportViewerSceneToGlb(build.Scene)",
            "build.Scene,",
            "build.ExternalGeometry.IncompleteWarningMessage");
        Assert.Contains("BethesdaViewerScene? Scene", workflow, StringComparison.Ordinal);
        // The GLB's role is now named by a parameter rather than a comment: the workflow builds
        // the native scene first and emits the GLB only as an opt-in compatibility artefact.
        Assert.Contains("bool includeCompatibilityGlb = true,", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void MeshViewerPublishesHandleFreeModelFamilyProvenance()
    {
        var browser = BrowserServiceSource();
        var scene = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Viewer",
            "BethesdaViewerScene.cs");
        var xaml = SourceContract.ReadAppSource("NifConverterTab.xaml");
        var code = SourceContract.ReadAppSource("NifConverterTab.xaml.cs");

        SourceContract.AssertOrder(
            browser,
            "ResolveModelFamilyAnimations(sourcePath ?? sourceLabel",
            "viewerScene.SetModelFamilyAnimations(modelFamilyAnimations)");
        Assert.Contains("private sealed class DeferredGameFileSystem", browser,
            StringComparison.Ordinal);
        Assert.Contains("files.EnumerateFiles(prefix).ToArray()", browser,
            StringComparison.Ordinal);
        Assert.Contains("Animations = catalog.Animations", scene, StringComparison.Ordinal);
        Assert.DoesNotContain("IGameFileSystem", scene, StringComparison.Ordinal);
        Assert.DoesNotContain("ArchiveLease", scene, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"NifViewerAnimationSourcesText\"", xaml, StringComparison.Ordinal);
        Assert.Contains("SetNifViewerAnimationCatalog(result.Scene?.ModelFamilyAnimations)", code,
            StringComparison.Ordinal);
    }
}