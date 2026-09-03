using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.App;

public sealed class NifConverterTextureArchiveStateSourceContractTests
{
    [Fact]
    public void ManualOverrideAndActualTextureSourcesUseSeparateControls()
    {
        var xaml = SourceContract.ReadAppSource("NifConverterTab.xaml");
        var code = SourceContract.ReadAppSource("NifConverterTab.xaml.cs");

        Assert.Contains("x:Name=\"NifViewerTextureOverrideTextBox\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"NifViewerTextureSourcesText\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("AutomationProperties.Name=\"Active texture sources\"", xaml,
            StringComparison.Ordinal); // Preserve the dynamic Text as the accessible name.

        Assert.Contains("var overrideText = NifViewerTextureOverrideTextBox.Text;", code,
            StringComparison.Ordinal);
        Assert.Contains("NifViewerTextureSourcesText.Text =", code, StringComparison.Ordinal);
        Assert.Contains("$\"Texture sources: {state.TexturePathsDisplay}\"", code, StringComparison.Ordinal);
        Assert.Equal(1, SourceContract.CountOccurrences(code, "NifViewerTextureOverrideTextBox.Text ="));
        Assert.Contains("NifViewerTextureOverrideTextBox.Text = file.Path;", code, StringComparison.Ordinal);
        Assert.DoesNotContain("var hasOverride =", code, StringComparison.Ordinal);
    }

    [Fact]
    public void SourceAndTextureArchivePickersBothAdmitBsaAndBa2()
    {
        var code = SourceContract.ReadAppSource("NifConverterTab.xaml.cs");

        Assert.Equal(2, SourceContract.CountOccurrences(code, "FileTypeFilter.Add(\".bsa\")"));
        Assert.Equal(2, SourceContract.CountOccurrences(code, "FileTypeFilter.Add(\".ba2\")"));
    }

    [Fact]
    public void WorkflowParsesPathListsAndViewModelAlwaysReturnsActualSources()
    {
        var workflow = SourceContract.ReadAppSource("NifConverterWorkflowService.cs");
        var viewModel = SourceContract.ReadAppSource("NifConverterViewModel.cs");

        Assert.Contains("NifTextureSourcePathText.ParseOverride(texturePathOverride)", workflow,
            StringComparison.Ordinal);
        Assert.Contains("NifTextureSourcePathText.Format(service.TexturePaths)", workflow,
            StringComparison.Ordinal);
        Assert.DoesNotContain("new[] { texturePathOverride.Trim() }", workflow, StringComparison.Ordinal);

        Assert.Contains("result.TexturePathsDisplay", viewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("keepTextureOverride", viewModel, StringComparison.Ordinal);
    }

    [Fact]
    public void ViewerSourceLoadingExposesTruthfulPhaseProgressAndClearsStaleState()
    {
        var xaml = SourceContract.ReadAppSource("NifConverterTab.xaml");
        var code = SourceContract.ReadAppSource("NifConverterTab.xaml.cs");
        var workflow = SourceContract.ReadAppSource("NifConverterWorkflowService.cs");
        var viewModel = SourceContract.ReadAppSource("NifConverterViewModel.cs");
        var browser = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering",
            "NifBrowserService.cs");

        Assert.Contains("x:Name=\"NifViewerSourceProgressPanel\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"NifViewerSourceProgressBar\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.LiveSetting=\"Polite\"", xaml, StringComparison.Ordinal);
        Assert.Contains("_nifViewer.ClearSource();", code, StringComparison.Ordinal);
        Assert.Contains("PopulateNifTree([]);", code, StringComparison.Ordinal);
        Assert.Contains("new Progress<NifViewerSourceLoadProgress>", code, StringComparison.Ordinal);
        Assert.Contains("Opening archive and related asset indexes...", code, StringComparison.Ordinal);
        Assert.Contains("Scanning archive entries:", code, StringComparison.Ordinal);
        Assert.Contains("Scanning folder:", code, StringComparison.Ordinal);
        Assert.Contains("Building mesh list for", code, StringComparison.Ordinal);
        Assert.Contains("Finishing related mesh, material, and texture discovery...", code,
            StringComparison.Ordinal);
        Assert.Contains("_nifViewerSourceLoadingGeneration == sourceGeneration", code,
            StringComparison.Ordinal);

        Assert.Contains("NifViewerSourceLoadPhase.OpeningArchiveIndexes", workflow,
            StringComparison.Ordinal);
        Assert.Contains("NifViewerSourceLoadPhase.ScanningArchiveEntries", workflow,
            StringComparison.Ordinal);
        Assert.Contains("NifViewerSourceLoadPhase.BuildingTree", workflow, StringComparison.Ordinal);
        Assert.Contains("service.BeginRelatedArchiveDiscovery()", workflow, StringComparison.Ordinal);
        Assert.Contains("NifViewerSourceLoadPhase.DiscoveringRelatedArchives", workflow,
            StringComparison.Ordinal);
        Assert.Contains("relatedArchiveDiscovery.WaitAsync(cancellationToken)", workflow,
            StringComparison.Ordinal);
        Assert.Contains("Action<NifBrowserScanProgress>? progress", browser, StringComparison.Ordinal);
        Assert.Contains("new NifBrowserScanProgress(0, totalEntries, 0)", browser,
            StringComparison.Ordinal);
        Assert.Contains("_archive.EnumerateFilePaths()", browser, StringComparison.Ordinal);
        Assert.Contains("DiscoverInDirectoryWithKnownArchive", browser, StringComparison.Ordinal);
        Assert.DoesNotContain("_archive.ListFiles()", browser, StringComparison.Ordinal);
        Assert.Contains("public void ClearSource()", viewModel, StringComparison.Ordinal);
    }

    [Fact]
    public void ViewerSourceLoadingYieldsForPaint_AndPropagatesCancellationThroughTreeProjection()
    {
        var code = SourceContract.ReadAppSource("NifConverterTab.xaml.cs");
        var workflow = SourceContract.ReadAppSource("NifConverterWorkflowService.cs");
        var treeItem = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "App", "Tabs", "Models", "NifTreeViewItem.cs");

        Assert.Contains("private CancellationTokenSource? _nifViewerSourceLoadCts;", code,
            StringComparison.Ordinal);
        Assert.Contains("await Task.Yield();", code, StringComparison.Ordinal);
        Assert.Contains("catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)",
            code, StringComparison.Ordinal);
        Assert.Contains("_nifViewerSourceLoadCts?.Cancel();", code, StringComparison.Ordinal);
        Assert.Contains("CancellationToken cancellationToken = default", workflow,
            StringComparison.Ordinal);
        Assert.Contains("}, cancellationToken);", workflow, StringComparison.Ordinal);
        Assert.Contains("service.ListNifFiles(scanProgress =>", workflow, StringComparison.Ordinal);
        Assert.Contains("}, cancellationToken);", workflow, StringComparison.Ordinal);
        Assert.Contains("NifTreeViewItem.FromTreeEntries(entries, cancellationToken)", workflow,
            StringComparison.Ordinal);
        Assert.Contains("cancellationToken.ThrowIfCancellationRequested();", treeItem,
            StringComparison.Ordinal);
    }
}
