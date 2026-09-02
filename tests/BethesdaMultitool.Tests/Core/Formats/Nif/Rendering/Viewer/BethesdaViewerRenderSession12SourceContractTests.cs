using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Viewer;

public sealed class BethesdaViewerRenderSession12SourceContractTests
{
    private static string SessionSource() => SourceContract.ReadSource(
        "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12", "Viewer",
        "BethesdaViewerRenderSession12.cs");

    [Fact]
    public void SessionMaterializesBeforeReadyAndRendersWaterBetweenDepthAndTransparentGeometry()
    {
        var source = SessionSource();
        var build = SourceContract.Extract(
            source,
            "private void BuildGpuScene()",
            "private void ConfigureWaterRenderer(");
        var render = SourceContract.Extract(
            source,
            "public void Render(in BethesdaSceneViewerFrame12 frame)",
            "public void Dispose()");

        SourceContract.AssertOrder(
            build,
            "ReferenceMeshCache12.UploadDecodedMesh(",
            "ConfigureRawSkyRenderer(graphics, posed)",
            "new ReferencePipelineFactory12(",
            "new BethesdaViewerStaticRenderer12(",
            "ConfigureWaterRenderer(graphics, posed)",
            "BethesdaSceneViewerRenderState.Ready");
        SourceContract.AssertOrder(
            render,
            "_textureCache.ResetFrameStats()",
            "RefreshRawSkyLayers();",
            "_skyGeometry.Render(",
            "RenderDepthWriting(",
            "HasVisibleWaterToPartition(",
            "RenderTransparentBelowWater(",
            "_waterRenderer.Render(",
            "RenderTransparentAtOrAboveWater(");
        Assert.Contains("WaterTransparencyPartition.IsWhollyBelow(", StaticRendererSource(),
            StringComparison.Ordinal);
        Assert.Contains("posedScene.Mesh.Submeshes[sourceIndex].Vertices", StaticRendererSource(),
            StringComparison.Ordinal);
        Assert.Contains("RenderOrder is the primary authored layer boundary", StaticRendererSource(),
            StringComparison.Ordinal);
        var water = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
            "WaterRenderer12.cs");
        Assert.Contains("geometry.TryGetHeightAtXY(worldX, worldY, out var nifHeight)", water,
            StringComparison.Ordinal);
        Assert.Contains("!TexturesSettled", source, StringComparison.Ordinal);
        Assert.Contains("Standalone water has no record-level WATR context", source, StringComparison.Ordinal);
        Assert.Contains("constants[(1 * 4) + 3] = 1f", source, StringComparison.Ordinal);
        Assert.Contains("neutral studio lighting", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RawNifSkyUsesDedicatedRendererBeforeGeometryWithoutPlaceholderTextures()
    {
        var session = SessionSource();
        var renderer = StaticRendererSource();
        var pose = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12", "Viewer",
            "BethesdaViewerScenePoseMaterializer12.cs");
        var skyRenderer = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
            "SkyGeometryRenderer12.cs");
        var render = SourceContract.Extract(
            session,
            "public void Render(in BethesdaSceneViewerFrame12 frame)",
            "public void Dispose()");
        var release = SourceContract.Extract(
            session,
            "private void ReleaseGpuScene(bool waitForIdle)",
            "private void PublishState(");

        SourceContract.AssertOrder(
            render,
            "BindViewerAtmosphere(",
            "RefreshRawSkyLayers();",
            "_skyGeometry.Render(",
            "radiusOverride: ResolveRawSkyRadius(frame.Camera)",
            "_staticRenderer?.RenderDepthWriting(",
            "frame.Camera.ViewProjection");
        Assert.Contains("BethesdaViewerNativeSkyPolicy.IsDedicatedRawNifLayer", session,
            StringComparison.Ordinal);
        Assert.Contains("type is SkyObjectType.Stars or SkyObjectType.Clouds", session,
            StringComparison.Ordinal);
        Assert.Contains("candidate.AuthoredTexture.IsResident", session,
            StringComparison.Ordinal);
        Assert.Contains("candidate.Layer.Type == SkyObjectType.Clouds", session,
            StringComparison.Ordinal);
        Assert.Contains("authoredTexture?.BindlessIndex ?? uint.MaxValue", session,
            StringComparison.Ordinal);
        Assert.Contains("AtmosphereState.Resolve(", session, StringComparison.Ordinal);
        Assert.Contains("RawSkyPreviewHour = 12f", session, StringComparison.Ordinal);
        Assert.Contains("neutral clear-noon atmosphere", session, StringComparison.Ordinal);
        Assert.Contains("star visibility are held at neutral inspection strength", session,
            StringComparison.Ordinal);
        Assert.Contains("radiusOverride: ResolveRawSkyRadius(frame.Camera)", session,
            StringComparison.Ordinal);
        Assert.Contains("var insideFarPlane = camera.FarPlane * 0.9f;", session,
            StringComparison.Ordinal);
        Assert.Contains("insideFarPlane > camera.NearPlane", session, StringComparison.Ordinal);
        Assert.Contains("float? radiusOverride = null", skyRenderer, StringComparison.Ordinal);
        Assert.Contains("requestedRadius > 0f", skyRenderer, StringComparison.Ordinal);
        Assert.DoesNotContain("ProbeFirstExisting", session, StringComparison.Ordinal);
        Assert.Contains("ResolveAggregateBounds(scene, verticesByPart, supported)", pose,
            StringComparison.Ordinal);
        Assert.Contains("BethesdaViewerNativeSkyPolicy.IsDedicatedRawNifLayer(", pose,
            StringComparison.Ordinal);
        Assert.Contains("BethesdaViewerNativeSkyPolicy.IsDedicatedRawNifLayer(", renderer,
            StringComparison.Ordinal);
        SourceContract.AssertOrder(
            release,
            "var skyGeometry = _skyGeometry;",
            "DisposeSceneResourceNoThrow(skyGeometry, \"raw sky renderer\");",
            "_rawSkyCandidates.Clear();",
            "var mesh = _mesh;",
            "DisposeSceneResourceNoThrow(mesh, \"mesh cache entry\");",
            "DisposeSceneResourceNoThrow(textureCache, \"texture cache\");");
        SourceContract.AssertOrder(
            release,
            "_graphics.WaitForGpuIdle();",
            "catch (Exception ex)",
            "var skyGeometry = _skyGeometry;");

        // Constructor-local ownership closes the first/second-PSO failure hole before a session can
        // receive the renderer; BuildGpuScene can therefore publish Faulted without leaking it.
        SourceContract.AssertOrder(
            skyRenderer,
            "gradient = CreatePso(",
            "stars = CreatePso(",
            "clouds = CreatePso(",
            "_psoGradient = gradient;",
            "catch",
            "clouds?.Dispose();",
            "stars?.Dispose();",
            "gradient?.Dispose();");
    }

    [Fact]
    public void RawSkyReadinessDistinguishesPendingResidentAndTerminalUnavailableTextures()
    {
        var session = SessionSource();
        var render = SourceContract.Extract(
            session,
            "public void Render(in BethesdaSceneViewerFrame12 frame)",
            "public void Dispose()");
        var refresh = SourceContract.Extract(
            session,
            "private bool RefreshRawSkyLayers()",
            "private void RefreshRawSkyReadyState()");
        var readyState = SourceContract.Extract(
            session,
            "private void RefreshRawSkyReadyState()",
            "private static SkyGeometryLayer BuildRawSkyLayer(");

        SourceContract.AssertOrder(
            render,
            "_textureCache.ResetFrameStats()",
            "var rawSkyAvailabilityChanged = RefreshRawSkyLayers();",
            "RefreshRawSkyReadyState();",
            "if (_state != BethesdaSceneViewerRenderState.Ready)",
            "_skyGeometry.Render(");
        SourceContract.AssertOrder(
            refresh,
            "candidate.AuthoredTexture.IsResident",
            "candidate.AuthoredTexture.IsReady",
            "unavailableTextureCount++;",
            "var activeLayersChanged =",
            "_skyGeometry.SetLayers(",
            "_rawSkyPendingTextureCount = pendingTextureCount;",
            "_rawSkyUnavailableTextureCount = unavailableTextureCount;");
        Assert.Contains("_rawSkyResidentLayerCount == 0", readyState, StringComparison.Ordinal);
        Assert.Contains("_rawSkyPendingTextureCount == 0", readyState, StringComparison.Ordinal);
        Assert.Contains("_rawSkyUnavailableTextureCount > 0", readyState, StringComparison.Ordinal);
        Assert.Contains("BethesdaSceneViewerRenderState.Faulted", readyState, StringComparison.Ordinal);
        Assert.Contains("BuildTerminalRawSkyFailureStatus(posed)", readyState, StringComparison.Ordinal);
        Assert.Contains("var hasRawSkyCandidates = _rawSkyCandidates.Count > 0;", session,
            StringComparison.Ordinal);
        Assert.Contains("_rawSkyClassifiedPartCount > 0", session, StringComparison.Ordinal);
        Assert.Contains("are still resolving; those layers remain withheld until resident", session,
            StringComparison.Ordinal);
        Assert.Contains("resolved unavailable and were omitted; no white placeholder was substituted", session,
            StringComparison.Ordinal);
        Assert.Contains("has no drawable layer", session, StringComparison.Ordinal);
    }

    [Fact]
    public void DepthWritingPassKeepsRenderOrderPrimaryAcrossOpaqueAndBlendKinds()
    {
        var renderer = StaticRendererSource();

        SourceContract.AssertOrder(
            renderer,
            "_depthOrdered = depthOrdered",
            ".OrderBy(static entry => entry.Draw.NativeSemantics.RenderOrder)",
            ".ThenBy(static entry => entry.Kind)",
            "foreach (var entry in _depthOrdered)",
            "RenderTransparentCore(");
        Assert.Contains("DepthDrawKind.Opaque", renderer, StringComparison.Ordinal);
        Assert.Contains("DepthDrawKind.DepthWritingBlend", renderer, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var draw in _opaque)", renderer, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var draw in _depthWriting)", renderer, StringComparison.Ordinal);
    }

    [Fact]
    public void DirectModernSpecializationsAreClassifiedOnceAndFailClosedToTheUberPipeline()
    {
        var renderer = StaticRendererSource();
        var session = SessionSource();

        var classify = SourceContract.Extract(
            renderer,
            "private static OpaqueSpecializationRoute ResolveOpaqueSpecialization(",
            "private static DecodedBethesdaViewerSubmeshSemantics12 ResolveNativeSemantics(");
        foreach (var excludedAxis in new[]
                 {
                     "draw.NativeSemantics.SkyType is not null",
                     "submesh.AlphaBlend",
                     "submesh.IsDecal",
                     "submesh.DepthTestOff",
                     "NifAlphaRenderMode.AlphaToCoverage",
                     "submesh.MaterialAlphaController is not null",
                 })
        {
            Assert.Contains(excludedAxis, classify, StringComparison.Ordinal);
        }

        SourceContract.AssertOrder(
            renderer,
            "ResolveOpaqueSpecialization(",
            "_depthOrdered = depthOrdered",
            "foreach (var entry in _depthOrdered)",
            "entry.SpecializedPipeline is { } specializedPipeline",
            "_pipelines.GetDirectOpaquePipeline(");
        SourceContract.AssertOrder(
            classify,
            "ModernStandardOpaqueShaderPolicy.Resolve(in facts)",
            "TryGetDirectStarfieldDiffuseLitPso(",
            "TryGetDirectModernStandardOpaquePso(",
            "return default;");
        Assert.Contains("DescribeOpaqueSpecialization()", session, StringComparison.Ordinal);
        Assert.Contains("DirectModernStandardOpaqueRequested", renderer, StringComparison.Ordinal);
        Assert.Contains("DirectModernStandardOpaqueAvailable", renderer, StringComparison.Ordinal);
        Assert.Contains("DirectStarfieldDiffuseLitRequested", renderer, StringComparison.Ordinal);
        Assert.Contains("DirectStarfieldDiffuseLitOpaqueAvailable", renderer, StringComparison.Ordinal);
        Assert.Contains("eligible opaque part(s) use the direct", renderer, StringComparison.Ordinal);
    }

    private static string StaticRendererSource() => SourceContract.ReadSource(
        "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12", "Viewer",
        "BethesdaViewerStaticRenderer12.cs");

    [Fact]
    public void ViewerRetainsNativeAlphaToCoverageSemanticsAndHasAnExplicitSingleSampleFallback()
    {
        var decoder = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12", "Viewer",
            "BethesdaViewerSceneDecoder12.cs");
        var renderer = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12", "Viewer",
            "BethesdaViewerStaticRenderer12.cs");
        var pipelines = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
            "ReferencePipelineFactory12.cs");

        Assert.Contains("NifAlphaRenderMode NativeAlphaRenderMode", decoder, StringComparison.Ordinal);
        Assert.Contains("NifAlphaClassifier.Classify(source, diffuseTexture: null).RenderMode", decoder,
            StringComparison.Ordinal);
        Assert.Contains("_pipelines.GetDirectAlphaToCoveragePipeline", renderer, StringComparison.Ordinal);
        Assert.Contains("submesh.IsDecal,\n                    submesh.DepthTestOff)", renderer,
            StringComparison.Ordinal);
        Assert.Contains("if (nativeAlphaToCoverage && submesh.AlphaTest)", renderer,
            StringComparison.Ordinal);
        Assert.Contains("alphaState.X = submesh.AlphaTestThreshold;", renderer,
            StringComparison.Ordinal);
        Assert.Contains("_alphaToCoverageFallbackCount", renderer, StringComparison.Ordinal);
        Assert.Contains("AlphaToCoverageEnable = alphaToCoverage", pipelines, StringComparison.Ordinal);
        Assert.Contains("AlphaToCoverageAvailable = _gpu.SceneSampleCount > 1", pipelines,
            StringComparison.Ordinal);
        Assert.Contains("(true, true, true) => DirectOpaqueDoubleDecalNoDepthA2CPso", pipelines,
            StringComparison.Ordinal);
        Assert.Contains("(false, true, false) => DirectOpaqueBackDecalA2CPso", pipelines,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CurrentPoseTintSeamsAndControllerTimeAreMaterializedBeforeUpload()
    {
        var pose = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12", "Viewer",
            "BethesdaViewerScenePoseMaterializer12.cs");
        var session = SessionSource();

        SourceContract.AssertOrder(
            pose,
            "SkinCurrentPose(scene, partIndex, part.Name, skin, vertices)",
            "ApplyNativeTint(",
            "var stitchedVertexCount = ApplyBoundaryStitchGroups(",
            "var posedBounds = ResolveAggregateBounds(",
            "new DecodedNifMesh12(");
        Assert.Contains("NifSkinningMath.ApplySkinningPositionsDqs(", pose, StringComparison.Ordinal);
        Assert.Contains("(vertices[index].VertexColorRgba & 0xFF000000u) | 0x00FFFFFFu", pose,
            StringComparison.Ordinal);
        Assert.Contains("submesh.StarfieldMaterialColor.IsVertexLerp", pose, StringComparison.Ordinal);
        Assert.Contains("return effectTint * encoded;", pose, StringComparison.Ordinal);
        Assert.Contains("EffectTintSpecified = source.EffectTintSpecified ||", pose,
            StringComparison.Ordinal);
        SourceContract.AssertOrder(
            session,
            "_posedScene = BethesdaViewerScenePoseMaterializer12.Materialize(_decodedScene);",
            "scene.Bounds = _posedScene.Bounds;",
            "BuildGpuScene();");
        SourceContract.AssertOrder(
            session,
            "_elapsedSeconds += deltaSeconds;",
            "RenderDepthWriting(",
            "_elapsedSeconds);",
            "RenderTransparent(");
    }

    [Fact]
    public void NativeAnimationUsesSnapshottedClipsAtomicUploadsAndSharedAccessibleControls()
    {
        var session = SessionSource();
        var animatedPose = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12", "Viewer",
            "BethesdaViewerAnimatedPose12.cs");
        var browser = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering",
            "NifBrowserService.cs");
        var control = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "App", "Controls", "BethesdaSceneViewer",
            "BethesdaSceneViewerControl.Animation.cs");
        var xaml = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "App", "Controls", "BethesdaSceneViewer",
            "BethesdaSceneViewerControl.xaml");
        var lifecycle = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "App", "Controls", "BethesdaSceneViewer",
            "BethesdaSceneViewerControl.Lifecycle.cs");

        SourceContract.AssertOrder(
            session,
            "_decodedScene = BethesdaViewerSceneDecoder12.Decode(scene);",
            "_animationClipNames = _decodedScene.AnimationClips",
            "_posedScene = BethesdaViewerScenePoseMaterializer12.Materialize(_decodedScene);");
        Assert.Contains("_decodedScene.AnimationClips[_selectedAnimationClipIndex]", session,
            StringComparison.Ordinal);
        SourceContract.AssertOrder(
            SourceContract.Extract(
                session,
                "private void ConfigureAnimationPlayer()",
                "private void ClearAnimatedVertexBufferViews()"),
            "ClearAnimatedVertexBufferViews();",
            "_animatedPose = null;",
            "new BethesdaViewerAnimatedPose12(");
        SourceContract.AssertOrder(
            SourceContract.Extract(
                session,
                "public void Render(in BethesdaSceneViewerFrame12 frame)",
                "public void Dispose()"),
            "animatedPose.Update(",
            "RefreshRawSkyLayers();",
            "RenderDepthWriting(");

        Assert.Contains("BethesdaViewerNativeSkyPolicy.IsDedicatedRawNifLayer(", animatedPose,
            StringComparison.Ordinal);
        Assert.Contains("out var upload,", animatedPose, StringComparison.Ordinal);
        Assert.DoesNotContain("RingAllocation[]", animatedPose, StringComparison.Ordinal);
        SourceContract.AssertOrder(
            animatedPose,
            "if (!ring.TryAllocate(",
            "part.WorkingVertices.AsSpan().CopyTo(destination);",
            "part.CachedSubmesh.AnimatedVertexBufferView = new VertexBufferView");

        SourceContract.AssertOrder(
            browser,
            "NifNodeKeyframeTrackCollector.Collect(",
            "embedded node animation",
            "if (animation is null)",
            "NifControllerSequenceTrackCollector.Collect(");
        Assert.Contains("preserveFileRootTransformAndTrack: true", browser, StringComparison.Ordinal);

        Assert.Contains("AnimationPlayPauseButton_Click", control, StringComparison.Ordinal);
        Assert.Contains("AnimationPlayPauseIcon.Glyph", control, StringComparison.Ordinal);
        Assert.Contains("private const string AnimationPlayGlyph = \"\\uE768\";", control,
            StringComparison.Ordinal);
        Assert.Contains("private const string AnimationPauseGlyph = \"\\uE769\";", control,
            StringComparison.Ordinal);
        Assert.Contains("x:Name=\"AnimationPlayPauseIcon\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Glyph=\"&#xE769;\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("&#x23F8;", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u23F8", control, StringComparison.Ordinal);
        Assert.Contains("AnimationLoadKfButton_Click", control, StringComparison.Ordinal);
        Assert.Contains("NifControllerSequenceNameTrackReader.ReadAll(data, nif)", control,
            StringComparison.Ordinal);
        Assert.Contains("BethesdaViewerNameTargetedAnimationAdapter.TryCreateClip(", control,
            StringComparison.Ordinal);
        SourceContract.AssertOrder(
            control,
            "targetScene.AnimationClips.Add(clip);",
            "ReloadSessionAfterAnimationMutation(targetScene)",
            "_renderSession.SetScene(null);",
            "_renderSession.SetScene(targetScene);");
        Assert.Contains("AnimationClipComboBox_SelectionChanged", control, StringComparison.Ordinal);
        Assert.Contains("AnimationTimeline_ValueChanged", control, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromMilliseconds(100)", control, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Animation clip\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Animation timeline\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Load KF animation\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Animation load status\"", xaml,
            StringComparison.Ordinal);
        Assert.Contains("if (!_isPresentationActive || !IsEffectivelyVisible())", lifecycle,
            StringComparison.Ordinal);
        Assert.Contains("DetachRenderLoop();", lifecycle, StringComparison.Ordinal);
    }
}
