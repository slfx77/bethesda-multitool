using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.App;

public sealed class NativeBethesdaViewerHostSourceContractTests
{
    [Fact]
    public void MeshAndNpcTabsAttachTheNativeSessionAndKeepWebViewOnlyAsPreReadyFallback()
    {
        var meshHost = SourceContract.ReadAppSource("NifConverterTab.xaml.cs");
        var meshXaml = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "App", "Tabs", "NifConverterTab.xaml");
        var npcHost = SourceContract.ReadAppSource("SingleFileTab.xaml.cs");
        var npcBrowser = SourceContract.ReadAppSource("SingleFileTab.NpcBrowser.cs");
        var npcXaml = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "App", "Tabs", "SingleFile", "SingleFileTab.xaml");

        Assert.Contains(
            "NifSceneViewer.AttachRenderSession(new BethesdaViewerRenderSession12())",
            meshHost,
            StringComparison.Ordinal);
        Assert.Contains(
            "NpcSceneViewer.AttachRenderSession(new BethesdaViewerRenderSession12())",
            npcHost,
            StringComparison.Ordinal);
        SourceContract.AssertOrder(meshXaml, "x:Name=\"NifSceneViewer\"", "x:Name=\"NifModelViewer\"");
        SourceContract.AssertOrder(npcXaml, "x:Name=\"NpcSceneViewer\"", "x:Name=\"NpcModelViewer\"");
        Assert.Contains("x:Name=\"NifModelViewer\"\n                  Visibility=\"Collapsed\"", meshXaml,
            StringComparison.Ordinal);
        Assert.Contains("x:Name=\"NpcModelViewer\" Visibility=\"Collapsed\"", npcXaml,
            StringComparison.Ordinal);

        SourceContract.AssertOrder(
            meshHost,
            "includeCompatibilityGlb: false",
            "NifSceneViewer.SetScene(result.Scene)",
            "service.ExportViewerSceneToGlb(result.Scene)");
        Assert.Contains("NifModelViewer.Close()", meshHost, StringComparison.Ordinal);
        SourceContract.AssertOrder(
            npcBrowser,
            "NpcSceneViewer.SetScene(scene)",
            "await InitializeWebViewAsync()",
            "service.ExportViewerSceneToGlb(scene)");
        Assert.Contains("NpcModelViewer.Close()", npcBrowser, StringComparison.Ordinal);
    }

    [Fact]
    public void ColdLoadsDoNotStartChromiumUntilTheExactNativeSceneFaults()
    {
        var meshHost = SourceContract.ReadAppSource("NifConverterTab.xaml.cs");
        var npcBrowser = SourceContract.ReadAppSource("SingleFileTab.NpcBrowser.cs");

        var meshLoad = SourceContract.Extract(
            meshHost,
            "private async Task LoadNifIntoViewerAsync(",
            "private async Task SetNifViewerFallbackStatusAsync(");
        SourceContract.AssertOrder(
            meshLoad,
            "nativeOutcome = new TaskCompletionSource<BethesdaSceneViewerRenderState>",
            "NifSceneViewer.SetScene(result.Scene)",
            "await nativeOutcome.Task.WaitAsync(cancellationToken)",
            "nativeState == BethesdaSceneViewerRenderState.Faulted",
            "await InitializeNifViewerWebViewAsync()",
            "service.ExportViewerSceneToGlb(result.Scene)");

        var npcLoad = SourceContract.Extract(
            npcBrowser,
            "private async Task LoadNpcIntoViewerAsync(",
            "private async void NpcRenderOption_Changed(");
        SourceContract.AssertOrder(
            npcLoad,
            "nativeOutcome = new TaskCompletionSource<BethesdaSceneViewerRenderState>",
            "NpcSceneViewer.SetScene(scene)",
            "await nativeOutcome.Task.WaitAsync(cancellationToken)",
            "nativeState == BethesdaSceneViewerRenderState.Faulted",
            "await InitializeWebViewAsync()",
            "service.ExportViewerSceneToGlb(scene)");

        Assert.Contains("CompleteNifViewerNativeOutcome(e.State)", meshHost, StringComparison.Ordinal);
        Assert.Contains("CompleteNpcViewerNativeOutcome(e.State)", npcBrowser, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadyPromotionIsOneWayAndHiddenTabsStopNativePresentation()
    {
        var meshHost = SourceContract.ReadAppSource("NifConverterTab.xaml.cs");
        var npcHost = SourceContract.ReadAppSource("SingleFileTab.xaml.cs");
        var npcBrowser = SourceContract.ReadAppSource("SingleFileTab.NpcBrowser.cs");
        var control = SourceContract.ReadAppSource("BethesdaSceneViewerControl.xaml.cs");
        var lifecycle = SourceContract.ReadAppSource("BethesdaSceneViewerControl.Lifecycle.cs");

        SourceContract.AssertOrder(
            SourceContract.Extract(
                meshHost,
                "private void NifSceneViewer_RenderStateChanged(",
                "private void CloseNifViewerCompatibilityHost()"),
            "e.State != BethesdaSceneViewerRenderState.Ready || _nifViewerNativeReady",
            "_nifViewerNativeReady = true",
            "CloseNifViewerCompatibilityHost()");
        SourceContract.AssertOrder(
            SourceContract.Extract(
                npcBrowser,
                "private void NpcSceneViewer_RenderStateChanged(",
                "private void CloseNpcViewerCompatibilityHost()"),
            "e.State != BethesdaSceneViewerRenderState.Ready || _npcViewerNativeReady",
            "_npcViewerNativeReady = true",
            "CloseNpcViewerCompatibilityHost()");

        Assert.Contains("NifSceneViewer.SetPresentationActive(viewerSelected)", meshHost, StringComparison.Ordinal);
        Assert.Contains("NpcSceneViewer.SetPresentationActive(ReferenceEquals(selected, NpcBrowserTab))", npcHost,
            StringComparison.Ordinal);
        Assert.Contains("NpcSceneViewer.SetPresentationActive(actorsSelected)", npcHost,
            StringComparison.Ordinal);
        SourceContract.AssertOrder(
            SourceContract.Extract(
                npcHost,
                "private bool TrySelectSubTab(AnalysisSubTab tab)",
                "#endregion"),
            "SubTabView.SelectedItem = item;",
            "NpcSceneViewer.SetPresentationActive(actorsSelected);",
            "NpcSceneViewer.InvalidateViewport();");
        Assert.Contains("internal void SetPresentationActive(bool active)", control, StringComparison.Ordinal);
        Assert.Contains("if (!_isPresentationActive || !IsEffectivelyVisible())", lifecycle, StringComparison.Ordinal);
        Assert.Contains(
            "_renderState == BethesdaSceneViewerRenderState.Ready &&\n            _scene is not null &&\n            (_surface is null || !_hasPresentedFrame)",
            lifecycle,
            StringComparison.Ordinal);
        SourceContract.AssertOrder(
            lifecycle,
            "_surface = GpuSwapChainSurface12.Create(",
            "_frameInvalidated = true;",
            "NotifyObservableRenderStateChanged();");
        SourceContract.AssertOrder(
            lifecycle,
            "recording.Submit(capture);",
            "surface.Present();",
            "_hasPresentedFrame = true;");
        var rendering = SourceContract.Extract(
            lifecycle,
            "private void OnRendering(",
            "private bool IsEffectivelyVisible()");
        SourceContract.AssertOrder(
            rendering,
            "RenderNativeFrame(graphics, surface, session, scene, deltaSeconds);",
            "_renderingFrame = false;",
            "SynchronizeRenderState();");
    }

    [Fact]
    public void InFlightChromiumStartupCannotResurrectFallbackAfterNativeReadyOrDisposal()
    {
        var meshHost = SourceContract.ReadAppSource("NifConverterTab.xaml.cs");
        var npcBrowser = SourceContract.ReadAppSource("SingleFileTab.NpcBrowser.cs");

        var meshInit = SourceContract.Extract(
            meshHost,
            "private async Task InitializeNifViewerWebViewCoreAsync()",
            "private async void NifViewerBrowseFolder_Click");
        SourceContract.AssertOrder(
            meshInit,
            "await NifModelViewer.EnsureCoreWebView2Async();",
            "_nifViewerWebViewInitialized = true;",
            "if (_nifViewerNativeReady || _nifViewerDisposed)",
            "CloseNifViewerCompatibilityHost();",
            "NifModelViewer.Visibility = Visibility.Visible;");
        Assert.Contains("if (_nifViewerDisposed) return;", meshInit, StringComparison.Ordinal);

        var npcInit = SourceContract.Extract(
            npcBrowser,
            "private async Task InitializeWebViewCoreAsync()",
            "#endregion");
        SourceContract.AssertOrder(
            npcInit,
            "await NpcModelViewer.EnsureCoreWebView2Async();",
            "_webViewInitialized = true;",
            "if (_npcViewerNativeReady || _npcViewerDisposed)",
            "CloseNpcViewerCompatibilityHost();",
            "NpcModelViewer.Visibility = Visibility.Visible;");
        Assert.Contains("if (_npcViewerDisposed) return;", npcInit, StringComparison.Ordinal);
    }

    [Fact]
    public void BothTabsExposeExactLiveNativeFramebufferCapture()
    {
        var meshHost = SourceContract.ReadAppSource("NifConverterTab.xaml.cs");
        var meshXaml = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "App", "Tabs", "NifConverterTab.xaml");
        var npcBrowser = SourceContract.ReadAppSource("SingleFileTab.NpcBrowser.cs");
        var npcXaml = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "App", "Tabs", "SingleFile", "SingleFileTab.xaml");

        Assert.Contains("Click=\"NifViewerCaptureNativePng_Click\"", meshXaml, StringComparison.Ordinal);
        Assert.Contains("await NifSceneViewer.CapturePngAsync();", meshHost, StringComparison.Ordinal);
        Assert.Contains("Click=\"NpcCaptureNativePng_Click\"", npcXaml, StringComparison.Ordinal);
        Assert.Contains("await NpcSceneViewer.CapturePngAsync();", npcBrowser, StringComparison.Ordinal);
    }
}
