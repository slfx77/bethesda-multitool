using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.App;

public sealed class NativeBethesdaViewerHostSourceContractTests
{
    [Fact]
    public void StandalonePluginActorsReuseTheAnalyzedRecordIndex()
    {
        var tab = SourceContract.ReadAppSource("SingleFileTab.xaml.cs");
        var npcBrowser = SourceContract.ReadAppSource("SingleFileTab.NpcBrowser.cs");

        Assert.Contains(
            "openAccessor: fileType is AnalysisFileType.SaveFile or AnalysisFileType.EsmFile",
            tab,
            StringComparison.Ordinal);
        SourceContract.AssertOrder(
            npcBrowser,
            "_session.Accessor != null && analyzedRecords is { MainRecords.Count: > 0 }",
            "NpcBrowserWorkflowService.CreateFromAnalyzedEsmAsync(",
            ": await NpcBrowserWorkflowService.CreateFromEsmAsync(");
        SourceContract.AssertOrder(
            npcBrowser,
            "var listState = _npcBrowser.LoadList(",
            "ApplyNpcListState(listState);",
            "_session.NpcBrowserPopulated = true;");
    }

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
            "NifSceneViewer.SetPresentationActive(ReferenceEquals(NifTabView.SelectedItem, NifViewerTab))",
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
            "NpcSceneViewer.SetPresentationActive(ReferenceEquals(SubTabView.SelectedItem, NpcBrowserTab))",
            "NpcSceneViewer.SetScene(scene)",
            "await nativeOutcome.Task.WaitAsync(cancellationToken)",
            "nativeState == BethesdaSceneViewerRenderState.Faulted",
            "await InitializeWebViewAsync()",
            "service.ExportViewerSceneToGlb(scene)");

        Assert.Contains("CompleteNifViewerNativeOutcome(e.State)", meshHost, StringComparison.Ordinal);
        Assert.Contains("CompleteNpcViewerNativeOutcome(e.State)", npcBrowser, StringComparison.Ordinal);
    }

    [Fact]
    public void MeshViewerRendererLayersHaveExclusiveVisibilityOwners()
    {
        var meshHost = SourceContract.ReadAppSource("NifConverterTab.xaml.cs");
        var meshLoad = SourceContract.Extract(
            meshHost,
            "private async Task LoadNifIntoViewerAsync(",
            "private async Task SetNifViewerFallbackStatusAsync(");

        SourceContract.AssertOrder(
            meshLoad,
            "ShowNifViewerNativeHost();",
            "NifConverterWorkflowService.LoadModelAsync(",
            "nativeState == BethesdaSceneViewerRenderState.Faulted",
            "await InitializeNifViewerWebViewAsync();",
            "ShowNifViewerCompatibilityHost();",
            "service.ExportViewerSceneToGlb(result.Scene)");

        var initialization = SourceContract.Extract(
            meshHost,
            "private async Task InitializeNifViewerWebViewCoreAsync()",
            "private async void NifViewerBrowseFolder_Click");
        Assert.DoesNotContain(
            "NifModelViewer.Visibility = Visibility.Visible;",
            initialization,
            StringComparison.Ordinal);
        Assert.Contains(
            "ShowNifViewerPlaceholder($\"WebView2 init failed: {ex.Message}\");",
            initialization,
            StringComparison.Ordinal);

        var fallbackStatus = SourceContract.Extract(
            meshHost,
            "private async Task SetNifViewerFallbackStatusAsync(",
            "private void ShowNifViewerNativeHost()");
        SourceContract.AssertOrder(
            fallbackStatus,
            "NifModelViewer.Visibility == Visibility.Visible",
            "await NifModelViewer.ExecuteScriptAsync(",
            "ShowNifViewerPlaceholder(message);");

        SourceContract.AssertOrder(
            SourceContract.Extract(
                meshHost,
                "private void ShowNifViewerNativeHost()",
                "private void ShowNifViewerCompatibilityHost()"),
            "NifModelViewer.Visibility = Visibility.Collapsed;",
            "NifViewerPlaceholderText.Visibility = Visibility.Collapsed;",
            "NifSceneViewer.Visibility = Visibility.Visible;");
        SourceContract.AssertOrder(
            SourceContract.Extract(
                meshHost,
                "private void ShowNifViewerCompatibilityHost()",
                "private void ShowNifViewerPlaceholder("),
            "NifSceneViewer.Visibility = Visibility.Collapsed;",
            "NifViewerPlaceholderText.Visibility = Visibility.Collapsed;",
            "NifModelViewer.Visibility = Visibility.Visible;");
        SourceContract.AssertOrder(
            SourceContract.Extract(
                meshHost,
                "private void ShowNifViewerPlaceholder(",
                "private async Task CancelNifViewerLoadAndDrainAsync()"),
            "NifSceneViewer.Visibility = Visibility.Collapsed;",
            "NifModelViewer.Visibility = Visibility.Collapsed;",
            "NifViewerPlaceholderText.Text = message;",
            "NifViewerPlaceholderText.Visibility = Visibility.Visible;");

        SourceContract.AssertOrder(
            SourceContract.Extract(
                meshHost,
                "private void NifSceneViewer_RenderStateChanged(",
                "private void CompleteNifViewerNativeOutcome("),
            "_nifViewerNativeReady = true;",
            "ShowNifViewerNativeHost();",
            "CloseNifViewerCompatibilityHost();");
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
        var presentationActivation = SourceContract.Extract(
            control,
            "internal void SetPresentationActive(bool active)",
            "internal void AttachRenderSession(");
        SourceContract.AssertOrder(
            presentationActivation,
            "if (_isPresentationActive == active)",
            "if (active)",
            "InvalidateViewport();",
            "return;");
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
    public void FileTypeTabFilteringKeepsTheSelectedNativeViewerMounted()
    {
        var npcHost = SourceContract.ReadAppSource("SingleFileTab.xaml.cs");
        var configure = SourceContract.Extract(
            npcHost,
            "private void ConfigureSubTabsForFileType(AnalysisFileType fileType)",
            "/// <summary>The TabViewItem backing a policy sub-tab.</summary>");

        SourceContract.AssertOrder(
            configure,
            "var visibleItems = visibleTabs.Select(SubTabItem).ToArray();",
            "var retainSelected = selectedItem is not null",
            "if (retainSelected)",
            "if (!ReferenceEquals(SubTabView.TabItems[i], selectedItem))",
            "SubTabView.TabItems.RemoveAt(i);",
            "if (ReferenceEquals(item, selectedItem)) continue;",
            "SubTabView.TabItems.Insert(i, item);",
            "else",
            "SubTabView.TabItems.Clear();",
            "SubTabView.TabItems.Add(item);",
            "TrySelectSubTab(AnalysisSubTabPolicy.Fallback(previous, fileType));");
    }

    [Fact]
    public void TransientSceneInitializationRetainsTheBoundSwapChainSurface()
    {
        var lifecycle = SourceContract.ReadAppSource("BethesdaSceneViewerControl.Lifecycle.cs");
        var publish = SourceContract.Extract(
            lifecycle,
            "private void PublishRenderState(",
            "private void NotifyObservableRenderStateChanged()");

        Assert.Contains(
            "if (state != BethesdaSceneViewerRenderState.Ready || _scene is null)",
            publish,
            StringComparison.Ordinal);
        Assert.Contains(
            "if (_scene is null || state == BethesdaSceneViewerRenderState.Faulted)",
            publish,
            StringComparison.Ordinal);
        SourceContract.AssertOrder(
            publish,
            "DetachRenderLoop();",
            "CancelPendingCapture(",
            "state == BethesdaSceneViewerRenderState.Faulted",
            "ReleasePanelSurface();");
    }

    [Fact]
    public void NpcListFilteringDoesNotPublishATransientNullViewerScene()
    {
        var npcBrowser = SourceContract.ReadAppSource("SingleFileTab.NpcBrowser.cs");
        var applyList = SourceContract.Extract(
            npcBrowser,
            "private void ApplyNpcListState(",
            "private void NpcSearchBox_TextChanged(");
        var selectionChanged = SourceContract.Extract(
            npcBrowser,
            "private async void NpcListView_SelectionChanged(",
            "private void ApplyNpcSelectionState(");

        SourceContract.AssertOrder(
            applyList,
            "_npcListRefreshInProgress = true;",
            "NpcListView.ItemsSource = state.Items;",
            "NpcListView.SelectedItem = state.RestoredSelection;",
            "_npcListRefreshInProgress = false;");
        SourceContract.AssertOrder(
            selectionChanged,
            "if (_npcListRefreshInProgress) return;",
            "NpcSceneViewer.ClearScene();");
    }

    [Fact]
    public void StreamingDescriptorPromotionDrainsEarlierDirectFramesBeforeRecording()
    {
        var lifecycle = SourceContract.ReadAppSource("BethesdaSceneViewerControl.Lifecycle.cs");
        var frame = SourceContract.Extract(
            lifecycle,
            "private void RenderNativeFrame(",
            "private static void BindNeutralFrameConstants(");
        var session = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12", "Viewer",
            "BethesdaViewerRenderSession12.cs");

        SourceContract.AssertOrder(
            frame,
            "if (session.RequiresGpuIdleBeforeFrame)",
            "graphics.WaitForGpuIdle();",
            "using var recording = graphics.BeginFrame();",
            "session.Render(frame);");
        Assert.Contains(
            "BethesdaViewerFrameSynchronizationPolicy.RequiresGpuIdleBeforeFrame(\n            _state == BethesdaSceneViewerRenderState.Ready,\n            TexturesSettled)",
            session,
            StringComparison.Ordinal);
        Assert.Contains("texture streaming settled after {0} descriptor-safety drain(s)", frame,
            StringComparison.Ordinal);
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
            "CloseNifViewerCompatibilityHost();");
        Assert.DoesNotContain(
            "NifModelViewer.Visibility = Visibility.Visible;",
            meshInit,
            StringComparison.Ordinal);
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
