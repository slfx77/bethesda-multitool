using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.App;

public sealed class NifAnimationCatalogSourceContractTests
{
    [Fact]
    public void RightPanelOwnsVirtualizedAnimationCatalogWithoutRemovingManualPicker()
    {
        var tabXaml = SourceContract.ReadAppSource("NifConverterTab.xaml");
        var viewerXaml = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "App", "Controls", "BethesdaSceneViewer",
            "BethesdaSceneViewerControl.xaml");

        Assert.Contains("<TabViewItem Header=\"Animation\"", tabXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"NifViewerAnimationList\"", tabXaml, StringComparison.Ordinal);
        Assert.Contains("<ItemsStackPanel CacheLength=\"2.0\"", tabXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"NifViewerLoadSelectedAnimationButton\"", tabXaml,
            StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.LiveSetting=\"Polite\"", tabXaml,
            StringComparison.Ordinal);
        Assert.Contains("x:Name=\"AnimationLoadKfButton\"", viewerXaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Load KF…\"", viewerXaml, StringComparison.Ordinal);
    }

    [Fact]
    public void CatalogReadIsGenerationGatedAndDrainedBeforeServiceDisposal()
    {
        var code = SourceContract.ReadAppSource("NifConverterTab.xaml.cs");
        var selectedLoad = SourceContract.Extract(
            code,
            "private async Task LoadSelectedModelFamilyAnimationAsync()",
            "private bool IsCurrentNifViewerAnimationLoad(");
        var currentGate = SourceContract.Extract(
            code,
            "private bool IsCurrentNifViewerAnimationLoad(",
            "private async Task CancelNifViewerAnimationLoadAndDrainAsync()");
        var sourceLoad = SourceContract.Extract(
            code,
            "private async Task LoadNifSourceAsync(",
            "private void UpdateNifViewerSourceProgress(");

        SourceContract.AssertOrder(
            selectedLoad,
            "CancelNifViewerAnimationLoadAndDrainAsync()",
            "var service = _nifBrowserService;",
            "var targetScene = _nifViewerScene;",
            "asset.Size != selected.Size",
            "LoadModelFamilyAnimationAsync(",
            "IsCurrentNifViewerAnimationLoad(",
            "NifSceneViewer.ApplyKfBindingResult(targetScene, binding)");
        Assert.Contains("modelGeneration == _nifViewerLoadGeneration", currentGate,
            StringComparison.Ordinal);
        Assert.Contains("animationGeneration == _nifViewerAnimationLoadGeneration", currentGate,
            StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(service, _nifBrowserService)", currentGate,
            StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(scene, _nifViewerScene)", currentGate,
            StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(scene, NifSceneViewer.Scene)", currentGate,
            StringComparison.Ordinal);
        SourceContract.AssertOrder(
            sourceLoad,
            "CancelNifViewerAnimationLoadAndDrainAsync()",
            "previousService?.Dispose()");
        Assert.Contains("Task.WhenAll(pendingLoads)", code, StringComparison.Ordinal);
        Assert.Contains("_nifViewerAnimationLoadTask", code, StringComparison.Ordinal);
    }

    [Fact]
    public void CatalogPayloadUsesStrictBoundedArchiveReadAndSharedTransactionalBinder()
    {
        var browser = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering",
            "NifBrowserService.cs");
        var archiveReader = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Bsa", "Index",
            "ArchiveReader.cs");
        var bsaExtractor = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Bsa", "Extraction",
            "BsaExtractor.cs");
        var workflow = SourceContract.ReadAppSource("NifConverterWorkflowService.cs");
        var binder = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Viewer",
            "BethesdaViewerKfAnimationBinder.cs");
        var control = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "App", "Controls", "BethesdaSceneViewer",
            "BethesdaSceneViewerControl.Animation.cs");

        SourceContract.AssertOrder(
            browser,
            "asset.Size > BethesdaViewerKfAnimationBinder.MaximumPayloadBytes",
            "var current = _modelFamilyFiles.TryStat(asset.VirtualPath)",
            "current.Size != asset.Size",
            "ArchiveHandleRegistry.Shared.Acquire(current.Source)",
            "archiveEntry.Size != current.Size",
            "archiveLease.Reader.ExtractBounded(");
        Assert.Contains("ExtractFileBounded(bsaRecord, maximumBytes)", archiveReader,
            StringComparison.Ordinal);
        SourceContract.AssertOrder(
            bsaExtractor,
            "uncompressedSize = _view.ReadUInt32(dataOffset)",
            "uncompressedSize > outputLimit",
            "var result = new byte[uncompressedSize]");
        SourceContract.AssertOrder(
            workflow,
            "service.ReadModelFamilyAnimationData(asset, cancellationToken)",
            "BethesdaViewerKfAnimationBinder.ParseAndBind(");
        Assert.Contains("NifControllerSequenceNameTrackReader.ReadAll(data, nif)", binder,
            StringComparison.Ordinal);
        Assert.Contains("BethesdaViewerNameTargetedAnimationAdapter.TryCreateClip(", binder,
            StringComparison.Ordinal);
        Assert.DoesNotContain("AnimationClips.Add", binder, StringComparison.Ordinal);
        Assert.Contains("internal bool ApplyKfBindingResult(", control, StringComparison.Ordinal);
        SourceContract.AssertOrder(
            control,
            "var firstAppendedClipIndex = targetScene.AnimationClips.Count;",
            "targetScene.AnimationClips.Add(clip with { Name = uniqueName });",
            "ReloadSessionAfterAnimationMutation(targetScene, firstAppendedClipIndex)",
            "_renderSession.SetScene(targetScene);",
            "_renderSession.SelectAnimationClip(preferredClipIndex);",
            "SynchronizeRenderState();",
            "SynchronizeAnimationControls();",
            "InvalidateViewport();");
    }
}